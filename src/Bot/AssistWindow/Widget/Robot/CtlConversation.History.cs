using Bot.ChromeNs;
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Bot.AssistWindow.Widget.Robot
{
    public partial class CtlConversation
    {
        private string _historyId = Guid.NewGuid().ToString("N");
        private DateTime _historyCreatedAt = DateTime.Now;
        private bool _historyPersistenceAttached;
        private bool _historyRestoreMode;

        internal string HistoryId
        {
            get { return _historyId ?? string.Empty; }
        }

        internal long HistorySortTicks
        {
            get { return _historyCreatedAt == DateTime.MinValue ? 0 : _historyCreatedAt.Ticks; }
        }

        protected override void OnInitialized(EventArgs e)
        {
            base.OnInitialized(e);
            Dispatcher.BeginInvoke(new Action(AttachConversationHistoryPersistence));
        }

        internal static CtlConversation CreateFromHistory(BotConversationHistoryEntity entity)
        {
            if (entity == null) return null;

            var ctl = new CtlConversation();
            ctl._historyRestoreMode = true;
            ctl._historyId = string.IsNullOrWhiteSpace(entity.EntityId)
                ? Guid.NewGuid().ToString("N")
                : entity.EntityId.Trim();
            ctl._historyCreatedAt = TicksToDateTime(entity.CreatedAtTicks, DateTime.Now);
            ctl._shopKey = (entity.ShopKey ?? string.Empty).Trim();

            ctl.Setup(
                entity.Seller,
                entity.Buyer,
                entity.Question,
                entity.Answer,
                false,
                entity.AnswerSource,
                entity.ShopKey);

            ctl._question = entity.Question ?? string.Empty;
            ctl._answer = entity.Answer ?? string.Empty;
            ctl._questionDetectedAt = TicksToDateTime(entity.QuestionDetectedAtTicks, ctl._historyCreatedAt);
            ctl._answerReadyAt = entity.AnswerReadyAtTicks <= 0
                ? DateTime.MinValue
                : TicksToDateTime(entity.AnswerReadyAtTicks, DateTime.MinValue);
            ctl._canResend = entity.CanResend;

            var statusText = entity.StatusText ?? string.Empty;
            var statusKind = NormalizeStatusKind(entity.StatusKind, statusText);
            if (statusKind == "processing" || statusKind == "pending")
            {
                statusKind = "interrupted";
                statusText = "上次运行未完成";
                ctl._canResend = !string.IsNullOrWhiteSpace(ctl._answer)
                    && ctl._answer.IndexOf("正在", StringComparison.Ordinal) < 0
                    && !ctl._answer.StartsWith("错误：", StringComparison.Ordinal);
            }

            ctl.txtQuestion.Text = ctl._question;
            ctl.txtAnswer.Text = statusKind == "sent"
                ? ctl._answer + ctl.sendedChar
                : ctl._answer;
            ctl.SetSource(entity.AnswerSource ?? string.Empty);
            ctl.txtStatus.Text = string.IsNullOrWhiteSpace(statusText)
                ? "历史记录"
                : statusText;
            ctl.txtStatus.ToolTip = entity.StatusDetail ?? string.Empty;
            ApplyHistoryStatusColor(ctl.txtStatus, statusKind);
            ctl.UpdateTimingText();
            // Setup initially runs with the current time; bind the context menu again
            // after restoring the historical question timestamp so an old [图片]
            // card can never import a newer image from the same buyer.
            ctl.RefreshQuestionContextMenu();
            ctl._historyRestoreMode = false;
            return ctl;
        }

        private void AttachConversationHistoryPersistence()
        {
            if (_historyPersistenceAttached) return;
            if (txtQuestion == null || txtAnswer == null || txtStatus == null || txtSource == null) return;

            _historyPersistenceAttached = true;
            AddTextWatcher(txtQuestion);
            AddTextWatcher(txtAnswer);
            AddTextWatcher(txtStatus);
            AddTextWatcher(txtSource);
            PersistConversationHistory();
        }

        private void AddTextWatcher(TextBlock block)
        {
            var descriptor = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
            if (descriptor != null)
            {
                descriptor.AddValueChanged(block, HistoryVisualStateChanged);
            }
        }

        private void HistoryVisualStateChanged(object sender, EventArgs e)
        {
            PersistConversationHistory();
        }

        private void PersistConversationHistory()
        {
            if (_historyRestoreMode) return;
            if (string.IsNullOrWhiteSpace(_seller) || string.IsNullOrWhiteSpace(_buyer)) return;

            if (string.IsNullOrWhiteSpace(_historyId)) _historyId = Guid.NewGuid().ToString("N");
            if (_historyCreatedAt == DateTime.MinValue)
            {
                _historyCreatedAt = _questionDetectedAt == DateTime.MinValue
                    ? DateTime.Now
                    : _questionDetectedAt;
            }

            var statusText = txtStatus == null ? string.Empty : (txtStatus.Text ?? string.Empty);
            BotConversationHistoryStore.QueueSave(new BotConversationHistoryEntity
            {
                EntityId = _historyId,
                ShopKey = _shopKey ?? string.Empty,
                Seller = _seller ?? string.Empty,
                Buyer = _buyer ?? string.Empty,
                Question = _question ?? string.Empty,
                Answer = _answer ?? string.Empty,
                AnswerSource = txtSource == null ? string.Empty : (txtSource.Text ?? string.Empty),
                StatusText = statusText,
                StatusKind = DetectStatusKind(statusText),
                StatusDetail = txtStatus == null ? string.Empty : Convert.ToString(txtStatus.ToolTip),
                CanResend = _canResend,
                QuestionDetectedAtTicks = _questionDetectedAt == DateTime.MinValue ? 0 : _questionDetectedAt.Ticks,
                AnswerReadyAtTicks = _answerReadyAt == DateTime.MinValue ? 0 : _answerReadyAt.Ticks,
                CreatedAtTicks = _historyCreatedAt.Ticks,
                UpdatedAtTicks = DateTime.Now.Ticks
            });
        }

        private static string DetectStatusKind(string statusText)
        {
            var text = (statusText ?? string.Empty).Trim();
            if (text.IndexOf("已发送", StringComparison.Ordinal) >= 0
                || text.IndexOf("重发成功", StringComparison.Ordinal) >= 0)
            {
                return "sent";
            }
            if (text.IndexOf("正在发送", StringComparison.Ordinal) >= 0
                || text.IndexOf("准备发送", StringComparison.Ordinal) >= 0
                || text.IndexOf("手动重发中", StringComparison.Ordinal) >= 0)
            {
                return "pending";
            }
            if (text.IndexOf("处理中", StringComparison.Ordinal) >= 0
                || text.IndexOf("正在获取", StringComparison.Ordinal) >= 0
                || text.IndexOf("正在识别", StringComparison.Ordinal) >= 0)
            {
                return "processing";
            }
            if (text.IndexOf("已跳过", StringComparison.Ordinal) >= 0
                || text.IndexOf("人工回复", StringComparison.Ordinal) >= 0
                || text.IndexOf("已取消", StringComparison.Ordinal) >= 0
                || text.IndexOf("替代", StringComparison.Ordinal) >= 0
                || text.IndexOf("不会发送", StringComparison.Ordinal) >= 0)
            {
                return "warning";
            }
            if (text.IndexOf("失败", StringComparison.Ordinal) >= 0
                || text.IndexOf("异常", StringComparison.Ordinal) >= 0
                || text.IndexOf("未发送", StringComparison.Ordinal) >= 0)
            {
                return "failed";
            }
            if (text.IndexOf("仅生成答案", StringComparison.Ordinal) >= 0)
            {
                return "generated";
            }
            return "success";
        }

        private static string NormalizeStatusKind(string kind, string statusText)
        {
            kind = (kind ?? string.Empty).Trim().ToLowerInvariant();
            switch (kind)
            {
                case "sent":
                case "pending":
                case "processing":
                case "warning":
                case "failed":
                case "generated":
                case "success":
                case "interrupted":
                    return kind;
                default:
                    return DetectStatusKind(statusText);
            }
        }

        private static void ApplyHistoryStatusColor(TextBlock block, string statusKind)
        {
            if (block == null) return;
            switch (statusKind)
            {
                case "sent":
                case "success":
                    block.Foreground = new SolidColorBrush(Color.FromRgb(39, 174, 96));
                    break;
                case "processing":
                case "pending":
                    block.Foreground = new SolidColorBrush(Color.FromRgb(47, 128, 237));
                    break;
                case "warning":
                case "interrupted":
                    block.Foreground = new SolidColorBrush(Color.FromRgb(242, 153, 74));
                    break;
                case "generated":
                    block.Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128));
                    break;
                default:
                    block.Foreground = new SolidColorBrush(Color.FromRgb(235, 87, 87));
                    break;
            }
        }

        private static DateTime TicksToDateTime(long ticks, DateTime fallback)
        {
            if (ticks <= 0) return fallback;
            try
            {
                return new DateTime(ticks);
            }
            catch
            {
                return fallback;
            }
        }
    }
}
