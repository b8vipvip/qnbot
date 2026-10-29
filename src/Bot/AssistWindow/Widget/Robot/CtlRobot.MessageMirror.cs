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
            var lastBuyerQuestion = string.Empty;
            foreach (var turn in turns.Where(x => x != null))
            {
                if (string.Equals(turn.Role, "user", StringComparison.Ordinal)
                    && !turn.Withdrawn && !string.IsNullOrWhiteSpace(turn.Text))
                    lastBuyerQuestion = turn.Text;
                UIElement messageElement = null;
                string botAnswer;
                if (TryExtractBotEchoAnswer(turn, out botAnswer))
                {
                    // [A] is a transport marker, not display text. A confirmed Qianniu
                    // echo is authoritative even if the transient local answer card was
                    // lost (e.g. fixed/preset reply, restart, race or history refresh).
                    // Never discard a real sent message just because local matching failed.
                    var nativeBotConversation = FindMessageMirrorActionConversation(
                        seller,
                        buyer,
                        botAnswer,
                        turn.Timestamp,
                        usedNativeBotConversations);
                    if (nativeBotConversation == null)
                    {
                        // Reconstruct only provenance that can be proven from the
                        // exact built-in response; a missing transient UI card
                        // is not itself a message source.
                        var source = ConversationContextStore.IsProductLinkPresetReply(botAnswer)
                            ? "商品链接预设回复（内置固定话术，非知识库条目）"
                            : KnowledgeLearningService.ResolveAnswerSource(
                                seller, buyer, lastBuyerQuestion, botAnswer);
                        if (string.IsNullOrWhiteSpace(source))
                            source = "千牛已发送回显（来源记录未留存，不能确定知识库命中）";
                        nativeBotConversation = CtlConversation.Create(
                            seller, buyer, lastBuyerQuestion, botAnswer, true, source);
                        nativeBotConversation.SetSendResult(true, "千牛回显确认已发送");
                        nativeBotConversation.ResendRequested += CtlConversation_ResendRequested;
                        nativeBotConversation.EditRequested += CtlConversation_EditRequested;
                    }

                    usedNativeBotConversations.Add(nativeBotConversation);
                    // A Bot transport echo must use the SAME right-aligned seller
                    // bubble layout as manual replies. The linked native answer
                    // still owns 查看/复制/重发/修改/来源, but is not rendered as
                    // the old left-aligned green Q/A card.
                    messageElement = BuildMessageMirrorBubble(
                        turn, seller, buyer, botAnswer, nativeBotConversation);
                }
                else
                {
                    messageElement = BuildMessageMirrorBubble(turn, seller, buyer);
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

        private UIElement BuildMessageMirrorBubble(
            ConversationContextTurn turn,
            string seller,
            string buyer,
            string botAnswer = null,
            CtlConversation botAction = null)
        {
            var isSeller = string.Equals(turn.Role, "assistant", StringComparison.Ordinal);
            var isBot = isSeller && botAction != null;
            // Seller + Bot both appear on the right; the buyer stays on the left.
            // Do not infer Bot solely from the role: only [A] transport echoes are Bot.
            var sender = isBot ? "Bot" : (isSeller ? seller : buyer);
            var timestamp = turn.Timestamp == DateTime.MinValue
                ? string.Empty
                : turn.Timestamp.ToString("yyyy-MM-dd HH:mm:ss");
            var caption = string.IsNullOrEmpty(timestamp)
                ? sender
                : timestamp + " " + sender;
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
                Text = caption,
                Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128)),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = maxWidth,
                TextAlignment = isSeller ? TextAlignment.Right : TextAlignment.Left
            });

            var messageText = turn.Withdrawn ? "已撤回一条消息"
                : isBot ? botAnswer : (turn.Text ?? string.Empty);
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
            bubble.ContextMenu = isBot
                ? BuildMessageMirrorBotContextMenu(botAction, turn, messageText, bubble)
                : BuildCopyOnlyContextMenu(messageText, bubble);

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

        private static string ResolveMirrorBotSource(CtlConversation action, string messageText)
        {
            if (ConversationContextStore.IsProductLinkPresetReply(messageText))
                return "商品链接预设回复（系统内置固定话术，不属于知识库 V2）";
            var source = action == null ? string.Empty : (action.MirrorAnswerSource ?? string.Empty).Trim();
            if (source.IndexOf("本地答复卡片未找到", StringComparison.Ordinal) >= 0)
                return "千牛已发送回显；原始来源记录未留存，不能确认知识库命中";
            return string.IsNullOrWhiteSpace(source)
                ? "千牛已发送回显；来源尚未记录，不能确认知识库命中"
                : source;
        }

        private static bool IsVerifiedKnowledgeMirrorSource(string source)
        {
            return source == "智能路由-本地直答"
                || source == "智能路由-离线知识兜底"
                || source == "知识库直答"
                || source == "知识库匹配";
        }

        private ContextMenu BuildMessageMirrorBotContextMenu(
            CtlConversation action,
            ConversationContextTurn turn,
            string messageText,
            FrameworkElement placementTarget)
        {
            var menu = BuildCopyOnlyContextMenu(messageText, placementTarget);
            var source = ResolveMirrorBotSource(action, messageText);
            var timestamp = turn == null || turn.Timestamp == DateTime.MinValue
                ? "时间未记录" : turn.Timestamp.ToString("yyyy-MM-dd HH:mm:ss");
            var detail = "发送时间：" + timestamp
                + "\n发送者：Bot"
                + "\n消息来源：" + source
                + "\n\n回复内容：\n" + messageText;
            var view = new MenuItem { Header = "查看" };
            view.Click += (s, e) =>
            {
                // Only a confirmed knowledge-source answer can navigate to the
                // legacy knowledge locator. Presets and unknown-source echoes
                // are perfectly valid Bot replies without a Knowledge V2 entry.
                if (IsVerifiedKnowledgeMirrorSource(source))
                {
                    var result = MessageBox.Show(Window.GetWindow(this),
                        detail + "\n\n来源标记指向知识检索。是否进一步尝试定位知识库条目？",
                        "Bot 回复详情", MessageBoxButton.YesNo, MessageBoxImage.Information);
                    if (result == MessageBoxResult.Yes)
                        action.MirrorOpenKnowledge(Window.GetWindow(this));
                }
                else
                {
                    MessageBox.Show(Window.GetWindow(this),
                        detail + "\n\n此回复并非已验证的知识库命中，不会错误跳转知识库。",
                        "Bot 回复详情", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            };
            menu.Items.Insert(0, view);

            if (action.MirrorCanResend)
            {
                var resend = new MenuItem { Header = "重发" };
                resend.Click += (s, e) => action.MirrorRequestResend();
                menu.Items.Add(resend);
            }

            var edit = new MenuItem { Header = "修改" };
            edit.Click += (s, e) => action.MirrorRequestEdit();
            menu.Items.Add(edit);

            var provenance = new MenuItem { Header = "来源" };
            provenance.Click += (s, e) => MessageBox.Show(
                Window.GetWindow(this),
                "消息来源：" + source
                    + "\n该消息已由千牛回显确认发送。来源标签仅表示回复生成路径，不代表知识库一定存在同名条目。"
                    + "\n修改本地答案或知识不会修改已送达买家的消息。",
                "消息来源", MessageBoxButton.OK, MessageBoxImage.Information);
            menu.Items.Add(provenance);
            return menu;
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
