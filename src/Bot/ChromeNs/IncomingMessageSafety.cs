using Bot.ChatRecord;
using DbEntity;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Bot.ChromeNs
{
    internal sealed class IncomingMessageDecision
    {
        public bool ShouldCallAi { get; set; }
        public string MessageLabel { get; set; }
        public string Note { get; set; }
    }

    internal sealed class IncomingMessageDeduplicator
    {
        private readonly object _sync = new object();
        private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);
        private readonly Queue<string> _order = new Queue<string>();
        private readonly int _capacity;

        public IncomingMessageDeduplicator(int capacity)
        {
            _capacity = Math.Max(100, capacity);
        }

        public bool TryAccept(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return true;
            lock (_sync)
            {
                if (_seen.Contains(key)) return false;
                _seen.Add(key);
                _order.Enqueue(key);
                while (_order.Count > _capacity)
                {
                    var old = _order.Dequeue();
                    _seen.Remove(old);
                }
                return true;
            }
        }
    }

    internal static class NonBuyerConversationGuard
    {
        private static readonly string[] StrongIdentityMarkers =
        {
            "(行业小二)", "（行业小二）", "行业小二",
            "(平台小二)", "（平台小二）", "平台小二",
            "(淘宝小二)", "（淘宝小二）", "淘宝小二",
            "(天猫小二)", "（天猫小二）", "天猫小二",
            "(阿里小二)", "（阿里小二）", "阿里小二",
            "(官方小二)", "（官方小二）", "官方小二",
            "(服务商)", "（服务商）", "官方服务商", "服务商消息",
            "1688消息", "1688官方",
            "群聊消息", "群消息",
            "淘宝系统", "千牛系统", "平台系统", "系统消息",
            "平台通知", "官方通知", "系统通知"
        };

        private static readonly string[] NonBuyerSourceTokens =
        {
            "cnalichn", "1688", "xiaoer", "serviceprovider", "service-provider",
            "service_provider", "group", "chatroom", "chat-room", "tribe", "qun",
            "official", "system", "platform-notify", "platform_notify"
        };

        public static bool ShouldBlockConversation(LocalUser seller, Conversation conversation, out string reason)
        {
            reason = string.Empty;
            if (conversation == null) return false;

            var sellerNick = seller == null ? string.Empty : Normalize(seller.Nick);
            var sellerTargetId = seller == null ? string.Empty : Normalize(seller.TargetId);
            var candidateNick = Normalize(conversation.Nick);
            var candidateTargetId = Normalize(conversation.TargetId);

            if (DirectOrderIdentityResolver.IdentityEquals(sellerNick, candidateNick)
                || SameNonEmpty(sellerTargetId, candidateTargetId))
            {
                reason = "self_identity";
                return true;
            }

            if (ContainsStrongIdentityMarker(Join(conversation.Nick, conversation.Display)))
            {
                reason = "non_buyer_identity_marker";
                return true;
            }

            if (IsNonBuyerSourceToken(conversation.TargetType)
                || IsNonBuyerSourceToken(conversation.Type)
                || IsNonBuyerSourceToken(conversation.ConversationType)
                || IsNonBuyerSourceToken(conversation.Scene)
                || IsNonBuyerSourceToken(conversation.Category)
                || IsNonBuyerSourceToken(conversation.Source)
                || IsNonBuyerSourceToken(conversation.Channel))
            {
                reason = "non_buyer_conversation_source";
                return true;
            }
            return false;
        }

        public static bool ShouldBlockIdentity(string sellerNick, string candidateNick, out string reason)
        {
            reason = string.Empty;
            sellerNick = Normalize(sellerNick);
            candidateNick = Normalize(candidateNick);
            if (DirectOrderIdentityResolver.IdentityEquals(sellerNick, candidateNick))
            {
                reason = "self_identity";
                return true;
            }
            if (ContainsStrongIdentityMarker(candidateNick))
            {
                reason = "non_buyer_identity_marker";
                return true;
            }
            return false;
        }

        public static bool ShouldBlockMessage(QNChatMessage message, string sellerNick, string messageText, out string reason)
        {
            reason = string.Empty;
            if (message == null) return false;

            sellerNick = Normalize(sellerNick);
            if (sellerNick.Length == 0 && message.loginid != null)
                sellerNick = Normalize(message.loginid.nick);

            var fromNick = message.fromid == null ? string.Empty : Normalize(message.fromid.nick);
            var fromDisplay = message.fromid == null ? string.Empty : Normalize(message.fromid.display);

            // Never classify seller echoes as non-buyer ingress; they are observational delivery/learning evidence.
            if (DirectOrderIdentityResolver.IdentityEquals(sellerNick, fromNick)) return false;

            if (message.fromid != null && IsNonBuyerSourceToken(message.fromid.targetType))
            {
                reason = "non_buyer_sender_type";
                return true;
            }
            if (message.toid != null && IsGroupSourceToken(message.toid.targetType))
            {
                reason = "group_conversation";
                return true;
            }
            if (ContainsStrongIdentityMarker(Join(fromNick, fromDisplay)))
            {
                reason = "non_buyer_sender_identity";
                return true;
            }
            if (ConversationContextStore.IsPlatformSystemTip(message, messageText))
            {
                reason = "platform_system_tip";
                return true;
            }

            var headerTitle = message.originalData == null || message.originalData.header == null
                ? string.Empty
                : Normalize(message.originalData.header.title);
            if (IsStrongSystemHeader(headerTitle))
            {
                reason = "platform_system_card";
                return true;
            }
            return false;
        }

        private static bool IsStrongSystemHeader(string value)
        {
            value = Normalize(value);
            if (value.Length == 0) return false;
            return value == "系统通知"
                || value == "平台通知"
                || value == "官方通知"
                || value == "淘宝通知"
                || value == "千牛通知"
                || value == "小二通知"
                || value == "服务商通知"
                || value.StartsWith("系统消息：", StringComparison.Ordinal)
                || value.StartsWith("平台消息：", StringComparison.Ordinal);
        }

        private static bool IsGroupSourceToken(string value)
        {
            var token = NormalizeToken(value);
            return token.Contains("group")
                || token.Contains("chatroom")
                || token.Contains("tribe")
                || token == "qun"
                || token.Contains("群聊");
        }

        private static bool IsNonBuyerSourceToken(string value)
        {
            var token = NormalizeToken(value);
            if (token.Length == 0) return false;
            foreach (var marker in NonBuyerSourceTokens)
            {
                if (token.Contains(NormalizeToken(marker))) return true;
            }
            return token.Contains("群聊")
                || token.Contains("小二")
                || token.Contains("服务商")
                || token.Contains("系统通知")
                || token.Contains("平台通知");
        }

        private static bool ContainsStrongIdentityMarker(string value)
        {
            value = Normalize(value);
            if (value.Length == 0) return false;
            foreach (var marker in StrongIdentityMarkers)
            {
                if (value.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private static string Join(params string[] values)
        {
            return string.Join("|", (values ?? new string[0]).Where(x => !string.IsNullOrWhiteSpace(x)));
        }

        private static bool SameNonEmpty(string left, string right)
        {
            return left.Length > 0 && right.Length > 0
                && string.Equals(left, right, StringComparison.Ordinal);
        }

        private static string Normalize(string value)
        {
            return (value ?? string.Empty).Trim();
        }

        private static string NormalizeToken(string value)
        {
            return Normalize(value).ToLowerInvariant().Replace(" ", string.Empty);
        }
    }

    internal static class IncomingMessageSafety
    {
        private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".heif" };
        private static readonly string[] VideoExtensions = { ".mp4", ".mov", ".avi", ".mkv", ".webm", ".m4v" };
        private static readonly string[] AudioExtensions = { ".amr", ".mp3", ".wav", ".m4a", ".aac", ".ogg" };

        public static IncomingMessageDecision Evaluate(
            QNChatMessage message,
            string messageText,
            DateTime safetyStartedAt,
            string shopKey = null,
            CDPClient browserClient = null)
        {
            string nonBuyerReason;
            var sellerNick = message == null || message.loginid == null ? string.Empty : message.loginid.nick;
            if (NonBuyerConversationGuard.ShouldBlockMessage(message, sellerNick, messageText, out nonBuyerReason))
            {
                return Skip("[非买家消息]", "已跳过：检测到小二/服务商/1688/群聊或平台系统消息；未调用AI，也未发送回复。 reason=" + nonBuyerReason);
            }

            // Refresh the same seller+buyer timeline before deciding how to answer. This loads
            // recent manual-agent questions as well as buyer replies such as phone/model/account IDs.
            ConversationContextStore.RefreshAndRecord(message, messageText, shopKey);

            // EvRecieveNewMessage subscribers run before the ordinary buyer-message loop. The
            // recharge service claims only explicit progress questions with a same-conversation
            // seller message containing “兑换码：...”. Once claimed, do not also start Smart Reply.
            string rechargeQueryNote;
            if (RechargeStatusAutoQueryService.TryConsumeHandled(
                message,
                messageText,
                out rechargeQueryNote,
                shopKey))
            {
                return Skip("[充值进度查询]", rechargeQueryNote);
            }

            DateTime messageTime;
            if (TryGetMessageTime(message, out messageTime) && messageTime < safetyStartedAt.AddSeconds(-8))
            {
                return Skip("历史消息", "已跳过：这是 Bot 启动前的历史或未读消息，未调用AI，也未发送给买家。");
            }

            if (ConversationContextStore.IsWithdrawalNotice(message, messageText))
            {
                // A buyer withdrawal must invalidate only the outgoing reply, not the already
                // started download/vision analysis. Mark the latest image so the withdrawal-aware
                // visual pipeline can suppress stale sending while preserving the local cache.
                VisionImageCacheService.MarkLatestBuyerImageWithdrawn(message, messageText, shopKey);
                return Skip("[撤回提示]", "已跳过：检测到消息撤回提示；图片若已收到将继续后台分析，但不会直接回复已撤回内容。");
            }

            if (ConversationContextStore.IsPlatformSystemTip(message, messageText))
            {
                return Skip("[淘宝系统提示]", "已跳过：这是淘宝/千牛自动生成的系统提示，未调用AI，也未发送给买家。");
            }

            // Product links and product cards use a local preset reply. ShouldCallAi remains true
            // only so the existing UI/send pipeline is reused; MyOpenAI returns before any HTTP call.
            if (ConversationContextStore.IsProductLink(message, messageText))
            {
                ConversationContextStore.RegisterProductLinkReply(message, messageText, shopKey);
                return new IncomingMessageDecision
                {
                    ShouldCallAi = true,
                    MessageLabel = string.IsNullOrWhiteSpace(messageText) ? "[商品链接]" : messageText,
                    Note = "商品链接使用本地预设随机回复，不调用AI接口。"
                };
            }

            var unsupportedType = DetectUnsupportedType(message, messageText);
            if (string.Equals(unsupportedType, "图片", StringComparison.Ordinal))
            {
                // Start downloading immediately, before the burst quiet delay. The complete image
                // is written under the persistent user-data directory and remains available even
                // if the buyer withdraws the remote message a moment later.
                VisionImageCacheService.Prime(message, messageText, shopKey, browserClient);
            }
            if (!string.IsNullOrWhiteSpace(unsupportedType))
            {
                return Skip("[" + unsupportedType + "]", "已跳过：收到" + unsupportedType + "消息，当前版本未启用对应内容理解能力；未调用AI，也未发送给买家。");
            }

            if (string.IsNullOrWhiteSpace(messageText))
            {
                return Skip("[空白或未知消息]", "已跳过：消息内容为空或类型无法识别，未调用AI，也未发送给买家。");
            }

            return new IncomingMessageDecision
            {
                ShouldCallAi = true,
                MessageLabel = messageText,
                Note = string.Empty
            };
        }

        public static string GetDisplayText(QNChatMessage message, string messageText)
        {
            if (ConversationContextStore.IsProductLink(message, messageText))
            {
                return string.IsNullOrWhiteSpace(messageText) ? "[商品链接]" : messageText.Trim();
            }
            var unsupportedType = DetectUnsupportedType(message, messageText);
            if (string.Equals(unsupportedType, "图片", StringComparison.Ordinal))
                return PreserveImageCaption(messageText);
            if (!string.IsNullOrWhiteSpace(unsupportedType)) return "[" + unsupportedType + "]";
            var text = (messageText ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(text) ? "[空白或未知消息]" : text;
        }

        private static string PreserveImageCaption(string value)
        {
            var caption = (value ?? string.Empty).Trim();
            if (caption == "[图片]" || caption == "图片消息" || caption == "[image]")
                return "[图片]";

            // Keep buyer-authored text when the same message also carries an image.
            // Strip only standalone transport placeholders, never words inside a sentence.
            caption = caption.Replace("[图片]", " ").Replace("[image]", " ");
            var words = caption.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(x => !string.Equals(x, "图片消息", StringComparison.OrdinalIgnoreCase));
            caption = string.Join(" ", words).Trim();
            return string.IsNullOrWhiteSpace(caption) ? "[图片]" : caption + " [图片]";
        }

        public static bool IsMediaPlaceholder(string value)
        {
            value = (value ?? string.Empty).Trim();
            return value == "[图片]"
                || value == "[视频]"
                || value == "[语音]"
                || value == "[表情]"
                || value == "[文件]"
                || value == "[位置]";
        }

        public static string BuildMessageKey(QNChatMessage message, string messageText)
        {
            if (message == null) return string.Empty;
            if (message.mcode != null && (!string.IsNullOrWhiteSpace(message.mcode.clientId) || !string.IsNullOrWhiteSpace(message.mcode.messageId)))
            {
                return "mcode:" + (message.mcode.clientId ?? string.Empty) + ":" + (message.mcode.messageId ?? string.Empty);
            }
            if (message.ext != null && message.ext.ww_msgid != 0)
            {
                return "ww:" + message.ext.ww_msgid;
            }
            return string.Join("|", new[]
            {
                message.cid == null ? string.Empty : message.cid.ccode,
                message.fromid == null ? string.Empty : message.fromid.nick,
                message.toid == null ? string.Empty : message.toid.nick,
                message.sendTime ?? string.Empty,
                message.sortTimeMicrosecond ?? string.Empty,
                message.templateId.ToString(CultureInfo.InvariantCulture),
                messageText ?? string.Empty,
                message.originalData == null ? string.Empty : (message.originalData.fileId ?? string.Empty)
            });
        }

        public static long GetSortValue(QNChatMessage message)
        {
            if (message == null) return 0;
            DateTime time;
            if (TryGetMessageTime(message, out time)) return time.Ticks;
            long raw;
            if (long.TryParse(message.sortTimeMicrosecond, out raw)) return raw;
            return 0;
        }

        private static IncomingMessageDecision Skip(string label, string note)
        {
            return new IncomingMessageDecision { ShouldCallAi = false, MessageLabel = label, Note = note };
        }

        internal static bool IsImageMessage(QNChatMessage message, string messageText)
        {
            if (message == null) return false;
            try
            {
                if (message.HasImage) return true;
            }
            catch { }
            return string.Equals(
                DetectUnsupportedType(message, messageText),
                "图片",
                StringComparison.Ordinal);
        }

        private static string DetectUnsupportedType(QNChatMessage message, string messageText)
        {
            if (message == null) return "未知类型";
            var original = message.originalData;
            var fileId = original == null ? string.Empty : (original.fileId ?? string.Empty);
            var url = original == null ? string.Empty : (original.url ?? string.Empty);
            var headerSummary = original == null || original.header == null ? string.Empty : (original.header.summary ?? string.Empty);
            var headerTitle = original == null || original.header == null ? string.Empty : (original.header.title ?? string.Empty);
            var combined = ((messageText ?? string.Empty) + " " + (message.summary ?? string.Empty)
                + " " + headerSummary + " " + headerTitle).Trim().ToLowerInvariant();
            var imageMetadata = (headerSummary + " " + headerTitle).Trim().ToLowerInvariant();

            // Do not classify ordinary buyer text such as "这个图片可以吗" as an
            // attachment. Image detection requires an image file/url, image metadata,
            // or an explicit platform media placeholder.
            if (HasExtension(fileId, ImageExtensions) || HasExtension(url, ImageExtensions)
                || ContainsMarker(imageMetadata, "图片", "image", "photo")
                || IsExplicitImagePlaceholder(messageText)
                || IsExplicitImagePlaceholder(message.summary)) return "图片";
            if (HasExtension(fileId, VideoExtensions) || HasExtension(url, VideoExtensions) || ContainsMarker(combined, "视频", "video")) return "视频";
            if (HasExtension(fileId, AudioExtensions) || HasExtension(url, AudioExtensions) || ContainsMarker(combined, "语音", "音频", "voice", "audio")) return "语音";
            if (ContainsMarker(combined, "表情", "emoji", "emotion", "face")
                || combined.Contains("发送了一个表情")
                || combined.Contains("动态表情")) return "表情";
            if (ContainsMarker(combined, "位置", "定位", "location")) return "位置";
            if (ContainsMarker(combined, "文件", "附件", "file")) return "文件";
            if (!string.IsNullOrWhiteSpace(fileId)) return "文件";
            return string.Empty;
        }

        private static bool IsExplicitImagePlaceholder(string value)
        {
            value = (value ?? string.Empty).Trim().ToLowerInvariant();
            return value == "[图片]" || value == "图片消息"
                || value == "[image]";
        }

        private static bool ContainsMarker(string text, params string[] markers)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            foreach (var marker in markers)
            {
                if (text == "[" + marker + "]" || text == marker || text.Contains("[" + marker + "]")) return true;
            }
            return false;
        }

        private static bool HasExtension(string value, IEnumerable<string> extensions)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var clean = value.Split('?', '#')[0].ToLowerInvariant();
            return extensions.Any(clean.EndsWith);
        }

        internal static bool TryGetMessageTime(QNChatMessage message, out DateTime localTime)
        {
            localTime = DateTime.MinValue;
            if (message == null) return false;
            if (TryParseTimeValue(message.sendTime, out localTime)) return true;
            return TryParseTimeValue(message.sortTimeMicrosecond, out localTime);
        }

        private static bool TryParseTimeValue(string value, out DateTime localTime)
        {
            localTime = DateTime.MinValue;
            if (string.IsNullOrWhiteSpace(value)) return false;
            long raw;
            if (long.TryParse(value.Trim(), out raw))
            {
                try
                {
                    if (raw > 1000000000000000L) localTime = DateTimeOffset.FromUnixTimeMilliseconds(raw / 1000L).LocalDateTime;
                    else if (raw > 100000000000L) localTime = DateTimeOffset.FromUnixTimeMilliseconds(raw).LocalDateTime;
                    else if (raw > 1000000000L) localTime = DateTimeOffset.FromUnixTimeSeconds(raw).LocalDateTime;
                    if (localTime != DateTime.MinValue) return true;
                }
                catch
                {
                }
            }

            DateTimeOffset dto;
            if (DateTimeOffset.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out dto)
                || DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dto))
            {
                localTime = dto.LocalDateTime;
                return true;
            }
            return false;
        }
    }
}
