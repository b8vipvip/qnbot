using Bot.Knowledge;
using Bot.ShopScope;
using BotLib;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Bot.Options
{
    internal sealed class ShopBindingOptionsControl : UserControl, IOptions
    {
        private readonly ShopScopedPathProvider _paths = new ShopScopedPathProvider();
        private ShopContext _shop;
        private ShopControlPlaneConnectionStore _connection;
        private TextBlock _serverUrl;
        private PasswordBox _token;
        private TextBlock _identity;
        private TextBlock _fingerprint;
        private TextBlock _status;
        private CheckBox _allowNicknameFallback;
        private CheckBox _knowledgeCloudSync;
        private Button _clearToken;
        private Button _importLegacy;
        private Button _syncKnowledge;
        private Button _openFolder;
        private Button _testApi;
        private Button _testAnswerChain;
        private TextBlock _diagnosticStatus;
        private TextBox _diagnosticDetails;
        private bool _diagnosticsRunning;
        private string _seller;

        public ShopBindingOptionsControl(string seller)
        {
            BuildUi();
            InitUI(seller);
        }

        public OptionEnum OptionType
        {
            get { return OptionEnum.ShopBinding; }
        }

        public void InitUI(string seller)
        {
            _seller = (seller ?? string.Empty).Trim();
            try
            {
                _shop = ShopContextLocator.ResolveBySellerNick(_seller);
                _connection = new ShopControlPlaneConnectionStore(_shop, _paths);
                _serverUrl.Text = _connection.GetServerUrl();
                _token.Password = string.Empty;
                _identity.Text = BuildIdentityText(_shop);
                _allowNicknameFallback.Visibility = _shop.HasStableSellerId
                    ? Visibility.Collapsed
                    : Visibility.Visible;
                _allowNicknameFallback.IsChecked = false;
                _knowledgeCloudSync.IsChecked = KnowledgeCloudSyncService.IsEnabledForShop(_shop);
                ResetDiagnostics();
                SetEnabled(true);
                RefreshTokenStatus();
            }
            catch (Exception ex)
            {
                _shop = null;
                _connection = null;
                _serverUrl.Text = ShopControlPlaneConnectionStore.GetLegacyGlobalServerUrl();
                _identity.Text = "店铺身份解析失败：" + ex.Message;
                _fingerprint.Text = "未绑定";
                _status.Text = "请保持目标千牛店铺在线后重新打开设置。";
                _status.Foreground = Brushes.IndianRed;
                _diagnosticStatus.Text = "店铺身份未就绪，无法执行诊断。";
                _diagnosticStatus.Foreground = Brushes.IndianRed;
                SetEnabled(false);
            }
        }

        public void Save(string seller)
        {
            SaveConnection(true);
        }

        private void SaveConnection(bool queueCloudSync)
        {
            if (_shop == null || _connection == null) return;
            try
            {
                var candidate = (_token.Password ?? string.Empty).Trim();
                if (candidate.Length > 0
                    && !_shop.HasStableSellerId
                    && _allowNicknameFallback.IsChecked != true)
                {
                    throw new InvalidOperationException(
                        "当前千牛身份没有 TargetId。若必须临时使用，请勾选昵称回退确认后再保存。" );
                }

                if (candidate.Length > 0)
                {
                    ValidateTokenBinding(candidate);
                    _connection.SaveToken(candidate);
                    _token.Password = string.Empty;
                }

                KnowledgeCloudSyncService.SetEnabledForShop(
                    _shop,
                    _knowledgeCloudSync.IsChecked == true,
                    queueCloudSync);

                RefreshTokenStatus();
                _status.Text = "本店设置已保存。Bot 服务端地址由程序统一配置；本店 Token、知识库、规则、消息状态和云数据继续按 ShopKey 隔离。";
                _status.Foreground = Brushes.SeaGreen;
                Log.Info("店铺绑定已保存: shopKey=" + _shop.ShopKey
                    + ", stable=" + _shop.HasStableSellerId
                    + ", serverUrl=" + _connection.GetServerUrl()
                    + ", tokenFingerprint=" + _connection.TokenFingerprint
                    + ", knowledgeCloud=" + (_knowledgeCloudSync.IsChecked == true));
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
                throw;
            }
        }

        private void ValidateTokenBinding(string candidate)
        {
            try
            {
                var result = ShopTokenBindingService
                    .ClaimAsync(_shop, candidate, false)
                    .GetAwaiter()
                    .GetResult();
                if (result.Success) return;

                if (result.Conflict)
                {
                    var oldShop = string.IsNullOrWhiteSpace(result.BoundShopKey)
                        ? "其他店铺"
                        : result.BoundShopKey;
                    var message =
                        "这个 Bot 客户端令牌已经绑定到店铺：" + oldShop + "。\n\n"
                        + "是否踢出旧店铺，并把令牌重新绑定到当前店铺："
                        + (_shop.DisplayName ?? _shop.ShopKey) + "？\n\n"
                        + "确认后，服务端会清理该令牌旧店铺的运行缓存、消息、云知识和云备份索引，防止跨店数据串用。";
                    if (MessageBox.Show(
                        message,
                        "Bot 令牌已绑定其他店铺",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    {
                        throw new InvalidOperationException("已取消令牌重新绑定。" );
                    }

                    var forced = ShopTokenBindingService
                        .ClaimAsync(_shop, candidate, true)
                        .GetAwaiter()
                        .GetResult();
                    if (!forced.Success)
                        throw new InvalidOperationException("令牌重新绑定失败：" + forced.Error);

                    ShopTokenBindingService.ClearDuplicateLocalTokenCopies(_shop, candidate);
                    return;
                }

                // A definitive HTTP rejection (especially 401 for a disabled token) is not
                // a transient network outage. Never overwrite a working shop token with it.
                // Unreachable/offline requests still follow the existing catch path below.
                throw new InvalidOperationException(
                    "服务端拒绝本店令牌，未保存新令牌：" + result.Error);
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _status.Text = "暂时无法连接服务端验证令牌：" + ex.Message + "。令牌会先保存在本机，联网后自动再次校验。";
                _status.Foreground = Brushes.DarkOrange;
            }
        }

        public void RestoreDefault()
        {
            if (_connection == null) return;
            _serverUrl.Text = _connection.GetServerUrl();
            _token.Password = string.Empty;
            _allowNicknameFallback.IsChecked = false;
            _knowledgeCloudSync.IsChecked = _shop != null && KnowledgeCloudSyncService.IsEnabledForShop(_shop);
            ResetDiagnostics();
            _status.Text = "已恢复界面内容；不会删除本店令牌或云端知识。服务端地址由程序统一提供。";
            _status.Foreground = Brushes.Gray;
        }

        public void NavHelp()
        {
            MessageBox.Show(
                "同一台 Windows 上的所有店铺共用一个 Bot API Control Plane 地址；该地址由客户端内置配置提供，不需要每家店重复填写。每个店铺仍使用独立 ShopKey 和独立 Bot 客户端令牌，令牌使用 Windows DPAPI CurrentUser 加密。服务端强制一个令牌只绑定一个 ShopKey；如果令牌已被其他店铺使用，客户端会提示是否踢出旧店铺并重新绑定。\n\n“测试 API 连接”会验证网络、Token、ShopKey 和 Control Plane 配置读取；“测试 AI 回答链路”会走正式 text-default 路由，实际调用当前供应商/模型，取得 AI 回复后继续使用生产 SendTextWithRetryAsync 发送到当前千牛会话买家，并确认发送结果。测试消息带“Bot链路测试”标记，可在千牛中手动撤回。启用云同步后，Windows 与 Web 端只同步当前 ShopKey 的知识库。",
                "店铺绑定帮助",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void BuildUi()
        {
            var root = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            var panel = new StackPanel { Margin = new Thickness(18) };
            root.Content = panel;
            Content = root;

            panel.Children.Add(new TextBlock
            {
                Text = "店铺身份、Bot 令牌与云端知识库",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 12)
            });
            panel.Children.Add(new TextBlock
            {
                Text = "Bot 服务端地址属于程序级配置，所有店铺共用；每家店只保存自己的客户端令牌、知识库、规则和运行状态。",
                Foreground = Brushes.DimGray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            });

            _identity = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Background = new SolidColorBrush(Color.FromRgb(244, 247, 251)),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 12)
            };
            panel.Children.Add(_identity);

            panel.Children.Add(Label("Bot 服务端（程序内置）"));
            var serverCard = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(248, 250, 252)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 4, 0, 4)
            };
            _serverUrl = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(30, 64, 175)),
                FontWeight = FontWeights.SemiBold
            };
            serverCard.Child = _serverUrl;
            panel.Children.Add(serverCard);
            panel.Children.Add(new TextBlock
            {
                Text = "该地址对本机所有店铺一致，不需要逐店填写。部署域名变化时通过客户端构建配置或 QIANNIU_BOT_SERVER_URL 覆盖。",
                FontSize = 11,
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            });

            panel.Children.Add(Label("本店 Bot 客户端令牌"));
            _token = new PasswordBox
            {
                MinWidth = 420,
                Margin = new Thickness(0, 4, 0, 6),
                ToolTip = "留空表示保留当前令牌；一个令牌只能绑定一个 ShopKey。"
            };
            panel.Children.Add(_token);

            _fingerprint = new TextBlock
            {
                Foreground = Brushes.SteelBlue,
                Margin = new Thickness(0, 0, 0, 8)
            };
            panel.Children.Add(_fingerprint);

            _allowNicknameFallback = new CheckBox
            {
                Content = new TextBlock
                {
                    Text = "我确认当前版本未提供稳定 TargetId，临时按规范化昵称绑定（店铺改名后需要重新处理）",
                    TextWrapping = TextWrapping.Wrap
                },
                Foreground = Brushes.DarkOrange,
                Margin = new Thickness(0, 0, 0, 12)
            };
            panel.Children.Add(_allowNicknameFallback);

            var diagnosticCard = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(248, 250, 252)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 12)
            };
            var diagnosticPanel = new StackPanel();
            diagnosticCard.Child = diagnosticPanel;
            diagnosticPanel.Children.Add(new TextBlock
            {
                Text = "连接与 AI 回答链路诊断",
                FontWeight = FontWeights.SemiBold,
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.FromRgb(15, 23, 42))
            });
            diagnosticPanel.Children.Add(new TextBlock
            {
                Text = "使用本店已经保存的 Token 测试。AI 链路测试会真实调用 text-default → Control Plane → 当前供应商/模型/协议，取得 AI 回复后继续通过正式千牛发送链路发送给当前会话买家，并验证发送结果。测试消息带“Bot链路测试”标记，可手动撤回。",
                Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 8)
            });
            var diagnosticButtons = new WrapPanel();
            _testApi = MakeButton("测试 API 连接", 126);
            _testApi.Click += async (s, e) => await RunDiagnosticAsync(false);
            diagnosticButtons.Children.Add(_testApi);
            _testAnswerChain = MakeButton("测试 AI 回答链路", 154);
            _testAnswerChain.Click += async (s, e) => await RunDiagnosticAsync(true);
            diagnosticButtons.Children.Add(_testAnswerChain);
            diagnosticPanel.Children.Add(diagnosticButtons);

            _diagnosticStatus = new TextBlock
            {
                Text = "尚未测试。保存本店令牌后即可执行诊断。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Gray,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 2, 0, 6)
            };
            diagnosticPanel.Children.Add(_diagnosticStatus);

            _diagnosticDetails = new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MinHeight = 90,
                MaxHeight = 220,
                Padding = new Thickness(8),
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                Text = "测试结果会显示在这里。"
            };
            diagnosticPanel.Children.Add(_diagnosticDetails);
            panel.Children.Add(diagnosticCard);

            var cloudCard = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(239, 246, 255)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(191, 219, 254)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 12)
            };
            var cloudPanel = new StackPanel();
            cloudCard.Child = cloudPanel;
            cloudPanel.Children.Add(new TextBlock
            {
                Text = "云端知识库",
                FontWeight = FontWeights.SemiBold,
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.FromRgb(30, 64, 175))
            });
            _knowledgeCloudSync = new CheckBox
            {
                Content = "启用本店知识库云同步",
                Margin = new Thickness(0, 8, 0, 5),
                ToolTip = "仅同步当前 ShopKey；云端版本应用前会自动备份本店本机知识。"
            };
            cloudPanel.Children.Add(_knowledgeCloudSync);
            cloudPanel.Children.Add(new TextBlock
            {
                Text = "首次绑定本店 Token 后，可直接点击“保存令牌并立即同步知识库”拉取该店云端知识；后续继续按 revision 自动同步。",
                Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105)),
                TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(cloudCard);

            var buttons = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
            _syncKnowledge = MakeButton("保存令牌并立即同步知识库", 210);
            _syncKnowledge.Click += async (s, e) => await SyncKnowledge_Click();
            buttons.Children.Add(_syncKnowledge);
            _importLegacy = MakeButton("导入旧全局令牌", 132);
            _importLegacy.Click += ImportLegacy_Click;
            buttons.Children.Add(_importLegacy);
            _clearToken = MakeButton("清除本店令牌", 118);
            _clearToken.Click += ClearToken_Click;
            buttons.Children.Add(_clearToken);
            _openFolder = MakeButton("打开本店配置目录", 142);
            _openFolder.Click += OpenFolder_Click;
            buttons.Children.Add(_openFolder);
            panel.Children.Add(buttons);

            _status = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 0, 0, 6)
            };
            panel.Children.Add(_status);
        }

        private async Task RunDiagnosticAsync(bool includeAiAnswer)
        {
            if (_shop == null || _connection == null || _diagnosticsRunning) return;
            _diagnosticsRunning = true;
            RefreshDiagnosticButtons();
            _diagnosticStatus.Text = includeAiAnswer
                ? "正在测试 API、Token、AI 获取答案以及当前买家的千牛真实发送链路..."
                : "正在测试 API 网络、Token、ShopKey 和 Control Plane...";
            _diagnosticStatus.Foreground = Brushes.SteelBlue;
            _diagnosticDetails.Text = includeAiAnswer
                ? "正在执行完整链路。成功后会向当前千牛会话真实发送一条带“Bot链路测试”标记的消息。"
                : "正在执行，请稍候...";

            try
            {
                var token = GetSavedTokenForDiagnostics();
                var serverUrl = _connection.GetServerUrl();
                var result = includeAiAnswer
                    ? await ShopApiDiagnosticsService.TestAnswerChainAsync(_shop, _seller, serverUrl, token)
                    : await ShopApiDiagnosticsService.TestConnectionAsync(_shop, serverUrl, token);

                _diagnosticStatus.Text = result.Summary;
                _diagnosticStatus.Foreground = result.Success ? Brushes.SeaGreen : Brushes.IndianRed;
                _diagnosticDetails.Text = result.Details ?? string.Empty;
                Log.Info("店铺API诊断完成: shopKey=" + _shop.ShopKey
                    + ", kind=" + (includeAiAnswer ? "answer-chain-real-send" : "connection")
                    + ", success=" + result.Success);
            }
            catch (Exception ex)
            {
                _diagnosticStatus.Text = includeAiAnswer ? "AI回答/真实发送链路测试失败" : "API连接测试失败";
                _diagnosticStatus.Foreground = Brushes.IndianRed;
                _diagnosticDetails.Text = ex.Message;
                Log.Exception(ex);
            }
            finally
            {
                _diagnosticsRunning = false;
                RefreshDiagnosticButtons();
            }
        }

        private string GetSavedTokenForDiagnostics()
        {
            if (!string.IsNullOrWhiteSpace(_token.Password))
            {
                throw new InvalidOperationException(
                    "令牌输入框里还有尚未保存的内容。请先点击设置窗口底部“保存设置”，再执行诊断，避免测试时产生未保存的服务端绑定。" );
            }

            string token;
            string error;
            if (!_connection.TryGetToken(out token, out error) || string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(error)
                        ? "本店尚未保存 Bot 客户端令牌，请先保存后再测试。"
                        : "读取本店 Bot 客户端令牌失败：" + error);
            }
            return token;
        }

        private void ResetDiagnostics()
        {
            if (_diagnosticStatus == null || _diagnosticDetails == null) return;
            _diagnosticStatus.Text = "尚未测试。保存本店令牌后即可执行诊断。";
            _diagnosticStatus.Foreground = Brushes.Gray;
            _diagnosticDetails.Text = "测试结果会显示在这里。";
            RefreshDiagnosticButtons();
        }

        private void RefreshDiagnosticButtons()
        {
            if (_testApi == null || _testAnswerChain == null) return;
            var enabled = !_diagnosticsRunning
                && _shop != null
                && _connection != null
                && _connection.HasToken;
            _testApi.IsEnabled = enabled;
            _testAnswerChain.IsEnabled = enabled;
        }

        private async Task SyncKnowledge_Click()
        {
            if (_shop == null || _connection == null) return;
            _syncKnowledge.IsEnabled = false;
            try
            {
                _knowledgeCloudSync.IsChecked = true;
                SaveConnection(false);
                if (string.IsNullOrWhiteSpace(_connection.GetServerUrl()))
                    throw new InvalidOperationException("程序没有配置 Bot 服务端地址。" );
                if (!_connection.HasToken)
                    throw new InvalidOperationException("请先输入或导入本店 Bot 客户端令牌。" );

                _status.Text = "正在连接服务端并同步本店云端知识库...";
                _status.Foreground = Brushes.SteelBlue;
                await KnowledgeCloudSyncService.SyncNowAsync(_shop);
                _status.Text = "本店云端知识库同步完成。可以进入“知识库”页面核对条目；写入前的本机版本会保留在本店 backup 目录。";
                _status.Foreground = Brushes.SeaGreen;
            }
            catch (Exception ex)
            {
                _status.Text = "云端知识库同步失败：" + ex.Message;
                _status.Foreground = Brushes.IndianRed;
                Log.Exception(ex);
            }
            finally
            {
                _syncKnowledge.IsEnabled = true;
                RefreshTokenStatus(false);
            }
        }

        private void ImportLegacy_Click(object sender, RoutedEventArgs e)
        {
            var legacy = ShopControlPlaneConnectionStore.GetLegacyGlobalToken();
            if (string.IsNullOrWhiteSpace(legacy))
            {
                _status.Text = "旧全局配置中没有可导入的 Bot 客户端令牌。";
                _status.Foreground = Brushes.DarkOrange;
                return;
            }
            _token.Password = legacy;
            _status.Text = "旧全局令牌已放入输入框，点击窗口底部“保存”或“保存令牌并立即同步知识库”后才会写入本店 DPAPI 令牌文件。";
            _status.Foreground = Brushes.SteelBlue;
        }

        private void ClearToken_Click(object sender, RoutedEventArgs e)
        {
            if (_connection == null || !_connection.HasToken) return;
            if (MessageBox.Show(
                "确定清除店铺“" + (_shop.DisplayName ?? _shop.ShopKey) + "”的独立 Bot 客户端令牌吗？不会删除其他店铺令牌。",
                "清除本店令牌",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            try
            {
                _connection.ClearToken();
                _token.Password = string.Empty;
                ResetDiagnostics();
                RefreshTokenStatus();
                _status.Text = "本店令牌及其加密备份副本已清除。";
                _status.Foreground = Brushes.DarkOrange;
            }
            catch (Exception ex)
            {
                MessageBox.Show("清除失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_shop == null) return;
            try
            {
                Process.Start("explorer.exe", _paths.GetConfigRoot(_shop));
            }
            catch (Exception ex)
            {
                _status.Text = "打开目录失败：" + ex.Message;
                _status.Foreground = Brushes.IndianRed;
            }
        }

        private void RefreshTokenStatus(bool updateMessage = true)
        {
            if (_connection == null)
            {
                _fingerprint.Text = "未绑定";
                RefreshDiagnosticButtons();
                return;
            }
            _serverUrl.Text = _connection.GetServerUrl();
            var fingerprint = _connection.TokenFingerprint;
            _fingerprint.Text = string.IsNullOrWhiteSpace(fingerprint)
                ? "状态：本店尚未保存独立令牌"
                : "状态：已保存本店令牌｜指纹 " + fingerprint + "（不显示令牌原文）";
            _clearToken.IsEnabled = _connection.HasToken;
            RefreshDiagnosticButtons();
            if (!updateMessage) return;
            _status.Text = string.IsNullOrWhiteSpace(fingerprint)
                ? "可输入本店令牌，或显式导入旧全局令牌。"
                : "本店 Token 已按 ShopKey 保存；可在上方直接测试 API 连接和 AI 回答链路。";
            _status.Foreground = string.IsNullOrWhiteSpace(fingerprint) ? Brushes.Gray : Brushes.SeaGreen;
        }

        private void SetEnabled(bool enabled)
        {
            _token.IsEnabled = enabled;
            _clearToken.IsEnabled = enabled && _connection != null && _connection.HasToken;
            _importLegacy.IsEnabled = enabled;
            _syncKnowledge.IsEnabled = enabled;
            _openFolder.IsEnabled = enabled;
            _allowNicknameFallback.IsEnabled = enabled;
            _knowledgeCloudSync.IsEnabled = enabled;
            if (!enabled)
            {
                _testApi.IsEnabled = false;
                _testAnswerChain.IsEnabled = false;
            }
            else
            {
                RefreshDiagnosticButtons();
            }
        }

        private string BuildIdentityText(ShopContext shop)
        {
            return "店铺：" + (string.IsNullOrWhiteSpace(shop.DisplayName) ? "（未提供显示名）" : shop.DisplayName)
                + "\nShopKey：" + shop.ShopKey
                + "\n平台：" + shop.Platform
                + "\n卖家身份：" + (shop.HasStableSellerId ? "TargetId（稳定凭据）" : "规范化昵称（兼容回退）")
                + "\n店铺目录：" + _paths.GetShopRoot(shop);
        }

        private static TextBlock Label(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 4, 0, 0)
            };
        }

        private static Button MakeButton(string text, double width)
        {
            return new Button
            {
                Content = text,
                MinWidth = width,
                Height = 34,
                Margin = new Thickness(0, 0, 8, 8),
                Padding = new Thickness(10, 0, 10, 0)
            };
        }
    }
}
