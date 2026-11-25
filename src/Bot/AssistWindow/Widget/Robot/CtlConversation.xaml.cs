using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Bot.ChromeNs;
using Bot.Knowledge;
using BotLib;
using Bot.ShopScope;

namespace Bot.AssistWindow.Widget.Robot
{
    public class ConversationResendEventArgs : EventArgs
    {
        public string Seller { get; private set; }
        public string Buyer { get; private set; }
        public string Question { get; private set; }
        public string Answer { get; private set; }
        public string ShopKey { get; private set; }

        public ConversationResendEventArgs(
            string seller, string buyer, string question, string answer, string shopKey = "")
        {
            Seller = seller ?? string.Empty;
            Buyer = buyer ?? string.Empty;
            Question = question ?? string.Empty;
            Answer = answer ?? string.Empty;
            ShopKey = (shopKey ?? string.Empty).Trim();
        }
    }

    public class ConversationEditEventArgs : EventArgs
    {
        public string Seller { get; private set; }
        public string Buyer { get; private set; }
        public string Question { get; private set; }
        public string Answer { get; private set; }
        public string SourceLabel { get; private set; }
        public string MessageRecordId { get; private set; }
        public bool SourceWasManuallyCorrected { get; private set; }
        public string ShopKey { get; private set; }

        public ConversationEditEventArgs(string seller, string buyer, string question, string answer)
            : this(seller, buyer, question, answer, string.Empty, string.Empty, false, string.Empty) { }

        public ConversationEditEventArgs(string seller, string buyer, string question,
            string answer, string sourceLabel, string messageRecordId,
            bool sourceWasManuallyCorrected, string shopKey = "")
        {
            Seller = seller ?? string.Empty;
            Buyer = buyer ?? string.Empty;
            Question = question ?? string.Empty;
            Answer = answer ?? string.Empty;
            SourceLabel = sourceLabel ?? string.Empty;
            MessageRecordId = messageRecordId ?? string.Empty;
            SourceWasManuallyCorrected = sourceWasManuallyCorrected;
            ShopKey = (shopKey ?? string.Empty).Trim();
        }
    }

    public partial class CtlConversation : UserControl
    {
        private readonly string sendedChar = "   √√";
        private string _shopKey = string.Empty;
        private string _seller = string.Empty;
        private string _buyer = string.Empty;
        private string _question = string.Empty;
        private string _answer = string.Empty;
        private string _answerSource = string.Empty;
        private bool _canResend = true;
        private DateTime _questionDetectedAt = DateTime.MinValue;
        private DateTime _answerReadyAt = DateTime.MinValue;
        private VisionCachedImageReference _imageReference;

        public event EventHandler<ConversationResendEventArgs> ResendRequested;
        public event EventHandler<ConversationEditEventArgs> EditRequested;

        public CtlConversation()
        {
            InitializeComponent();
        }

        public static CtlConversation Create(
            string seller, string buyer, string question, string answer,
            bool isAutoReply = false, string answerSource = "", string shopKey = "")
        {
            var dlg = new CtlConversation();
            dlg.Setup(seller, buyer, question, answer, isAutoReply, answerSource, shopKey);
            return dlg;
        }

        public void Setup(
            string seller, string buyer, string question, string answer,
            bool isAutoReply, string answerSource, string shopKey = "")
        {
            _shopKey = (shopKey ?? string.Empty).Trim();
            _seller = seller ?? string.Empty;
            _buyer = buyer ?? string.Empty;
            _question = question ?? string.Empty;
            _answer = answer ?? string.Empty;
            _canResend = true;
            _questionDetectedAt = DateTime.Now;
            _answerReadyAt = DateTime.Now;
            txtQuestion.Text = _question;
            txtAnswer.Text = _answer;
            var resolvedSource = string.IsNullOrWhiteSpace(answerSource)
                ? ResolveAnswerSourceForShop()
                : answerSource;
            SetSource(resolvedSource);
            txtStatus.Text = isAutoReply ? "正在发送..." : "仅生成答案";
            txtStatus.Foreground = new SolidColorBrush(isAutoReply ? Color.FromRgb(47, 128, 237) : Color.FromRgb(107, 114, 128));
            UpdateTimingText();
            RefreshQuestionContextMenu();
        }

        public void SetQuestion(string question, DateTime detectedAt)
        {
            _question = question ?? string.Empty;
            _questionDetectedAt = detectedAt == DateTime.MinValue ? DateTime.Now : detectedAt;
            Ui(() =>
            {
                txtQuestion.Text = _question;
                UpdateTimingText();
                RefreshQuestionContextMenu();
            });
        }

        public void SetProcessing(string text)
        {
            _answerReadyAt = DateTime.MinValue;
            Ui(() =>
            {
                txtAnswer.Text = string.IsNullOrWhiteSpace(text) ? "正在识别并获取答案..." : text;
                txtStatus.Text = "处理中";
                txtStatus.Foreground = new SolidColorBrush(Color.FromRgb(47, 128, 237));
                UpdateTimingText();
            });
        }

        public void SetAnswer(string answer, string answerSource = "")
        {
            SetAnswer(answer, answerSource, DateTime.Now);
        }

        public void SetAnswer(string answer, string answerSource, DateTime answerReadyAt)
        {
            _answer = answer ?? string.Empty;
            _answerReadyAt = answerReadyAt == DateTime.MinValue ? DateTime.Now : answerReadyAt;
            Ui(() =>
            {
                txtAnswer.Text = _answer;
                var source = string.IsNullOrWhiteSpace(answerSource)
                    ? ResolveAnswerSourceForShop()
                    : answerSource;
                if (!string.IsNullOrWhiteSpace(source)) SetSource(source);
                UpdateTimingText();
            });
        }

        private void UpdateTimingText()
        {
            txtQuestionTime.Text = _questionDetectedAt == DateTime.MinValue
                ? string.Empty
                : "识别 " + _questionDetectedAt.ToString("HH:mm:ss.fff");
            txtAnswerTime.Text = _answerReadyAt == DateTime.MinValue
                ? "答案等待中"
                : "答案 " + _answerReadyAt.ToString("HH:mm:ss.fff");
            txtLatency.Text = _questionDetectedAt == DateTime.MinValue || _answerReadyAt == DateTime.MinValue
                ? string.Empty
                : "响应 " + Math.Max(0, (long)(_answerReadyAt - _questionDetectedAt).TotalMilliseconds) + "ms";
        }

        private string ResolveAnswerSourceForShop()
        {
            if (string.IsNullOrWhiteSpace(_shopKey)) return string.Empty;
            try
            {
                var shop = ShopContextLocator.ResolveRuntimeByShopKey(_shopKey);
                using (ShopSettingsScope.Enter(shop))
                    return KnowledgeLearningService.ResolveAnswerSource(
                        _seller, _buyer, _question, _answer);
            }
            catch
            {
                // A missing/ambiguous scope must not infer provenance from another shop.
                return string.Empty;
            }
        }

        public void SetSource(string source)
        {
            Ui(() =>
            {
                source = (source ?? string.Empty).Trim();
                _answerSource = source;
                var visible = !string.IsNullOrWhiteSpace(source);
                txtSource.Text = source;
                bdSource.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                txtSourceSeparator.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                var local = source == "本地";
                var manual = source.StartsWith("人工", StringComparison.Ordinal);
                txtSource.Foreground = new SolidColorBrush(local
                    ? Color.FromRgb(22, 101, 52)
                    : manual ? Color.FromRgb(107, 33, 168) : Color.FromRgb(29, 78, 216));
                bdSource.Background = new SolidColorBrush(local
                    ? Color.FromRgb(220, 252, 231)
                    : manual ? Color.FromRgb(243, 232, 255) : Color.FromRgb(219, 234, 254));
            });
        }

        public void SetStatus(string text, bool success)
        {
            Ui(() =>
            {
                txtStatus.Text = text ?? string.Empty;
                txtStatus.Foreground = new SolidColorBrush(success ? Color.FromRgb(39, 174, 96) : Color.FromRgb(235, 87, 87));
            });
        }

        public void SetSkipped(string detail)
        {
            _canResend = false;
            Ui(() =>
            {
                txtAnswer.Text = _answer;
                txtStatus.Text = "已跳过，未发送";
                txtStatus.ToolTip = detail ?? string.Empty;
                txtStatus.Foreground = new SolidColorBrush(Color.FromRgb(242, 153, 74));
            });
        }

        private void Ui(Action action)
        {
            if (Dispatcher.CheckAccess()) action();
            else Dispatcher.BeginInvoke(action);
        }

        public void SetSendPending(string text)
        {
            Ui(() =>
            {
                txtAnswer.Text = _answer;
                txtStatus.Text = string.IsNullOrWhiteSpace(text) ? "正在发送..." : text;
                txtStatus.Foreground = new SolidColorBrush(Color.FromRgb(47, 128, 237));
            });
        }

        public void SetSendResult(bool success, string detail)
        {
            Ui(() =>
            {
                string blockedReason = string.Empty;
                string manualAnswer = string.Empty;
                var blocked = false;
                if (!success && !string.IsNullOrWhiteSpace(_shopKey))
                {
                    try
                    {
                        var shop = ShopContextLocator.ResolveRuntimeByShopKey(_shopKey);
                        using (ShopSettingsScope.Enter(shop))
                            blocked = KnowledgeLearningService.TryTakeSendBlock(
                                _seller, _buyer, _answer, out blockedReason, out manualAnswer);
                    }
                    catch
                    {
                        // Do not query another shop's send-block state on identity failure.
                    }
                }

                if (blocked)
                {
                    txtAnswer.Text = _answer;
                    txtStatus.Text = blockedReason;
                    txtStatus.Foreground = new SolidColorBrush(Color.FromRgb(242, 153, 74));
                    SetSource("人工回复");
                }
                else
                {
                    txtAnswer.Text = success ? _answer + sendedChar : _answer;
                    txtStatus.Text = success ? (string.IsNullOrWhiteSpace(detail) ? "已发送" : detail) : (string.IsNullOrWhiteSpace(detail) ? "发送失败" : detail);
                    txtStatus.Foreground = new SolidColorBrush(success ? Color.FromRgb(39, 174, 96) : Color.FromRgb(235, 87, 87));
                }
            });
        }

        internal string MirrorQuestionText
        {
            get { return _question ?? string.Empty; }
        }

        internal string MirrorAnswerText
        {
            get { return _answer ?? string.Empty; }
        }

        internal string MirrorAnswerSource
        {
            get { return _answerSource ?? string.Empty; }
        }

        internal void MirrorSetAnswerSource(string source)
        {
            SetSource(source);
        }

        internal bool MirrorCanResend
        {
            get { return _canResend; }
        }

        internal void MirrorRequestResend()
        {
            RaiseResendRequested();
        }

        internal void MirrorRequestEdit()
        {
            RaiseEditRequested();
        }

        internal void MirrorRequestEdit(string originalSource, string messageRecordId,
            bool sourceWasManuallyCorrected)
        {
            var handler = EditRequested;
            if (handler != null)
                handler(this, new ConversationEditEventArgs(
                    _seller, _buyer, _question, _answer,
                    originalSource, messageRecordId, sourceWasManuallyCorrected, _shopKey));
        }

        internal void MirrorOpenKnowledge(Window owner)
        {
            KnowledgeCenterWindow.ShowManagerAndLocate(
                owner,
                _seller,
                _buyer,
                _question,
                _answer,
                _shopKey);
        }

        private void RaiseResendRequested()
        {
            var handler = ResendRequested;
            if (handler != null) handler(this, new ConversationResendEventArgs(
                _seller, _buyer, _question, _answer, _shopKey));
        }

        private void RaiseEditRequested()
        {
            var handler = EditRequested;
            if (handler != null) handler(this, new ConversationEditEventArgs(
                _seller, _buyer, _question, _answer, _answerSource, string.Empty, false, _shopKey));
        }

        internal void UseAnswerOnlyMirrorPresentation()
        {
            if (gridQuestion != null)
            {
                gridQuestion.Visibility = Visibility.Collapsed;
            }
        }

        private bool IsImageQuestion
        {
            get
            {
                var value = _question ?? string.Empty;
                return value.IndexOf("[图片]", StringComparison.OrdinalIgnoreCase) >= 0
                    || value.IndexOf("[图片消息]", StringComparison.OrdinalIgnoreCase) >= 0
                    || value.IndexOf("图片消息", StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        private void RefreshQuestionContextMenu()
        {
            if (txtQuestion == null) return;
            _imageReference = null;
            if (IsImageQuestion && !string.IsNullOrWhiteSpace(_shopKey))
            {
                try
                {
                    var shop = ShopContextLocator.ResolveRuntimeByShopKey(_shopKey);
                    using (ShopSettingsScope.Enter(shop))
                    {
                        VisionCachedImageReference reference;
                        if (VisionImageCacheService.TryGetNearestReference(
                            _seller, _buyer, _questionDetectedAt,
                            TimeSpan.FromMinutes(5), _shopKey, out reference)
                            && reference != null && !reference.Withdrawn)
                            _imageReference = reference;
                    }
                }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount("绑定买家图片上下文菜单失败：" + ex.Message, 5);
                }
            }

            txtQuestion.ContextMenu = CreateQuestionContextMenu(txtQuestion);
            Stylus.SetIsPressAndHoldEnabled(txtQuestion, true);
        }

        private ContextMenu CreateQuestionContextMenu(FrameworkElement placementTarget)
        {
            var menu = new ContextMenu();
            var copy = new MenuItem { Header = "复制" };
            copy.Click += (s, args) =>
            {
                try { Clipboard.SetText(_question ?? string.Empty); }
                catch { }
            };
            menu.Items.Add(copy);

            if (IsImageQuestion)
            {
                var import = new MenuItem { Header = "导入图片知识" };
                import.Click += async (s, args) => await ImportImageKnowledgeAsync();
                menu.Items.Add(import);
            }
            menu.PlacementTarget = placementTarget ?? txtQuestion;
            return menu;
        }

        private async Task ImportImageKnowledgeAsync()
        {
            if (!IsImageQuestion)
            {
                MessageBox.Show(Window.GetWindow(this), "当前消息不是图片消息。",
                    "导入图片知识", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (string.IsNullOrWhiteSpace(_shopKey))
            {
                MessageBox.Show(Window.GetWindow(this), "无法确认图片消息所属店铺，已阻止跨店导入。",
                    "导入图片知识", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var reference = _imageReference;
            if (reference == null || reference.Message == null
                || Math.Abs((reference.ObservedAt - _questionDetectedAt).TotalMinutes) > 5)
            {
                reference = null;
                try
                {
                    var shopForLookup = ShopContextLocator.ResolveRuntimeByShopKey(_shopKey);
                    using (ShopSettingsScope.Enter(shopForLookup))
                    {
                        VisionCachedImageReference latest;
                        if (VisionImageCacheService.TryGetNearestReference(
                            _seller, _buyer, _questionDetectedAt,
                            TimeSpan.FromMinutes(5), _shopKey, out latest)
                            && latest != null && !latest.Withdrawn)
                            reference = latest;
                    }
                }
                catch (Exception ex)
                {
                    Log.Exception(ex);
                }
            }

            if (reference == null || reference.Message == null || reference.Withdrawn)
            {
                MessageBox.Show(Window.GetWindow(this),
                    "未找到与这条买家图片消息对应的原始图片缓存。请在图片消息到达后尽快导入；不会用其他消息的图片代替。",
                    "导入图片知识", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                var shop = ShopContextLocator.ResolveRuntimeByShopKey(_shopKey);
                VisionImageResult image;
                using (ShopSettingsScope.Enter(shop))
                {
                    image = await VisionImageCacheService.ResolveAsync(
                        reference.Message, null, CancellationToken.None, _shopKey);
                }
                if (image == null || !image.Success
                    || string.IsNullOrWhiteSpace(image.LocalCachePath)
                    || !File.Exists(image.LocalCachePath))
                    throw new InvalidOperationException(image == null ? "图片缓存未返回结果。" : image.Error);

                var extension = (Path.GetExtension(image.LocalCachePath) ?? string.Empty).ToLowerInvariant();
                if (extension != ".jpg" && extension != ".jpeg" && extension != ".png"
                    && extension != ".webp" && extension != ".gif")
                    extension = ".png";
                var imageName = "买家图片_" + reference.ObservedAt.ToString("yyyyMMdd_HHmmss") + extension;
                KnowledgeCenterV2UiBridge.ShowImageKnowledgeImport(
                    Window.GetWindow(this), _seller, _shopKey, image.LocalCachePath, imageName);
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("导入买家图片知识失败：" + ex.Message, 10);
                MessageBox.Show(Window.GetWindow(this), "无法导入原始图片：" + ex.Message,
                    "导入图片知识", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        internal ContextMenu CreateAnswerContextMenu(FrameworkElement placementTarget = null)
        {
            var menu = new ContextMenu();

            var view = new MenuItem { Header = "查看" };
            view.Click += (s, args) => KnowledgeCenterWindow.ShowManagerAndLocate(
                Window.GetWindow(this),
                _seller,
                _buyer,
                _question,
                _answer,
                _shopKey);
            menu.Items.Add(view);

            var copy = new MenuItem { Header = "复制" };
            copy.Click += (s, args) =>
            {
                try
                {
                    Clipboard.SetText(_answer ?? string.Empty);
                }
                catch
                {
                }
            };
            menu.Items.Add(copy);

            if (_canResend)
            {
                var resend = new MenuItem { Header = "重发" };
                resend.Click += (s, args) => RaiseResendRequested();
                menu.Items.Add(resend);
            }

            var edit = new MenuItem { Header = "修改" };
            edit.Click += (s, args) => RaiseEditRequested();
            menu.Items.Add(edit);

            var provenance = new MenuItem { Header = "来源" };
            provenance.Click += (s, args) => MessageBox.Show(
                Window.GetWindow(this),
                "消息来源：" + (string.IsNullOrWhiteSpace(_answerSource) ? "未知" : _answerSource)
                    + "\n此处修改的是本地答案和知识库，不会修改已送达买家的千牛消息。",
                "消息来源",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            menu.Items.Add(provenance);
            menu.PlacementTarget = placementTarget ?? txtAnswer;
            return menu;
        }

        private void txtAnswer_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            var menu = CreateAnswerContextMenu(txtAnswer);
            menu.IsOpen = true;
        }
    }
}
