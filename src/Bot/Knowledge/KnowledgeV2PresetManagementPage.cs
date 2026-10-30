using Bot.ChromeNs;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace Bot.Knowledge
{
    /// <summary>
    /// Edit shop-scoped built-in fixed replies without recompiling the client.
    /// Only the product-link trigger is wired in the current receive pipeline.
    /// </summary>
    internal sealed class KnowledgeV2PresetManagementPage : UserControl, IKnowledgeV2Searchable, IKnowledgeV2Refreshable
    {
        private readonly string _seller;
        private readonly ObservableCollection<BotReplyPreset> _items = new ObservableCollection<BotReplyPreset>();
        private readonly DataGrid _grid;
        private readonly TextBox _name;
        private readonly TextBox _reply;
        private readonly CheckBox _enabled;
        private readonly TextBlock _status;
        private string _filter = string.Empty;

        public KnowledgeV2PresetManagementPage(string seller)
        {
            _seller = seller ?? string.Empty;
            var root = new Grid { Margin = new Thickness(4) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            foreach (var button in new[]
            {
                MakeButton("新增", (s,e)=>Add()),
                MakeButton("保存修改", (s,e)=>Save()),
                MakeButton("删除", (s,e)=>Delete()),
                MakeButton("刷新", (s,e)=>RefreshView())
            }) toolbar.Children.Add(button);
            Grid.SetRow(toolbar, 0);
            root.Children.Add(toolbar);

            _grid = new DataGrid
            {
                ItemsSource = _items, IsReadOnly = true, AutoGenerateColumns = false,
                SelectionMode = DataGridSelectionMode.Single, CanUserAddRows = false
            };
            _grid.Columns.Add(new DataGridTextColumn { Header = "ID", Binding = new Binding("Id"), Width = 175 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "名称", Binding = new Binding("Name"), Width = 155 });
            _grid.Columns.Add(new DataGridCheckBoxColumn { Header = "启用", Binding = new Binding("Enabled"), Width = 65 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "触发条件", Binding = new Binding("Trigger"), Width = 175 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "话术内容", Binding = new Binding("Text"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            _grid.SelectionChanged += delegate { LoadSelected(); };
            Grid.SetRow(_grid, 1);
            root.Children.Add(_grid);

            var editor = new StackPanel { Margin = new Thickness(2, 10, 2, 0) };
            editor.Children.Add(new TextBlock { Text = "编辑当前话术", FontSize = 17, FontWeight = FontWeights.SemiBold });
            editor.Children.Add(new TextBlock { Text = "当前仅支持“买家发送商品链接”这一真实触发器。多条启用话术按消息稳定选择，全部禁用后不发送此预设回复。", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray });
            editor.Children.Add(new TextBlock { Text = "名称", Margin = new Thickness(0, 8, 0, 3) });
            _name = new TextBox { Height = 28, MaxLength = 100 };
            editor.Children.Add(_name);
            editor.Children.Add(new TextBlock { Text = "回复内容", Margin = new Thickness(0, 8, 0, 3) });
            _reply = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 108, MaxLength = 4000 };
            editor.Children.Add(_reply);
            _enabled = new CheckBox { Content = "启用此话术", Margin = new Thickness(0, 8, 0, 3) };
            editor.Children.Add(_enabled);
            Grid.SetRow(editor, 2);
            root.Children.Add(editor);

            _status = new TextBlock { Foreground = Brushes.DimGray, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(_status, 3);
            root.Children.Add(_status);
            Content = root;
            Loaded += delegate { RefreshView(); };
        }

        private static Button MakeButton(string label, RoutedEventHandler click)
        {
            var button = new Button { Content = label, Width = label == "保存修改" ? 94 : 70, Height = 30, Margin = new Thickness(0, 0, 8, 0) };
            button.Click += click;
            return button;
        }

        public void ApplyGlobalSearch(string text)
        {
            _filter = (text ?? string.Empty).Trim();
            ApplyFilter();
        }

        public void RefreshView()
        {
            if (_seller.Length == 0) { _status.Text = "未识别当前店铺，话术管理不可用。"; return; }
            try
            {
                _all = BotReplyPresetService.GetPresets(_seller);
                ApplyFilter();
                _status.Text = "已加载店铺独立话术 " + _all.Count
                    + " 条；启用 " + _all.Count(x => x.Enabled) + " 条。修改保存后对新消息立即生效。";
            }
            catch (Exception ex) { _status.Text = "加载失败：" + ex.Message; }
        }

        private List<BotReplyPreset> _all = new List<BotReplyPreset>();

        private void ApplyFilter()
        {
            var selectedId = (_grid.SelectedItem as BotReplyPreset)?.Id;
            _items.Clear();
            foreach (var item in _all.Where(x => _filter.Length == 0 ||
                (x.Name ?? "").IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                (x.Text ?? "").IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                (x.Id ?? "").IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0))
                _items.Add(item);
            if (selectedId != null)
                _grid.SelectedItem = _items.FirstOrDefault(x => x.Id == selectedId);
            if (_grid.SelectedItem == null && _items.Count > 0)
                _grid.SelectedIndex = 0;
        }

        private void LoadSelected()
        {
            var item = _grid.SelectedItem as BotReplyPreset;
            _name.Text = item == null ? string.Empty : item.Name ?? string.Empty;
            _reply.Text = item == null ? string.Empty : item.Text ?? string.Empty;
            _enabled.IsChecked = item != null && item.Enabled;
        }

        private void Add()
        {
            var item = new BotReplyPreset
            {
                Id = Guid.NewGuid().ToString("N"), Name = "新话术",
                Trigger = "买家消息包含商品链接", Description = "管理员新建",
                Text = "请问想了解哪方面？", Enabled = false,
                UpdatedAt = DateTime.Now
            };
            _all.Add(item);
            ApplyFilter();
            _grid.SelectedItem = item;
            _status.Text = "新话术已建立在编辑区，点击“保存修改”才会生效。";
        }

        private void Save()
        {
            var current = _grid.SelectedItem as BotReplyPreset;
            if (current == null) { _status.Text = "请选择话术后保存。"; return; }
            current.Name = (_name.Text ?? "").Trim();
            current.Text = (_reply.Text ?? "").Trim();
            current.Enabled = _enabled.IsChecked == true;
            current.UpdatedAt = DateTime.Now;
            try
            {
                BotReplyPresetService.SavePresets(_seller, _all);
                RefreshView();
                _status.Text = "话术已按店铺保存，新消息立即生效；历史已发送内容不变。";
            }
            catch (Exception ex) { _status.Text = "保存失败：" + ex.Message; }
        }

        private void Delete()
        {
            var current = _grid.SelectedItem as BotReplyPreset;
            if (current == null) return;
            if (MessageBox.Show(Window.GetWindow(this), "确认删除话术“" + current.Name + "”？历史 Bot 回复的原始来源不会被改写。",
                "内置话术管理", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            var next = _all.Where(x => x.Id != current.Id).ToList();
            try
            {
                BotReplyPresetService.SavePresets(_seller, next);
                RefreshView();
                _status.Text = "已删除当前店铺的话术，历史发送记录仍保留。";
            }
            catch (Exception ex) { _status.Text = "删除失败：" + ex.Message; }
        }
    }
}
