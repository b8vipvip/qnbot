using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Bot.ChromeNs;
using Bot.Knowledge;

namespace Bot.AssistWindow.Widget.Robot
{
    public class ConversationResendEventArgs : EventArgs
    {
        public string Seller { get; private set; }
        public string Buyer { get; private set; }
        public string Question { get; private set; }
        public string Answer { get; private set; }

        public ConversationResendEventArgs(string seller, string buyer, string question, string answer)
        {
            Seller = seller ?? string.Empty;
            Buyer = buyer ?? string.Empty;
            Question = question ?? string.Empty;
            Answer = answer ?? string.Empty;
        }
    }

    public class ConversationEditEventArgs : EventArgs
    {
        public string Seller { get; private set; }
        public string Buyer { get; private set; }
        public string Question { get; private set; }
        public string Answer { get; private set; }

        public ConversationEditEventArgs(string seller, string buyer, string question, string answer)
        {
            Seller = seller ?? string.Empty;
            Buyer = buyer ?? string.Empty;
            Question = question ?? string.Empty;
            Answer = answer ?? string.Empty;
        }
    }

    public partial class CtlConversation : UserControl
    {
        private readonly string sendedChar = "   √√";
        private string _seller = string.Empty;
        private string _buyer = string.Empty;
        private string _question = string.Empty;
        private string _answer = string.Empty;
        private bool _canResend = true;
        private DateTime _questionDetectedAt = DateTime.MinValue;
        private DateTime _answerReadyAt = DateTime.MinValue;

        public event EventHandler<ConversationResendEventArgs> ResendRequested;
        public event EventHandler<ConversationEditEventArgs> EditRequested;

        public CtlConversation()
        {
            InitializeComponent();
        }

        public static CtlConversation Create(string seller, string buyer, string question, string answer, bool isAutoReply = false, string answerSource = "")
        {
            var dlg = new CtlConversation();
            dlg.Setup(seller, buyer, question, answer, isAutoReply, answerSource);
            return dlg;
        }

        public void Setup(string seller, string buyer, string question, string answer, bool isAutoReply, string answerSource)
        {
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
                ? KnowledgeLearningService.ResolveAnswerSource(_seller, _buyer, _question, _answer)
                : answerSource;
            SetSource(resolvedSource);
            txtStatus.Text = isAutoReply ? "正在发送..." : "仅生成答案";
            txtStatus.Foreground = new SolidColorBrush(isAutoReply ? Color.FromRgb(47, 128, 237) : Color.FromRgb(107, 114, 128));
            UpdateTimingText();
        }

        public void SetQuestion(string question, DateTime detectedAt)
        {
            _question = question ?? string.Empty;
            _questionDetectedAt = detectedAt == DateTime.MinValue ? DateTime.Now : detectedAt;
            Ui(() =>
            {
                txtQuestion.Text = _question;
                UpdateTimingText();
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
                    ? KnowledgeLearningService.ResolveAnswerSource(_seller, _buyer, _question, _answer)
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

        public void SetSource(string source)
        {
            Ui(() =>
            {
                source = (source ?? string.Empty).Trim();
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
                string blockedReason;
                string manualAnswer;
                if (!success && KnowledgeLearningService.TryTakeSendBlock(_seller, _buyer, _answer, out blockedReason, out manualAnswer))
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

        internal string MirrorAnswerText
        {
            get { return _answer ?? string.Empty; }
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

        internal void MirrorOpenKnowledge(Window owner)
        {
            KnowledgeCenterWindow.ShowManagerAndLocate(
                owner,
                _seller,
                _buyer,
                _question,
                _answer);
        }

        private void RaiseResendRequested()
        {
            var handler = ResendRequested;
            if (handler != null) handler(this, new ConversationResendEventArgs(_seller, _buyer, _question, _answer));
        }

        private void RaiseEditRequested()
        {
            var handler = EditRequested;
            if (handler != null) handler(this, new ConversationEditEventArgs(_seller, _buyer, _question, _answer));
        }

        internal void UseAnswerOnlyMirrorPresentation()
        {
            if (gridQuestion != null)
            {
                gridQuestion.Visibility = Visibility.Collapsed;
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
                _answer);
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
