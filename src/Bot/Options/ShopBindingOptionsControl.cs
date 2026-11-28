using Bot.Knowledge;
using Bot.ShopScope;
using BotLib;
using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
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
        private TextBlock _identity;
        private TextBlock _fingerprint;
        private TextBlock _status;
        private CheckBox _allowNicknameFallback;
        private CheckBox _knowledgeCloudSync;
        private Button _manageToken;
        private Button _exportAll;
        private Button _importAll;
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
                var scoped = ShopSettingsScope.Current;
                // WndOption enters this control with the exact ShopContext already
                // resolved from the owning desk/ShopKey. That scope is authoritative;
                // comparing a legacy seller label to it can turn a valid ShopKey into
                // a nickname lookup and disable every token-editing control.
                if (scoped != null && !string.IsNullOrWhiteSpace(scoped.ShopKey))
                    _shop = scoped;
                else
                    _shop = ShopContextLocator.ResolveBySellerNick(_seller);
                _connection = new ShopControlPlaneConnectionStore(_shop, _paths);
                _serverUrl.Text = _connection.GetServerUrl();
                _identity.Text = BuildIdentityText(_shop);
                _allowNicknameFallback.Visibility = _shop.HasStableSellerId
                    ? Visibility.Collapsed
                    : Visibility.Visible;
                _allowNicknameFallback.IsChecked = false;
                _knowledgeCloudSync.IsChecked = KnowledgeV2CloudSyncService.IsEnabledForShop(_shop);
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
                // Token changes are committed immediately and only through the dedicated
                // management dialog. Saving the page must never silently replace a token.
                KnowledgeV2CloudSyncService.SetEnabledForShop(
                    _shop,
                    _knowledgeCloudSync.IsChecked == true,
                    queueCloudSync);

                RefreshTokenStatus(false);
                _status.Text = "本店设置已保存。Bot 服务端地址由程序统一配置；令牌由“管理令牌”单独管理；本店 Token、知识库、规则、消息状态和云数据继续按 ShopKey 隔离。";
                _status.Foreground = Brushes.SeaGreen;
                Log.Info("店铺绑定设置已保存: shopKey=" + _shop.ShopKey
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

        private bool ValidateTokenBinding(string candidate)
        {
            try
            {
                var result = ShopTokenBindingService
                    .ClaimAsync(_shop, candidate, false)
                    .GetAwaiter()
                    .GetResult();
                if (result.Success) return true;

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

                    return ShopTokenBindingService.ClearDuplicateLocalTokenCopies(_shop, candidate);
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
                return true; // Preserve the existing offline-staging behavior; no duplicate cleanup was attempted.
            }
        }

        public void RestoreDefault()
        {
            if (_connection == null) return;
            _serverUrl.Text = _connection.GetServerUrl();
            _allowNicknameFallback.IsChecked = false;
            _knowledgeCloudSync.IsChecked = _shop != null && KnowledgeV2CloudSyncService.IsEnabledForShop(_shop);
            ResetDiagnostics();
            _status.Text = "已恢复界面内容；不会删除本店令牌或云端知识。服务端地址由程序统一提供。";
            _status.Foreground = Brushes.Gray;
        }

        public void NavHelp()
        {
            MessageBox.Show(
                "同一台 Windows 上的所有店铺共用一个 Bot API Control Plane 地址；该地址由客户端内置配置提供，不需要每家店重复填写。每个店铺仍使用独立 ShopKey 和独立 Bot 客户端令牌，令牌使用 Windows DPAPI CurrentUser 加密。服务端强制一个令牌只绑定一个 ShopKey；如果令牌已被其他店铺使用，客户端会提示是否踢出旧店铺并重新绑定。\n\n“管理令牌”可查看、修改或清除本店令牌；更换已有令牌时可选择保留或重置本地配置与知识库。“导出全店信息/导入全店信息”用于本店配置迁移，导出文件不包含令牌，导入仅允许相同 ShopKey。\n\n“测试 API 连接”会验证网络、Token、ShopKey 和 Control Plane 配置读取；“测试 AI 回答链路”会走正式 text-default 路由，实际调用当前供应商/模型，取得 AI 回复后继续使用生产 SendTextWithRetryAsync 发送到当前千牛会话买家，并确认发送结果。测试消息带“Bot链路测试”标记，可在千牛中手动撤回。启用云同步后，Windows 与 Web 端只同步当前 ShopKey 的知识库。",
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
                Text = "该地址对本机所有店铺一致，可通过托盘【帮助 → 服务端配置】修改。店铺 Token 和数据仍按 ShopKey 独立隔离。",
                FontSize = 11,
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            });

            panel.Children.Add(Label("本店 Bot 客户端令牌"));
            var tokenActions = new WrapPanel { Margin = new Thickness(0, 4, 0, 6) };
            _manageToken = MakeButton("管理令牌", 120);
            _manageToken.ToolTip = "查看或修改本店令牌；一个令牌只能绑定一个 ShopKey。";
            _manageToken.Click += ManageToken_Click;
            tokenActions.Children.Add(_manageToken);
            panel.Children.Add(tokenActions);

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
                Text = "尚未测试。使用“管理令牌”保存本店令牌后即可执行诊断。",
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
                Text = "首次配置本店 Token 后，可点击“立即同步知识库”拉取该店云端知识；后续继续按 revision 自动同步。",
                Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105)),
                TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(cloudCard);

            var transferCard = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(248, 250, 252)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 12)
            };
            var transferPanel = new StackPanel();
            transferCard.Child = transferPanel;
            transferPanel.Children.Add(new TextBlock
            {
                Text = "全店信息导出 / 导入",
                FontWeight = FontWeights.SemiBold,
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.FromRgb(15, 23, 42))
            });
            transferPanel.Children.Add(new TextBlock
            {
                Text = "导出本店配置、知识库、规则、策略和本店业务数据；自动排除 Bot 客户端令牌及其他敏感凭据。导入只接受相同 ShopKey 的备份，导入前会生成本机加密回滚包，完成后自动重启。",
                Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 8)
            });
            var transferButtons = new WrapPanel();
            _exportAll = MakeButton("导出全店信息", 140);
            _exportAll.Click += ExportAll_Click;
            transferButtons.Children.Add(_exportAll);
            _importAll = MakeButton("导入全店信息", 140);
            _importAll.Click += ImportAll_Click;
            transferButtons.Children.Add(_importAll);
            transferPanel.Children.Add(transferButtons);
            panel.Children.Add(transferCard);

            var buttons = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
            _syncKnowledge = MakeButton("立即同步知识库", 150);
            _syncKnowledge.Click += async (s, e) => await SyncKnowledge_Click();
            buttons.Children.Add(_syncKnowledge);
            _importLegacy = MakeButton("导入旧全局令牌", 132);
            _importLegacy.Click += ImportLegacy_Click;
            buttons.Children.Add(_importLegacy);
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
            string token;
            string error;
            if (!_connection.TryGetToken(out token, out error) || string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(error)
                        ? "本店尚未保存 Bot 客户端令牌，请先使用“管理令牌”保存后再测试。"
                        : "读取本店 Bot 客户端令牌失败：" + error);
            }
            return token;
        }

        private void ResetDiagnostics()
        {
            if (_diagnosticStatus == null || _diagnosticDetails == null) return;
            _diagnosticStatus.Text = "尚未测试。使用“管理令牌”保存本店令牌后即可执行诊断。";
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
                if (string.IsNullOrWhiteSpace(_connection.GetServerUrl()))
                    throw new InvalidOperationException("程序没有配置 Bot 服务端地址。" );
                if (!_connection.HasToken)
                    throw new InvalidOperationException("请先点击“管理令牌”配置本店 Bot 客户端令牌。");
                _knowledgeCloudSync.IsChecked = true;
                SaveConnection(false);

                _status.Text = "正在连接服务端并同步本店云端知识库...";
                _status.Foreground = Brushes.SteelBlue;
                var converged = await KnowledgeV2CloudSyncService.SyncNowAsync(_shop);
                _status.Text = converged
                    ? "本店云端知识库同步完成。可以进入“知识库”页面核对条目；写入前的本机版本会保留在本店 backup 目录。"
                    : "本次同步尚未收敛（可能存在并发修改或待上传的合并结果），后台将继续同步；请稍后核对。";
                _status.Foreground = converged ? Brushes.SeaGreen : Brushes.SteelBlue;
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

        private void ManageToken_Click(object sender, RoutedEventArgs e)
        {
            OpenTokenManagement(null);
        }

        private void OpenTokenManagement(string suggestedToken)
        {
            if (_shop == null || _connection == null) return;

            string savedToken;
            string readError;
            var hasReadableToken = _connection.TryGetToken(out savedToken, out readError);
            var hadTokenFile = _connection.HasToken;
            var dialog = new ShopTokenManagementWindow(
                _shop.DisplayName,
                hasReadableToken ? savedToken : string.Empty,
                suggestedToken,
                hasReadableToken ? string.Empty : readError)
            {
                Owner = Window.GetWindow(this)
            };
            ShopScopedUiBridge.Attach(dialog, _shop);
            if (dialog.ShowDialog() != true) return;

            if (dialog.ClearRequested)
            {
                if (!hadTokenFile) return;
                if (MessageBox.Show(
                    "确定清除店铺“" + (_shop.DisplayName ?? _shop.ShopKey)
                        + "”的 Bot 客户端令牌吗？此操作不会删除本地配置和知识库。",
                    "清除本店令牌",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                try
                {
                    _connection.ClearToken();
                    ResetDiagnostics();
                    RefreshTokenStatus();
                    _status.Text = "本店令牌已清除；本地配置和知识库均已保留。";
                    _status.Foreground = Brushes.DarkOrange;
                }
                catch (Exception ex)
                {
                    MessageBox.Show("清除令牌失败：" + ex.Message, "错误",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
                return;
            }

            var candidate = (dialog.Token ?? string.Empty).Trim();
            if (candidate.Length < 16)
            {
                MessageBox.Show("Bot 客户端令牌长度无效，请检查后重试。",
                    "令牌无效", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (hasReadableToken && string.Equals(candidate, savedToken, StringComparison.Ordinal))
            {
                _status.Text = "令牌未发生变化，本地配置和知识库均未修改。";
                _status.Foreground = Brushes.Gray;
                return;
            }
            if (!_shop.HasStableSellerId && _allowNicknameFallback.IsChecked != true)
            {
                MessageBox.Show(
                    "当前千牛身份没有 TargetId（没有稳定 TargetId）。若必须临时使用昵称绑定，请先勾选下方的昵称回退确认。",
                    "店铺身份确认", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var keepLocalConfiguration = true;
            if (hadTokenFile)
            {
                var choice = MessageBox.Show(
                    "检测到本店 Bot 令牌将被更换。\n\n"
                    + "是否保留本地原配置信息（包括知识库、自动回复规则、策略和本店业务数据）？\n\n"
                    + "选择“是”：保留全部本地配置和知识库，不执行重置。\n"
                    + "选择“否”：先生成本机加密回滚包，再重置本店本地配置和知识库。\n"
                    + "选择“取消”：不修改令牌。",
                    "更换本店 Bot 令牌",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question,
                    MessageBoxResult.Yes);
                if (choice == MessageBoxResult.Cancel) return;
                keepLocalConfiguration = choice == MessageBoxResult.Yes;
                if (!keepLocalConfiguration
                    && MessageBox.Show(
                        "你选择了不保留原配置。此操作会清空当前店铺的本地配置、知识库、规则、策略和本店业务状态；"
                            + "其他店铺不受影响。程序会先生成加密回滚包，并在完成后自动重启。\n\n确定继续吗？",
                        "确认重置本店配置",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning,
                        MessageBoxResult.No) != MessageBoxResult.Yes)
                    return;
            }

            string rollbackPath = null;
            var tokenSaved = false;
            try
            {
                // Validate/claim the replacement token before any destructive local action.
                var localDuplicateCleanupComplete = ValidateTokenBinding(candidate);
                if (!keepLocalConfiguration)
                    rollbackPath = ClientDataCloudBackupService.ResetLocalConfiguration(_shop);

                _connection.SaveToken(candidate);
                tokenSaved = true;
                ResetDiagnostics();
                RefreshTokenStatus(false);

                if (!keepLocalConfiguration)
                {
                    _knowledgeCloudSync.IsChecked = false;
                    var cleanupWarning = localDuplicateCleanupComplete
                        ? string.Empty
                        : "\n\n注意：服务端已完成重新绑定，但本机旧店铺重复令牌清理未完全完成。";
                    _status.Text = "新令牌已保存；本店原配置和知识库已重置。回滚包：" + rollbackPath + cleanupWarning;
                    _status.Foreground = localDuplicateCleanupComplete ? Brushes.DarkOrange : Brushes.IndianRed;
                    MessageBox.Show(
                        "本店令牌已更新，本地配置和知识库已重置。\n"
                            + "加密回滚包：" + rollbackPath + cleanupWarning + "\n\n客户端现在将自动重启。",
                        "令牌更新完成", MessageBoxButton.OK, MessageBoxImage.Information);
                    ClientDataCloudBackupService.RestartApplication();
                    return;
                }

                _status.Text = localDuplicateCleanupComplete
                    ? "本店令牌已更新；原有本地配置、知识库、规则和策略均已完整保留。"
                    : "服务端已完成令牌重新绑定；但本机旧店铺重复令牌清理未完全完成，请检查其他店铺的令牌设置。";
                _status.Foreground = localDuplicateCleanupComplete ? Brushes.SeaGreen : Brushes.DarkOrange;
                if (!localDuplicateCleanupComplete)
                    MessageBox.Show(
                        "服务端已完成令牌重新绑定，当前店铺可以继续连接。\n"
                            + "但本机旧店铺重复令牌清理未完全完成，请逐个检查其他店铺的令牌设置。",
                        "令牌已绑定，需检查本机清理",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                if (!tokenSaved && !string.IsNullOrWhiteSpace(rollbackPath))
                {
                    try { ClientDataCloudBackupService.RestorePortableRollback(_shop, rollbackPath); }
                    catch (Exception rollbackError)
                    {
                        MessageBox.Show(
                            "令牌保存失败：" + ex.Message + "\n\n本地配置自动回滚也失败："
                                + rollbackError.Message + "\n请保留回滚包：" + rollbackPath,
                            "令牌更新失败", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                }
                MessageBox.Show("令牌更新失败，未完成本次修改：" + ex.Message,
                    "令牌更新失败", MessageBoxButton.OK, MessageBoxImage.Error);
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
            OpenTokenManagement(legacy);
        }

        private void ExportAll_Click(object sender, RoutedEventArgs e)
        {
            if (_shop == null) return;
            var dialog = new SaveFileDialog
            {
                Title = "导出本店全店信息",
                Filter = "QnBot 全店信息 (*.qnbot)|*.qnbot",
                DefaultExt = ".qnbot",
                AddExtension = true,
                FileName = "本店全店信息-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".qnbot",
                OverwritePrompt = true
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

            try
            {
                PortableDataExportResult result;
                using (ShopSettingsScope.Enter(_shop))
                    result = ClientDataCloudBackupService.ExportPortable(_shop, dialog.FileName);
                _status.Text = "全店信息导出成功：" + dialog.FileName;
                _status.Foreground = Brushes.SeaGreen;
                MessageBox.Show(
                    "本店全店信息已导出。\n配置项：" + result.SettingCount
                        + "\n数据文件：" + result.FileCount
                        + "\n原始数据：" + ClientDataCloudBackupService.FormatBytes(result.DataBytes)
                        + "\n导出文件：" + ClientDataCloudBackupService.FormatBytes(result.ArchiveBytes)
                        + "\n未导出文件：" + result.SkippedCount
                        + "\n\n已排除本店 Bot 令牌及其他敏感凭据。"
                        + (result.SkippedCount > 0
                            ? "\n部分文件因大小限制或读取失败未能导出，详细列表位于备份包 manifest.json。"
                            : string.Empty),
                    "导出完成", MessageBoxButton.OK,
                    result.SkippedCount > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _status.Text = "导出全店信息失败：" + ex.Message;
                _status.Foreground = Brushes.IndianRed;
                MessageBox.Show(_status.Text, "导出失败",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ImportAll_Click(object sender, RoutedEventArgs e)
        {
            if (_shop == null) return;
            var dialog = new OpenFileDialog
            {
                Title = "导入本店全店信息",
                Filter = "QnBot 全店信息 (*.qnbot)|*.qnbot|ZIP 文件 (*.zip)|*.zip",
                CheckFileExists = true,
                Multiselect = false
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            if (MessageBox.Show(
                "将用备份文件完整覆盖当前店铺的本地配置、知识库、规则和本店业务数据。\n\n"
                    + "导入前会自动生成本机加密回滚包；当前 Bot 令牌、店铺身份、当前设备已有 API Key/密码等敏感凭据、其他店铺数据不会被覆盖。"
                    + "\n备份必须属于当前 ShopKey。导入成功后客户端自动重启。\n\n是否继续？",
                "导入全店信息",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes) return;

            try
            {
                RestoreResult result;
                using (ShopSettingsScope.Enter(_shop))
                    result = ClientDataCloudBackupService.ImportPortable(_shop, dialog.FileName);
                MessageBox.Show(
                    "本店全店信息导入成功。\n来源电脑：" + (string.IsNullOrWhiteSpace(result.SourceDevice) ? "未知" : result.SourceDevice)
                        + "\n备份时间：" + (string.IsNullOrWhiteSpace(result.CreatedAt) ? "未知" : result.CreatedAt)
                        + "\n恢复配置项：" + result.ParamCount
                        + "\n恢复文件：" + result.FileCount
                        + "\n本机加密回滚包：" + result.RollbackPath
                        + "\n\nBot 令牌保持不变。客户端现在将自动重启。",
                    "导入完成", MessageBoxButton.OK, MessageBoxImage.Information);
                ClientDataCloudBackupService.RestartApplication();
            }
            catch (Exception ex)
            {
                _status.Text = "导入全店信息失败：" + ex.Message;
                _status.Foreground = Brushes.IndianRed;
                MessageBox.Show(_status.Text, "导入失败",
                    MessageBoxButton.OK, MessageBoxImage.Error);
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
            _manageToken.IsEnabled = true;
            RefreshDiagnosticButtons();
            if (!updateMessage) return;
            _status.Text = string.IsNullOrWhiteSpace(fingerprint)
                ? "点击“管理令牌”可查看、修改或清除本店令牌，也可导入旧全局令牌。"
                : "本店 Token 已按 ShopKey 安全保存；点击“管理令牌”可查看或修改。";
            _status.Foreground = string.IsNullOrWhiteSpace(fingerprint) ? Brushes.Gray : Brushes.SeaGreen;
        }

        private void SetEnabled(bool enabled)
        {
            _manageToken.IsEnabled = enabled;
            _exportAll.IsEnabled = enabled;
            _importAll.IsEnabled = enabled;
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


    internal sealed class ShopTokenManagementWindow : Window
    {
        private readonly PasswordBox _password;
        private readonly TextBox _visibleToken;
        private readonly CheckBox _showToken;

        public string Token { get; private set; }
        public bool ClearRequested { get; private set; }

        public ShopTokenManagementWindow(
            string shopName,
            string savedToken,
            string suggestedToken,
            string readError)
        {
            Title = "管理本店 Bot 令牌";
            Width = 560;
            Height = 300;
            MinWidth = 520;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            var initial = string.IsNullOrWhiteSpace(suggestedToken)
                ? (savedToken ?? string.Empty)
                : suggestedToken.Trim();
            var root = new StackPanel { Margin = new Thickness(20) };
            Content = root;
            root.Children.Add(new TextBlock
            {
                Text = "管理本店 Bot 令牌",
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 6)
            });
            root.Children.Add(new TextBlock
            {
                Text = "店铺：" + (string.IsNullOrWhiteSpace(shopName) ? "当前店铺" : shopName)
                    + "\n令牌仅保存在本机 DPAPI 加密文件中，不会显示在设置主页面或导出文件中。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 0, 0, 12)
            });

            _password = new PasswordBox
            {
                Password = initial,
                Height = 32,
                Padding = new Thickness(8),
                Margin = new Thickness(0, 0, 0, 6)
            };
            _visibleToken = new TextBox
            {
                Text = initial,
                Height = 32,
                Padding = new Thickness(8),
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 0, 0, 6)
            };
            root.Children.Add(_password);
            root.Children.Add(_visibleToken);

            _showToken = new CheckBox
            {
                Content = "显示令牌",
                Margin = new Thickness(0, 0, 0, 8)
            };
            _showToken.Checked += (s, e) =>
            {
                _visibleToken.Text = _password.Password;
                _password.Visibility = Visibility.Collapsed;
                _visibleToken.Visibility = Visibility.Visible;
                _visibleToken.Focus();
                _visibleToken.CaretIndex = _visibleToken.Text.Length;
            };
            _showToken.Unchecked += (s, e) =>
            {
                _password.Password = _visibleToken.Text;
                _visibleToken.Visibility = Visibility.Collapsed;
                _password.Visibility = Visibility.Visible;
            };
            root.Children.Add(_showToken);

            if (!string.IsNullOrWhiteSpace(readError))
            {
                root.Children.Add(new TextBlock
                {
                    Text = "当前令牌读取失败：" + readError + "\n可以在下方粘贴新的有效令牌进行替换。",
                    Foreground = Brushes.DarkOrange,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 10)
                });
            }

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            var save = new Button
            {
                Content = "保存令牌",
                Width = 100,
                Height = 32,
                Margin = new Thickness(0, 0, 8, 0),
                IsDefault = true
            };
            save.Click += (s, e) =>
            {
                var candidate = CurrentToken().Trim();
                if (candidate.Length < 16)
                {
                    MessageBox.Show("Bot 客户端令牌长度无效，请检查后重试。",
                        "令牌无效", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                Token = candidate;
                DialogResult = true;
            };
            var clear = new Button
            {
                Content = "清除令牌",
                Width = 92,
                Height = 32,
                Margin = new Thickness(0, 0, 8, 0)
            };
            clear.Click += (s, e) =>
            {
                ClearRequested = true;
                DialogResult = true;
            };
            var cancel = new Button
            {
                Content = "取消",
                Width = 82,
                Height = 32,
                IsCancel = true
            };
            buttons.Children.Add(save);
            buttons.Children.Add(clear);
            buttons.Children.Add(cancel);
            root.Children.Add(buttons);
        }

        private string CurrentToken()
        {
            return _showToken.IsChecked == true ? _visibleToken.Text : _password.Password;
        }
    }
    }
}
