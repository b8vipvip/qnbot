using Bot.AssistWindow.Widget.Robot;
using Bot.Automation.ChatDeskNs;
using BotLib;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace Bot.ChromeNs
{
    /// <summary>
    /// CtlRobot 历史上按 seller#buyer 精确字符串保存消息卡片。
    /// 当当前会话显示名与 receiveNewMsg 内部 nick 不同时，把两组 key 指向同一列表，
    /// 避免千牛里已经收到图片而 Bot 右侧仍显示“暂无聊天内容”。
    /// </summary>
    internal static class BuyerIdentityAliasUiBridge
    {
        private static Timer _timer;
        private static int _started;
        private static int _refreshQueued;
        private static FieldInfo _conversationField;
        private static string _lastSignature = string.Empty;

        public static void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0) return;
            _conversationField = typeof(CtlRobot).GetField(
                "buyerConversations",
                BindingFlags.Instance | BindingFlags.NonPublic);
            _timer = new Timer(_ => QueueRefreshOnUiThread(), null, 500, 500);
        }

        private static void QueueRefreshOnUiThread()
        {
            try
            {
                var app = Application.Current;
                if (app == null || app.Dispatcher == null || app.Dispatcher.HasShutdownStarted) return;

                if (app.Dispatcher.CheckAccess())
                {
                    RefreshOnUiThread();
                    return;
                }

                // Timer 每 500ms 触发一次；只允许积压一个 UI 刷新，避免窗口繁忙时队列膨胀。
                if (Interlocked.Exchange(ref _refreshQueued, 1) != 0) return;
                try
                {
                    app.Dispatcher.BeginInvoke(
                        DispatcherPriority.Background,
                        new Action(() =>
                        {
                            try
                            {
                                RefreshOnUiThread();
                            }
                            catch (Exception ex)
                            {
                                Log.ErrorWithMaxCount("合并Bot买家昵称别名消息失败：" + ex.Message, 10);
                            }
                            finally
                            {
                                Interlocked.Exchange(ref _refreshQueued, 0);
                            }
                        }));
                }
                catch
                {
                    Interlocked.Exchange(ref _refreshQueued, 0);
                    throw;
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("调度Bot买家昵称别名UI刷新失败：" + ex.Message, 10);
            }
        }

        private static void RefreshOnUiThread()
        {
            var qn = QN.CurQN;
            var desk = Desk.Inst;
            var ctl = desk == null ? null : desk.CtlRobot;
            if (qn == null || qn.Seller == null || qn.Buyer == null || ctl == null || _conversationField == null) return;
            if (!ctl.Dispatcher.CheckAccess())
            {
                throw new InvalidOperationException("买家昵称别名UI刷新未在CtlRobot所属Dispatcher执行。");
            }

            var seller = (qn.Seller.Nick ?? string.Empty).Trim();
            var visibleBuyer = (qn.Buyer.Nick ?? string.Empty).Trim();
            if (seller.Length == 0 || visibleBuyer.Length == 0) return;

            var internalNick = BuyerIdentityAliasService.ResolveInternalNick(seller, visibleBuyer);
            var display = BuyerIdentityAliasService.ResolveDisplay(seller, visibleBuyer);
            if (internalNick.Length == 0 || display.Length == 0 || string.Equals(internalNick, display, StringComparison.OrdinalIgnoreCase)) return;

            var map = _conversationField.GetValue(ctl) as ConcurrentDictionary<string, List<CtlConversation>>;
            if (map == null) return;
            var internalKey = seller + "#" + internalNick;
            var displayKey = seller + "#" + display;

            List<CtlConversation> internalList;
            List<CtlConversation> displayList;
            map.TryGetValue(internalKey, out internalList);
            map.TryGetValue(displayKey, out displayList);
            if ((internalList == null || internalList.Count == 0) && (displayList == null || displayList.Count == 0)) return;

            // After the initial alias merge both dictionary keys reference the
            // same UI list. The old 500-ms timer still created another list
            // and performed an O(n²) Any/ReferenceEquals dedupe on every tick.
            // Detect the already-shared identity *before* constructing controls
            // or allocating an intermediate merged list.
            if (internalList != null && ReferenceEquals(internalList, displayList))
            {
                var existingSignature = seller + "#" + visibleBuyer + "#"
                    + internalNick + "#" + display + "#" + internalList.Count;
                if (string.Equals(existingSignature, _lastSignature, StringComparison.Ordinal))
                    return;
                _lastSignature = existingSignature;
                ctl.ReShowAfterQNChange();
                return;
            }

            var merged = new List<CtlConversation>();
            var unique = new HashSet<CtlConversation>();
            foreach (var item in displayList ?? Enumerable.Empty<CtlConversation>())
            {
                if (item != null && unique.Add(item)) merged.Add(item);
            }
            foreach (var item in internalList ?? Enumerable.Empty<CtlConversation>())
            {
                if (item != null && unique.Add(item)) merged.Add(item);
            }
            map[internalKey] = merged;
            map[displayKey] = merged;

            var signature = seller + "#" + visibleBuyer + "#" + internalNick + "#" + display + "#" + merged.Count;
            if (string.Equals(signature, _lastSignature, StringComparison.Ordinal)) return;
            _lastSignature = signature;
            ctl.ReShowAfterQNChange();
            Log.Info("Bot消息列表已合并买家昵称别名: seller=" + seller
                + ", internal=" + internalNick + ", display=" + display + ", cards=" + merged.Count);
        }
    }
}
