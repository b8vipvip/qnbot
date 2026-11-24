using Bot.ShopScope;
using BotLib;
using BotLib.Db.Sqlite;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Bot.ChromeNs
{
    internal sealed class VisualUnderstandingRuleEntity
    {
        [PrimaryKey] public string EntityId { get; set; }
        public string ShopKey { get; set; }
        public string Seller { get; set; }
        public string Name { get; set; }
        public string AppName { get; set; }
        public string ScreenName { get; set; }
        public string RequiredKeywords { get; set; }
        public string OptionalKeywords { get; set; }
        public string ExcludedKeywords { get; set; }
        public string FieldPatterns { get; set; }
        public string Description { get; set; }
        public bool Enabled { get; set; }
        public long CreatedAtTicks { get; set; }
        public long UpdatedAtTicks { get; set; }
    }

    internal sealed class VisualUnderstandingMatch
    {
        public string RuleId { get; set; }
        public string RuleName { get; set; }
        public string AppName { get; set; }
        public string ScreenName { get; set; }
        public string Description { get; set; }
        public double Score { get; set; }
        public Dictionary<string, string> ExtractedFields { get; set; }
        public string ContextText { get; set; }
    }

    internal static class VisualUnderstandingRuleService
    {
        private static readonly object Sync = new object();
        private static bool _ready;

        public static List<VisualUnderstandingRuleEntity> Load(string shopKey, string seller)
        {
            EnsureScope(shopKey, seller);
            EnsureSchema();
            EnsureDefaultKugouPreset(shopKey, seller);
            lock (Sync)
                return (DbHelper.Db.Select(typeof(VisualUnderstandingRuleEntity),
                    "where ShopKey = ? and Seller = ? order by UpdatedAtTicks desc", shopKey, seller)
                    ?? new List<object>()).OfType<VisualUnderstandingRuleEntity>().ToList();
        }

        public static void Save(string shopKey, string seller, VisualUnderstandingRuleEntity rule)
        {
            EnsureScope(shopKey, seller);
            if (rule == null) throw new ArgumentNullException("rule");
            rule.EntityId = string.IsNullOrWhiteSpace(rule.EntityId) ? Guid.NewGuid().ToString("N") : rule.EntityId.Trim();
            rule.ShopKey = shopKey;
            rule.Seller = seller;
            rule.Name = Clean(rule.Name, 120);
            rule.AppName = Clean(rule.AppName, 120);
            rule.ScreenName = Clean(rule.ScreenName, 120);
            rule.RequiredKeywords = Clean(rule.RequiredKeywords, 2000);
            rule.OptionalKeywords = Clean(rule.OptionalKeywords, 2000);
            rule.ExcludedKeywords = Clean(rule.ExcludedKeywords, 1000);
            rule.FieldPatterns = Clean(rule.FieldPatterns, 6000);
            rule.Description = Clean(rule.Description, 1200);
            if (rule.Name.Length == 0 || rule.RequiredKeywords.Length == 0)
                throw new InvalidOperationException("规则名称和必需 OCR 关键词不能为空。");
            ValidatePatterns(rule.FieldPatterns);
            EnsureSchema();
            lock (Sync)
            {
                var old = (DbHelper.Db.Select(typeof(VisualUnderstandingRuleEntity),
                    "where EntityId = ? and ShopKey = ? and Seller = ? limit 1",
                    rule.EntityId, shopKey, seller) ?? new List<object>())
                    .OfType<VisualUnderstandingRuleEntity>().FirstOrDefault();
                var now = DateTime.Now.Ticks;
                if (old == null) rule.CreatedAtTicks = now;
                else rule.CreatedAtTicks = old.CreatedAtTicks;
                rule.UpdatedAtTicks = now;
                DbHelper.Db.SaveRecordsInTransaction(new List<object> { rule });
            }
        }

        public static bool Delete(string shopKey, string seller, string id)
        {
            EnsureScope(shopKey, seller);
            EnsureSchema();
            lock (Sync)
            {
                var rows = (DbHelper.Db.Select(typeof(VisualUnderstandingRuleEntity),
                    "where EntityId = ? and ShopKey = ? and Seller = ? limit 1",
                    (id ?? string.Empty).Trim(), shopKey, seller) ?? new List<object>())
                    .OfType<VisualUnderstandingRuleEntity>().ToList();
                if (rows.Count == 0) return false;
                // Keep the built-in preset as a disabled row so it is not silently
                // recreated on the next page refresh. Users can re-enable it explicitly.
                rows[0].Enabled = false;
                rows[0].UpdatedAtTicks = DateTime.Now.Ticks;
                DbHelper.Db.SaveRecordsInTransaction(new List<object> { rows[0] });
                return true;
            }
        }

        public static bool TryMatch(string shopKey, string seller, string ocrText, out VisualUnderstandingMatch match)
        {
            match = null;
            EnsureScope(shopKey, seller);
            ocrText = (ocrText ?? string.Empty).Trim();
            if (ocrText.Length < 4) return false;
            var rules = Load(shopKey, seller).Where(x => x.Enabled).ToList();
            VisualUnderstandingRuleEntity best = null;
            double bestScore = 0;
            foreach (var rule in rules)
            {
                var required = Split(rule.RequiredKeywords);
                if (required.Count == 0 || required.Any(x => ocrText.IndexOf(x, StringComparison.OrdinalIgnoreCase) < 0))
                    continue;
                var excluded = Split(rule.ExcludedKeywords);
                if (excluded.Any(x => ocrText.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0))
                    continue;
                var optional = Split(rule.OptionalKeywords);
                var score = required.Count + optional.Count(x => ocrText.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0) * 0.5;
                if (score > bestScore) { best = rule; bestScore = score; }
            }
            if (best == null) return false;

            var fields = Extract(best.FieldPatterns, ocrText);
            var context = BuildContext(best, fields);
            match = new VisualUnderstandingMatch
            {
                RuleId = best.EntityId,
                RuleName = best.Name,
                AppName = best.AppName,
                ScreenName = best.ScreenName,
                Description = best.Description,
                Score = bestScore,
                ExtractedFields = fields,
                ContextText = context
            };
            Log.Info("OCR 命中视觉理解预设: shopRef=" + MyWebSocketServer.DiagnosticRef("shop", shopKey)
                + ", ruleId=" + best.EntityId + ", fields=" + fields.Count);
            return true;
        }

        private static Dictionary<string, string> Extract(string patterns, string text)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in (patterns ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var split = line.IndexOf('|');
                if (split <= 0 || split >= line.Length - 1) continue;
                var name = line.Substring(0, split).Trim();
                var pattern = line.Substring(split + 1).Trim();
                try
                {
                    var m = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline, TimeSpan.FromMilliseconds(80));
                    if (!m.Success) continue;
                    var value = m.Groups.Count > 1 ? m.Groups[1].Value.Trim() : m.Value.Trim();
                    if (value.Length > 0) result[name] = value.Length > 240 ? value.Substring(0, 240) : value;
                }
                catch (RegexMatchTimeoutException) { }
            }
            return result;
        }

        private static string BuildContext(VisualUnderstandingRuleEntity rule, Dictionary<string, string> fields)
        {
            var lines = new List<string>
            {
                "【视觉理解命中】" + rule.AppName + " / " + rule.ScreenName,
                "页面判定：" + rule.Description
            };
            foreach (var field in fields) lines.Add(field.Key + "：" + field.Value);
            lines.Add("以上为本张图片 OCR 直接提取的事实；回答时结合当前买家问题和同一店铺知识库，不得把图片事实误当成买家提问。");
            return string.Join("\n", lines.Where(x => !string.IsNullOrWhiteSpace(x)));
        }

        private static void ValidatePatterns(string patterns)
        {
            foreach (var line in (patterns ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var split = line.IndexOf('|');
                if (split <= 0 || split >= line.Length - 1) throw new InvalidOperationException("字段提取规则格式应为：字段名|正则表达式");
                try { new Regex(line.Substring(split + 1).Trim(), RegexOptions.IgnoreCase | RegexOptions.Multiline, TimeSpan.FromMilliseconds(80)); }
                catch (ArgumentException ex) { throw new InvalidOperationException("字段提取正则表达式无效：" + ex.Message); }
            }
        }

        private static List<string> Split(string value)
        {
            return (value ?? string.Empty).Split(new[] { '\r', '\n', '|', ';', '；' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void EnsureScope(string shopKey, string seller)
        {
            shopKey = (shopKey ?? string.Empty).Trim();
            seller = (seller ?? string.Empty).Trim();
            if (shopKey.Length == 0 || seller.Length == 0) throw new InvalidOperationException("缺少店铺身份，拒绝访问视觉理解规则。");
            var shop = ShopContextLocator.ResolveRuntimeByShopKey(shopKey);
            if (shop == null || !string.Equals(shop.SellerNick, seller, StringComparison.Ordinal))
                throw new InvalidOperationException("视觉理解规则的 ShopKey 与店铺身份不匹配。");
        }

        private static void EnsureDefaultKugouPreset(string shopKey, string seller)
        {
            lock (Sync)
            {
                var exists = (DbHelper.Db.Select(typeof(VisualUnderstandingRuleEntity),
                    "where ShopKey = ? and Seller = ? and Name = ? limit 1",
                    shopKey, seller, "手机端酷狗账号中心") ?? new List<object>())
                    .OfType<VisualUnderstandingRuleEntity>().FirstOrDefault();
                if (exists != null) return;
                var now = DateTime.Now.Ticks;
                var preset = new VisualUnderstandingRuleEntity
                {
                    EntityId = Guid.NewGuid().ToString("N"),
                    ShopKey = shopKey,
                    Seller = seller,
                    Name = "手机端酷狗账号中心",
                    AppName = "酷狗音乐",
                    ScreenName = "账号中心",
                    RequiredKeywords = string.Join(Environment.NewLine, new[] { "帐号中心", "酷狗ID", "编辑资料", "帐号与绑定", "我的会员" }),
                    OptionalKeywords = string.Join(Environment.NewLine, new[] { "主页隐私设置", "我的酷币", "已购音乐", "超级VIP" }),
                    ExcludedKeywords = string.Join(Environment.NewLine, new[] { "登录页", "验证码" }),
                    FieldPatterns = @"酷狗ID|酷狗ID\s*[:：]?\s*([A-Za-z0-9_-]{5,})" + Environment.NewLine
                        + @"昵称|帐号中心\s*[\r\n]+([^\r\n]+)\s*[\r\n]+酷狗ID" + Environment.NewLine
                        + @"会员状态|我的会员\s*[\r\n]+(超级VIP)" + Environment.NewLine
                        + @"会员到期|超级VIP\s*(\d{4}-\d{2}-\d{2}到期)",
                    Description = "识别手机端酷狗音乐账号中心页面；提取酷狗ID、昵称、会员等级和到期日期。账号ID、昵称等字段按 OCR 原文保留，不做通用长数字脱敏。",
                    Enabled = true,
                    CreatedAtTicks = now,
                    UpdatedAtTicks = now
                };
                DbHelper.Db.SaveRecordsInTransaction(new List<object> { preset });
            }
        }

        private static void EnsureSchema()
        {
            if (_ready) return;
            lock (Sync)
            {
                if (_ready) return;
                DbHelper.Db.Execute("create table if not exists VisualUnderstandingRuleEntity (EntityId text primary key not null,ShopKey text not null,Seller text not null,Name text,AppName text,ScreenName text,RequiredKeywords text,OptionalKeywords text,ExcludedKeywords text,FieldPatterns text,Description text,Enabled integer not null default 1,CreatedAtTicks integer not null default 0,UpdatedAtTicks integer not null default 0)");
                DbHelper.Db.Execute("create index if not exists IX_VisualUnderstandingRule_ShopSeller on VisualUnderstandingRuleEntity(ShopKey,Seller,Enabled,UpdatedAtTicks)");
                _ready = true;
            }
        }

        private static string Clean(string value, int max)
        {
            value = (value ?? string.Empty).Trim();
            return value.Length <= max ? value : value.Substring(0, max);
        }
    }
}