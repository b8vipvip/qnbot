using Bot.ChromeNs;
using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Bot.Knowledge
{
    internal sealed class KnowledgeV2ReceptionRulesPage : UserControl, IKnowledgeV2Refreshable
    {
        private readonly string _seller;
        private readonly TextBox _editor;
        private readonly TextBlock _status;

        public KnowledgeV2ReceptionRulesPage(string seller)
        {
            _seller = (seller ?? string.Empty).Trim();
            var root = new DockPanel { Margin = new Thickness(12) };
            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            header.Children.Add(new TextBlock
            {
                Text = "店铺接待规则",
                FontSize = 19, FontWeight = FontWeights.SemiBold
            });
            header.Children.Add(new TextBlock
            {
                Text = "此配置只属于当前稳定 ShopKey。默认关闭且不含任何店铺商品ID、知识ID、教程链接或业务话术；请先导入/填写经核验的私有业务资料。保存会校验字段并创建备份；不能通过JSON开启自动充值或关闭人工审核。",
                Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 5, 0, 10)
            });
            var buttons = new WrapPanel();
            var refresh = new Button { Content = "读取规则", Width = 98, Height = 30, Margin = new Thickness(0, 0, 8, 0) };
            refresh.Click += (s, e) => RefreshView();
            buttons.Children.Add(refresh);
            var save = new Button { Content = "校验并保存", Width = 110, Height = 30, Margin = new Thickness(0, 0, 8, 0) };
            save.Click += (s, e) => Save();
            buttons.Children.Add(save);
            var restore = new Button { Content = "恢复上个备份", Width = 118, Height = 30, Margin = new Thickness(0, 0, 8, 0) };
            restore.Click += (s, e) => Restore();
            buttons.Children.Add(restore);
            header.Children.Add(buttons);
            _status = new TextBlock { Margin = new Thickness(0, 8, 0, 0), Foreground = Brushes.DimGray };
            header.Children.Add(_status);
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);
            _editor = new TextBox
            {
                AcceptsReturn = true,
                AcceptsTab = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Background = new SolidColorBrush(Color.FromRgb(248, 249, 252))
            };
            root.Children.Add(_editor);
            Content = root;
            Loaded += (s, e) => RefreshView();
        }

        public void RefreshView()
        {
            _status.Text = "正在读取当前店铺的规则...";
            Task.Run(() => ShopReceptionConfigService.GetJson(_seller))
                .ContinueWith(t => Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (t.IsFaulted)
                    {
                        _status.Text = "读取失败：" + t.Exception.GetBaseException().Message;
                        return;
                    }
                    _editor.Text = t.Result ?? string.Empty;
                    _status.Text = "已加载当前 ShopKey 配置。若 enabled=false，专属接待保持关闭；请核对私有商品ID、知识隔离ID、教程链接、视觉规则和提示词后再启用。";
                })));
        }

        private void Save()
        {
            if (MessageBox.Show(
                "确定保存当前店铺接待规则？系统将校验安全字段、保留备份，不会立即触发任何买家回复或充值。",
                "确认保存", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            try
            {
                _status.Text = ShopReceptionConfigService.SaveJson(_seller, _editor.Text);
            }
            catch (Exception ex)
            {
                _status.Text = "校验或保存失败，原规则未变更：" + ex.Message;
            }
        }

        private void Restore()
        {
            if (MessageBox.Show(
                "确定恢复上一份规则备份？当前版本也会被单独备份。",
                "恢复历史版本", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            try
            {
                _status.Text = ShopReceptionConfigService.RestoreLatestBackup(_seller);
                RefreshView();
            }
            catch (Exception ex)
            {
                _status.Text = "恢复失败：" + ex.Message;
            }
        }
    }
}
