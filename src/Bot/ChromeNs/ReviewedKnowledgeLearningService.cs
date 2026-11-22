using Bot.Knowledge;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Bot.ChromeNs
{
    internal static class ReviewedKnowledgeLearningService
    {
        private static readonly object SaveLock = new object();

        public static KnowledgeLearningResult ApplyReviewedKnowledge(
            string question,
            string answer,
            string category,
            string keywords,
            string sourceType,
            double confidence,
            string evidenceType)
        {
            question = Clean(question, 400);
            answer = StripAiMarker(Clean(answer, 1200));
            category = Clean(category, 80);
            keywords = Clean(keywords, 500);
            sourceType = string.IsNullOrWhiteSpace(sourceType) ? "人工接待复盘" : sourceType.Trim();
            evidenceType = (evidenceType ?? string.Empty).Trim().ToLowerInvariant();

            if (double.IsNaN(confidence) || double.IsInfinity(confidence)
                || confidence < 0.0 || confidence > 1.0)
            {
                return new KnowledgeLearningResult
                {
                    Success = false,
                    Message = "置信度不是有效的0到1有限数值，未写入知识库"
                };
            }

            if (string.IsNullOrWhiteSpace(question) || string.IsNullOrWhiteSpace(answer))
            {
                return new KnowledgeLearningResult
                {
                    Success = false,
                    Message = "问题或答案为空，未写入知识库"
                };
            }
            if (answer.StartsWith("错误：", StringComparison.Ordinal)
                || answer.IndexOf("已跳过", StringComparison.Ordinal) >= 0)
            {
                return new KnowledgeLearningResult
                {
                    Success = false,
                    Message = "答案属于错误或跳过状态，未写入知识库"
                };
            }

            var strongCorrection = evidenceType == "manual_correction"
                || evidenceType == "withdrawn_bot_then_manual"
                || evidenceType == "repeated_human_pattern";
            var normalizedQuestion = KnowledgeAiService.NormalizeQuestion(question);
            var normalizedAnswer = Normalize(answer);
            if (string.IsNullOrWhiteSpace(normalizedQuestion) || string.IsNullOrWhiteSpace(normalizedAnswer))
            {
                return new KnowledgeLearningResult
                {
                    Success = false,
                    Message = "问题或答案规范化后为空，未写入知识库"
                };
            }

            lock (SaveLock)
            {
                var list = BotFeatureStore.GetKnowledgeBase();
                var existing = FindExisting(list, question);
                var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                if (existing != null)
                {
                    var previousAnswer = existing.Answer ?? string.Empty;
                    KnowledgePolicyProfileService.RecordReviewEvidence(
                        question,
                        previousAnswer,
                        answer,
                        evidenceType);
                    ReplyQualityMetricsService.RecordHumanEvidence(evidenceType);

                    if (Normalize(previousAnswer) == normalizedAnswer)
                    {
                        MergeMetadata(existing, category, keywords, sourceType, now);
                        BotFeatureStore.SaveKnowledgeBase(list);
                        RaiseChanged();
                        return new KnowledgeLearningResult
                        {
                            Success = true,
                            Message = evidenceType == "conversation_synthesis"
                                ? "现有知识与本轮会话AI复盘结论一致，已完成去重并刷新知识元数据"
                                : "现有知识答案与人工复盘结论一致，已提高该知识的人工确认可靠度"
                        };
                    }

                    var existingSource = (existing.SourceType ?? string.Empty).Trim();
                    var existingLooksCurated = !existing.AiGenerated
                        && !existingSource.Contains("自动学习")
                        && !existingSource.Contains("智能导入")
                        && !existingSource.Contains("历史扫描")
                        && !existingSource.Contains("AI生成")
                        && !existingSource.Contains("人工接待复盘")
                        && !existingSource.Contains("会话AI复盘");

                    if (existingLooksCurated && (!strongCorrection || confidence < 0.95))
                    {
                        return new KnowledgeLearningResult
                        {
                            Success = true,
                            Message = "现有知识属于人工维护内容，当前证据强度不足，未自动覆盖；可靠度证据已记录"
                        };
                    }

                    existing.Answer = answer;
                    existing.Category = string.IsNullOrWhiteSpace(category)
                        ? (string.IsNullOrWhiteSpace(existing.Category) ? "自动学习" : existing.Category)
                        : category;
                    existing.Keywords = string.IsNullOrWhiteSpace(keywords) ? existing.Keywords : keywords;
                    existing.UpdatedAt = now;
                    existing.SourceType = sourceType;
                    existing.AiGenerated = false;
                    BotFeatureStore.SaveKnowledgeBase(list);
                    RaiseChanged();
                    return new KnowledgeLearningResult
                    {
                        Success = true,
                        Updated = true,
                        Message = evidenceType == "conversation_synthesis"
                            ? "已根据本轮完整会话复盘优化相似知识答案"
                            : "已根据本轮人工接待最终回复优化现有知识答案，并降低旧答案直答可靠度"
                    };
                }

                var contentHash = KnowledgeAiService.ContentHash(question, answer);
                if (list.Any(x => x != null
                    && KnowledgeAiService.ContentHash(x.Title, x.Answer) == contentHash))
                {
                    KnowledgePolicyProfileService.RecordKnowledgeAccepted(question, answer);
                    ReplyQualityMetricsService.RecordHumanEvidence(evidenceType == "conversation_synthesis" ? "conversation_synthesis" : "human_confirmed");
                    return new KnowledgeLearningResult
                    {
                        Success = true,
                        Message = "知识库已存在相同问答内容，已去重，不重复新增"
                    };
                }

                var added = new KnowledgeBaseEntry
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Enabled = true,
                    Category = string.IsNullOrWhiteSpace(category) ? "自动学习" : category,
                    Title = question,
                    Answer = answer,
                    Keywords = keywords ?? string.Empty,
                    CreatedAt = now,
                    UpdatedAt = now,
                    AiGenerated = false,
                    SourceType = sourceType
                };
                list.Add(added);
                BotFeatureStore.SaveKnowledgeBase(list);
                KnowledgePolicyProfileService.RecordKnowledgeAccepted(question, answer);
                ReplyQualityMetricsService.RecordHumanEvidence(evidenceType == "conversation_synthesis" ? "conversation_synthesis" : "human_confirmed");
                RaiseChanged();
                return new KnowledgeLearningResult
                {
                    Success = true,
                    Added = true,
                    Message = evidenceType == "conversation_synthesis"
                        ? "已根据本轮完整会话AI复盘加入新的可复用知识"
                        : "已根据本轮人工接待复盘加入新的可复用知识，并建立初始可靠度"
                };
            }
        }

        private static KnowledgeBaseEntry FindExisting(
            List<KnowledgeBaseEntry> list,
            string question)
        {
            if (list == null || list.Count == 0) return null;
            var normalizedQuestion = KnowledgeAiService.NormalizeQuestion(question);
            var exact = list.FirstOrDefault(x => x != null
                && KnowledgeAiService.NormalizeQuestion(x.Title) == normalizedQuestion);
            if (exact != null) return exact;

            KnowledgeBaseEntry best = null;
            var bestScore = 0.0;
            foreach (var item in list.Where(x => x != null && !string.IsNullOrWhiteSpace(x.Title)))
            {
                var score = Similarity(normalizedQuestion, KnowledgeAiService.NormalizeQuestion(item.Title));
                if (score > bestScore)
                {
                    bestScore = score;
                    best = item;
                }
            }
            return bestScore >= 0.92 ? best : null;
        }

        private static double Similarity(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return 0;
            if (a == b) return 1;
            if (Math.Min(a.Length, b.Length) >= 4 && (a.Contains(b) || b.Contains(a))) return 0.96;
            var aa = Bigrams(a);
            var bb = Bigrams(b);
            if (aa.Count == 0 || bb.Count == 0) return 0;
            var common = aa.Intersect(bb).Count();
            return (2.0 * common) / (aa.Count + bb.Count);
        }

        private static HashSet<string> Bigrams(string value)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i + 1 < (value ?? string.Empty).Length; i++)
            {
                result.Add(value.Substring(i, 2));
            }
            return result;
        }

        private static void MergeMetadata(
            KnowledgeBaseEntry entry,
            string category,
            string keywords,
            string sourceType,
            string now)
        {
            if (!string.IsNullOrWhiteSpace(category)) entry.Category = category;
            if (!string.IsNullOrWhiteSpace(keywords)) entry.Keywords = keywords;
            entry.UpdatedAt = now;
            entry.SourceType = sourceType;
            entry.AiGenerated = false;
        }

        private static void RaiseChanged()
        {
            // QueueLearn/SaveLearned raises this event from KnowledgeLearningService.
            // Reviewed knowledge is immediately visible because BotFeatureStore is re-read by routing.
        }

        private static string StripAiMarker(string value)
        {
            value = (value ?? string.Empty).Trim();
            while (value.EndsWith("[AI]", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(0, value.Length - 4).TrimEnd();
            }
            return value;
        }

        private static string Normalize(string value)
        {
            return Regex.Replace(
                StripAiMarker(value).Trim().ToLowerInvariant(),
                @"\s+",
                string.Empty);
        }

        private static string Clean(string value, int max)
        {
            value = (value ?? string.Empty).Trim();
            if (max > 0 && value.Length > max) value = value.Substring(0, max).Trim();
            return value;
        }
    }
}
