using Bot.ChromeNs;
using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Bot.Knowledge
{
    /// <summary>Draft-only generic flow editor: no publish/send action is available.</summary>
    internal sealed class KnowledgeV2WorkflowDraftPage : UserControl, IKnowledgeV2Refreshable
    {
        private readonly string _seller;
        private readonly TextBlock _status;
        private readonly TextBox _editor;

        public KnowledgeV2WorkflowDraftPage(string seller)
        {
            _seller = (seller ?? string.Empty).Trim();
            var root = new DockPanel { Margin = new Thickness(12) };
            var top = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            top.Children.Add(new TextBlock { Text = "接待流程草稿", FontSize = 20,
                FontWeight = FontWeights.SemiBold });
            top.Children.Add(new TextBlock {
                Text = "通用店铺/商品/SKU 分流草稿。仅支持安全动作和证据依赖；保存不会生效或触发付款、发送、退款等业务操作。须完成审核和灰度验收才能激活。",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 9),
                Foreground = Brushes.DimGray
            });
            var buttons = new WrapPanel();
            var reload = new Button { Content = "读取草稿", Width = 100, Height = 30,
                Margin = new Thickness(0, 0, 8, 0) };
            reload.Click += (s, e) => RefreshView();
            buttons.Children.Add(reload);
            var save = new Button { Content = "校验并保存草稿", Width = 146, Height = 30,
                Margin = new Thickness(0, 0, 8, 0) };
            save.Click += (s, e) => Save();
            buttons.Children.Add(save);
            var restore = new Button { Content = "恢复备份草稿", Width = 125, Height = 30 };
            restore.Click += (s, e) => Restore();
            buttons.Children.Add(restore);
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
            _status.Text = "读取当前店铺流程草稿...";
            Task.Run(() => ReceptionWorkflowCatalogService.GetDraftJson(_seller))
                .ContinueWith(t => Dispatcher.BeginInvoke(new Action(() => {
                    if (t.IsFaulted) { _status.Text = "读取失败：" + t.Exception.GetBaseException().Message; return; }
                    _editor.Text = t.Result;
                    _status.Text = "草稿已读取；未发布。";
                })));
        }

        private void Save()
        {
            if (MessageBox.Show("校验并保存流程草稿？不会影响正在运行的接待流程。",
                "保存草稿", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            try { _status.Text = ReceptionWorkflowCatalogService.SaveDraft(_seller, _editor.Text); }
            catch (Exception ex) { _status.Text = "草稿未保存：" + ex.Message; }
        }

        private void Restore()
        {
            if (MessageBox.Show("恢复上个流程草稿备份？当前草稿也会先备份。",
                "恢复草稿", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            try {
                _status.Text = ReceptionWorkflowCatalogService.RestoreLatestDraft(_seller);
                RefreshView();
            }
            catch (Exception ex) { _status.Text = "恢复失败：" + ex.Message; }
        }
    }
}
