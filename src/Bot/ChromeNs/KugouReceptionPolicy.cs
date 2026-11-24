using Bot.Knowledge;
using Bot.ShopScope;
using BotLib;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Bot.ChromeNs
{
    // Store-scoped reception facts. This is an authoritative guard in front of the
    // existing text/vision reply paths, NOT another independent message sender.
    // Unrelated shops are unaffected; detect the store by its existing verified item.
    internal static class KugouReceptionPolicy
    {
        private sealed class ShopDetection
        {
            public bool Match;
            public DateTime ExpiresUtc;
            public string ProfileRevision;
        }

        private sealed class ReceptionAuditEntry
        {
            public DateTime AtUtc { get; set; }
            public string BuyerHash { get; set; }
            public string Decision { get; set; }
            public string Stage { get; set; }
            public string Evidence { get; set; }
            public string RuleId { get; set; }
        }

        private static readonly ConcurrentDictionary<string, ShopDetection> Shops =
            new ConcurrentDictionary<string, ShopDetection>(StringComparer.Ordinal);
        private static readonly Regex PhoneOnly = new Regex(
            @"^(?:\+?86[- ]?)?1[3-9]\d{9}$", RegexOptions.Compiled);
        private static readonly Regex PhotoInstruction = new Regex(
            "拍照|截图|拍一张|发图片|发张图|拍给我", RegexOptions.Compiled);
        private static readonly object AuditSync = new object();
        private static long _lastShopCacheSweepUtcTicks;

        private static void CleanupExpiredShopCache()
        {
            // Keep inactive ShopKey entries from accumulating in long-running multi-shop
            // clients. Sweep at most once per minute and remove only the exact expired pair,
            // so a concurrent refresh cannot be deleted by an older enumeration snapshot.
            var nowUtcTicks = DateTime.UtcNow.Ticks;
            var previousTicks = Interlocked.Read(ref _lastShopCacheSweepUtcTicks);
            if (nowUtcTicks - previousTicks < TimeSpan.FromMinutes(1).Ticks
                || Interlocked.CompareExchange(
                    ref _lastShopCacheSweepUtcTicks, nowUtcTicks, previousTicks) != previousTicks)
                return;

            var now = DateTime.UtcNow;
            var exactEntries = (ICollection<KeyValuePair<string, ShopDetection>>)Shops;
            foreach (var pair in Shops)
            {
                if (pair.Value != null && pair.Value.ExpiresUtc <= now)
                    exactEntries.Remove(pair);
            }
        }

        internal static ShopContext ResolveShopForSeller(string seller)
        {
            seller = (seller ?? string.Empty).Trim();
            if (seller.Length == 0)
                throw new InvalidOperationException("卖家昵称为空，无法确定店铺作用域。");
            // Reuse the shared resolver so offline DisplayName labels are accepted
            // only for the exact active scope, while mismatched SellerNick/ShopKey
            // values are rejected instead of silently reading another shop's rules.
            return ShopContextLocator.ResolveCurrentOrBySellerNick(seller);
        }

        public static bool Applies(string seller, string shopKey)
        {
            seller = (seller ?? string.Empty).Trim();
            shopKey = (shopKey ?? string.Empty).Trim();
            if (seller.Length == 0 || shopKey.Length == 0) return false;
            try
            {
                var shop = ShopContextLocator.ResolveRuntimeByShopKey(shopKey);
                if (shop == null || !shop.HasStableSellerId
                    || !string.Equals(shop.ShopKey, shopKey, StringComparison.Ordinal)
                    || !string.Equals(shop.SellerNick, seller, StringComparison.OrdinalIgnoreCase))
                    return false;
                using (ShopSettingsScope.Enter(shop)) return Applies(seller);
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("按精确ShopKey检查接待策略失败，保持原流程：" + ex.Message, 5);
                return false;
            }
        }

        public static bool Applies(string seller)
        {
            CleanupExpiredShopCache();
            seller = (seller ?? string.Empty).Trim();
            if (seller.Length == 0) return false;
            ShopContext shop;
            try
            {
                shop = ResolveShopForSeller(seller);
                if (shop == null || string.IsNullOrWhiteSpace(shop.ShopKey) || !shop.HasStableSellerId)
                    return false;
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("店铺专属接待身份无法唯一解析，保持原流程：" + ex.Message, 5);
                return false;
            }

            var cacheKey = shop.ShopKey;
            // The cache is shared by multiple Bot processes indirectly through the
            // same profile file. Compare its on-disk revision before reusing a match,
            // so a disable/product-scope change from another process takes effect now.
            var profileRevision = KugouReceptionConfigService.GetProfileRevision(shop);
            ShopDetection cached;
            if (!string.IsNullOrEmpty(profileRevision)
                && Shops.TryGetValue(cacheKey, out cached)
                && cached.ExpiresUtc > DateTime.UtcNow
                && string.Equals(cached.ProfileRevision, profileRevision, StringComparison.Ordinal))
                return cached.Match;

            var matched = false;
            try
            {
                var config = KugouReceptionConfigService.GetConfig(shop);
                if (config.Enabled && config.ProductIds != null && config.ProductIds.Count > 0)
                {
                    using (ShopSettingsScope.Enter(shop))
                    {
                        var productIds = new HashSet<string>(config.ProductIds, StringComparer.OrdinalIgnoreCase);
                        var records = KnowledgeEngineV2Repository.LoadAll(seller);
                        matched = records.Any(x => x != null && x.ProductIds != null
                            && x.ProductIds.Any(id => productIds.Contains(id ?? string.Empty)));
                    }
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("店铺专属接待规则识别失败，保持原流程：" + ex.Message, 5);
            }
            Shops[cacheKey] = new ShopDetection
            {
                Match = matched,
                ProfileRevision = profileRevision,
                ExpiresUtc = DateTime.UtcNow.AddMinutes(matched ? 10 : 2)
            };
            return matched;
        }

        internal static void InvalidateShopCache(string shopKey)
        {
            if (string.IsNullOrWhiteSpace(shopKey)) return;
            ShopDetection ignored;
            Shops.TryRemove(shopKey, out ignored);
        }

        internal static bool RequiresManualKnowledgeReview(string seller, string question, string answer)
        {
            // High-risk facts require review for every shop, even when a shop has not
            // configured its optional business profile. The profile enable switch must
            // never weaken this global learning safety boundary.
            var text = (question ?? string.Empty) + " " + (answer ?? string.Empty);
            return Regex.IsMatch(text,
                @"会员|设备|账号|登录|绑定|充值|退款|支付|付款|到账|验证码|密码|银行卡|隐私|实名|发货异常|教程",
                RegexOptions.IgnoreCase);
        }

        public static bool IsQuarantined(string seller, string id, string title, string answer)
        {
            if (!Applies(seller)) return false;
            var config = KugouReceptionConfigService.GetConfig(seller);
            if (config.QuarantineKnowledgeIds.Contains((id ?? string.Empty).Trim(), StringComparer.OrdinalIgnoreCase))
                return true;
            var body = (title ?? string.Empty) + " " + (answer ?? string.Empty);
            return config.QuarantineTextPatterns.Any(pattern => SafeIsMatch(body, pattern));
        }

        // Called only after the order action ledger confirms the instruction was delivered.
        // This does not claim that payment, binding or recharge has been verified.
        internal static void ObserveDeliveredOrderInstruction(string seller, string buyer, string text)
        {
            if (!Applies(seller)) return;
            var config = KugouReceptionConfigService.GetConfig(seller);
            if (!config.EnablePhoneWait) return;
            text = (text ?? string.Empty).Trim();
            if (!PhotoInstruction.IsMatch(text)
                || !HasAllCues(text, config.PhotoInstructionAllCues)
                || !HasAnyCue(text, config.PhotoInstructionAnyCues)
                || HasAnyCue(text, config.PhotoInstructionExcludedCues)) return;
            ReceptionEvidenceLedger.RecordDeliveredEvidenceRequest(seller, buyer, text);
            Audit(seller, buyer, "WAIT", "awaiting_account_screenshot",
                "verified_order_photo_instruction_delivered", "configured_photo_instruction");
        }

        public static bool TryWaitForEvidence(string seller, string buyer, string question, out string reason)
        {
            reason = string.Empty;
            if (!Applies(seller)) return false;
            var config = KugouReceptionConfigService.GetConfig(seller);
            if (!config.EnablePhoneWait) return false;
            var input = (question ?? string.Empty).Trim();
            if (!PhoneOnly.IsMatch(input)) return false;
            var turns = ConversationContextStore.GetRecentTurns(seller, buyer, string.Empty, 24);
            var lastSeller = turns.LastOrDefault(x => x != null
                && x.Role == "assistant" && !x.Withdrawn && !string.IsNullOrWhiteSpace(x.Text));
            if (lastSeller == null)
            {
                if (GetRecentStage(seller, buyer) != "awaiting_account_screenshot") return false;
                reason = config.PhoneWaitReason;
                Audit(seller, buyer, "WAIT", "awaiting_account_screenshot",
                    "phone_only_after_order_instruction", "configured_phone_wait");
                return true;
            }
            if (lastSeller.Timestamp == DateTime.MinValue
                || lastSeller.Timestamp < DateTime.Now.AddHours(-2)) return false;
            var instruction = lastSeller.Text ?? string.Empty;
            if (!PhotoInstruction.IsMatch(instruction)
                || !HasAllCues(instruction, config.PhotoInstructionAllCues)
                || !HasAnyCue(instruction, config.PhotoInstructionAnyCues)
                || HasAnyCue(instruction, config.PhotoInstructionExcludedCues)) return false;
            reason = config.PhoneWaitReason;
            Audit(seller, buyer, "WAIT", "awaiting_account_screenshot",
                "phone_only_after_configured_photo_instruction", "configured_phone_wait");
            return true;
        }

        public static bool BlockOutgoing(string seller, string buyer, string question, string answer, out string reason)
        {
            reason = string.Empty;
            if (!Applies(seller)) return false;
            var config = KugouReceptionConfigService.GetConfig(seller);
            var output = answer ?? string.Empty;
            if (!config.EnableAutoGuidance && !string.IsNullOrWhiteSpace(config.GuideUrl)
                && output.IndexOf(config.GuideUrl, StringComparison.OrdinalIgnoreCase) >= 0)
                reason = "当前店铺已关闭自动设备指导";
            else if (config.BlockedOutgoingPatterns.Any(pattern => SafeIsMatch(output, pattern)))
                reason = "命中当前店铺配置的禁止发送规则";
            else if (IsScreenshotQuestion(question, config)
                && !string.IsNullOrWhiteSpace(config.ScreenshotReplyPattern)
                && SafeIsMatch(output, config.ScreenshotReplyPattern))
                reason = "当前问题与店铺配置的截图答复校验规则冲突";
            if (reason.Length == 0) return false;
            Audit(seller, buyer, "SUPPRESS", "reply_validation", reason, "configured_outgoing_guard");
            return true;
        }

        public static bool BlockOutgoing(
            string seller, string buyer, string question, string answer, out string reason, string shopKey)
        {
            return BlockOutgoing(seller, buyer, question, answer, out reason, shopKey, true);
        }

        internal static bool BlockOutgoing(
            string seller, string buyer, string question, string answer,
            out string reason, string shopKey, bool requireEnabled)
        {
            reason = string.Empty;
            seller = (seller ?? string.Empty).Trim();
            shopKey = (shopKey ?? string.Empty).Trim();
            if (seller.Length == 0 || shopKey.Length == 0)
            {
                reason = "缺少精确Seller/ShopKey身份，拒绝发送接待流程回复";
                return true;
            }
            try
            {
                var shop = ShopContextLocator.ResolveRuntimeByShopKey(shopKey);
                if (shop == null || !shop.HasStableSellerId
                    || !string.Equals(shop.ShopKey, shopKey, StringComparison.Ordinal)
                    || !string.Equals(shop.SellerNick, seller, StringComparison.OrdinalIgnoreCase))
                {
                    reason = "Seller/ShopKey身份不匹配，拒绝发送接待流程回复";
                    return true;
                }
                using (ShopSettingsScope.Enter(shop))
                {
                    var config = KugouReceptionConfigService.GetConfig(shop);
                    var output = answer ?? string.Empty;
                    if (config == null || !config.Enabled)
                    {
                        if (!requireEnabled) return false;
                        reason = "当前店铺已关闭Reception Agent，拒绝发送旧流程回复";
                        return true;
                    }
                    if (requireEnabled && !config.EnableAutoGuidance)
                    {
                        reason = "当前店铺已关闭自动接待流程，拒绝发送旧流程回复";
                        return true;
                    }
                    if (!config.EnableAutoGuidance && !string.IsNullOrWhiteSpace(config.GuideUrl)
                        && output.IndexOf(config.GuideUrl, StringComparison.OrdinalIgnoreCase) >= 0)
                        reason = "当前店铺已关闭自动设备指导";
                    else if (config.BlockedOutgoingPatterns.Any(pattern => SafeIsMatch(output, pattern)))
                        reason = "命中当前店铺配置的禁止发送规则";
                    else if (IsScreenshotQuestion(question, config)
                        && !string.IsNullOrWhiteSpace(config.ScreenshotReplyPattern)
                        && SafeIsMatch(output, config.ScreenshotReplyPattern))
                        reason = "当前问题与店铺配置的截图答复校验规则冲突";
                    if (reason.Length == 0) return false;
                    Audit(seller, buyer, "SUPPRESS", "reply_validation", reason, "configured_outgoing_guard");
                    return true;
                }
            }
            catch (Exception ex)
            {
                reason = "无法验证精确店铺接待策略：" + ex.Message;
                return true;
            }
        }

        internal static bool IsScreenshotQuestion(string question, KugouReceptionConfig config)
        {
            var value = (question ?? string.Empty).Replace(" ", "").Trim();
            return HasAnyCue(value, config == null ? null : config.ScreenshotQuestionCues);
        }

        // Visual decisions are data-driven. The rule engine is generic; store facts,
        // vendor cues and customer-facing templates are loaded only from the ShopKey profile.
        public static bool TryResolveVisual(
            string seller, string buyer, string question, string summary, string ocr,
            out string answer, out string reason)
        {
            answer = string.Empty;
            reason = string.Empty;
            if (!Applies(seller)) return false;
            var config = KugouReceptionConfigService.GetConfig(seller);
            if (!config.EnableAutoGuidance) return false;
            var evidence = (summary ?? string.Empty) + " " + (ocr ?? string.Empty);
            if (evidence.Trim().Length == 0) return false;
            var compact = Regex.Replace(evidence, @"\s+", "").ToLowerInvariant();

            if (config.VisualUncertainPatterns.Any(pattern => SafeIsMatch(compact, pattern)))
            {
                if (string.IsNullOrWhiteSpace(config.VisualUncertainReply)) return false;
                answer = config.VisualUncertainReply;
                reason = "visual_evidence_uncertain";
                Audit(seller, buyer, "ASK_EVIDENCE", "need_resource_confirmation",
                    reason, "configured_visual_uncertainty");
                return true;
            }

            foreach (var rule in config.VisualRules)
            {
                if (!MatchesVisualRule(compact, rule)) continue;
                if (rule.Reply.IndexOf("{guide_url}", StringComparison.Ordinal) >= 0
                    && string.IsNullOrWhiteSpace(config.GuideUrl)) continue;
                answer = rule.Reply.Replace("{guide_url}", config.GuideUrl ?? string.Empty);
                reason = rule.Reason;
                Audit(seller, buyer, "REPLY", rule.Stage, reason, rule.Id);
                return true;
            }
            return false;
        }

        private static bool SafeIsMatch(string input, string pattern)
        {
            try
            {
                return Regex.IsMatch(input ?? string.Empty, pattern,
                    RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
            }
            catch (RegexMatchTimeoutException)
            {
                // A pathological private pattern must never bypass a quarantine/send guard.
                // For uncertainty checks, this conservatively requests additional evidence.
                Log.ErrorWithMaxCount("店铺接待规则正则匹配超时，按安全拦截处理。", 5);
                return true;
            }
        }

        private static bool MatchesVisualRule(string text, KugouVisualRule rule)
        {
            if (rule == null) return false;
            foreach (var group in rule.AllCueGroups)
                if (!group.Any(cue => ContainsCue(text, cue))) return false;
            if (rule.AnyCues.Count > 0 && !HasAnyCue(text, rule.AnyCues)) return false;
            if (HasAnyCue(text, rule.NoneCues)) return false;
            foreach (var group in rule.PositiveCueGroups)
                if (!group.Any(cue => HasPositiveCue(text, cue))) return false;
            if (rule.NonePositiveCues.Any(cue => HasPositiveCue(text, cue))) return false;
            return true;
        }

        private static bool HasAllCues(string text, IEnumerable<string> cues)
        {
            var values = cues == null ? new List<string>() : cues.ToList();
            return values.Count > 0 && values.All(cue => ContainsCue(text, cue));
        }

        private static bool HasAnyCue(string text, IEnumerable<string> cues)
        {
            return cues != null && cues.Any(cue => ContainsCue(text, cue));
        }

        private static bool ContainsCue(string text, string cue)
        {
            return !string.IsNullOrWhiteSpace(cue)
                && (text ?? string.Empty).IndexOf(cue, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool HasPositiveCue(string text, string cue)
        {
            if (string.IsNullOrWhiteSpace(cue)) return false;
            var cursor = 0;
            while (cursor < (text ?? string.Empty).Length)
            {
                var i = text.IndexOf(cue, cursor, StringComparison.OrdinalIgnoreCase);
                if (i < 0) break;
                var prefix = text.Substring(Math.Max(0, i - 10), Math.Min(10, i));
                if (!Regex.IsMatch(prefix, @"(?:未|无|没有|并非|不是|不|无法|看不到)[^，。；]{0,5}$"))
                    return true;
                cursor = i + cue.Length;
            }
            return false;
        }

        public static string BuildPromptAddon(string seller, string buyer)
        {
            if (!Applies(seller)) return string.Empty;
            var config = KugouReceptionConfigService.GetConfig(seller);
            if (string.IsNullOrWhiteSpace(config.PolicyPrompt)) return string.Empty;
            var knownStage = GetRecentStage(seller, buyer);
            return "\n\n" + config.PolicyPrompt
                + "\n最近经过规则决策的接待阶段：" + (knownStage.Length == 0 ? "未确认" : knownStage)
                + "。该阶段不是订单实时状态，必须用最新消息或外部订单证据重新验证。"
                + (config.EnableAutoGuidance ? string.Empty
                    : "\n当前店铺已关闭自动设备指导；涉及设备判断时应转人工核验，不得自动发送教程。")
                + "\n任何情况下都不得仅凭付款、品牌或图片相似性确认履约完成；自动充值权限始终关闭，高风险业务事实必须人工审核。\n";
        }

        public static string GetAuditJson(string seller)
        {
            if (!Applies(seller)) return "当前店铺尚未满足专属接待规则条件，未启用接待审计。";
            try
            {
                var shop = ResolveShopForSeller(seller);
                if (shop == null) return "没有可用的店铺作用域，禁止跨店铺读取审计。";
                using (ShopSettingsScope.Enter(shop))
                {
                    var directory = Path.GetDirectoryName(BusinessPolicyProfileService.GetUserPath());
                    if (string.IsNullOrWhiteSpace(directory)) return "没有审计目录。";
                    var path = Path.Combine(directory, "kugou-reception-decisions.json");
                    string error;
                    var content = CrossProcessAtomicStateFile.ReadAllTextShared(path, 2, 25, out error);
                    if (!string.IsNullOrWhiteSpace(error)) return "读取审计失败：" + error;
                    return string.IsNullOrWhiteSpace(content) ? "当前店铺还没有接待决策记录。" : content;
                }
            }
            catch (Exception ex)
            {
                return "读取接待审计异常：" + ex.Message;
            }
        }

        // Restore a short-lived stage from the privacy-minimized per-shop decision journal.
        // Explicitly ignore send validation and general FAQ audit entries; these are not
        // business state transitions. The real order and account evidence remain external.
        internal static string GetRecentStage(string seller, string buyer)
        {
            if (!Applies(seller)) return string.Empty;
            try
            {
                var shop = ResolveShopForSeller(seller);
                if (shop == null) return string.Empty;
                using (ShopSettingsScope.Enter(shop))
                {
                    var directory = Path.GetDirectoryName(BusinessPolicyProfileService.GetUserPath());
                    if (string.IsNullOrWhiteSpace(directory)) return string.Empty;
                    var path = Path.Combine(directory, "kugou-reception-decisions.json");
                    string error;
                    var raw = CrossProcessAtomicStateFile.ReadAllTextShared(path, 2, 25, out error);
                    if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
                    var recent = JsonConvert.DeserializeObject<List<ReceptionAuditEntry>>(raw);
                    if (recent == null) return string.Empty;
                    var key = HashBuyer(seller, buyer);
                    var allowed = new HashSet<string>(StringComparer.Ordinal)
                    {
                        "awaiting_account_screenshot", "awaiting_binding_proof",
                        "device_rejected", "awaiting_order_review", "human_handoff", "completed"
                    };
                    var entry = recent.Where(x => x != null
                            && x.BuyerHash == key
                            && x.AtUtc > DateTime.UtcNow.AddHours(-2)
                            && allowed.Contains(x.Stage))
                        .OrderByDescending(x => x.AtUtc).FirstOrDefault();
                    return entry == null ? string.Empty : entry.Stage;
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("读取店铺接待阶段失败，回退到会话历史：" + ex.Message, 5);
                return string.Empty;
            }
        }

        internal static void Audit(string seller, string buyer, string action, string stage, string evidence, string ruleId)
        {
            if (!Applies(seller)) return;
            try
            {
                var shop = ResolveShopForSeller(seller);
                if (shop == null) return;
                using (ShopSettingsScope.Enter(shop))
                {
                var basePath = BusinessPolicyProfileService.GetUserPath();
                var directory = Path.GetDirectoryName(basePath);
                if (string.IsNullOrWhiteSpace(directory)) return;
                var path = Path.Combine(directory, "kugou-reception-decisions.json");
                using (var lease = CrossProcessAtomicStateFile.Acquire(path, "KugouReceptionAudit", 1500))
                {
                    if (lease == null || !lease.Acquired) return;
                    string error;
                    var raw = CrossProcessAtomicStateFile.ReadAllTextShared(path, 2, 25, out error);
                    if (!string.IsNullOrWhiteSpace(error))
                        throw new IOException("读取既有接待审计失败，保留原文件不覆盖：" + error);
                    List<ReceptionAuditEntry> entries;
                    if (string.IsNullOrWhiteSpace(raw))
                    {
                        entries = new List<ReceptionAuditEntry>();
                    }
                    else
                    {
                        try { entries = JsonConvert.DeserializeObject<List<ReceptionAuditEntry>>(raw); }
                        catch (Exception ex)
                        {
                            throw new InvalidDataException("既有接待审计结构损坏，保留原文件不覆盖。", ex);
                        }
                        if (entries == null)
                            throw new InvalidDataException("既有接待审计不是有效数组，保留原文件不覆盖。");
                    }
                    entries.Add(new ReceptionAuditEntry
                    {
                        AtUtc = DateTime.UtcNow, BuyerHash = HashBuyer(seller, buyer),
                        Decision = action, Stage = stage, Evidence = evidence, RuleId = ruleId
                    });
                    entries = entries.Where(x => x != null && x.AtUtc > DateTime.UtcNow.AddDays(-7))
                        .OrderByDescending(x => x.AtUtc).Take(300).ToList();
                    if (!CrossProcessAtomicStateFile.WriteAllTextAtomic(
                        path, JsonConvert.SerializeObject(entries, Formatting.Indented), 2, 25, out error))
                        Log.ErrorWithMaxCount("接待决策审计保存失败: " + error, 10);
                }
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("接待决策审计异常: " + ex.Message, 10);
            }
        }

        private static string HashBuyer(string seller, string buyer)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes((seller ?? "") + "#" + (buyer ?? "")));
                return BitConverter.ToString(bytes, 0, 10).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
