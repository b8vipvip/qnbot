using Bot.ChromeNs;
using Microsoft.Win32;
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Bot.Knowledge
{
    // Read-only view: all audit events are buyer-hashed and shop-scoped.
    // Reviewing or exporting events never invokes a new AI request or changes policy.
    internal sealed class KnowledgeV2ReceptionAuditPage : UserControl, IKnowledgeV2Refreshable
    {
        private readonly string _seller;
        private readonly TextBox _output;
        private readonly TextBlock _status;

        public KnowledgeV2ReceptionAuditPage(string seller)
        {
            _seller = (seller ?? string.Empty).Trim();
            var root = new DockPanel { Margin = new Thickness(12) };
            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            header.Children.Add(new TextBlock
            {
                Text = "接待流程决策审计",
                FontSize = 19, FontWeight = FontWeights.SemiBold
            });
            header.Children.Add(new TextBlock
            {
                Text = "仅展示当前店铺最近七天至多300条脱敏决策；此页不会发送消息或自动修改知识库。",
                Foreground = Brushes.DimGray, Margin = new Thickness(0, 5, 0, 8),
                TextWrapping = TextWrapping.Wrap
            });
            var bar = new WrapPanel();
            var refresh = new Button
            {
                Content = "刷新记录", Width = 100, Height = 30,
                Margin = new Thickness(0, 0, 8, 0)
            };
            refresh.Click += (s, e) => RefreshView();
            bar.Children.Add(refresh);
            var export = new Button
            {
                Content = "导出脱敏JSON", Width = 130, Height = 30,
                Margin = new Thickness(0, 0, 8, 0)
            };
            export.Click += (s, e) => ExportAudit();
            bar.Children.Add(export);
            header.Children.Add(bar);
            _status = new TextBlock { Margin = new Thickness(0, 8, 0, 0), Foreground = Brushes.Gray };
            header.Children.Add(_status);
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);
            _output = new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                AcceptsTab = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                TextWrapping = TextWrapping.NoWrap,
                Background = new SolidColorBrush(Color.FromRgb(248, 249, 252))
            };
            root.Children.Add(_output);
            Content = root;
            Loaded += (s, e) => RefreshView();
        }

        public void RefreshView()
        {
            _status.Text = "正在读取当前店铺的脱敏接待决策...";
            Task.Run(() => KugouReceptionPolicy.GetAuditJson(_seller))
                .ContinueWith(t => Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (t.IsFaulted)
                    {
                        _status.Text = "读取失败";
                        _output.Text = t.Exception.GetBaseException().Message;
                        return;
                    }
                    _output.Text = t.Result ?? string.Empty;
                    _status.Text = "已刷新。WAIT=等待；REPLY=规则答复；SUPPRESS=禁止不安全回答。";
                })));
        }

        private void ExportAudit()
        {
            var dialog = new SaveFileDialog
            {
                Filter = "JSON文件|*.json|所有文件|*.*",
                FileName = "qnbot-reception-audit-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json"
            };
            if (dialog.ShowDialog() != true) return;
            try
            {
                var json = KugouReceptionPolicy.GetAuditJson(_seller);
                // The source is a shop-isolated, hashed audit: never export raw buyer messages.
                if (!json.TrimStart().StartsWith("[", StringComparison.Ordinal))
                {
                    MessageBox.Show("当前没有可导出的有效审计JSON。", "接待审计");
                    return;
                }
                File.WriteAllText(dialog.FileName, json, new UTF8Encoding(false));
                _status.Text = "脱敏审计导出成功。";
            }
            catch (Exception ex)
            {
                _status.Text = "导出失败：" + ex.Message;
            }
        }
    }
}
