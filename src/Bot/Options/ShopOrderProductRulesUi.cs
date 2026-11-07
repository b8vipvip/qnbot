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
    /// An inline "applies to product" selector for the EXISTING order-reply
    /// mode and textbox. No second editor or independently persisted global form.
    /// </summary>
    internal sealed class ShopOrderProductRulesUi : StackPanel
    {
        private sealed class ProductChoice
        {
            public string ItemId;
            public string Label;
            public override string ToString() { return Label; }
        }

        private readonly string _seller;
        private readonly TextBox _sharedReply;
        private readonly ComboBox _sharedMode;
        private readonly ComboBox _products;
        private readonly TextBlock _status;
        private readonly List<ShopOrderProductReply> _rules;
        private string _selectedId;
        private string _globalReply;
        private object _globalMode;
        private bool _switching;
        private bool _hasChanges;

        internal ShopOrderProductRulesUi(string seller, TextBox sharedReply, ComboBox sharedMode)
        {
            if (sharedReply == null || sharedMode == null)
                throw new InvalidOperationException("原下单回复控件未就绪，不能添加商品选择器");
            _seller = seller;
            _sharedReply = sharedReply;
            _sharedMode = sharedMode;
            _globalReply = sharedReply.Text ?? "";
            _globalMode = sharedMode.SelectedItem;
            _rules = ShopProductCatalogService.GetOrderRules(seller);
            Margin = new Thickness(0, 3, 0, 10);

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock
            {
                Text = "适用商品",
                Width = 90,
                VerticalAlignment = VerticalAlignment.Center
            });
            _products = new ComboBox
            {
                Width = 400,
                MinWidth = 280,
                Height = 27
            };
            _products.SelectionChanged += ProductChanged;
            row.Children.Add(_products);

            var refresh = new Button
            {
                Content = "获取商品",
                Width = 84, Height = 27,
                Margin = new Thickness(9, 0, 0, 0)
            };
            refresh.Click += async (s, e) => await RefreshNowAsync(refresh);
            row.Children.Add(refresh);
            Children.Add(row);

            _status = new TextBlock
            {
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(90, 4, 0, 0),
                FontSize = 11
            };
            Children.Add(_status);
            _sharedMode.SelectionChanged += (s, e) =>
            {
                if (!_switching && _selectedId == null)
                    _globalMode = _sharedMode.SelectedItem;
            };
            ReloadChoices(null);
        }

        private void FlushCurrent()
        {
            if (_switching) return;
            if (_selectedId == null)
            {
                _globalReply = _sharedReply.Text ?? "";
                _globalMode = _sharedMode.SelectedItem;
                return;
            }
            var value = (_sharedReply.Text ?? "").Trim();
            var existing = _rules.FirstOrDefault(x =>
                string.Equals(x.ItemId, _selectedId, StringComparison.Ordinal));
            if (string.Equals(existing == null ? "" : existing.Reply, value, StringComparison.Ordinal))
                return;
            if (existing != null) _rules.Remove(existing);
            if (value.Length > 0)
                _rules.Add(new ShopOrderProductReply { ItemId = _selectedId, Reply = value });
            _hasChanges = true;
        }

        private void SetSharedFieldsForSelection()
        {
            _switching = true;
            try
            {
                if (_selectedId == null)
                {
                    _sharedReply.Text = _globalReply;
                    _sharedMode.IsEnabled = true;
                    if (_globalMode != null) _sharedMode.SelectedItem = _globalMode;
                    _sharedReply.ToolTip = "全部商品通用的下单预设/HTTP失败兜底答案";
                    _status.Text = "全部商品：使用上面的回复来源和通用答案。选择具体商品时，在同一答案框中编辑该商品专属回复。";
                }
                else
                {
                    var rule = _rules.FirstOrDefault(x => x.ItemId == _selectedId);
                    _sharedReply.Text = rule == null ? "" : rule.Reply;
                    _sharedMode.SelectedItem = "固定预设答案";
                    _sharedMode.IsEnabled = false;
                    _sharedReply.ToolTip = "当前商品专属固定答案，留空即使用全部商品通用规则";
                    _status.Text = "当前商品 ID：" + _selectedId
                        + "；直接编辑下面同一个“预设/兜底答案”框。专属答案优先，留空则使用全部商品规则；HTTP设置属于全部商品。";
                }
            }
            finally { _switching = false; }
        }

        private void ProductChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_switching) return;
            FlushCurrent();
            var choice = _products.SelectedItem as ProductChoice;
            _selectedId = choice == null ? null : choice.ItemId;
            SetSharedFieldsForSelection();
        }

        private void ReloadChoices(string preferredId)
        {
            var catalog = ShopProductCatalogService.Load(_seller);
            _switching = true;
            try
            {
                _products.Items.Clear();
                _products.Items.Add(new ProductChoice
                {
                    Label = "全部商品（通用回复）",
                    ItemId = null
                });
                foreach (var product in catalog.Products)
                {
                    _products.Items.Add(new ProductChoice
                    {
                        ItemId = product.ItemId,
                        Label = product.ToString()
                    });
                }
                // Keep existing saved rules editable even when an upstream item
                // was temporarily missing from the latest catalog snapshot.
                foreach (var rule in _rules.Where(x => x != null && !string.IsNullOrWhiteSpace(x.ItemId)
                    && !_products.Items.OfType<ProductChoice>().Any(p => p.ItemId == x.ItemId)))
                {
                    _products.Items.Add(new ProductChoice
                    {
                        ItemId = rule.ItemId,
                        Label = "已配置商品 ID：" + rule.ItemId + "（不在当前缓存）"
                    });
                }
                _products.SelectedItem = _products.Items.OfType<ProductChoice>()
                    .FirstOrDefault(x => x.ItemId == preferredId) ?? _products.Items[0];
                _selectedId = ((_products.SelectedItem as ProductChoice) ?? new ProductChoice()).ItemId;
            }
            finally { _switching = false; }
            SetSharedFieldsForSelection();
            if (catalog.Products.Count == 0)
                _status.Text += " 当前无商品缓存，请点击“获取商品”；已有规则不会被删除。";
        }

        private async Task RefreshNowAsync(Button button)
        {
            FlushCurrent();
            var selected = _selectedId;
            button.IsEnabled = false;
            _status.Text = "正在通过本店千牛会话获取商品…";
            try
            {
                var count = await ShopProductCatalogService.RefreshAsync(_seller, true);
                ReloadChoices(selected);
                _status.Text += " 已获取 " + count + " 件商品，专属答案请点击底部“保存设置”。";
            }
            catch (Exception ex)
            {
                Log.Info("下单回复商品下拉手动更新失败: seller=" + _seller
                    + ", error=" + ex.Message);
                _status.Text = "获取商品失败：" + ex.Message + "；原话术和缓存保留。";
            }
            finally { button.IsEnabled = true; }
        }

        internal void BeginSave()
        {
            FlushCurrent();
            // FeatureSettingsWindow.SaveAll() reads the shared legacy controls.
            // Stage global values there so saving a specific product does not
            // accidentally overwrite the whole-shop default template or mode.
            if (_selectedId == null) return;
            _switching = true;
            try
            {
                _sharedReply.Text = _globalReply;
                _sharedMode.IsEnabled = true;
                if (_globalMode != null) _sharedMode.SelectedItem = _globalMode;
            }
            finally { _switching = false; }
        }

        internal void Save()
        {
            if (!_hasChanges) return;
            ShopProductCatalogService.SaveOrderRules(_seller, _rules);
            _hasChanges = false;
        }

        internal void EndSave()
        {
            SetSharedFieldsForSelection();
        }
    }
}
