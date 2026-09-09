using BotLib;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    internal static class VisionFollowUpContextPipeline
    {
        private const int FollowUpWindowSeconds = 45;
        private const int CaptionWindowSeconds = 15;
        private const int SourceClockSkewToleranceSeconds = 15;

        private sealed class RecentVisionContext
        {
            public BuyerMessageBurstItem Item;
            public DateTime ObservedAt;
        }

        private static readonly ConcurrentDictionary<int, Func<BuyerMessageBurstLease, Task>> InstalledWrappers = new ConcurrentDictionary<int, Func<BuyerMessageBurstLease, Task>>();
        private static readonly ConcurrentDictionary<string, RecentVisionContext> RecentVision = new ConcurrentDictionary<string, RecentVisionContext>(StringComparer.Ordinal);
        private static Timer _patchTimer;
        private static int _initialized;

        public static void Initialize()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
            PatchExisting();
            _patchTimer = new Timer(_ => PatchExisting(), null, 250, 500);
            Log.Info("图片指代续问上下文管线已启动：图片后的‘这个/这种/这类能用吗’会继续走视觉理解；手机号、充值状态、人工/投诉等独立短消息不再误绑旧图片。");
        }

        private static void PatchExisting()
        {
            try
            {
                QN[] qns;
                try { qns = QN.QNSet == null ? new QN[0] : QN.QNSet.ToArray(); }
                catch { return; }
                var coordinatorField = typeof(QN).GetField("_buyerMessageBurstCoordinator", BindingFlags.Instance | BindingFlags.NonPublic);
                var handlerField = typeof(BuyerMessageBurstCoordinator).GetField("_handler", BindingFlags.Instance | BindingFlags.NonPublic);
                if (coordinatorField == null || handlerField == null) return;
                foreach (var qn in qns)
                {
                    if (qn == null) continue;
                    var coordinator = coordinatorField.GetValue(qn) as BuyerMessageBurstCoordinator;
                    if (coordinator == null) continue;
                    var key = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(coordinator);
                    var current = handlerField.GetValue(coordinator) as Func<BuyerMessageBurstLease, Task>;
                    if (current == null) continue;
                    Func<BuyerMessageBurstLease, Task> installed;
                    if (InstalledWrappers.TryGetValue(key, out installed) && ReferenceEquals(current, installed)) continue;
                    var next = current;
                    Func<BuyerMessageBurstLease, Task> wrapped = lease => HandleAsync(next, lease);
                    handlerField.SetValue(coordinator, wrapped);
                    InstalledWrappers[key] = wrapped;
                    Log.Info("已为客服实例启用图片指代续问上下文: seller=" + (qn.Seller == null ? string.Empty : qn.Seller.Nick));
                }
                CleanupExpired();
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("安装图片指代续问上下文失败，将继续使用原消息流程：" + ex.Message, 10);
            }
        }

        private static async Task HandleAsync(Func<BuyerMessageBurstLease, Task> next, BuyerMessageBurstLease lease)
        {
            if (next == null) return;
            var burst = lease == null ? null : lease.Burst;
            if (burst == null || burst.Items == null || burst.Items.Count == 0) { await next(lease); return; }
            var conversationKey = Key(burst.SellerNick, burst.BuyerNick);
            var vision = burst.LatestVisionItem;
            if (vision != null && vision.Message != null) { Remember(conversationKey, vision); await next(lease); return; }
            RecentVisionContext recent;
            if (!RecentVision.TryGetValue(conversationKey, out recent) || recent == null || recent.Item == null || recent.Item.Message == null) { await next(lease); return; }

            var latestAt = burst.Items.Where(x => x != null).Select(x => x.ReceivedAt == DateTime.MinValue ? DateTime.Now : x.ReceivedAt).DefaultIfEmpty(DateTime.Now).Max();
            var elapsed = latestAt - recent.ObservedAt;
            if (elapsed < TimeSpan.Zero && elapsed >= TimeSpan.FromSeconds(-SourceClockSkewToleranceSeconds))
            {
                Log.Info("最近图片上下文检测到可容忍的消息时间逆序，按同轮续问处理: seller=" + burst.SellerNick + ", buyer=" + burst.BuyerNick + ", elapsedMs=" + (long)elapsed.TotalMilliseconds);
                elapsed = TimeSpan.Zero;
            }
            if (elapsed < TimeSpan.Zero || elapsed > TimeSpan.FromSeconds(FollowUpWindowSeconds))
            {
                RecentVisionContext expired; RecentVision.TryRemove(conversationKey, out expired);
                Log.Info("最近图片上下文已过期，文字按普通消息处理: seller=" + burst.SellerNick + ", buyer=" + burst.BuyerNick + ", elapsedMs=" + (long)elapsed.TotalMilliseconds);
                await next(lease); return;
            }

            var question = burst.CombinedQuestion ?? string.Empty;
            var referential = IsVisionReferentialFollowUp(question);
            var likelyCaption = elapsed <= TimeSpan.FromSeconds(CaptionWindowSeconds) && IsLikelyImageCaption(question);
            if (!referential && !likelyCaption)
            {
                RecentVisionContext ignored; RecentVision.TryRemove(conversationKey, out ignored);
                Log.Info("最近图片未与后续文字合并: seller=" + burst.SellerNick + ", buyer=" + burst.BuyerNick + ", elapsedMs=" + Math.Max(0, (long)elapsed.TotalMilliseconds) + ", referential=false, captionWindow=" + CaptionWindowSeconds + ", question=" + SafeLog(question, 100));
                await next(lease); return;
            }

            var items = new List<BuyerMessageBurstItem> { CloneVisionItem(recent.Item) };
            items.AddRange(burst.Items.Where(x => x != null));
            var combinedBurst = new BuyerMessageBurst(burst.SellerNick, burst.BuyerNick, items, burst.Version);
            var combinedLease = new BuyerMessageBurstLease(combinedBurst, () => lease.IsCurrent, ResolveSessionAgent(lease));
            Log.Info("图片指代续问已重新绑定最近图片: seller=" + burst.SellerNick + ", buyer=" + burst.BuyerNick + ", elapsedMs=" + Math.Max(0, (long)elapsed.TotalMilliseconds) + ", reason=" + (referential ? "图片指代" : "图片说明文字") + ", question=" + SafeLog(question, 100));
            await next(combinedLease);
        }

        private static BuyerSessionAgent ResolveSessionAgent(BuyerMessageBurstLease lease)
        {
            if (lease == null) return null;
            try
            {
                var field = typeof(BuyerMessageBurstLease).GetField("_sessionAgent", BindingFlags.Instance | BindingFlags.NonPublic);
                return field == null ? null : field.GetValue(lease) as BuyerSessionAgent;
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("图片续问恢复generation取消令牌失败，将退回IsCurrent保护: " + ex.Message, 10);
                return null;
            }
        }

        internal static bool IsVisionReferentialFollowUp(string text)
        {
            var compact = Normalize(text);
            if (string.IsNullOrWhiteSpace(compact) || compact.Length > 36) return false;
            if (compact == "对吗" || compact == "是吗" || compact == "这个吗" || compact == "是这个吗" || compact == "就是这个吗" || compact == "是不是这个" || compact == "是不是这个吗" || compact == "这个对吗" || compact == "这张吗" || compact == "是这张吗" || compact == "这里吗" || compact == "是这里吗" || compact == "这样吗" || compact == "是这样吗" || compact == "这种能使用吗" || compact == "这种能用吗" || compact == "这类能使用吗" || compact == "这类能用吗") return true;
            var hasReference = compact.Contains("这个") || compact.Contains("这种") || compact.Contains("这类") || compact.Contains("这款") || compact.Contains("这张") || compact.Contains("这里") || compact.Contains("这样") || compact.Contains("图里") || compact.Contains("图片里") || compact.Contains("上面") || compact.Contains("界面") || compact.Contains("页面") || compact.Contains("设备") || compact.Contains("软件");
            var asksConfirmation = compact.Contains("吗") || compact.Contains("么") || compact.Contains("是不是") || compact.Contains("对不对") || compact.Contains("可以") || compact.Contains("行不行") || compact.Contains("能用") || compact.Contains("能使用") || compact.Contains("支持");
            return hasReference && asksConfirmation;
        }

        internal static bool IsLikelyImageCaption(string text)
        {
            var compact = Normalize(text);
            if (string.IsNullOrWhiteSpace(compact) || compact.Length > 80) return false;
            // Field incident 2026-09-09: phone/account identifiers and recharge/status messages arrived
            // inside the old 15s caption window and were rebound to the previous screenshot. Captions
            // are now opt-in and require explicit visual/descriptive evidence.
            if (Regex.IsMatch(compact, @"^\d{5,20}$")) return false;
            if (Regex.IsMatch(compact, @"(充值|充这个|充没|充上|到账|付款|发货|退款|退货|订单|手机号|号码|人工|客服|投诉|不满意|有病|垃圾|骗子|转人工)")) return false;
            if (Regex.IsMatch(compact, @"^[?？!！.。]+$")) return false;
            return compact.Contains("这是") || compact.Contains("这个是") || compact.Contains("图里") || compact.Contains("图片") || compact.Contains("截图") || compact.Contains("页面") || compact.Contains("界面") || compact.Contains("这里显示") || compact.Contains("上面显示") || compact.Contains("显示") || compact.Contains("提示") || compact.Contains("报错") || compact.Contains("型号") || compact.Contains("版本") || compact.Contains("设备") || compact.Contains("软件");
        }

        private static void Remember(string key, BuyerMessageBurstItem item)
        {
            RecentVision[key] = new RecentVisionContext { Item = CloneVisionItem(item), ObservedAt = item.ReceivedAt == DateTime.MinValue ? DateTime.Now : item.ReceivedAt };
            CleanupExpired();
        }

        private static BuyerMessageBurstItem CloneVisionItem(BuyerMessageBurstItem source)
        {
            return new BuyerMessageBurstItem { SellerNick = source.SellerNick, BuyerNick = source.BuyerNick, MessageKey = source.MessageKey, DisplayText = source.DisplayText, Message = source.Message, SafetyDecision = source.SafetyDecision, VisionDecision = source.VisionDecision, SortValue = source.SortValue, ReceivedAt = source.ReceivedAt, SessionGeneration = source.SessionGeneration, SemanticContinuationContext = source.SemanticContinuationContext };
        }

        private static void CleanupExpired()
        {
            var cutoff = DateTime.Now.AddSeconds(-FollowUpWindowSeconds);
            foreach (var pair in RecentVision.Where(x => x.Value == null || x.Value.ObservedAt < cutoff).ToList()) { RecentVisionContext ignored; RecentVision.TryRemove(pair.Key, out ignored); }
        }

        private static string Normalize(string value)
        {
            return Regex.Replace((value ?? string.Empty).Trim().ToLowerInvariant(), @"[\s，。！？、；：,.!?:;\-—_()（）\[\]【】]+", string.Empty);
        }

        private static string SafeLog(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }

        private static string Key(string seller, string buyer)
        {
            return (seller ?? string.Empty).Trim() + "#" + (buyer ?? string.Empty).Trim();
        }
    }
}