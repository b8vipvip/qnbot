using Bot.ShopScope;
using BotLib;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Bot.ChromeNs
{
    internal sealed class ReceptionWorkflowDecision
    {
        public string ShopKey { get; set; }
        public string Seller { get; set; }
        public string Buyer { get; set; }
        public string FlowId { get; set; }
        public int FlowVersion { get; set; }
        public long CatalogRevision { get; set; }
        public string State { get; set; }
        public string NextState { get; set; }
        public string Action { get; set; }
        public string ReplyText { get; set; }
        public string SourceKey { get; set; }
        public string OrderFingerprint { get; set; }
        public bool NextStateTerminal { get; set; }
        public bool SuppressReply { get; set; }
        public bool RequiresSend { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>
    /// ShopKey-scoped runtime for autonomous Reception Agent workflows.
    /// It never sends directly: reply decisions are returned to the existing canonical
    /// text/vision pipelines, which retain generation, takeover, relevance, dedup and send gates.
    /// Runtime state and anomaly counters are durable so restart does not reset buyer progress.
    /// </summary>
    internal static class ReceptionWorkflowRuntimeService
    {
        private const string RuntimeSchema = "qnbot.reception-workflow-runtime.v1";
        private const int MaxRuntimeRecords = 5000;
        private const int MaxFaultRecords = 500;
        private const int AutoRollbackFailureThreshold = 3;
        private static readonly TimeSpan FaultWindow = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan PendingDeliveryUncertainAfter = TimeSpan.FromMinutes(2);
        private static readonly ConcurrentDictionary<string, CatalogIndex> CatalogCache =
            new ConcurrentDictionary<string, CatalogIndex>(StringComparer.Ordinal);

        private sealed class CatalogIndex
        {
            public long Revision;
            public List<JObject> Flows;
        }

        private static string Digest(string value)
        {
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty)))
                    .Replace("-", string.Empty).ToLowerInvariant();
        }

        private static string FingerprintOrders(IEnumerable<OrderSnapshot> orders)
        {
            var ids = (orders ?? Enumerable.Empty<OrderSnapshot>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.OrderId))
                .Select(x => x.OrderId + "|" + x.ItemId + "|" + x.SkuId + "|"
                    + x.Quantity.ToString(CultureInfo.InvariantCulture) + "|" + x.TradeStatus
                    + "|" + (x.IsPaid.HasValue ? x.IsPaid.Value.ToString() : "unknown"))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal);
            return Digest(string.Join("|", ids));
        }

        private static string RuntimePath(ShopContext shop)
        {
            if (shop == null || string.IsNullOrWhiteSpace(shop.ShopKey) || !shop.HasStableSellerId)
                throw new InvalidOperationException("Reception Agent runtime requires a stable ShopKey");
            using (ShopSettingsScope.Enter(shop))
            {
                var directory = Path.GetDirectoryName(BusinessPolicyProfileService.GetUserPath());
                if (string.IsNullOrWhiteSpace(directory))
                    throw new InvalidOperationException("Reception Agent runtime directory is unavailable");
                return Path.Combine(directory, "reception-workflow-runtime.json");
            }
        }

        private static ShopContext ResolveShop(string seller, string shopKey)
        {
            seller = (seller ?? string.Empty).Trim();
            shopKey = (shopKey ?? string.Empty).Trim();
            if (shopKey.Length == 0) throw new InvalidOperationException("ShopKey is required");
            var shop = ShopContextLocator.ResolveRuntimeByShopKey(shopKey);
            if (shop == null || !string.Equals(shop.ShopKey, shopKey, StringComparison.Ordinal)
                || !shop.HasStableSellerId
                || (seller.Length > 0 && !string.Equals(shop.SellerNick, seller, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Seller/ShopKey mismatch");
            return shop;
        }

        private static JObject EmptyDocument()
        {
            return new JObject
            {
                ["schema"] = RuntimeSchema,
                ["records"] = new JArray(),
                ["faults"] = new JArray(),
                ["updated_at_utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
            };
        }

        private static JObject ReadDocument(string path)
        {
            string error;
            var raw = CrossProcessAtomicStateFile.ReadAllTextShared(path, 3, 30, out error);
            if (!string.IsNullOrWhiteSpace(error)) throw new IOException(error);
            if (string.IsNullOrWhiteSpace(raw)) return EmptyDocument();
            var document = JObject.Parse(raw);
            if (!string.Equals(Convert.ToString(document["schema"]), RuntimeSchema, StringComparison.Ordinal)
                || !(document["records"] is JArray) || !(document["faults"] is JArray))
                throw new InvalidDataException("Reception Agent runtime state schema is invalid");
            return document;
        }

        private static void WriteDocument(string path, JObject document)
        {
            document["updated_at_utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            var records = document["records"] as JArray ?? new JArray();
            var faults = document["faults"] as JArray ?? new JArray();
            var cutoff = DateTime.UtcNow.AddDays(-30).Ticks;
            document["records"] = new JArray(records.OfType<JObject>()
                .Where(x => (x.Value<long?>("updated_utc_ticks") ?? 0) >= cutoff)
                .OrderByDescending(x => x.Value<long?>("updated_utc_ticks") ?? 0)
                .Take(MaxRuntimeRecords));
            document["faults"] = new JArray(faults.OfType<JObject>()
                .Where(x => (x.Value<long?>("last_failure_utc_ticks") ?? 0) >= cutoff)
                .OrderByDescending(x => x.Value<long?>("last_failure_utc_ticks") ?? 0)
                .Take(MaxFaultRecords));
            string error;
            if (!CrossProcessAtomicStateFile.WriteAllTextAtomic(
                path, document.ToString(), 3, 50, out error))
                throw new IOException("Reception Agent runtime state write failed: " + error);
        }

        private static T Mutate<T>(ShopContext shop, Func<JObject, T> action)
        {
            var path = RuntimePath(shop);
            var directory = Path.GetDirectoryName(path);
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
            using (var lease = CrossProcessAtomicStateFile.Acquire(path, "ReceptionWorkflowRuntime", 3000))
            {
                if (lease == null || !lease.Acquired)
                    throw new IOException("Reception Agent runtime lock unavailable");
                var document = ReadDocument(path);
                var result = action(document);
                WriteDocument(path, document);
                return result;
            }
        }

        private static CatalogIndex LoadCatalog(string seller, string shopKey)
        {
            var raw = ReceptionWorkflowCatalogService.GetActiveJson(seller, shopKey);
            var document = JObject.Parse(raw);
            var revision = document.Value<long>("catalog_revision");
            CatalogIndex cached;
            if (CatalogCache.TryGetValue(shopKey, out cached) && cached != null && cached.Revision == revision)
                return cached;

            var flows = (document["flows"] as JArray ?? new JArray())
                .OfType<JObject>().Select(x => (JObject)x.DeepClone()).ToList();
            var updated = new CatalogIndex { Revision = revision, Flows = flows };
            CatalogCache.AddOrUpdate(shopKey, updated, (_, __) => updated);
            return updated;
        }

        internal static void InvalidateShop(string shopKey)
        {
            shopKey = (shopKey ?? string.Empty).Trim();
            if (shopKey.Length == 0) return;
            CatalogIndex ignored;
            CatalogCache.TryRemove(shopKey, out ignored);
        }

        internal static ReceptionWorkflowDecision EvaluateText(BuyerMessageBurst burst)
        {
            if (burst == null) return null;
            var messageKey = string.Join("|", burst.Items
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.MessageKey))
                .Select(x => x.MessageKey).Distinct(StringComparer.Ordinal));
            return Evaluate(
                burst.SellerNick, burst.BuyerNick, burst.ShopKey, burst.CombinedQuestion,
                messageKey, false, string.Empty);
        }

        internal static ReceptionWorkflowDecision EvaluateVision(
            BuyerMessageBurst burst, string messageKey, string visualSummary, bool withdrawn)
        {
            if (burst == null) return null;
            return Evaluate(
                burst.SellerNick, burst.BuyerNick, burst.ShopKey, burst.CombinedQuestion,
                messageKey, !withdrawn, withdrawn ? string.Empty : visualSummary);
        }

        private static bool ContainsAny(string text, IEnumerable<string> keywords)
        {
            text = (text ?? string.Empty).Trim();
            var values = (keywords ?? Enumerable.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            return values.Count == 0 || values.Any(x =>
                text.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool HasHumanCorrectionEvidence(JObject flow)
        {
            var source = flow == null ? null : flow["source_evidence"] as JArray;
            return source != null && source.OfType<JObject>().Any(x =>
                string.Equals(Convert.ToString(x["kind"]), "manual_correction", StringComparison.Ordinal));
        }

        private static bool HasRequiredEvidence(
            JArray required,
            bool hasBuyerMessage,
            bool hasImage,
            bool hasVision,
            bool hasOrder,
            bool hasManualCorrection)
        {
            foreach (var token in required ?? new JArray())
            {
                var kind = Convert.ToString(token);
                var present = kind == "buyer_message" ? hasBuyerMessage
                    : kind == "image_message" ? hasImage
                    : kind == "vision_observation" ? hasVision
                    : kind == "order_snapshot" ? hasOrder
                    : kind == "manual_correction" ? hasManualCorrection
                    : false;
                if (!present) return false;
            }
            return true;
        }

        private static string InitialState(JObject flow)
        {
            var configured = Convert.ToString(flow["initial_state"]);
            if (!string.IsNullOrWhiteSpace(configured)) return configured;
            var steps = flow["steps"] as JArray;
            var first = steps == null ? null : steps.OfType<JObject>().FirstOrDefault();
            return first == null ? string.Empty : Convert.ToString(first["state"]);
        }

        private static JObject FindRecord(JArray records, string buyerKey, string flowId)
        {
            return (records ?? new JArray()).OfType<JObject>().FirstOrDefault(x =>
                string.Equals(Convert.ToString(x["buyer_key"]), buyerKey, StringComparison.Ordinal)
                && string.Equals(Convert.ToString(x["flow_id"]), flowId, StringComparison.Ordinal));
        }

        private static bool QueueHandoff(
            ShopContext shop, string seller, string buyer, string question, string shopKey, string flowId)
        {
            if (shop == null) return false;
            try
            {
                using (ShopSettingsScope.Enter(shop))
                    return HandoffNotificationService.QueueReceptionWorkflowHandoff(
                        seller, buyer, question, shopKey, flowId);
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("Reception Agent转人工通知入队失败：" + ex.Message, 5);
                return false;
            }
        }

        private static ReceptionWorkflowDecision Suppressed(
            string seller, string buyer, string shopKey, string flowId, int flowVersion,
            long revision, string state, string nextState, string action, string sourceKey, string reason)
        {
            return new ReceptionWorkflowDecision
            {
                Seller = seller, Buyer = buyer, ShopKey = shopKey, FlowId = flowId,
                FlowVersion = flowVersion, CatalogRevision = revision,
                State = state, NextState = nextState, Action = action,
                SourceKey = sourceKey, SuppressReply = true, RequiresSend = false, Reason = reason
            };
        }

        private static ReceptionWorkflowDecision Evaluate(
            string seller, string buyer, string shopKey, string question,
            string messageKey, bool isImage, string visualSummary)
        {
            seller = (seller ?? string.Empty).Trim();
            buyer = (buyer ?? string.Empty).Trim();
            shopKey = (shopKey ?? string.Empty).Trim();
            question = (question ?? string.Empty).Trim();
            if (seller.Length == 0 || buyer.Length == 0 || shopKey.Length == 0) return null;

            ShopContext shop;
            CatalogIndex catalog;
            List<OrderSnapshot> orders;
            KugouReceptionConfig config;
            try
            {
                shop = ResolveShop(seller, shopKey);
                using (ShopSettingsScope.Enter(shop))
                {
                    config = KugouReceptionConfigService.GetConfig(shop);
                    if (config == null || !config.Enabled || config.ProductIds == null || config.ProductIds.Count == 0)
                        return null;
                    if (isImage && !config.EnableAutoGuidance) return null;
                    catalog = LoadCatalog(seller, shopKey);
                    orders = OrderGuidanceDeliveryGuard.GetRecentOrderSnapshots(seller, buyer, 12, shopKey)
                        .Where(x => x != null
                            && string.Equals(x.ShopKey, shopKey, StringComparison.Ordinal)
                            && string.Equals(x.Seller, seller, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(x.Buyer, buyer, StringComparison.OrdinalIgnoreCase)
                            && !string.IsNullOrWhiteSpace(x.OrderId)
                            && x.EventTime >= DateTime.Now.AddDays(-7))
                        .GroupBy(x => x.OrderId, StringComparer.Ordinal)
                        .Select(g => g.OrderByDescending(x => x.EventTime).First())
                        .Where(x => (x.EventType == OrderEventType.Created || x.EventType == OrderEventType.Paid)
                            && !System.Text.RegularExpressions.Regex.IsMatch(
                                x.TradeStatus ?? string.Empty, @"关闭|退款|CLOSED|REFUND|CANCEL",
                                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("Reception Agent运行时无法读取ShopKey目录或订单证据，已交回原回复管线：" + ex.Message, 10);
                return null;
            }
            if (catalog == null || catalog.Flows == null || catalog.Flows.Count == 0 || orders.Count == 0)
                return null;

            var sourceMaterial = string.IsNullOrWhiteSpace(messageKey)
                ? DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture)
                : messageKey;
            var sourceKey = Digest(shopKey + "|" + sourceMaterial
                + "|" + Digest(question) + "|" + (isImage ? "image" : "text"));
            var buyerKey = Digest(shopKey + "|" + seller + "|" + buyer);
            var matchingFlows = catalog.Flows.Where(flow =>
            {
                var products = (flow["product_ids"] as JArray ?? new JArray()).Values<string>().ToList();
                var skus = (flow["sku_ids"] as JArray ?? new JArray()).Values<string>().ToList();
                return orders.Any(x =>
                    (products.Count == 0 || products.Contains(x.ItemId, StringComparer.Ordinal))
                    && (skus.Count == 0 || skus.Contains(x.SkuId, StringComparer.Ordinal))
                    && config.ProductIds.Contains(x.ItemId, StringComparer.Ordinal));
            }).ToList();
            if (matchingFlows.Count == 0) return null;

            // Read the visual ledger before taking the cross-process state lock. SQLite I/O
            // must not extend the buyer/flow transaction lock or stall other shop conversations.
            List<VisualKnowledgeObservationEntity> recentVisual;
            try { recentVisual = RecentVisualContextService.GetRecentWorkflowEvidence(shopKey, seller, buyer, 8); }
            catch (Exception ex)
            {
                recentVisual = new List<VisualKnowledgeObservationEntity>();
                Log.ErrorWithMaxCount("Reception Agent运行时读取视觉证据失败，本轮按缺少图片证据处理：" + ex.Message, 5);
            }

            try
            {
                var decision = Mutate(shop, document =>
                {
                    var records = document["records"] as JArray;
                    var progressedFlows = new List<JObject>();
                    var triggeredFlows = new List<JObject>();
                    foreach (var candidate in matchingFlows)
                    {
                        var candidateId = Convert.ToString(candidate["flow_id"]);
                        var candidateSteps = candidate["steps"] as JArray ?? new JArray();
                        var candidateInitial = InitialState(candidate);
                        if (candidateSteps.Count == 0 || string.IsNullOrWhiteSpace(candidateInitial)) continue;
                        var candidateRecord = FindRecord(records, buyerKey, candidateId);
                        var candidateState = candidateRecord == null
                            ? candidateInitial : Convert.ToString(candidateRecord["current_state"]);
                        var candidateStep = candidateSteps.OfType<JObject>().FirstOrDefault(x =>
                            string.Equals(Convert.ToString(x["state"]), candidateState, StringComparison.Ordinal));
                        var terminalOrderChanged = false;
                        if (candidateStep != null && candidateStep.Value<bool?>("terminal") == true)
                        {
                            var candidateProducts = (candidate["product_ids"] as JArray ?? new JArray()).Values<string>().ToList();
                            var candidateSkus = (candidate["sku_ids"] as JArray ?? new JArray()).Values<string>().ToList();
                            var candidateOrders = orders.Where(x =>
                                (candidateProducts.Count == 0 || candidateProducts.Contains(x.ItemId, StringComparer.Ordinal))
                                && (candidateSkus.Count == 0 || candidateSkus.Contains(x.SkuId, StringComparer.Ordinal))
                                && config.ProductIds.Contains(x.ItemId, StringComparer.Ordinal));
                            var currentFingerprint = FingerprintOrders(candidateOrders);
                            var completedAt = candidateRecord == null
                                ? 0 : candidateRecord.Value<long?>("completed_utc_ticks") ?? 0;
                            var completedFingerprint = candidateRecord == null
                                ? string.Empty : Convert.ToString(candidateRecord["completed_order_fingerprint"]);
                            terminalOrderChanged = !string.Equals(
                                completedFingerprint, currentFingerprint, StringComparison.Ordinal);
                            var canReenter = candidateRecord == null || completedAt <= 0
                                || DateTime.UtcNow.Ticks - completedAt >= TimeSpan.FromHours(24).Ticks
                                || terminalOrderChanged;
                            if (!canReenter) continue;
                            candidateState = candidateInitial;
                            candidateStep = candidateSteps.OfType<JObject>().FirstOrDefault(x =>
                                string.Equals(Convert.ToString(x["state"]), candidateInitial, StringComparison.Ordinal));
                        }
                        if (candidateRecord != null
                            && (!string.Equals(candidateState, candidateInitial, StringComparison.Ordinal)
                                || !string.IsNullOrWhiteSpace(Convert.ToString(candidateRecord["pending_source_key"]))
                                || candidateRecord.Value<bool?>("delivery_uncertain") == true))
                        {
                            progressedFlows.Add(candidate);
                            continue;
                        }
                        var candidateKeywords = (candidateStep == null
                                ? new JArray()
                                : candidateStep["trigger_keywords"] as JArray ?? new JArray())
                            .Values<string>().ToList();
                        if (candidateKeywords.Count == 0)
                            candidateKeywords = (candidate["trigger_keywords"] as JArray ?? new JArray())
                                .Values<string>().ToList();
                        var candidateRequired = candidateStep == null
                            ? new JArray() : candidateStep["required_evidence"] as JArray ?? new JArray();
                        var candidateTimeout = Math.Max(1, Math.Min(10080,
                            candidateStep == null ? 60 : candidateStep.Value<int?>("timeout_minutes") ?? 60));
                        var hasRecentImage = recentVisual != null && recentVisual.Any(x =>
                            x != null && x.ObservedAtTicks >= DateTime.Now.AddMinutes(-candidateTimeout).Ticks);
                        var imageTrigger = (isImage || hasRecentImage)
                            && candidateRequired.Any(x => Convert.ToString(x) == "image_message");
                        if ((candidateKeywords.Count > 0 && ContainsAny(question, candidateKeywords))
                            || imageTrigger || terminalOrderChanged)
                            triggeredFlows.Add(candidate);
                    }

                    List<JObject> selectedFlows;
                    if (progressedFlows.Count == 1)
                        selectedFlows = progressedFlows;
                    else if (progressedFlows.Count > 1)
                    {
                        var queued = false;
                        try
                        {
                            using (ShopSettingsScope.Enter(shop))
                                queued = HandoffNotificationService.QueueReceptionWorkflowHandoff(
                                    seller, buyer, question, shopKey, "ambiguous-active-flows");
                        }
                        catch { queued = false; }
                        return Suppressed(seller, buyer, shopKey, "ambiguous", 1,
                            catalog.Revision, string.Empty, string.Empty,
                            queued ? "HANDOFF" : "WAIT", sourceKey,
                            "同一买家存在多个已推进的接待流程，已停止自动选择以避免跨流程串线");
                    }
                    else if (triggeredFlows.Count == 1)
                        selectedFlows = triggeredFlows;
                    else if (triggeredFlows.Count > 1)
                    {
                        var queued = false;
                        try
                        {
                            using (ShopSettingsScope.Enter(shop))
                                queued = HandoffNotificationService.QueueReceptionWorkflowHandoff(
                                    seller, buyer, question, shopKey, "ambiguous-trigger-flows");
                        }
                        catch { queued = false; }
                        return Suppressed(seller, buyer, shopKey, "ambiguous", 1,
                            catalog.Revision, string.Empty, string.Empty,
                            queued ? "HANDOFF" : "WAIT", sourceKey,
                            "当前消息同时匹配多个接待流程，已停止自动选择以避免错误执行");
                    }
                    else return null;

                    foreach (var flow in selectedFlows)
                    {
                        var flowId = Convert.ToString(flow["flow_id"]);
                        var flowVersion = flow.Value<int?>("flow_version") ?? 1;
                        var steps = flow["steps"] as JArray;
                        if (steps == null || steps.Count == 0) continue;
                        var initial = InitialState(flow);
                        if (string.IsNullOrWhiteSpace(initial)) continue;

                        var record = FindRecord(records, buyerKey, flowId);
                        var currentState = record == null ? initial : Convert.ToString(record["current_state"]);
                        var savedVersion = record == null ? flowVersion : record.Value<int?>("flow_version") ?? 0;
                        var flowProducts = (flow["product_ids"] as JArray ?? new JArray()).Values<string>().ToList();
                        var flowSkus = (flow["sku_ids"] as JArray ?? new JArray()).Values<string>().ToList();
                        var matchingOrders = orders.Where(x =>
                            (flowProducts.Count == 0 || flowProducts.Contains(x.ItemId, StringComparer.Ordinal))
                            && (flowSkus.Count == 0 || flowSkus.Contains(x.SkuId, StringComparer.Ordinal))
                            && config.ProductIds.Contains(x.ItemId, StringComparer.Ordinal))
                            .ToList();
                        var orderFingerprint = FingerprintOrders(matchingOrders);
                        var faults = document["faults"] as JArray;
                        var faultKey = flowId + "|" + catalog.Revision.ToString(CultureInfo.InvariantCulture);
                        var quarantinedFault = faults.OfType<JObject>().FirstOrDefault(x =>
                            string.Equals(Convert.ToString(x["key"]), faultKey, StringComparison.Ordinal)
                            && x.Value<bool?>("quarantined") == true);
                        if (quarantinedFault != null)
                        {
                            QueueHandoff(shop, seller, buyer, question, shopKey, flowId);
                            return Suppressed(seller, buyer, shopKey, flowId, flowVersion,
                                catalog.Revision, currentState, currentState, "HANDOFF", sourceKey,
                                "当前流程版本已隔离，等待修复或回滚，不再自动执行");
                        }

                        var deliveryUncertain = record != null && record.Value<bool?>("delivery_uncertain") == true;
                        if (deliveryUncertain)
                        {
                            QueueHandoff(shop, seller, buyer, question, shopKey, flowId);
                            return Suppressed(seller, buyer, shopKey, flowId, flowVersion,
                                catalog.Revision, currentState, currentState, "HANDOFF", sourceKey,
                                "上一条流程回复发送结果不确定，已停止自动重发并等待人工确认");
                        }

                        var pendingSourceKey = record == null ? string.Empty : Convert.ToString(record["pending_source_key"]);
                        if (!string.IsNullOrWhiteSpace(pendingSourceKey))
                        {
                            var pendingStarted = record.Value<long?>("pending_started_utc_ticks") ?? 0;
                            var pendingAge = pendingStarted <= 0
                                ? PendingDeliveryUncertainAfter
                                : TimeSpan.FromTicks(Math.Max(0, DateTime.UtcNow.Ticks - pendingStarted));
                            if (pendingAge >= PendingDeliveryUncertainAfter)
                            {
                                record["delivery_uncertain"] = true;
                                record["pending_source_key"] = string.Empty;
                                record["pending_started_utc_ticks"] = 0;
                                record["updated_utc_ticks"] = DateTime.UtcNow.Ticks;
                                QueueHandoff(shop, seller, buyer, question, shopKey, flowId);
                                return Suppressed(seller, buyer, shopKey, flowId, flowVersion,
                                    catalog.Revision, currentState, currentState, "HANDOFF", sourceKey,
                                    "检测到重启或超时遗留的待发送流程，交由人工确认，禁止重复发送");
                            }
                            return Suppressed(seller, buyer, shopKey, flowId, flowVersion,
                                catalog.Revision, currentState, currentState, "WAIT", sourceKey,
                                "上一条流程回复仍在发送或等待提交，本轮暂缓避免并发重复");
                        }

                        if (record != null
                            && string.Equals(Convert.ToString(record["last_source_key"]), sourceKey, StringComparison.Ordinal))
                            return Suppressed(seller, buyer, shopKey, flowId, flowVersion,
                                catalog.Revision, currentState, currentState, "WAIT", sourceKey,
                                "同一来源消息已处理，防止流程重复执行");

                        // Automatic repair: after a new flow version or an invalid persisted state,
                        // reset only this buyer+flow cursor to the first valid state.
                        if (savedVersion != flowVersion
                            || !steps.OfType<JObject>().Any(x => string.Equals(
                                Convert.ToString(x["state"]), currentState, StringComparison.Ordinal)))
                        {
                            currentState = initial;
                            if (record == null)
                            {
                                record = new JObject { ["buyer_key"] = buyerKey, ["flow_id"] = flowId };
                                records.Add(record);
                            }
                            record["flow_version"] = flowVersion;
                            record["current_state"] = currentState;
                            record["last_source_key"] = string.Empty;
                            record["pending_source_key"] = string.Empty;
                            record["pending_started_utc_ticks"] = 0;
                            record["delivery_uncertain"] = false;
                            record["completed_utc_ticks"] = 0;
                            record["completed_order_fingerprint"] = string.Empty;
                            record["state_entered_utc_ticks"] = DateTime.UtcNow.Ticks;
                            record["updated_utc_ticks"] = DateTime.UtcNow.Ticks;
                        }

                        var step = steps.OfType<JObject>().FirstOrDefault(x =>
                            string.Equals(Convert.ToString(x["state"]), currentState, StringComparison.Ordinal));
                        if (step == null) continue;

                        var stateEnteredTicks = record == null
                            ? 0 : record.Value<long?>("state_entered_utc_ticks") ?? record.Value<long?>("updated_utc_ticks") ?? 0;
                        var timeoutMinutes = Math.Max(1, Math.Min(10080, step.Value<int?>("timeout_minutes") ?? 60));
                        if (record != null && stateEnteredTicks > 0
                            && DateTime.UtcNow.Ticks - stateEnteredTicks > TimeSpan.FromMinutes(timeoutMinutes).Ticks)
                        {
                            currentState = initial;
                            record["current_state"] = initial;
                            record["last_source_key"] = string.Empty;
                            record["pending_source_key"] = string.Empty;
                            record["pending_started_utc_ticks"] = 0;
                            record["delivery_uncertain"] = false;
                            record["completed_utc_ticks"] = 0;
                            record["completed_order_fingerprint"] = string.Empty;
                            record["state_entered_utc_ticks"] = DateTime.UtcNow.Ticks;
                            record["updated_utc_ticks"] = DateTime.UtcNow.Ticks;
                            step = steps.OfType<JObject>().FirstOrDefault(x =>
                                string.Equals(Convert.ToString(x["state"]), currentState, StringComparison.Ordinal));
                            if (step == null) continue;
                        }

                        if (step.Value<bool?>("terminal") == true)
                        {
                            var completedAt = record == null ? 0 : record.Value<long?>("completed_utc_ticks") ?? 0;
                            var previousOrderFingerprint = record == null
                                ? string.Empty : Convert.ToString(record["completed_order_fingerprint"]);
                            var canReenter = record == null || completedAt <= 0
                                || DateTime.UtcNow.Ticks - completedAt >= TimeSpan.FromHours(24).Ticks
                                || !string.Equals(previousOrderFingerprint, orderFingerprint, StringComparison.Ordinal);
                            if (!canReenter)
                            {
                                record["last_source_key"] = sourceKey;
                                record["updated_utc_ticks"] = DateTime.UtcNow.Ticks;
                                return null;
                            }

                            currentState = initial;
                            if (record == null)
                            {
                                record = new JObject { ["buyer_key"] = buyerKey, ["flow_id"] = flowId };
                                records.Add(record);
                            }
                            record["flow_version"] = flowVersion;
                            record["current_state"] = initial;
                            record["last_source_key"] = string.Empty;
                            record["completed_utc_ticks"] = 0;
                            record["completed_order_fingerprint"] = string.Empty;
                            record["state_entered_utc_ticks"] = DateTime.UtcNow.Ticks;
                            step = steps.OfType<JObject>().FirstOrDefault(x =>
                                string.Equals(Convert.ToString(x["state"]), initial, StringComparison.Ordinal));
                            if (step != null)
                                timeoutMinutes = Math.Max(1, Math.Min(10080, step.Value<int?>("timeout_minutes") ?? 60));
                            if (step == null || step.Value<bool?>("terminal") == true)
                            {
                                record["current_state"] = initial;
                                record["last_source_key"] = sourceKey;
                                record["completed_utc_ticks"] = DateTime.UtcNow.Ticks;
                                record["completed_order_fingerprint"] = orderFingerprint;
                                record["updated_utc_ticks"] = DateTime.UtcNow.Ticks;
                                return null;
                            }
                        }

                        var keywords = (step["trigger_keywords"] as JArray ?? new JArray()).Values<string>().ToList();
                        var required = step["required_evidence"] as JArray ?? new JArray();
                        var hasBuyer = question.Length > 0;
                        var hasOrder = matchingOrders.Count > 0;
                        var visualCutoffTicks = DateTime.Now.AddMinutes(-timeoutMinutes).Ticks;
                        var hasRecentVisual = recentVisual != null && recentVisual.Any(x =>
                            x != null && x.ObservedAtTicks >= visualCutoffTicks);
                        var hasImage = isImage || hasRecentVisual;
                        var hasVision = !string.IsNullOrWhiteSpace(visualSummary) || hasRecentVisual;
                        var hasManual = HasHumanCorrectionEvidence(flow);
                        var evidenceReady = HasRequiredEvidence(
                            required, hasBuyer, hasImage, hasVision, hasOrder, hasManual);
                        var initialState = string.Equals(currentState, initial, StringComparison.Ordinal);
                        var action = Convert.ToString(step["action"]);
                        if (keywords.Count > 0 && !ContainsAny(question, keywords)
                            && !(hasImage && required.Any(x => Convert.ToString(x) == "image_message"))
                            && !(isImage && action == "ASK_EVIDENCE"))
                            continue;

                        var nextState = Convert.ToString(step["next_state"]);
                        var reply = ReplyTranscriptSanitizer.Sanitize(Convert.ToString(step["reply_template"]));

                        // If the buyer supplied the requested image in the same burst or a later
                        // image turn, do not ask for it again. Move to the evidence-consuming state.
                        if (hasImage && action == "ASK_EVIDENCE")
                        {
                            var nextStep = steps.OfType<JObject>().FirstOrDefault(x =>
                                string.Equals(Convert.ToString(x["state"]), nextState, StringComparison.Ordinal));
                            if (nextStep != null)
                            {
                                currentState = nextState;
                                step = nextStep;
                                required = step["required_evidence"] as JArray ?? new JArray();
                                action = Convert.ToString(step["action"]);
                                nextState = Convert.ToString(step["next_state"]);
                                reply = ReplyTranscriptSanitizer.Sanitize(Convert.ToString(step["reply_template"]));
                                timeoutMinutes = Math.Max(1, Math.Min(10080, step.Value<int?>("timeout_minutes") ?? 60));
                                visualCutoffTicks = DateTime.Now.AddMinutes(-timeoutMinutes).Ticks;
                                hasRecentVisual = recentVisual != null && recentVisual.Any(x =>
                                    x != null && x.ObservedAtTicks >= visualCutoffTicks);
                                hasImage = isImage || hasRecentVisual;
                                hasVision = !string.IsNullOrWhiteSpace(visualSummary) || hasRecentVisual;
                                evidenceReady = HasRequiredEvidence(
                                    required, hasBuyer, hasImage, hasVision, hasOrder, hasManual);
                            }
                        }
                        if (step.Value<bool?>("terminal") == true)
                        {
                            if (record == null)
                            {
                                record = new JObject { ["buyer_key"] = buyerKey, ["flow_id"] = flowId };
                                records.Add(record);
                            }
                            record["flow_version"] = flowVersion;
                            record["current_state"] = currentState;
                            record["last_source_key"] = sourceKey;
                            record["pending_source_key"] = string.Empty;
                            record["pending_started_utc_ticks"] = 0;
                            record["delivery_uncertain"] = false;
                            record["completed_utc_ticks"] = DateTime.UtcNow.Ticks;
                            record["completed_order_fingerprint"] = orderFingerprint;
                            record["state_entered_utc_ticks"] = DateTime.UtcNow.Ticks;
                            record["updated_utc_ticks"] = DateTime.UtcNow.Ticks;
                            return null;
                        }

                        if (action == "READ_ONLY_TOOL")
                            action = "HANDOFF";

                        if (action == "REPLY" || action == "REPLY_REVIEWED")
                        {
                            if (!hasManual || !required.Any(x => Convert.ToString(x) == "manual_correction"))
                                return Suppressed(seller, buyer, shopKey, flowId, flowVersion,
                                    catalog.Revision, currentState, currentState, "HANDOFF", sourceKey,
                                    "runtime_failure:reply_step_missing_human_evidence");
                            if (reply.Length == 0)
                                return Suppressed(seller, buyer, shopKey, flowId, flowVersion,
                                    catalog.Revision, currentState, currentState, "HANDOFF", sourceKey,
                                    "runtime_failure:missing_reply_template");
                            action = "REPLY_REVIEWED";
                        }

                        if (!evidenceReady)
                        {
                            if (action == "ASK_EVIDENCE" && hasBuyer && reply.Length > 0)
                            {
                                if (record == null)
                                {
                                    record = new JObject { ["buyer_key"] = buyerKey, ["flow_id"] = flowId };
                                    records.Add(record);
                                }
                                var stateChanged = !string.Equals(
                                    Convert.ToString(record["current_state"]), currentState, StringComparison.Ordinal);
                                record["flow_version"] = flowVersion;
                                record["current_state"] = currentState;
                                record["pending_source_key"] = sourceKey;
                                record["pending_started_utc_ticks"] = DateTime.UtcNow.Ticks;
                                if (stateChanged || (record.Value<long?>("state_entered_utc_ticks") ?? 0) <= 0)
                                    record["state_entered_utc_ticks"] = DateTime.UtcNow.Ticks;
                                record["updated_utc_ticks"] = DateTime.UtcNow.Ticks;
                                return new ReceptionWorkflowDecision
                                {
                                    Seller = seller, Buyer = buyer, ShopKey = shopKey,
                                    FlowId = flowId, FlowVersion = flowVersion, CatalogRevision = catalog.Revision,
                                    State = currentState, NextState = nextState, Action = action,
                                    ReplyText = reply, SourceKey = sourceKey, OrderFingerprint = orderFingerprint,
                                    NextStateTerminal = steps.OfType<JObject>().Any(x =>
                                        string.Equals(Convert.ToString(x["state"]), nextState, StringComparison.Ordinal)
                                        && x.Value<bool?>("terminal") == true),
                                    RequiresSend = true,
                                    Reason = "已根据流程索取缺失证据"
                                };
                            }
                            if (record == null)
                            {
                                record = new JObject { ["buyer_key"] = buyerKey, ["flow_id"] = flowId };
                                records.Add(record);
                            }
                            var missingEvidenceStateChanged = !string.Equals(
                                Convert.ToString(record["current_state"]), currentState, StringComparison.Ordinal);
                            record["flow_version"] = flowVersion;
                            record["current_state"] = currentState;
                            record["last_source_key"] = sourceKey;
                            if (missingEvidenceStateChanged || (record.Value<long?>("state_entered_utc_ticks") ?? 0) <= 0)
                                record["state_entered_utc_ticks"] = DateTime.UtcNow.Ticks;
                            record["updated_utc_ticks"] = DateTime.UtcNow.Ticks;
                            return Suppressed(seller, buyer, shopKey, flowId, flowVersion,
                                catalog.Revision, currentState, currentState, "WAIT", sourceKey,
                                "流程所需证据尚未齐全，已阻止未验证的自动答复");
                        }

                        if (action == "WAIT" || action == "HANDOFF")
                        {
                            if (action == "HANDOFF")
                            {
                                var queued = false;
                                try
                                {
                                    using (ShopSettingsScope.Enter(shop))
                                        queued = HandoffNotificationService.QueueReceptionWorkflowHandoff(
                                            seller, buyer, question, shopKey, flowId);
                                }
                                catch { queued = false; }
                                if (!queued)
                                    return Suppressed(seller, buyer, shopKey, flowId, flowVersion,
                                        catalog.Revision, currentState, currentState, "HANDOFF", sourceKey,
                                        "runtime_failure:handoff_notification_unavailable");
                            }
                            if (record == null)
                            {
                                record = new JObject { ["buyer_key"] = buyerKey, ["flow_id"] = flowId };
                                records.Add(record);
                            }
                            record["flow_version"] = flowVersion;
                            record["current_state"] = nextState;
                            record["last_source_key"] = sourceKey;
                            record["pending_source_key"] = string.Empty;
                            record["pending_started_utc_ticks"] = 0;
                            record["delivery_uncertain"] = false;
                            var transitionTerminal = steps.OfType<JObject>().Any(x =>
                                string.Equals(Convert.ToString(x["state"]), nextState, StringComparison.Ordinal)
                                && x.Value<bool?>("terminal") == true);
                            record["completed_utc_ticks"] = transitionTerminal ? DateTime.UtcNow.Ticks : 0;
                            record["completed_order_fingerprint"] = transitionTerminal ? orderFingerprint : string.Empty;
                            record["state_entered_utc_ticks"] = DateTime.UtcNow.Ticks;
                            record["updated_utc_ticks"] = DateTime.UtcNow.Ticks;
                            return Suppressed(seller, buyer, shopKey, flowId, flowVersion,
                                catalog.Revision, currentState, nextState, action, sourceKey,
                                action == "HANDOFF" ? "流程已交由人工客服处理" : "流程要求等待下一项证据");
                        }

                        if (action == "ASK_EVIDENCE" || action == "REPLY_REVIEWED")
                        {
                            if (reply.Length == 0)
                            {
                                return Suppressed(seller, buyer, shopKey, flowId, flowVersion,
                                    catalog.Revision, currentState, currentState, "HANDOFF", sourceKey,
                                    "runtime_failure:missing_reply_template");
                            }
                            if (record == null)
                            {
                                record = new JObject { ["buyer_key"] = buyerKey, ["flow_id"] = flowId };
                                records.Add(record);
                            }
                            var replyStateChanged = !string.Equals(
                                Convert.ToString(record["current_state"]), currentState, StringComparison.Ordinal);
                            record["flow_version"] = flowVersion;
                            record["current_state"] = currentState;
                            record["pending_source_key"] = sourceKey;
                            record["pending_started_utc_ticks"] = DateTime.UtcNow.Ticks;
                            if (replyStateChanged || (record.Value<long?>("state_entered_utc_ticks") ?? 0) <= 0)
                                record["state_entered_utc_ticks"] = DateTime.UtcNow.Ticks;
                            record["updated_utc_ticks"] = DateTime.UtcNow.Ticks;
                            return new ReceptionWorkflowDecision
                            {
                                Seller = seller, Buyer = buyer, ShopKey = shopKey,
                                FlowId = flowId, FlowVersion = flowVersion, CatalogRevision = catalog.Revision,
                                State = currentState, NextState = nextState, Action = action,
                                ReplyText = reply, SourceKey = sourceKey, OrderFingerprint = orderFingerprint,
                                NextStateTerminal = steps.OfType<JObject>().Any(x =>
                                    string.Equals(Convert.ToString(x["state"]), nextState, StringComparison.Ordinal)
                                    && x.Value<bool?>("terminal") == true),
                                RequiresSend = true,
                                Reason = action == "ASK_EVIDENCE" ? "流程索证步骤" : "采用真实人工回复证据"
                            };
                        }
                    }
                    return null;
                });
                if (decision != null && (decision.Reason ?? string.Empty).StartsWith(
                    "runtime_failure:", StringComparison.Ordinal))
                {
                    RegisterFailure(seller, shopKey, decision.FlowId, decision.CatalogRevision,
                        decision.Reason.Substring("runtime_failure:".Length));
                    var handoffQueued = QueueHandoff(shop, seller, buyer, question, shopKey, decision.FlowId);
                    decision.Reason = handoffQueued
                        ? "流程运行异常已记录并通知人工；连续异常达到阈值后将自动回滚"
                        : "流程运行异常已记录，但当前转人工通知渠道不可用；连续异常达到阈值后将自动回滚";
                }
                return decision;
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("Reception Agent状态机执行失败，未直接发送或修改订单：" + ex.Message, 10);
                QueueHandoff(shop, seller, buyer, question, shopKey, "runtime-storage-error");
                return new ReceptionWorkflowDecision
                {
                    ShopKey = shopKey, Seller = seller, Buyer = buyer,
                    SuppressReply = true, RequiresSend = false, Action = "HANDOFF",
                    Reason = "接待流程状态存储异常，已停止自动执行"
                };
            }
        }

        internal static bool IsDecisionCurrent(ReceptionWorkflowDecision decision)
        {
            if (decision == null || !decision.RequiresSend) return true;
            ShopContext shop;
            try { shop = ResolveShop(decision.Seller, decision.ShopKey); }
            catch { return false; }
            try
            {
                CatalogIndex catalog;
                using (ShopSettingsScope.Enter(shop))
                    catalog = LoadCatalog(decision.Seller, decision.ShopKey);
                if (catalog == null || catalog.Revision != decision.CatalogRevision)
                    return false;
                var flow = catalog.Flows.FirstOrDefault(x =>
                    string.Equals(Convert.ToString(x["flow_id"]), decision.FlowId, StringComparison.Ordinal));
                if (flow == null || (flow.Value<int?>("flow_version") ?? 0) != decision.FlowVersion)
                    return false;

                return Mutate(shop, document =>
                {
                    var buyerKey = Digest(decision.ShopKey + "|" + decision.Seller + "|" + decision.Buyer);
                    var record = FindRecord(document["records"] as JArray, buyerKey, decision.FlowId);
                    return record != null
                        && string.Equals(Convert.ToString(record["pending_source_key"]), decision.SourceKey, StringComparison.Ordinal)
                        && (record.Value<int?>("flow_version") ?? 0) == decision.FlowVersion
                        && record.Value<bool?>("delivery_uncertain") != true;
                });
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("Reception Agent发送前目录/状态二次校验失败，已阻止旧流程回复：" + ex.Message, 10);
                return false;
            }
        }

        internal static void CompleteDecision(ReceptionWorkflowDecision decision, bool sendSucceeded)
        {
            if (decision == null || !decision.RequiresSend || string.IsNullOrWhiteSpace(decision.ShopKey)
                || string.IsNullOrWhiteSpace(decision.FlowId)) return;
            ShopContext shop;
            try { shop = ResolveShop(decision.Seller, decision.ShopKey); }
            catch { return; }
            try
            {
                var committed = Mutate(shop, document =>
                {
                    var buyerKey = Digest(decision.ShopKey + "|" + decision.Seller + "|" + decision.Buyer);
                    var records = document["records"] as JArray;
                    var record = FindRecord(records, buyerKey, decision.FlowId);
                    if (record == null
                        || !string.Equals(Convert.ToString(record["pending_source_key"]), decision.SourceKey, StringComparison.Ordinal)
                        || (record.Value<int?>("flow_version") ?? 0) != decision.FlowVersion)
                        return false;

                    if (sendSucceeded)
                    {
                        record["current_state"] = decision.NextState;
                        record["last_source_key"] = decision.SourceKey;
                        record["delivery_uncertain"] = false;
                        if (decision.NextStateTerminal)
                        {
                            record["completed_utc_ticks"] = DateTime.UtcNow.Ticks;
                            record["completed_order_fingerprint"] = decision.OrderFingerprint ?? string.Empty;
                        }
                        else
                        {
                            record["completed_utc_ticks"] = 0;
                            record["completed_order_fingerprint"] = string.Empty;
                        }
                        record["state_entered_utc_ticks"] = DateTime.UtcNow.Ticks;
                    }
                    else
                    {
                        // A failed/uncertain transport result is not retried by the workflow engine.
                        // The delivery watchdog/manual timeline remains the authority for resolution.
                        record["last_source_key"] = decision.SourceKey;
                        record["delivery_uncertain"] = true;
                    }
                    record["pending_source_key"] = string.Empty;
                    record["pending_started_utc_ticks"] = 0;
                    record["updated_utc_ticks"] = DateTime.UtcNow.Ticks;
                    return true;
                });
                if (!sendSucceeded && committed)
                    QueueHandoff(shop, decision.Seller, decision.Buyer, string.Empty,
                        decision.ShopKey, decision.FlowId);
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("Reception Agent状态提交失败，后续事件将按未确认发送处理：" + ex.Message, 10);
            }
        }

        internal static void CancelDecision(ReceptionWorkflowDecision decision)
        {
            if (decision == null || !decision.RequiresSend || string.IsNullOrWhiteSpace(decision.ShopKey)
                || string.IsNullOrWhiteSpace(decision.FlowId)) return;
            ShopContext shop;
            try { shop = ResolveShop(decision.Seller, decision.ShopKey); }
            catch { return; }
            try
            {
                Mutate(shop, document =>
                {
                    var buyerKey = Digest(decision.ShopKey + "|" + decision.Seller + "|" + decision.Buyer);
                    var record = FindRecord(document["records"] as JArray, buyerKey, decision.FlowId);
                    if (record == null
                        || !string.Equals(Convert.ToString(record["pending_source_key"]), decision.SourceKey, StringComparison.Ordinal))
                        return false;
                    record["pending_source_key"] = string.Empty;
                    record["pending_started_utc_ticks"] = 0;
                    record["last_source_key"] = decision.SourceKey;
                    record["updated_utc_ticks"] = DateTime.UtcNow.Ticks;
                    return true;
                });
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("Reception Agent取消待发送决策失败：" + ex.Message, 10);
            }
        }

        private static bool RegisterFailure(
            string seller, string shopKey, string flowId, long revision, string reason)
        {
            ShopContext shop;
            try { shop = ResolveShop(seller, shopKey); }
            catch { return false; }
            var shouldRollback = false;
            try
            {
                Mutate(shop, document =>
                {
                    var faults = document["faults"] as JArray;
                    var key = flowId + "|" + revision.ToString(CultureInfo.InvariantCulture);
                    var fault = faults.OfType<JObject>().FirstOrDefault(x =>
                        string.Equals(Convert.ToString(x["key"]), key, StringComparison.Ordinal));
                    var now = DateTime.UtcNow.Ticks;
                    if (fault == null)
                    {
                        fault = new JObject
                        {
                            ["key"] = key,
                            ["flow_id"] = flowId,
                            ["catalog_revision"] = revision,
                            ["window_started_utc_ticks"] = now,
                            ["failure_count"] = 0,
                            ["quarantined"] = false
                        };
                        faults.Add(fault);
                    }
                    var windowStart = fault.Value<long?>("window_started_utc_ticks") ?? now;
                    if (now - windowStart > FaultWindow.Ticks)
                    {
                        fault["window_started_utc_ticks"] = now;
                        fault["failure_count"] = 0;
                        fault["quarantined"] = false;
                    }
                    var count = (fault.Value<int?>("failure_count") ?? 0) + 1;
                    fault["failure_count"] = count;
                    fault["last_failure_utc_ticks"] = now;
                    fault["last_failure"] = reason ?? string.Empty;
                    if (count >= AutoRollbackFailureThreshold && fault.Value<bool?>("quarantined") != true)
                    {
                        fault["quarantined"] = true;
                        shouldRollback = true;
                    }
                    return true;
                });
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("Reception Agent异常计数持久化失败：" + ex.Message, 10);
                return false;
            }

            if (shouldRollback)
            {
                try
                {
                    ReceptionWorkflowCatalogService.RollbackToPreviousPublished(
                        seller, shopKey, revision);
                    InvalidateShop(shopKey);
                    Log.ErrorWithMaxCount("Reception Agent检测到同一目录版本连续异常，已自动回滚：flow="
                        + flowId + ", revision=" + revision, 10);
                    return true;
                }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount("Reception Agent自动回滚未执行（无有效历史版本或CAS变化），当前流程保持隔离：" + ex.Message, 10);
                }
            }
            return false;
        }
    }
}