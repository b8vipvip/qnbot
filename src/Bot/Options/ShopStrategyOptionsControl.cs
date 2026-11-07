using Bot.ChromeNs;
using BotLib;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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
        private readonly ListBox _items;
        private readonly Button _refresh;

        public ShopStrategyOptionsControl(string seller)
        {
            _seller = seller;
            var root = new StackPanel { Margin = new Thickness(18) };
            root.Children.Add(new TextBlock
            {
                Text = "本店商品缓存",
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 8)
            });
            root.Children.Add(new TextBlock
            {
                Text = "Bot 运行时每小时检查一次商品缓存是否已满 7 天，过期后通过当前店铺的千牛会话获取商品。失败保留旧缓存；此处可立即手动更新。",
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
            _status = new TextBlock
            {
                Margin = new Thickness(0, 12, 0, 10),
                TextWrapping = TextWrapping.Wrap
            };
            root.Children.Add(_status);
            _items = new ListBox { MinHeight = 280 };
            root.Children.Add(_items);
            Content = new ScrollViewer
            {
                Content = root,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            Reload();
            ShopProductCatalogService.EnsureScheduled(_seller);
        }

        private void Reload()
        {
            try
            {
                var catalog = ShopProductCatalogService.Load(_seller);
                _items.ItemsSource = catalog.Products;
                _status.Text = catalog.Products.Count == 0
                    ? "本店暂无商品缓存。请确认千牛登录后点击“更新商品”。"
                    : "共 " + catalog.Products.Count + " 件商品；缓存更新时间："
                      + catalog.UpdatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                      + "；下次自动更新："
                      + catalog.UpdatedAtUtc.AddDays(7).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
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
                var count = await ShopProductCatalogService.RefreshAsync(_seller, true);
                Reload();
                _status.Text = "本店商品更新成功，共 " + count + " 件；" + _status.Text;
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
