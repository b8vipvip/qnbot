using Bot.ChromeNs;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Bot
{
    public partial class App
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            Knowledge.BusinessPolicyProfileUi.InitializeForApp();
            base.OnStartup(e);
        }
    }
}

namespace Bot.Knowledge
{
    internal static class BusinessPolicyProfileUi
    {
        private static int _initialized;

        public static void InitializeForApp()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
            EventManager.RegisterClassHandler(
                typeof(StorePromptProfileWindow),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnStoreWindowLoaded),
                true);
        }

        private static void OnStoreWindowLoaded(object sender, RoutedEventArgs e)
        {
            var window = sender as StorePromptProfileWindow;
            if (window == null) return;
            var save = GetField<Button>(window, "_save");
            var panel = save == null ? null : save.Parent as Panel;
            if (panel == null) return;
            foreach (var child in panel.Children)
            {
                var existing = child as Button;
                if (existing != null && Convert.ToString(existing.Tag) == "business-policy-json") return;
            }

            var button = new Button
            {
                Content = "运行策略JSON",
                Width = 112,
                Height = 30,
                Margin = new Thickness(0, 0, 8, 0),
                Tag = "business-policy-json",
                ToolTip = "编辑当前店铺私有的业务识别正则、流程提示、发送前校验和转人工例外。默认模板为空白，不含商家事实；保存前会校验并备份。保存后约2秒生效。"
            };
            button.Click += (s, args) =>
            {
                var editor = new BusinessPolicyProfileWindow { Owner = window };
                editor.ShowDialog();
            };
            var index = panel.Children.IndexOf(save);
            panel.Children.Insert(index < 0 ? 0 : index, button);
        }

        private static T GetField<T>(object source, string name) where T : class
        {
            if (source == null) return null;
            var field = source.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            return field == null ? null : field.GetValue(source) as T;
        }
    }

    internal sealed class BusinessPolicyProfileWindow : Window
    {
        private readonly TextBox _editor;
        private readonly TextBlock _status;

        public BusinessPolicyProfileWindow()
        {
            Title = "运行策略 JSON";
            Width = 980;
            Height = 760;
            MinWidth = 760;
            MinHeight = 560;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            var root = new Grid { Margin = new Thickness(14) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Content = root;

            var intro = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(242, 247, 255)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(196, 216, 245)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 10),
                Child = new TextBlock
                {
                    Text = "这里配置当前店铺私有的业务识别正则、流程提示、发送前校验和转人工例外。"
                        + "安装包默认模板不包含任何商家业务事实，空白正则表示不启用该项。请按当前店铺单独配置；保存后约2秒生效，保存前会校验并备份旧文件。",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brushes.DimGray
                }
            };
            Grid.SetRow(intro, 0);
            root.Children.Add(intro);

            _editor = new TextBox
            {
                AcceptsReturn = true,
                AcceptsTab = true,
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12.5
            };
            Grid.SetRow(_editor, 1);
            root.Children.Add(_editor);

            var footer = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _status = new TextBlock
            {
                Text = "配置文件：" + BusinessPolicyProfileService.GetUserPath(),
                Foreground = Brushes.DimGray,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
            Grid.SetColumn(_status, 0);
            footer.Children.Add(_status);

            var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(CreateButton("导入", 68, Import));
            buttons.Children.Add(CreateButton("导出", 68, Export));
            buttons.Children.Add(CreateButton("格式化", 76, Format));
            buttons.Children.Add(CreateButton("恢复默认", 88, RestoreDefault));
            buttons.Children.Add(CreateButton("打开目录", 88, OpenDirectory));
            buttons.Children.Add(CreateButton("保存", 76, Save));
            var close = CreateButton("关闭", 76, () => Close());
            close.Margin = new Thickness(0);
            buttons.Children.Add(close);
            Grid.SetColumn(buttons, 1);
            footer.Children.Add(buttons);
            Grid.SetRow(footer, 2);
            root.Children.Add(footer);

            LoadCurrent();
        }

        private static Button CreateButton(string text, double width, Action action)
        {
            var button = new Button
            {
                Content = text,
                Width = width,
                Height = 30,
                Margin = new Thickness(0, 0, 8, 0)
            };
            button.Click += (s, e) => action();
            return button;
        }

        private void LoadCurrent()
        {
            try
            {
                _editor.Text = BusinessPolicyProfileService.GetJson();
                _status.Text = "已加载：" + BusinessPolicyProfileService.GetUserPath();
                _status.Foreground = Brushes.DimGray;
            }
            catch (Exception ex)
            {
                _status.Text = "加载失败：" + ex.Message;
                _status.Foreground = Brushes.Firebrick;
            }
        }

        private void Format()
        {
            try
            {
                _editor.Text = JToken.Parse(_editor.Text).ToString(Formatting.Indented);
                SetSuccess("JSON格式化完成，尚未保存。");
            }
            catch (Exception ex)
            {
                ShowError("JSON格式错误：" + ex.Message);
            }
        }

        private void Save()
        {
            try
            {
                var backup = BusinessPolicyProfileService.SaveJson(_editor.Text);
                _editor.Text = BusinessPolicyProfileService.GetJson();
                SetSuccess("保存成功，约2秒内生效。" + (string.IsNullOrWhiteSpace(backup) ? string.Empty : " 已备份：" + backup));
            }
            catch (Exception ex)
            {
                ShowError("保存失败：" + ex.Message);
            }
        }

        private void RestoreDefault()
        {
            if (MessageBox.Show("恢复安装包默认运行策略？当前文件会先自动备份。", "恢复默认",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            try
            {
                var backup = BusinessPolicyProfileService.RestoreDefault();
                LoadCurrent();
                SetSuccess("已恢复默认策略。" + (string.IsNullOrWhiteSpace(backup) ? string.Empty : " 原文件备份：" + backup));
            }
            catch (Exception ex)
            {
                ShowError("恢复默认失败：" + ex.Message);
            }
        }

        private void Import()
        {
            var dialog = new OpenFileDialog
            {
                Title = "导入运行策略 JSON",
                Filter = "JSON文件 (*.json)|*.json",
                Multiselect = false
            };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                _editor.Text = File.ReadAllText(dialog.FileName, Encoding.UTF8);
                Format();
                SetSuccess("已读取 " + Path.GetFileName(dialog.FileName) + "，请点击保存后生效。");
            }
            catch (Exception ex)
            {
                ShowError("导入失败：" + ex.Message);
            }
        }

        private void Export()
        {
            var dialog = new SaveFileDialog
            {
                Title = "导出运行策略 JSON",
                Filter = "JSON文件 (*.json)|*.json",
                FileName = "qianniu-business-policy-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json",
                AddExtension = true,
                DefaultExt = ".json"
            };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                var formatted = JToken.Parse(_editor.Text).ToString(Formatting.Indented);
                File.WriteAllText(dialog.FileName, formatted, new UTF8Encoding(false));
                SetSuccess("已导出：" + dialog.FileName);
            }
            catch (Exception ex)
            {
                ShowError("导出失败：" + ex.Message);
            }
        }

        private void OpenDirectory()
        {
            try
            {
                var directory = Path.GetDirectoryName(BusinessPolicyProfileService.GetUserPath());
                Directory.CreateDirectory(directory);
                Process.Start("explorer.exe", directory);
            }
            catch (Exception ex)
            {
                ShowError("打开目录失败：" + ex.Message);
            }
        }

        private void SetSuccess(string text)
        {
            _status.Text = text;
            _status.Foreground = Brushes.SeaGreen;
        }

        private void ShowError(string text)
        {
            _status.Text = text;
            _status.Foreground = Brushes.Firebrick;
            MessageBox.Show(text, "运行策略 JSON", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
