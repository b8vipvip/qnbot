using Bot.ChromeNs;
using BotLib;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace Bot.Options
{
    /// <summary>
    /// Shop-level product cache management (one cache per authenticated ShopKey).
    /// </summary>
    internal sealed class ShopStrategyOptionsControl : UserControl, IOptions
    {
        private string _seller;
        private readonly TextBlock _status;
        private readonly DataGrid _items;
        private readonly Button _refresh;
        private readonly TextBox _manualUrl;
        private readonly TextBox _manualTitle;

        public ShopStrategyOptionsControl(string seller)
        {
            _seller = seller;
            var root = new StackPanel { Margin = new Thickness(18) };
            root.Children.Add(new TextBlock
            {
                Text = "商品列表",
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 8)
            });
            root.Children.Add(new TextBlock
            {
                Text = "展示当前店铺的商品信息。Bot 每 7 天自动同步一次；可以点击“更新商品”立即获取。失败时保留已有商品与回复规则。",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10),
                Foreground = Brushes.Gray
            });
            _refresh = new Button
            {
                Content = "更新商品", Width = 112, Height = 30,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            _refresh.Click += async (s, e) => await RefreshNowAsync();
            root.Children.Add(_refresh);
            root.Children.Add(new TextBlock
            {
                Text = "缺少商品时，可临时补录本店商品链接。人工补录不代表全店自动同步成功。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 12, 0, 6)
            });
            var manualRow = new StackPanel { Orientation = Orientation.Horizontal };
            manualRow.Children.Add(new TextBlock
            {
                Text = "商品链接", Width = 65, VerticalAlignment = VerticalAlignment.Center
            });
            _manualUrl = new TextBox
            {
                Width = 260, Height = 27,
                ToolTip = "例如 https://item.taobao.com/item.htm?id=123456789012"
            };
            manualRow.Children.Add(_manualUrl);
            manualRow.Children.Add(new TextBlock
            {
                Text = "商品名称", Margin = new Thickness(12, 0, 0, 0),
                Width = 65, VerticalAlignment = VerticalAlignment.Center
            });
            _manualTitle = new TextBox { Width = 170, Height = 27 };
            manualRow.Children.Add(_manualTitle);
            var addManual = new Button
            {
                Content = "补录商品", Width = 84, Height = 27,
                Margin = new Thickness(8, 0, 0, 0)
            };
            addManual.Click += (s, e) =>
            {
                try
                {
                    var id = ShopProductCatalogService.AddManualProduct(
                        _seller, _manualUrl.Text, _manualTitle.Text);
                    _manualUrl.Clear();
                    _manualTitle.Clear();
                    Reload();
                    _status.Text = "已补录商品 ID：" + id
                        + "（人工补录，非全量自动同步）。" + Environment.NewLine + _status.Text;
                }
                catch (Exception ex)
                {
                    Log.Info("本店商品手动补录失败: seller=" + _seller
                        + ", error=" + ex.Message);
                    _status.Text = "补录失败：" + ex.Message;
                }
            };
            manualRow.Children.Add(addManual);
            root.Children.Add(manualRow);
            _status = new TextBlock
            {
                Margin = new Thickness(0, 12, 0, 10),
                TextWrapping = TextWrapping.Wrap
            };
            root.Children.Add(_status);
            _items = new DataGrid
            {
                IsReadOnly = true,
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                SelectionMode = DataGridSelectionMode.Single,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                EnableRowVirtualization = true,
                MinHeight = 280,
                RowHeight = 32,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                BorderBrush = new SolidColorBrush(Color.FromRgb(220, 226, 233)),
                BorderThickness = new Thickness(1)
            };
            _items.Columns.Add(new DataGridTextColumn
            {
                Header = "商品名称",
                Binding = new Binding("Title"),
                Width = new DataGridLength(3, DataGridLengthUnitType.Star)
            });
            _items.Columns.Add(new DataGridTextColumn
            {
                Header = "商品 ID",
                Binding = new Binding("ItemId"),
                Width = new DataGridLength(1.4, DataGridLengthUnitType.Star)
            });
            _items.Columns.Add(new DataGridTextColumn
            {
                Header = "商品链接",
                Binding = new Binding("Url"),
                Width = new DataGridLength(2, DataGridLengthUnitType.Star)
            });
            _items.Columns.Add(new DataGridTextColumn
            {
                Header = "商品状态",
                Binding = new Binding("Status"),
                Width = new DataGridLength(1, DataGridLengthUnitType.Star)
            });
            root.Children.Add(_items);
            var layout = new DockPanel { Margin = new Thickness(18) };
            // The grid fills the available height instead of becoming an unbounded
            // nested list inside a scroll viewer.
            var header = new StackPanel();
            foreach (UIElement element in root.Children.Cast<UIElement>().ToList())
            {
                root.Children.Remove(element);
                if (!ReferenceEquals(element, _items)) header.Children.Add(element);
            }
            DockPanel.SetDock(header, Dock.Top);
            layout.Children.Add(header);
            layout.Children.Add(_items);
            Content = layout;
            Reload();
            ShopProductCatalogService.EnsureScheduled(_seller);
        }

        private void Reload()
        {
            try
            {
                var catalog = ShopProductCatalogService.Load(_seller);
                _items.ItemsSource = catalog.Products;
                if (catalog.Products.Count == 0)
                {
                    _status.Text = "本店暂无商品，请确认千牛登录后点击“更新商品”。";
                }
                else if (catalog.Partial)
                {
                    _status.Text = "当前列表共 " + catalog.Products.Count + " 件（推荐商品/人工补录），不是经过验证的本店完整商品列表；"
                        + "本次完整同步未成功，系统会继续重试。缓存时间："
                        + catalog.UpdatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                        + (string.IsNullOrWhiteSpace(catalog.LastFullCatalogError) ? ""
                            : "；全量接口失败原因：" + catalog.LastFullCatalogError);
                }
                else
                {
                    _status.Text = "本店完整商品列表 " + catalog.Products.Count + " 件；同步时间："
                        + catalog.UpdatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                        + "；下次计划同步："
                        + catalog.UpdatedAtUtc.AddDays(7).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                        + (string.IsNullOrWhiteSpace(catalog.LastFullCatalogError) ? ""
                            : "；最近一次完整接口失败，仍保留上次完整列表：" + catalog.LastFullCatalogError);
                }
            }
            catch (Exception ex)
            {
                _status.Text = "读取本店商品缓存失败：" + ex.Message;
                _items.ItemsSource = null;
            }
        }

        private async Task RefreshNowAsync()
        {
            _refresh.IsEnabled = false;
            _status.Text = "正在请求当前店铺的千牛商品信息…";
            try
            {
                await ShopProductCatalogService.RefreshAsync(_seller, true);
                Reload();
                var snapshot = ShopProductCatalogService.Load(_seller);
                if (snapshot.Partial)
                    _status.Text = "尚未完成本店商品全量同步。仅拿到推荐商品，不能据此判断商品总数。"
                        + Environment.NewLine + _status.Text;
                else if (!string.IsNullOrWhiteSpace(snapshot.LastFullCatalogError))
                    _status.Text = "本次全量获取失败，保留旧完整列表。"
                        + Environment.NewLine + _status.Text;
                else
                    _status.Text = "本店商品已完整更新。" + Environment.NewLine + _status.Text;
            }
            catch (Exception ex)
            {
                Log.Info("店铺策略手动更新商品失败: seller=" + _seller + ", error=" + ex.Message);
                Reload();
                _status.Text = "更新失败：" + ex.Message + "。旧缓存与已设置的商品话术保留。";
            }
            finally { _refresh.IsEnabled = true; }
        }

        public OptionEnum OptionType { get { return OptionEnum.ShopStrategy; } }
        public void InitUI(string seller) { _seller = seller; Reload(); }
        public void Save(string seller) { /* Catalog updates only via explicit refresh. */ }
        public void RestoreDefault()
        {
            MessageBox.Show("为避免误删除商品缓存或商品话术，店铺策略不提供批量清空。");
        }
        public void NavHelp()
        {
            MessageBox.Show("商品 ID 是匹配下单专属回复的唯一键。编辑答案请到“自动回复规则 → 买家下单后自动发送”。");
        }
    }
}
