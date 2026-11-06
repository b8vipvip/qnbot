using Bot.ChromeNs;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace Bot.AssistWindow.Widget.Robot
{
    public partial class CtlRobot
    {
        private readonly ConcurrentDictionary<string, byte> _historyLoadedKeys =
            new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        private DispatcherTimer _historyRestoreTimer;

        static CtlRobot()
        {
            EventManager.RegisterClassHandler(
                typeof(CtlRobot),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(CtlRobotHistory_ClassLoaded),
                true);
            EventManager.RegisterClassHandler(
                typeof(CtlRobot),
                FrameworkElement.UnloadedEvent,
                new RoutedEventHandler(CtlRobotHistory_ClassUnloaded),
                true);
        }

        private static void CtlRobotHistory_ClassLoaded(object sender, RoutedEventArgs e)
        {
            var ctl = sender as CtlRobot;
            if (ctl != null) ctl.StartHistoryRestoreMonitor();
        }

        private static void CtlRobotHistory_ClassUnloaded(object sender, RoutedEventArgs e)
        {
            var ctl = sender as CtlRobot;
            if (ctl != null && ctl._historyRestoreTimer != null)
            {
                ctl._historyRestoreTimer.Stop();
            }
        }

        private void StartHistoryRestoreMonitor()
        {
            BotConversationHistoryStore.Initialize();
            if (_historyRestoreTimer == null)
            {
                _historyRestoreTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(400)
                };
                _historyRestoreTimer.Tick += (s, args) => EnsureCurrentConversationHistoryLoaded();
            }
            _historyRestoreTimer.Start();
            Dispatcher.BeginInvoke(new Action(EnsureCurrentConversationHistoryLoaded));
        }

        private void EnsureCurrentConversationHistoryLoaded()
        {
            if (buyerConversations == null) return;
            var qn = QN.CurQN;
            if (qn == null || qn.Seller == null || qn.Buyer == null) return;

            var seller = (qn.Seller.Nick ?? string.Empty).Trim();
            var buyer = (qn.Buyer.Nick ?? string.Empty).Trim();
            if (seller.Length == 0 || buyer.Length == 0) return;

            var key = string.Format("{0}#{1}", seller, buyer);
            if (!_historyLoadedKeys.TryAdd(key, 0)) return;

            try
            {
                var records = BotConversationHistoryStore.LoadRecent(
                    seller,
                    buyer,
                    BotConversationHistoryStore.DefaultLoadCount);

                List<CtlConversation> conversations;
                if (!buyerConversations.TryGetValue(key, out conversations) || conversations == null)
                {
                    conversations = new List<CtlConversation>();
                }

                var knownIds = new HashSet<string>(
                    conversations
                        .Where(x => x != null && !string.IsNullOrWhiteSpace(x.HistoryId))
                        .Select(x => x.HistoryId),
                    StringComparer.Ordinal);

                foreach (var record in records)
                {
                    if (record == null || knownIds.Contains(record.EntityId)) continue;
                    var ctl = CtlConversation.CreateFromHistory(record);
                    if (ctl == null) continue;
                    ctl.ResendRequested += CtlConversation_ResendRequested;
                    ctl.EditRequested += CtlConversation_EditRequested;
                    conversations.Add(ctl);
                    knownIds.Add(ctl.HistoryId);
                }

                conversations = conversations
                    .Where(x => x != null)
                    .OrderBy(x => x.HistorySortTicks)
                    .ToList();
                buyerConversations.AddOrUpdate(key, conversations, (k, old) => conversations);
                PruneConversationUiCache(key);

                var current = QN.CurQN;
                if (current != null && current.Seller != null && current.Buyer != null
                    && string.Equals(current.Seller.Nick, seller, StringComparison.Ordinal)
                    && string.Equals(current.Buyer.Nick, buyer, StringComparison.Ordinal))
                {
                    RefreshConversations();
                }
            }
            catch (Exception ex)
            {
                byte ignored;
                _historyLoadedKeys.TryRemove(key, out ignored);
                BotLib.Log.ErrorWithMaxCount("恢复Bot问答历史失败：" + ex.Message, 10);
            }
        }
    }
}
