using Bot.ChromeNs;
using BotLib;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace Bot.Options
{
    /// <summary>
    /// Edit the Bot-side shop catalog DIRECTLY in its grid. Local changes are
    /// overlaid on QN sync results without changing the actual Taobao listing.
    /// ItemId is immutable for existing rows because order-specific replies
    /// use that canonical ID; deleting a row only hides it from this Bot list.
    /// </summary>
    internal sealed class ShopStrategyOptionsControl : UserControl, IOptions
    {
        private string _seller;
        private readonly TextBlock _status;
        private readonly DataGrid _items;
        private readonly Button _refresh;
        private readonly DataGridTextColumn _idColumn;
        private readonly DataGridTextColumn _nameColumn;
        private ObservableCollection<ShopCatalogProduct> _rows;
        private List<ShopCatalogProduct> _baseline = new List<ShopCatalogProduct>();
        private HashSet<ShopCatalogProduct> _existingRows = new HashSet<ShopCatalogProduct>();

        public ShopStrategyOptionsControl(string seller)
        {
            _seller = seller;
            var layout = new DockPanel { Margin = new Thickness(18) };
            var header = new StackPanel();

            header.Children.Add(new TextBlock
            {
                Text = "商品列表",
                FontSize = 16, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 8)
            });
            header.Children.Add(new TextBlock
            {
                Text = "直接双击表格修改商品名称或链接；新增、删除后点击“保存商品列表”。"
                    + "修改仅影响Bot本地商品列表，不会更改或下架千牛店铺的真实商品。"
                    + "7天自动同步不会覆盖本地修改，订单专属话术仍按商品ID匹配。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 0, 0, 9)
            });
            var toolbar = new WrapPanel();
            _refresh = MakeButton("更新商品", 100, async (s, e) => await RefreshNowAsync());
            toolbar.Children.Add(_refresh);
            toolbar.Children.Add(MakeButton("新增商品", 100, (s, e) => AddRow()));
            toolbar.Children.Add(MakeButton("删除选中", 100, (s, e) => DeleteSelected()));
            toolbar.Children.Add(MakeButton("保存商品列表", 112, (s, e) => SaveFromToolbar()));
            toolbar.Children.Add(MakeButton("撤销未保存修改", 126, (s, e) => DiscardChanges()));
            header.Children.Add(toolbar);

            _status = new TextBlock
            {
                Margin = new Thickness(0, 10, 0, 9),
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(75, 86, 105))
            };
            header.Children.Add(_status);
            DockPanel.SetDock(header, Dock.Top);
            layout.Children.Add(header);

            _items = new DataGrid
            {
                IsReadOnly = false,
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                SelectionMode = DataGridSelectionMode.Single,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                EnableRowVirtualization = true,
                MinHeight = 260,
                RowHeight = 32,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                BorderBrush = new SolidColorBrush(Color.FromRgb(220, 226, 233)),
                BorderThickness = new Thickness(1)
            };
            _nameColumn = new DataGridTextColumn
            {
                Header = "商品名称（双击修改）",
                Binding = EditableBinding("Title"),
                Width = new DataGridLength(3, DataGridLengthUnitType.Star)
            };
            _idColumn = new DataGridTextColumn
            {
                Header = "商品ID（已有商品不可改）",
                Binding = EditableBinding("ItemId"),
                Width = new DataGridLength(1.65, DataGridLengthUnitType.Star)
            };
            _items.Columns.Add(_nameColumn);
            _items.Columns.Add(_idColumn);
            _items.Columns.Add(new DataGridTextColumn
            {
                Header = "商品链接（双击修改）",
                Binding = EditableBinding("Url"),
                Width = new DataGridLength(2.4, DataGridLengthUnitType.Star)
            });
            _items.Columns.Add(new DataGridTextColumn
            {
                Header = "状态", Binding = new Binding("Status"), IsReadOnly = true,
                Width = new DataGridLength(0.9, DataGridLengthUnitType.Star)
            });
            _items.Columns.Add(new DataGridTextColumn
            {
                Header = "来源", Binding = new Binding("SourceLabel"), IsReadOnly = true,
                Width = new DataGridLength(0.9, DataGridLengthUnitType.Star)
            });
            _items.BeginningEdit += (s, e) =>
            {
                var row = e.Row.Item as ShopCatalogProduct;
                if (e.Column == _idColumn && row != null && _existingRows.Contains(row))
                {
                    e.Cancel = true;
                    _status.Text = "已有商品ID不能修改，否则原订单专属答案将失去匹配依据。"
                        + "需要更换商品请新增新ID，再删除旧行。";
                }
            };
            layout.Children.Add(_items);
            Content = layout;
            Reload();
            ShopProductCatalogService.EnsureScheduled(_seller);
        }

        private static Button MakeButton(string label, double width,
            RoutedEventHandler handler)
        {
            var button = new Button
            {
                Content = label, Width = width, Height = 30,
                Margin = new Thickness(0, 0, 8, 0)
            };
            button.Click += handler;
            return button;
        }

        private static Binding EditableBinding(string path)
        {
            return new Binding(path)
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.LostFocus
            };
        }

        private static ShopCatalogProduct Clone(ShopCatalogProduct row)
        {
            return new ShopCatalogProduct
            {
                ItemId = row.ItemId,
                Title = row.Title,
                Url = row.Url,
                Status = row.Status,
                ManuallyAdded = row.ManuallyAdded,
                LocalManaged = row.LocalManaged
            };
        }

        private void CommitGrid()
        {
            _items.CommitEdit(DataGridEditingUnit.Cell, true);
            _items.CommitEdit(DataGridEditingUnit.Row, true);
        }

        private bool HasChanges()
        {
            CommitGrid();
            if (_rows == null || _rows.Count != _baseline.Count) return true;
            var baseline = _baseline.ToDictionary(x => x.ItemId, x => x, StringComparer.Ordinal);
            foreach (var row in _rows)
            {
                ShopCatalogProduct old;
                if (string.IsNullOrWhiteSpace(row.ItemId)
                    || !baseline.TryGetValue(row.ItemId, out old)
                    || !string.Equals(old.Title, row.Title, StringComparison.Ordinal)
                    || !string.Equals(old.Url, row.Url, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private void Reload()
        {
            try
            {
                var snapshot = ShopProductCatalogService.Load(_seller);
                _baseline = snapshot.Products.Select(Clone).ToList();
                _rows = new ObservableCollection<ShopCatalogProduct>(
                    snapshot.Products.Select(Clone));
                // Key identity is immutable only for rows loaded from storage,
                // not for new rows while the user is still entering their ID.
                _existingRows = new HashSet<ShopCatalogProduct>(_rows);
                _items.ItemsSource = _rows;

                var localCount = _rows.Count(x => x.ManuallyAdded || x.LocalManaged);
                var summary = "当前显示 " + _rows.Count + " 件商品；本地维护 " + localCount + " 件。";
                if (snapshot.Partial)
                    summary += " 千牛目前仅返回部分商品，不能据此认定全店总数；系统继续自动重试。";
                else if (snapshot.UpdatedAtUtc != DateTime.MinValue)
                    summary += " 最近完整同步："
                        + snapshot.UpdatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                        + "；下次计划更新："
                        + snapshot.UpdatedAtUtc.AddDays(7).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + "。";
                if (!string.IsNullOrWhiteSpace(snapshot.LastFullCatalogError))
                    summary += " 全量接口诊断：" + snapshot.LastFullCatalogError;
                _status.Text = summary;
            }
            catch (Exception ex)
            {
                _status.Text = "读取本店商品列表失败：" + ex.Message;
                _items.ItemsSource = null;
                _baseline.Clear();
                _existingRows.Clear();
                _rows = new ObservableCollection<ShopCatalogProduct>();
            }
        }

        private void AddRow()
        {
            CommitGrid();
            if (_rows == null) return;
            var row = new ShopCatalogProduct
            {
                ItemId = "", Title = "", Url = "",
                Status = "本地新增", ManuallyAdded = true, LocalManaged = true
            };
            _rows.Add(row);
            _items.SelectedItem = row;
            _items.ScrollIntoView(row);
            _items.CurrentCell = new DataGridCellInfo(row, _nameColumn);
            _items.Focus();
            _items.BeginEdit();
            _status.Text = "已新增可编辑行：填写商品名称、商品ID和完整商品链接，"
                + "随后点击“保存商品列表”（商品链接中的id需与商品ID一致）。";
        }

        private void DeleteSelected()
        {
            CommitGrid();
            var row = _items.SelectedItem as ShopCatalogProduct;
            if (row == null) { _status.Text = "请先在商品列表中选择要删除的行。"; return; }
            if (MessageBox.Show("从Bot本地商品列表移除“" + row.Title + "”？"
                + Environment.NewLine + "不会删除千牛真实商品，也不会清空该商品已经设置的自动回复。"
                + Environment.NewLine + "确认后还需点击“保存商品列表”。",
                "删除本地商品", MessageBoxButton.YesNo, MessageBoxImage.Question)
                != MessageBoxResult.Yes) return;
            _rows.Remove(row);
            _status.Text = "已从表格移除所选商品，点击“保存商品列表”后生效；"
                + "或点击“撤销未保存修改”恢复。";
        }

        private void DiscardChanges()
        {
            if (HasChanges() && MessageBox.Show("放弃本页所有尚未保存的商品编辑？",
                "撤销修改", MessageBoxButton.YesNo, MessageBoxImage.Question)
                != MessageBoxResult.Yes) return;
            Reload();
        }

        private void SaveFromToolbar()
        {
            try
            {
                Save(_seller);
            }
            catch (Exception ex)
            {
                Log.Info("商品列表直接编辑保存失败: seller=" + _seller
                    + ", error=" + ex.Message);
                MessageBox.Show("保存商品列表失败：" + ex.Message,
                    "商品列表", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async Task RefreshNowAsync()
        {
            if (HasChanges() && MessageBox.Show("尚有未保存的商品编辑。"
                + "继续更新将丢弃这些草稿，是否继续？",
                "更新商品", MessageBoxButton.YesNo, MessageBoxImage.Question)
                != MessageBoxResult.Yes) return;
            _refresh.IsEnabled = false;
            _status.Text = "正在通过当前店铺千牛会话获取商品…";
            try
            {
                await ShopProductCatalogService.RefreshAsync(_seller, true);
                Reload();
                var snapshot = ShopProductCatalogService.Load(_seller);
                _status.Text = (snapshot.Partial
                    ? "只收到部分推荐商品，未完成全量同步。本地修改和隐藏记录仍保留。"
                    : (!string.IsNullOrWhiteSpace(snapshot.LastFullCatalogError)
                        ? "本次全量更新失败，已保留旧完整列表和本地修改。"
                        : "本店商品已完整同步，本地手动修改和隐藏仍有效。"))
                    + Environment.NewLine + _status.Text;
            }
            catch (Exception ex)
            {
                Log.Info("店铺策略更新商品失败: seller=" + _seller + ", error=" + ex.Message);
                Reload();
                _status.Text = "更新失败：" + ex.Message
                    + "；原有列表和商品回复规则已保留。" + Environment.NewLine + _status.Text;
            }
            finally { _refresh.IsEnabled = true; }
        }

        public OptionEnum OptionType { get { return OptionEnum.ShopStrategy; } }
        public void InitUI(string seller) { _seller = seller; Reload(); }
        public void Save(string seller)
        {
            if (_rows == null || !HasChanges()) return;
            ShopProductCatalogService.SaveManagedCatalog(seller, _rows.ToList(), _baseline);
            Reload();
            _status.Text = "商品列表已保存，仅影响当前店铺Bot本地商品管理；"
                + "不会修改千牛真实商品，也不会删除商品专属回复。"
                + Environment.NewLine + _status.Text;
        }
        public void RestoreDefault()
        {
            MessageBox.Show("本页不提供批量删除商品。请在列表中选择商品进行删除，"
                + "或使用“撤销未保存修改”。");
        }
        public void NavHelp()
        {
            MessageBox.Show("双击商品名称和商品链接可修改，新增商品可填写新ID；"
                + "已有商品ID不可修改，以免影响订单精确匹配。"
                + "删除仅隐藏Bot本地目录的商品，不会下架千牛真实商品。"
                + "点击“保存商品列表”或底部“保存设置”提交更改。");
        }
    }
}
