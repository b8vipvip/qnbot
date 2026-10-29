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
        private static readonly Regex BadOutgoingClaim = new Regex(
            "手机端不通用|不支持手机端|仅适用于电视大屏|仅限电视大屏|店铺仅提供当贝酷狗会员|充值成功后(绝对|一律)?不支持退款",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly HashSet<string> QuarantineIds = new HashSet<string>(
            new[] {
                "25ef03a5b5d6412dbb3c9c0e9d8dd69e",
                "3df9874e49d24a16b0c01685558e7c15",
                "9b17f1df3c504631b50ff33579399027",
                "1934c28bfad941f1852da38d193a829e",
                "347f8fa6e74d4133a9bcaeaf2a793833",
                "033f1ea1a3c84b18b8b4b36a6eac6996",
                "18d7d5cb57394393a8c475b9b2262c84",
                "07ed86dfbdb149d7a2565288c582c25a"
            }, StringComparer.OrdinalIgnoreCase);
        private static readonly object AuditSync = new object();
        internal const string TClGuideUrl = "https://howhub.k2n.cn/guide/tcl-ln";
        internal const string FirstProduct = "1057505313937";
        internal const string SecondProduct = "1066362196568";

        public static bool Applies(string seller)
        {
            seller = (seller ?? string.Empty).Trim();
            if (seller.Length == 0) return false;
            ShopDetection cached;
            if (Shops.TryGetValue(seller, out cached) && cached.ExpiresUtc > DateTime.UtcNow)
                return cached.Match;
            var matched = false;
            try
            {
                // Do not activate another merchant's flow on merely seeing "酷狗" in chat.
                var records = KnowledgeEngineV2Repository.LoadAll(seller);
                matched = records.Any(x => x != null && x.ProductIds != null
                    && x.ProductIds.Any(id => id == FirstProduct));
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("酷狗接待规则店铺识别失败，保持原流程：" + ex.Message, 5);
            }
            Shops[seller] = new ShopDetection
            {
                Match = matched,
                ExpiresUtc = DateTime.UtcNow.AddMinutes(matched ? 10 : 2)
            };
            return matched;
        }

        public static bool IsQuarantined(string seller, string id, string title, string answer)
        {
            if (!Applies(seller)) return false;
            if (QuarantineIds.Contains((id ?? string.Empty).Trim())) return true;
            var body = (title ?? string.Empty) + " " + (answer ?? string.Empty);
            return BadOutgoingClaim.IsMatch(body)
                || body.Contains("不要勾选“保留数据”")
                || body.Contains("恢复出厂设置后不要升级");
        }

        public static bool TryWaitForEvidence(string seller, string buyer, string question, out string reason)
        {
            reason = string.Empty;
            if (!Applies(seller)) return false;
            var input = (question ?? string.Empty).Trim();
            if (!PhoneOnly.IsMatch(input)) return false;
            var turns = ConversationContextStore.GetRecentTurns(seller, buyer, string.Empty, 24);
            var lastSeller = turns.LastOrDefault(x => x != null
                && x.Role == "assistant" && !x.Withdrawn && !string.IsNullOrWhiteSpace(x.Text));
            if (lastSeller == null || lastSeller.Timestamp == DateTime.MinValue
                || lastSeller.Timestamp < DateTime.Now.AddHours(-2)) return false;
            var instruction = lastSeller.Text ?? string.Empty;
            if (!PhotoInstruction.IsMatch(instruction)
                || (instruction.IndexOf("电视", StringComparison.Ordinal) < 0
                    && instruction.IndexOf("酷狗", StringComparison.Ordinal) < 0
                    && instruction.IndexOf("账号", StringComparison.Ordinal) < 0)
                || instruction.IndexOf("手机号", StringComparison.Ordinal) >= 0) return false;
            reason = "等待设备酷狗账号界面截图；手机号并非当前步骤需要的证据";
            Audit(seller, buyer, "WAIT", "awaiting_account_screenshot", "phone_only", "kg_wait_photo");
            return true;
        }

        public static bool BlockOutgoing(string seller, string buyer, string question, string answer, out string reason)
        {
            reason = string.Empty;
            if (!Applies(seller)) return false;
            if (BadOutgoingClaim.IsMatch(answer ?? string.Empty))
                reason = "命中已隔离的错误跨端/店铺/退款表述";
            else if (IsScreenshotQuestion(question)
                && Regex.IsMatch(answer ?? string.Empty, @"^.{0,12}(包含酷狗K歌会员|包含.*?会员的)", RegexOptions.IgnoreCase))
                reason = "买家正在核验设备截图，禁止以会员权益描述答非所问";
            if (reason.Length == 0) return false;
            Audit(seller, buyer, "SUPPRESS", "reply_validation", reason, "kg_outgoing");
            return true;
        }

        internal static bool IsScreenshotQuestion(string question)
        {
            var value = (question ?? string.Empty).Replace(" ", "").Trim();
            return value.IndexOf("是这个", StringComparison.Ordinal) >= 0
                || value.IndexOf("这张", StringComparison.Ordinal) >= 0
                || value.IndexOf("这个界面", StringComparison.Ordinal) >= 0
                || value.IndexOf("这页面", StringComparison.Ordinal) >= 0
                || value.IndexOf("这是吗", StringComparison.Ordinal) >= 0;
        }

        // Only trusted, observable attributes of the current image may choose a branch.
        // No "eligible" or recharge permission is inferred from a brand alone.
        public static bool TryResolveVisual(
            string seller, string buyer, string question, string summary, string ocr,
            out string answer, out string reason)
        {
            answer = string.Empty;
            reason = string.Empty;
            if (!Applies(seller)) return false;
            var evidence = ((summary ?? string.Empty) + " " + (ocr ?? string.Empty));
            if (evidence.Trim().Length == 0) return false;
            var compact = Regex.Replace(evidence, @"\s+", "").ToLowerInvariant();
            var official = compact.Contains("官方酷狗app") || compact.Contains("酷狗官方app")
                || compact.Contains("自行安装") || compact.Contains("独立安装");
            var tcl = compact.Contains("tcl") || compact.Contains("雷鸟");
            var builtIn = compact.Contains("自带音乐") || compact.Contains("系统音乐")
                || compact.Contains("聚好看") || compact.Contains("内置音乐");
            var otherProvider = compact.Contains("绑定qq") || compact.Contains("qq音乐账号")
                || compact.Contains("qq音乐资源");
            var kugouAccount = compact.Contains("已绑定酷狗")
                || compact.Contains("酷狗账号已绑定")
                || (compact.Contains("绑定成功") && compact.Contains("酷狗"));
            if ((compact.Contains("海信") || compact.Contains("vidaa")
                || compact.Contains("聚好看") || compact.Contains("东芝")) && builtIn && !official)
            {
                answer = "您当前使用的是电视系统自带的音乐栏目，这类栏目本店暂不提供充值服务。如果已经下单，可以先申请退款；如果是另行安装的酷狗官方应用，请把应用及登录页面发来再确认。";
                reason = "hisense_builtin_decline";
            }
            else if (tcl && otherProvider && !official)
            {
                answer = "从这张图看，当前音乐栏目使用的是 QQ 音乐账号体系，无法在这个栏目使用本店的酷狗会员。如果您另外安装了酷狗官方应用，可将那个应用的账号页面发来确认。";
                reason = "tcl_qq_reject";
            }
            else if (tcl && !official && kugouAccount)
            {
                answer = "收到您发来的绑定界面。图片上显示了酷狗账号绑定信息，我再结合订单核对；确认前暂不安排充值。";
                reason = "tcl_binding_seen_pending_order_review";
            }
            else if (tcl && !official && !kugouAccount)
            {
                answer = "这张图还不能确认已绑定您自己的酷狗账号。请按 TCL/雷鸟电视教程进入“我的账号”绑定页面，确认是酷狗资源，并使用您本人能再次登录的电视账号绑定酷狗账号，完成后拍照发给我确认：" + TClGuideUrl;
                reason = "tcl_need_verified_binding";
            }
            else if (!official && (compact.Contains("华数") || compact.Contains("iptv")
                || compact.Contains("点歌机") || compact.Contains("话费开通")))
            {
                answer = "您这张图显示的系统目前不属于本店支持的酷狗账号充值范围。如果您使用的是另行安装的酷狗官方应用，可以把官方应用的账号页面发来单独确认。";
                reason = "unsupported_platform";
            }
            else if (compact.Contains("小米") && compact.Contains("自带k歌") && !official)
            {
                answer = "小米电视系统自带的 K 歌栏目不支持本店酷狗会员。若您是使用独立安装的酷狗官方应用，请再发该应用的登录界面核验。";
                reason = "xiaomi_builtin_karaoke";
            }
            if (answer.Length == 0) return false;
            var newStage = reason == "tcl_need_verified_binding"
                ? "awaiting_binding_proof"
                : reason == "tcl_binding_seen_pending_order_review"
                    ? "awaiting_order_review" : "device_rejected";
            Audit(seller, buyer, "REPLY", newStage, reason, "kg_visual");
            return true;
        }

        public static string BuildPromptAddon(string seller, string buyer)
        {
            if (!Applies(seller)) return string.Empty;
            var knownStage = GetRecentStage(seller, buyer);
            return "\n\n【本店酷狗会员接待政策，必须遵守】"
                + "\n最近经过规则决策的接待阶段：" + (knownStage.Length == 0 ? "未确认" : knownStage) + "。该阶段不是订单实时状态，必须用最新消息或订单证据重新验证。"
                + "\n本店两商品1057505313937、1066362196568对应同一酷狗超级会员权益；按买家实际使用场景如实解释跨端权益，不得说手机端不通用；准确的价格、库存、会员类型仍需以实际订单及商品为准。"
                + "\n卖家店铺先核验设备，再执行充值；已付款≠已核验≠已绑定≠已充值。手机/电脑/平板官方酷狗应用无需电视截图。其他智能设备需以当前应用、资源提供方和账号体系核验。"
                + "\n海信/聚好看等系统自带音乐栏目按店铺经营政策暂不受理充值，独立安装的酷狗官方APP须单独核验。小米内置K歌不支持；自带听歌必须看开通会员页资源。"
                + "\nTCL/雷鸟内置音乐先确认酷狗而非QQ资源、本人可重复登录的电视账号，必要时解绑旧账号，然后绑定本人酷狗账号；没有已绑定证据不得批准充值。可用的店铺教程：" + TClGuideUrl
                + "\n图片提问‘是这个吗’先回答当前图片是否为指定账号界面；无法确认就要求当前步骤证据，禁止机械回答会员包含哪些权益。人工已经处理时不抢答。"
                + "\n不得擅自声称订单已充值/已到账，不能仅凭品牌或图片相似性确认可用；不能建议恢复出厂删除数据、拒绝升级或一律拒绝退款；有关退款和验证码由真实订单及人工/平台政策处理。\n";
        }


        // Restore a short-lived stage from the privacy-minimized per-shop decision journal.
        // Explicitly ignore send validation and general FAQ audit entries; these are not
        // business state transitions. The real order and account evidence remain external.
        internal static string GetRecentStage(string seller, string buyer)
        {
            if (!Applies(seller)) return string.Empty;
            try
            {
                var shop = ShopContextLocator.ResolveRuntimeBySellerNick(seller);
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
                var shop = ShopContextLocator.ResolveRuntimeBySellerNick(seller);
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
                    List<ReceptionAuditEntry> entries;
                    try { entries = JsonConvert.DeserializeObject<List<ReceptionAuditEntry>>(raw) ?? new List<ReceptionAuditEntry>(); }
                    catch { entries = new List<ReceptionAuditEntry>(); }
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
