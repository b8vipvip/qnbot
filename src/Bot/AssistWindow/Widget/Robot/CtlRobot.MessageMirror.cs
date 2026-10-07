using Bot.Automation.ChatDeskNs;
using Bot.ChromeNs;
using Bot.ShopScope;
using BotLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Bot.AssistWindow.Widget.Robot
{
    public partial class CtlRobot
    {
        private DispatcherTimer _messageMirrorTimer;
        private string _messageMirrorSeller = string.Empty;
        private string _messageMirrorBuyer = string.Empty;
        private string _messageMirrorSignature = string.Empty;
        private DateTime _messageMirrorLastRemoteRefresh = DateTime.MinValue;

        private void StartMessageMirror()
        {
            if (_messageMirrorTimer == null)
            {
                _messageMirrorTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(350)
                };
                _messageMirrorTimer.Tick += (s, e) => RefreshMessageMirror(false);
                Unloaded += CtlRobot_MessageMirrorUnloaded;
            }

            _messageMirrorTimer.Start();
            RefreshMessageMirror(true);
        }

        private void CtlRobot_MessageMirrorUnloaded(object sender, RoutedEventArgs e)
        {
            if (_messageMirrorTimer != null) _messageMirrorTimer.Stop();
        }

        private QN ResolveMessageMirrorQn()
        {
            var qn = _preQN;
            if (qn == null || qn.Seller == null || qn.Buyer == null)
            {
                qn = QN.CurQN;
            }
            if (qn == null || qn.Seller == null || qn.Buyer == null) return null;

            var seller = (qn.Seller.Nick ?? string.Empty).Trim();
            var buyer = (qn.Buyer.Nick ?? string.Empty).Trim();
            if (seller.Length == 0 || buyer.Length == 0) return null;

            if (_desk != null && !DeskSellerBindingRegistry.IsSellerForDesk(_desk, seller))
            {
                return null;
            }

            return qn;
        }

        private List<ConversationContextTurn> LoadMessageMirrorTurns(string seller, string buyer)
        {
            IDisposable scope = null;
            try
            {
                var shop = ShopContextLocator.ResolveRuntimeBySellerNick(seller);
                if (shop != null) scope = ShopSettingsScope.Enter(shop);
                return ConversationContextStore.GetMirrorTurns(seller, buyer, 100);
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("读取千牛镜像聊天记录失败：" + ex.Message, 10);
                return new List<ConversationContextTurn>();
            }
            finally
            {
                if (scope != null) scope.Dispose();
            }
        }

        private void RequestMessageMirrorHistory(QN qn, string seller, string buyer, bool force)
        {
            if (qn == null || qn.Buyer == null) return;
            var ccode = (qn.Buyer.Ccode ?? string.Empty).Trim();
            if (ccode.Length == 0) return;

            if (!force && _messageMirrorLastRemoteRefresh != DateTime.MinValue
                && DateTime.Now - _messageMirrorLastRemoteRefresh < TimeSpan.FromSeconds(5))
            {
                return;
            }

            _messageMirrorLastRemoteRefresh = DateTime.Now;
            ConversationContextStore.RequestMirrorRemoteRefresh(seller, buyer, ccode);
        }

        private void RefreshMessageMirror(bool force)
        {
            try
            {
                var qn = ResolveMessageMirrorQn();
                if (qn == null)
                {
                    RenderEmptyMessageMirror();
                    return;
                }

                var seller = (qn.Seller.Nick ?? string.Empty).Trim();
                var buyer = (qn.Buyer.Nick ?? string.Empty).Trim();
                var identityChanged = !string.Equals(_messageMirrorSeller, seller, StringComparison.Ordinal)
                    || !string.Equals(_messageMirrorBuyer, buyer, StringComparison.Ordinal);

                if (identityChanged)
                {
                    _messageMirrorSeller = seller;
                    _messageMirrorBuyer = buyer;
                    _messageMirrorSignature = string.Empty;
                    _messageMirrorLastRemoteRefresh = DateTime.MinValue;
                    RequestMessageMirrorHistory(qn, seller, buyer, true);
                }

                var turns = LoadMessageMirrorTurns(seller, buyer);
                if (turns.Count == 0)
                {
                    RequestMessageMirrorHistory(qn, seller, buyer, false);
                }

                var signature = BuildMessageMirrorSignature(seller, buyer, turns);
                if (!force && string.Equals(signature, _messageMirrorSignature, StringComparison.Ordinal))
                {
                    return;
                }

                var keepBottom = force || identityChanged
                    || scvBody.ScrollableHeight - scvBody.VerticalOffset < 48;
                _messageMirrorSignature = signature;
                RenderMessageMirror(seller, buyer, turns, keepBottom);
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("刷新千牛镜像聊天区失败：" + ex.Message, 10);
            }
        }

        private static string BuildMessageMirrorSignature(
            string seller,
            string buyer,
            IList<ConversationContextTurn> turns)
        {
            unchecked
            {
                long hash = 1469598103934665603L;
                foreach (var turn in turns ?? new List<ConversationContextTurn>())
                {
                    var value = (turn == null ? string.Empty
                        : (turn.Role ?? string.Empty) + "|" + turn.Timestamp.Ticks + "|"
                            + (turn.MessageKey ?? string.Empty) + "|" + turn.Withdrawn + "|"
                            + (turn.Text ?? string.Empty));
                    foreach (var ch in value)
                    {
                        hash ^= ch;
                        hash *= 1099511628211L;
                    }
                }
                return seller + "#" + buyer + "#" + (turns == null ? 0 : turns.Count) + "#" + hash;
            }
        }

        private void RenderEmptyMessageMirror()
        {
            var signature = "empty";
            if (string.Equals(_messageMirrorSignature, signature, StringComparison.Ordinal)
                && stkDialog.Children.Count > 0)
            {
                return;
            }

            _messageMirrorSignature = signature;
            stkDialog.Children.Clear();
            grdTipNoConv.Visibility = Visibility.Visible;
            stkDialog.Children.Add(grdTipNoConv);
        }

        private void RenderMessageMirror(
            string seller,
            string buyer,
            IList<ConversationContextTurn> turns,
            bool keepBottom)
        {
            stkDialog.Children.Clear();

            if (turns == null || turns.Count == 0)
            {
                grdTipNoConv.Visibility = Visibility.Visible;
                stkDialog.Children.Add(grdTipNoConv);
                return;
            }

            grdTipNoConv.Visibility = Visibility.Collapsed;
            DateTime? previousDate = null;
            var usedNativeBotConversations = new HashSet<CtlConversation>();
            foreach (var turn in turns.Where(x => x != null))
            {
                UIElement messageElement = null;
                string botAnswer;
                if (TryExtractBotEchoAnswer(turn, out botAnswer))
                {
                    // [A] belongs only to the Qianniu transport echo. Never render that
                    // mirrored seller message itself. If we can associate it with the
                    // local Bot answer, render the original native CtlConversation answer
                    // row exactly once; duplicate/stale [A] echoes are ignored.
                    var nativeBotConversation = FindMessageMirrorActionConversation(
                        seller,
                        buyer,
                        botAnswer,
                        turn.Timestamp,
                        usedNativeBotConversations);
                    if (nativeBotConversation == null)
                    {
                        continue;
                    }

                    usedNativeBotConversations.Add(nativeBotConversation);
                    nativeBotConversation.UseAnswerOnlyMirrorPresentation();
                    messageElement = nativeBotConversation;
                }
                else
                {
                    messageElement = BuildMessageMirrorBubble(turn);
                }

                if (messageElement == null) continue;

                var messageDate = turn.Timestamp == DateTime.MinValue
                    ? (DateTime?)null
                    : turn.Timestamp.Date;
                if (messageDate.HasValue
                    && (!previousDate.HasValue || previousDate.Value != messageDate.Value))
                {
                    stkDialog.Children.Add(BuildMessageMirrorDateSeparator(messageDate.Value));
                    previousDate = messageDate;
                }

                stkDialog.Children.Add(messageElement);
            }

            if (keepBottom)
            {
                Dispatcher.BeginInvoke(
                    new Action(() => scvBody.ScrollToEnd()),
                    DispatcherPriority.Background);
            }
        }

        private UIElement BuildMessageMirrorDateSeparator(DateTime date)
        {
            return new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(238, 242, 247)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(0, 7, 0, 5),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = new TextBlock
                {
                    Text = date.ToString("yyyy-MM-dd"),
                    Foreground = new SolidColorBrush(Color.FromRgb(120, 132, 148)),
                    FontSize = 10
                }
            };
        }

        private UIElement BuildMessageMirrorBubble(ConversationContextTurn turn)
        {
            var isSeller = string.Equals(turn.Role, "assistant", StringComparison.Ordinal);
            var maxWidth = Math.Max(210, Math.Min(380, scvBody.ActualWidth > 80
                ? scvBody.ActualWidth - 64
                : 300));

            var meta = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = isSeller ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Margin = new Thickness(2, 0, 2, 3)
            };
            meta.Children.Add(new TextBlock
            {
                Text = isSeller ? "客服" : "买家",
                Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128)),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });
            meta.Children.Add(new TextBlock
            {
                Text = turn.Timestamp == DateTime.MinValue ? string.Empty : "  " + turn.Timestamp.ToString("HH:mm:ss"),
                Foreground = new SolidColorBrush(Color.FromRgb(156, 163, 175)),
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center
            });

            var messageText = turn.Withdrawn ? "已撤回一条消息" : (turn.Text ?? string.Empty);
            var text = new TextBlock
            {
                Text = messageText,
                Foreground = new SolidColorBrush(turn.Withdrawn
                    ? Color.FromRgb(107, 114, 128)
                    : Color.FromRgb(31, 41, 55)),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 18
            };

            var bubble = new Border
            {
                Background = new SolidColorBrush(isSeller
                    ? Color.FromRgb(221, 243, 255)
                    : Color.FromRgb(255, 255, 255)),
                BorderBrush = new SolidColorBrush(isSeller
                    ? Color.FromRgb(190, 226, 247)
                    : Color.FromRgb(225, 231, 239)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(10, 7, 10, 7),
                HorizontalAlignment = isSeller ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                MaxWidth = maxWidth,
                Child = text
            };
            bubble.ContextMenu = BuildCopyOnlyContextMenu(messageText, bubble);

            var stack = new StackPanel
            {
                HorizontalAlignment = isSeller ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Margin = isSeller
                    ? new Thickness(38, 3, 9, 7)
                    : new Thickness(9, 3, 38, 7)
            };
            stack.Children.Add(meta);
            stack.Children.Add(bubble);
            return stack;
        }

        private ContextMenu BuildCopyOnlyContextMenu(string messageText, FrameworkElement placementTarget)
        {
            var menu = new ContextMenu();
            var copy = new MenuItem { Header = "复制" };
            copy.Click += (s, e) =>
            {
                try
                {
                    Clipboard.SetText(messageText ?? string.Empty);
                }
                catch
                {
                }
            };
            menu.Items.Add(copy);
            menu.PlacementTarget = placementTarget;
            return menu;
        }

        private static bool TryExtractBotEchoAnswer(
            ConversationContextTurn turn,
            out string answer)
        {
            answer = string.Empty;
            if (turn == null
                || turn.Withdrawn
                || !string.Equals(turn.Role, "assistant", StringComparison.Ordinal))
            {
                return false;
            }

            var text = (turn.Text ?? string.Empty).TrimEnd();
            const string marker = "[A]";
            if (!text.EndsWith(marker, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            answer = text.Substring(0, text.Length - marker.Length).TrimEnd();
            return answer.Length > 0;
        }

        private CtlConversation FindMessageMirrorActionConversation(
            string seller,
            string buyer,
            string messageText,
            DateTime messageTimestamp,
            ISet<CtlConversation> usedConversations)
        {
            var key = string.Format("{0}#{1}", seller, buyer);
            List<CtlConversation> conversations;
            if (buyerConversations == null
                || !buyerConversations.TryGetValue(key, out conversations)
                || conversations == null)
            {
                return null;
            }

            var normalized = NormalizeMessageMirrorText(messageText);
            if (normalized.Length == 0) return null;
            try
            {
                var matches = conversations
                    .Where(x => x != null
                        && (usedConversations == null || !usedConversations.Contains(x))
                        && string.Equals(
                            NormalizeMessageMirrorText(x.MirrorAnswerText),
                            normalized,
                            StringComparison.Ordinal))
                    .ToList();
                if (matches.Count == 0) return null;

                if (messageTimestamp == DateTime.MinValue)
                {
                    return matches
                        .OrderByDescending(x => x.HistorySortTicks)
                        .FirstOrDefault();
                }

                var targetTicks = messageTimestamp.Ticks;
                return matches
                    .OrderBy(x => Math.Abs((double)x.HistorySortTicks - targetTicks))
                    .ThenByDescending(x => x.HistorySortTicks)
                    .FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        private static string NormalizeMessageMirrorText(string value)
        {
            return (value ?? string.Empty)
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Trim();
        }
    }
}
