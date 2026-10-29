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
                Text = "酷狗店铺接待规则",
                FontSize = 19, FontWeight = FontWeights.SemiBold
            });
            header.Children.Add(new TextBlock
            {
                Text = "编辑前请核对真实教程地址。保存会校验字段并创建旧配置备份；不能通过JSON开启自动充值、关闭人工审核或改变其他店铺。",
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
            Task.Run(() => KugouReceptionConfigService.GetJson(_seller))
                .ContinueWith(t => Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (t.IsFaulted)
                    {
                        _status.Text = "读取失败：" + t.Exception.GetBaseException().Message;
                        return;
                    }
                    _editor.Text = t.Result ?? string.Empty;
                    _status.Text = "规则已加载；更改后点击「校验并保存」才会生效。";
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
                _status.Text = KugouReceptionConfigService.SaveJson(_seller, _editor.Text);
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
                _status.Text = KugouReceptionConfigService.RestoreLatestBackup(_seller);
                RefreshView();
            }
            catch (Exception ex)
            {
                _status.Text = "恢复失败：" + ex.Message;
            }
        }
    }
}
