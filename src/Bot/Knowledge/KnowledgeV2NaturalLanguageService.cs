using Bot.ChromeNs;
using BotLib;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.Knowledge
{
    internal static class KnowledgeV2NaturalLanguageService
    {
        private const int InitialTimeoutSeconds = 180;
        private const int RepairTimeoutSeconds = 60;
        private const string SystemPrompt =
            "你是电商客服 Knowledge Center V2 的结构化知识录入助手。" +
            "用户会用一句自然语言描述一条要新增的客服知识。你必须只依据这句话整理，不得补造价格、库存、时效、售后承诺、账号、密码或商品属性。" +
            "只输出一个严格 JSON 对象，不要 Markdown，不要解释。" +
            "字段固定为：title,type,intent,subject,predicate,entities,aliases,answer,short_answer,conditions,exclusions,required_context,product_ids,risk_level,confidence,authority,status。" +
            "type 只能是 business_fact/procedure/presale/order_rule/after_sale/safety_rule/fixed_reply/product_knowledge/learning_candidate/temporary；" +
            "risk_level 只能 normal/high；status 只能 active/candidate。" +
            "entities/aliases/conditions/exclusions/required_context/product_ids 必须是 JSON 字符串数组。" +
            "title 是便于管理的短标题；answer 是可直接给买家的标准答案；aliases 生成常见同义问法；" +
            "无法从用户原句确定的事实不要猜测，对可选字段使用空字符串或空数组。confidence/authority 范围 0~1。";

        public static async Task<KnowledgeV2Record> GenerateAsync(string naturalLanguage, KnowledgeV2RecordsPageMode mode, CancellationToken token)
        {
            naturalLanguage = (naturalLanguage ?? string.Empty).Trim();
            if (naturalLanguage.Length == 0) throw new ArgumentException("请输入一句要新增的知识。", "naturalLanguage");
            var modeHint = ModeHint(mode);
            Log.Info("KnowledgeV2 AI一句话新增开始: mode=" + mode + ", inputChars=" + naturalLanguage.Length);

            var messages = BuildInitialMessages(naturalLanguage, modeHint);
            var raw = await Task.Run(
                () => MyOpenAI.CallStructuredChat(messages, 2600, 0.1, InitialTimeoutSeconds, token),
                token).ConfigureAwait(false);
            LogResult("initial", raw == null ? false : raw.Success, raw == null ? string.Empty : raw.Answer,
                raw == null ? "result_null" : raw.Error);
            if (raw == null || !raw.Success)
                throw new InvalidOperationException(raw == null ? "AI 请求没有返回结果。" : raw.Error);

            JObject obj;
            string parseStrategy;
            Exception initialParseError;
            if (!TryParseObject(raw.Answer, out obj, out parseStrategy, out initialParseError))
            {
                Log.Info("KnowledgeV2 AI一句话新增首次结构化解析失败，准备一次受控修复: answerChars="
                    + SafeLength(raw.Answer) + ", shape=" + DescribeShape(raw.Answer)
                    + ", error=" + SafeError(initialParseError));

                var repairMessages = BuildRepairMessages(naturalLanguage, modeHint, raw.Answer, initialParseError);
                var repaired = await Task.Run(
                    () => MyOpenAI.CallStructuredChat(repairMessages, 2600, 0.0, RepairTimeoutSeconds, token),
                    token).ConfigureAwait(false);
                LogResult("repair", repaired == null ? false : repaired.Success,
                    repaired == null ? string.Empty : repaired.Answer,
                    repaired == null ? "result_null" : repaired.Error);
                if (repaired == null || !repaired.Success)
                {
                    throw new InvalidOperationException(
                        "AI 返回的知识字段格式无效，自动 JSON 修复请求也失败："
                        + (repaired == null ? "没有返回结果" : SafeText(repaired.Error, 180)));
                }

                Exception repairParseError;
                if (!TryParseObject(repaired.Answer, out obj, out parseStrategy, out repairParseError))
                {
                    Log.Info("KnowledgeV2 AI一句话新增 JSON 修复后仍无法解析: answerChars="
                        + SafeLength(repaired.Answer) + ", shape=" + DescribeShape(repaired.Answer)
                        + ", error=" + SafeError(repairParseError));
                    throw new InvalidOperationException(
                        "AI 返回的知识字段格式无效：首次返回不是有效 JSON，自动修复后仍无法解析（"
                        + SafeError(repairParseError) + "）");
                }
            }

            Log.Info("KnowledgeV2 AI一句话新增结构化解析成功: strategy=" + parseStrategy
                + ", fields=" + (obj == null ? 0 : obj.Properties().Count()));
            var now = DateTime.Now;
            var record = new KnowledgeV2Record
            {
                Id = Guid.NewGuid().ToString("N"), Title = Text(obj, "title"), Type = Text(obj, "type"), Intent = Text(obj, "intent"),
                Subject = Text(obj, "subject"), Predicate = Text(obj, "predicate"), Entities = List(obj, "entities"), Aliases = List(obj, "aliases"),
                Answer = Text(obj, "answer"), ShortAnswer = Text(obj, "short_answer"), Conditions = List(obj, "conditions"), Exclusions = List(obj, "exclusions"),
                RequiredContext = List(obj, "required_context"), ProductIds = List(obj, "product_ids"), RiskLevel = Text(obj, "risk_level"),
                Confidence = Number(obj, "confidence", 0.86), Authority = Number(obj, "authority", 0.90), Enabled = true, Status = Text(obj, "status"),
                SourceType = "manual_ai_generated", SourceId = "manual:" + now.ToString("yyyyMMddHHmmssfff"), CreatedAt = now, UpdatedAt = now, LastVerifiedAt = now
            };
            ApplySafeDefaults(record, mode, naturalLanguage);
            Log.Info("KnowledgeV2 AI一句话新增字段标准化完成: type=" + record.Type
                + ", status=" + record.Status + ", risk=" + record.RiskLevel
                + ", confidence=" + record.Confidence.ToString("0.00")
                + ", answerChars=" + SafeLength(record.Answer));
            return record;
        }

        private static JArray BuildInitialMessages(string naturalLanguage, string modeHint)
        {
            return new JArray
            {
                new JObject { ["role"] = "system", ["content"] = SystemPrompt },
                new JObject { ["role"] = "user", ["content"] = modeHint + "\n人工一句话：" + naturalLanguage }
            };
        }

        private static JArray BuildRepairMessages(string naturalLanguage, string modeHint, string invalidAnswer, Exception parseError)
        {
            var previous = SafeText(invalidAnswer, 1800);
            var error = SafeError(parseError);
            return new JArray
            {
                new JObject
                {
                    ["role"] = "system",
                    ["content"] = SystemPrompt
                        + " 上一次输出没有通过 JSON 解析。现在执行格式修复：必须从第一个字符 { 开始，到最后一个字符 } 结束；禁止代码块、前言、解释、道歉或额外文字；所有数组字段必须是真正的 JSON 数组。"
                },
                new JObject
                {
                    ["role"] = "user",
                    ["content"] = modeHint
                        + "\n原始人工一句话：" + naturalLanguage
                        + "\n上一次解析错误：" + error
                        + "\n上一次无效输出：" + previous
                        + "\n请仅返回修复后的严格 JSON 对象。"
                }
            };
        }

        private static string ModeHint(KnowledgeV2RecordsPageMode mode)
        {
            return mode == KnowledgeV2RecordsPageMode.Product ? "当前页面=商品知识，优先使用 product_knowledge。"
                : mode == KnowledgeV2RecordsPageMode.Process ? "当前页面=流程，优先使用 procedure/order_rule。"
                : mode == KnowledgeV2RecordsPageMode.Learning ? "当前页面=学习候选，type 使用 learning_candidate，status 使用 candidate。"
                : "当前页面=全部知识，请按语义选择最合适的 type。";
        }

        private static void ApplySafeDefaults(KnowledgeV2Record record, KnowledgeV2RecordsPageMode mode, string original)
        {
            if (string.IsNullOrWhiteSpace(record.Title)) record.Title = original.Length <= 60 ? original : original.Substring(0, 60);
            if (string.IsNullOrWhiteSpace(record.Answer)) record.Answer = original;
            if (string.IsNullOrWhiteSpace(record.Type)) record.Type = mode == KnowledgeV2RecordsPageMode.Product ? "product_knowledge" : mode == KnowledgeV2RecordsPageMode.Process ? "procedure" : mode == KnowledgeV2RecordsPageMode.Learning ? "learning_candidate" : "business_fact";
            if (mode == KnowledgeV2RecordsPageMode.Product && record.Type == "business_fact") record.Type = "product_knowledge";
            if (mode == KnowledgeV2RecordsPageMode.Learning) record.Type = "learning_candidate";
            if (string.IsNullOrWhiteSpace(record.Intent)) record.Intent = "general";
            if (string.IsNullOrWhiteSpace(record.Predicate)) record.Predicate = "general";
            record.RiskLevel = string.Equals(record.RiskLevel, "high", StringComparison.OrdinalIgnoreCase) ? "high" : "normal";
            record.Status = mode == KnowledgeV2RecordsPageMode.Learning ? "candidate" : string.Equals(record.Status, "candidate", StringComparison.OrdinalIgnoreCase) ? "candidate" : "active";
            record.Confidence = Clamp(record.Confidence <= 0 ? 0.86 : record.Confidence);
            record.Authority = Clamp(record.Authority <= 0 ? 0.90 : record.Authority);
        }

        private static bool TryParseObject(string raw, out JObject obj, out string strategy, out Exception error)
        {
            obj = null;
            strategy = string.Empty;
            error = null;
            raw = (raw ?? string.Empty).Trim();
            if (raw.Length == 0)
            {
                error = new InvalidOperationException("AI 返回为空");
                return false;
            }

            try
            {
                var direct = UnwrapToken(JToken.Parse(raw));
                if (direct != null)
                {
                    obj = direct;
                    strategy = "direct-json";
                    return true;
                }
                error = new InvalidOperationException("JSON 对象未包含知识字段");
            }
            catch (Exception ex) { error = ex; }

            var unfenced = StripCodeFence(raw);
            if (!string.Equals(unfenced, raw, StringComparison.Ordinal))
            {
                try
                {
                    var fenced = UnwrapToken(JToken.Parse(unfenced));
                    if (fenced != null)
                    {
                        obj = fenced;
                        strategy = "markdown-fence";
                        return true;
                    }
                    error = new InvalidOperationException("JSON 对象未包含知识字段");
                }
                catch (Exception ex) { error = ex; }
            }

            JObject extracted;
            Exception extractError;
            if (TryExtractBalancedObject(unfenced, out extracted, out extractError))
            {
                var accepted = UnwrapToken(extracted);
                if (accepted != null)
                {
                    obj = accepted;
                    strategy = "balanced-object";
                    return true;
                }
                error = new InvalidOperationException("JSON 对象未包含知识字段");
            }
            else if (extractError != null)
            {
                error = extractError;
            }
            if (error == null) error = new InvalidOperationException("未找到 JSON 对象");
            return false;
        }

        private static JObject UnwrapToken(JToken token)
        {
            if (token == null) return null;
            var obj = token as JObject;
            if (obj != null)
            {
                if (LooksLikeKnowledgeObject(obj)) return obj;
                foreach (var key in new[] { "knowledge", "record", "data", "result" })
                {
                    var nested = obj[key];
                    var nestedObject = nested as JObject;
                    if (nestedObject != null && LooksLikeKnowledgeObject(nestedObject)) return nestedObject;
                    var nestedArray = nested as JArray;
                    if (nestedArray != null && nestedArray.Count == 1)
                    {
                        var singleNested = nestedArray[0] as JObject;
                        if (singleNested != null && LooksLikeKnowledgeObject(singleNested)) return singleNested;
                    }
                }
                return null;
            }

            var array = token as JArray;
            if (array != null && array.Count == 1)
            {
                var single = array[0] as JObject;
                if (single != null && LooksLikeKnowledgeObject(single)) return single;
            }

            var value = token as JValue;
            if (value != null && value.Type == JTokenType.String)
            {
                var text = Convert.ToString(value.Value);
                if (!string.IsNullOrWhiteSpace(text) && text.TrimStart().StartsWith("{", StringComparison.Ordinal))
                {
                    try { return UnwrapToken(JToken.Parse(text)); }
                    catch { return null; }
                }
            }
            return null;
        }

        private static bool LooksLikeKnowledgeObject(JObject obj)
        {
            if (obj == null) return false;
            return obj["answer"] != null || obj["title"] != null || obj["type"] != null
                || obj["intent"] != null || obj["subject"] != null || obj["predicate"] != null;
        }

        private static string StripCodeFence(string raw)
        {
            raw = (raw ?? string.Empty).Trim();
            if (!raw.StartsWith("```", StringComparison.Ordinal)) return raw;
            var firstNewLine = raw.IndexOf('\n');
            var lastFence = raw.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewLine < 0 || lastFence <= firstNewLine) return raw;
            return raw.Substring(firstNewLine + 1, lastFence - firstNewLine - 1).Trim();
        }

        private static bool TryExtractBalancedObject(string raw, out JObject obj, out Exception error)
        {
            obj = null;
            error = null;
            raw = raw ?? string.Empty;
            for (var start = 0; start < raw.Length; start++)
            {
                if (raw[start] != '{') continue;
                var depth = 0;
                var inString = false;
                var escaped = false;
                for (var i = start; i < raw.Length; i++)
                {
                    var ch = raw[i];
                    if (inString)
                    {
                        if (escaped) { escaped = false; continue; }
                        if (ch == '\\') { escaped = true; continue; }
                        if (ch == '"') inString = false;
                        continue;
                    }
                    if (ch == '"') { inString = true; continue; }
                    if (ch == '{') depth++;
                    else if (ch == '}')
                    {
                        depth--;
                        if (depth != 0) continue;
                        try
                        {
                            obj = JObject.Parse(raw.Substring(start, i - start + 1));
                            return true;
                        }
                        catch (Exception ex)
                        {
                            error = ex;
                            break;
                        }
                    }
                }
            }
            if (error == null) error = new InvalidOperationException("未找到 JSON 对象");
            return false;
        }

        private static void LogResult(string stage, bool success, string answer, string error)
        {
            Log.Info("KnowledgeV2 AI一句话新增模型返回: stage=" + stage
                + ", success=" + success
                + ", answerChars=" + SafeLength(answer)
                + ", shape=" + DescribeShape(answer)
                + (string.IsNullOrWhiteSpace(error) ? string.Empty : ", error=" + SafeText(error, 220)));
        }

        private static string DescribeShape(string value)
        {
            value = (value ?? string.Empty).Trim();
            if (value.Length == 0) return "empty";
            if (value.StartsWith("{", StringComparison.Ordinal)) return "object-like";
            if (value.StartsWith("[", StringComparison.Ordinal)) return "array-like";
            if (value.StartsWith("```", StringComparison.Ordinal)) return "markdown-fence";
            return "plain-text";
        }

        private static int SafeLength(string value) { return string.IsNullOrEmpty(value) ? 0 : value.Length; }
        private static string SafeError(Exception ex) { return ex == null ? "unknown" : SafeText(ex.Message, 220); }
        private static string SafeText(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            while (value.Contains("  ")) value = value.Replace("  ", " ");
            if (value.Length > max) value = value.Substring(0, max) + "...";
            return value;
        }

        private static string Text(JObject obj, string name) { var token = obj == null ? null : obj[name]; return token == null ? string.Empty : token.ToString().Trim(); }
        private static List<string> List(JObject obj, string name)
        {
            var array = obj == null ? null : obj[name] as JArray; if (array == null) return new List<string>();
            return array.Select(x => (x == null ? string.Empty : x.ToString()).Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(48).ToList();
        }
        private static double Number(JObject obj, string name, double fallback) { double value; return double.TryParse(Text(obj, name), out value) && !double.IsNaN(value) && !double.IsInfinity(value) ? Clamp(value) : fallback; }
        private static double Clamp(double value) { return double.IsNaN(value) || double.IsInfinity(value) ? 0 : Math.Max(0, Math.Min(1, value)); }
    }
}
