using Bot.ChromeNs;
using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Bot.Knowledge
{
    /// <summary>
    /// Shop-scoped reception workflow editor. Validated saves publish immediately;
    /// the Agent does not wait for administrator approval or a manual activation stage.
    /// </summary>
    internal sealed class KnowledgeV2WorkflowDraftPage : UserControl, IKnowledgeV2Refreshable
    {
        private readonly string _seller;
        private readonly string _shopKey;
        private readonly TextBlock _status;
        private readonly TextBox _editor;

        public KnowledgeV2WorkflowDraftPage(string seller, string shopKey)
        {
            _seller = (seller ?? string.Empty).Trim();
            _shopKey = (shopKey ?? string.Empty).Trim();
            var root = new DockPanel { Margin = new Thickness(12) };
            var top = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            top.Children.Add(new TextBlock { Text = "Reception Agent 接待流程", FontSize = 20,
                FontWeight = FontWeights.SemiBold });
            top.Children.Add(new TextBlock {
                Text = "按当前店铺管理商品/SKU 接待流程。保存前自动校验；校验通过后立即发布并生效，无需管理员审批。发布版本自动留档，可直接回滚。流程仅能编排等待、补充证据、转人工、只读查询和受现有发送安全门控约束的回复意图；不会授予付款、充值、退款、账号修改或实际履约权限。",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 9),
                Foreground = Brushes.DimGray
            });
            var buttons = new WrapPanel();
            var reload = new Button { Content = "读取当前发布版本", Width = 145, Height = 30,
                Margin = new Thickness(0, 0, 8, 0) };
            reload.Click += (s, e) => RefreshView();
            buttons.Children.Add(reload);

            var publish = new Button { Content = "校验并立即发布", Width = 130, Height = 30,
                Margin = new Thickness(0, 0, 8, 0) };
            publish.Click += (s, e) => Publish();
            buttons.Children.Add(publish);

            var rollback = new Button { Content = "回滚上一发布版本", Width = 140, Height = 30 };
            rollback.Click += (s, e) => Rollback();
            buttons.Children.Add(rollback);

            top.Children.Add(buttons);
            _status = new TextBlock { Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
            top.Children.Add(_status);
            DockPanel.SetDock(top, Dock.Top);
            root.Children.Add(top);

            _editor = new TextBox {
                AcceptsReturn = true, AcceptsTab = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = new FontFamily("Consolas"), FontSize = 12
            };
            root.Children.Add(_editor);
            Content = root;
            Loaded += (s, e) => RefreshView();
        }

        public void RefreshView()
        {
            _status.Text = "读取当前店铺已发布流程...";
            Task.Run(() => ReceptionWorkflowCatalogService.GetActiveJson(_seller, _shopKey))
                .ContinueWith(t => Dispatcher.BeginInvoke(new Action(() => {
                    if (t.IsFaulted) {
                        _status.Text = "读取失败：" + t.Exception.GetBaseException().Message;
                        return;
                    }
                    _editor.Text = t.Result;
                    _status.Text = "已读取当前发布版本；目录修订号以 JSON 中的 catalog_revision 为准。";
                })));
        }

        private void Publish()
        {
            try
            {
                _status.Text = ReceptionWorkflowCatalogService.Publish(_seller, _editor.Text, _shopKey);
                RefreshView(); // Reload the incremented active revision before the next edit.
            }
            catch (Exception ex)
            {
                _status.Text = "发布失败；当前生效版本未被覆盖：" + ex.Message;
            }
        }

        private void Rollback()
        {
            if (MessageBox.Show("立即回滚到上一有效发布版本？回滚会生成新的修订版本。",
                "回滚接待流程", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            try
            {
                _status.Text = ReceptionWorkflowCatalogService.RollbackToPreviousPublished(_seller, _shopKey);
                RefreshView();
            }
            catch (Exception ex)
            {
                _status.Text = "回滚失败；当前生效版本未被覆盖：" + ex.Message;
            }
        }
    }
}
