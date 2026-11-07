using Bot.ChromeNs;
using BotLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Bot.Options
{
    /// <summary>
    /// Product-specific answers are edited separately from the generic order template.
    /// A dropdown entry must come from this seller's cached QN catalog.
    /// </summary>
    internal sealed class ShopOrderProductRulesUi : StackPanel
    {
        private readonly string _seller;
        private readonly ComboBox _products;
        private readonly TextBox _reply;
        private readonly TextBlock _status;
        private readonly List<ShopOrderProductReply> _rules;
        private string _selectedId;
        private bool _changing;
        private bool _dirty;
        private bool _hasChanges;

        internal ShopOrderProductRulesUi(string seller)
        {
            _seller = seller;
            _rules = ShopProductCatalogService.GetOrderRules(seller);

            Children.Add(new TextBlock
            {
                Text = "不同商品的下单自动回复",
                FontWeight = FontWeights.SemiBold,
                FontSize = 14,
                Margin = new Thickness(0, 12, 0, 8)
            });
            Children.Add(new TextBlock
            {
                Text = "从当前店铺商品列表选择商品，每个商品可设置独立下单答案。精确匹配订单商品ID时优先使用此答案；未配置时按上面的固定预设或HTTP规则处理。此处不影响买家发送商品链接后的话术。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 0, 0, 8)
            });
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock
            {
                Text = "选择本店商品", Width = 90, VerticalAlignment = VerticalAlignment.Center
            });
            _products = new ComboBox { MinWidth = 290, Width = 400, Height = 27 };
            _products.SelectionChanged += ProductChanged;
            row.Children.Add(_products);
            var refresh = new Button
            {
                Content = "获取商品", Width = 86, Height = 27,
                Margin = new Thickness(10, 0, 0, 0)
            };
            refresh.Click += async (s, e) => await RefreshNowAsync(refresh);
            row.Children.Add(refresh);
            Children.Add(row);
            _reply = new TextBox
            {
                Margin = new Thickness(90, 9, 0, 0), MinHeight = 74, MaxHeight = 130,
                AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(6)
            };
            _reply.TextChanged += (s, e) => { if (!_changing && _selectedId != null) _dirty = true; };
            Children.Add(_reply);
            _status = new TextBlock
            {
                Margin = new Thickness(90, 5, 0, 0),
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap
            };
            Children.Add(_status);
            ReloadProducts(null);
        }

        private void FlushDraft()
        {
            if (_selectedId == null || !_dirty) return;
            var existing = _rules.FirstOrDefault(x =>
                string.Equals(x.ItemId, _selectedId, StringComparison.Ordinal));
            var text = (_reply.Text ?? string.Empty).Trim();
            if ((existing == null && text.Length == 0) ||
                (existing != null && string.Equals(existing.Reply, text, StringComparison.Ordinal)))
            {
                _dirty = false;
                return;
            }
            if (existing != null) _rules.Remove(existing);
            _hasChanges = true;
            if (text.Length > 0) _rules.Add(new ShopOrderProductReply
            {
                ItemId = _selectedId, Reply = text
            });
            _dirty = false;
        }

        private void ProductChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_changing) return;
            FlushDraft();
            var product = _products.SelectedItem as ShopCatalogProduct;
            _selectedId = product == null ? null : product.ItemId;
            _changing = true;
            try
            {
                var rule = _rules.FirstOrDefault(x => x.ItemId == _selectedId);
                _reply.Text = rule == null ? string.Empty : rule.Reply;
                _reply.IsEnabled = _selectedId != null;
            }
            finally { _changing = false; _dirty = false; }
        }

        private void ReloadProducts(string preferredId)
        {
            _changing = true;
            try
            {
                var snapshot = ShopProductCatalogService.Load(_seller);
                _products.Items.Clear();
                foreach (var product in snapshot.Products) _products.Items.Add(product);
                var selected = snapshot.Products.FirstOrDefault(x => x.ItemId == preferredId);
                _products.SelectedItem = selected ?? snapshot.Products.FirstOrDefault();
                var rule = _rules.FirstOrDefault(x => x.ItemId ==
                    ((_products.SelectedItem as ShopCatalogProduct) == null
                        ? null : ((ShopCatalogProduct)_products.SelectedItem).ItemId));
                _selectedId = (_products.SelectedItem as ShopCatalogProduct) == null
                    ? null : ((ShopCatalogProduct)_products.SelectedItem).ItemId;
                _reply.Text = rule == null ? string.Empty : rule.Reply;
                _reply.IsEnabled = _selectedId != null;
                _status.Text = snapshot.Products.Count == 0
                    ? "尚无商品缓存，请点击“获取商品”；千牛不可用时不会清空已有答案。"
                    : "已缓存 " + snapshot.Products.Count + " 件商品，最近更新："
                      + snapshot.UpdatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            }
            finally { _changing = false; _dirty = false; }
        }

        private async Task RefreshNowAsync(Button button)
        {
            FlushDraft();
            button.IsEnabled = false;
            _status.Text = "正在通过当前店铺千牛会话获取商品…";
            try
            {
                var count = await ShopProductCatalogService.RefreshAsync(_seller, true);
                ReloadProducts(_selectedId);
                _status.Text = "成功更新本店商品 " + count + " 件。商品话术仍需点击窗口底部“保存设置”。";
            }
            catch (Exception ex)
            {
                Log.Info("商品手动更新失败: seller=" + _seller + ", error=" + ex.Message);
                _status.Text = "获取失败：" + ex.Message + "。已保留旧商品缓存及话术。";
            }
            finally { button.IsEnabled = true; }
        }

        internal void Save()
        {
            FlushDraft();
            if (!_hasChanges) return;
            ShopProductCatalogService.SaveOrderRules(_seller, _rules);
            _hasChanges = false;
        }
    }
}
