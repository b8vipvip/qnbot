using Bot.ChromeNs;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Bot.Knowledge
{
    /// <summary>
    /// Operator-managed image knowledge. A reference image is uploaded, server OCR is
    /// editable, and the operator supplies a concise semantic description for later
    /// OCR-similarity matching and conversation-aware answer generation.
    /// </summary>
    internal sealed class KnowledgeV2VisualUnderstandingPage : UserControl, IKnowledgeV2Refreshable
    {
        private readonly string _seller;
        private readonly string _shopKey;
        private readonly ObservableCollection<VisualImageKnowledgeEntry> _items =
            new ObservableCollection<VisualImageKnowledgeEntry>();
        private DataGrid _grid;
        private Image _preview;
        private TextBox _ocrText;
        private TextBox _description;
        private TextBox _imageName;
        private TextBlock _status;
        private Button _uploadButton;
        private Button _saveButton;
        private Button _deleteButton;
        private VisualImageKnowledgeEntry _selected;
        private string _currentImagePath = string.Empty;
        private string _currentImageSha256 = string.Empty;
        private bool _ocrRecognized;
        private bool _suppressSelection;

        public KnowledgeV2VisualUnderstandingPage(string seller, string shopKey)
        {
            _seller = (seller ?? string.Empty).Trim();
            _shopKey = (shopKey ?? string.Empty).Trim();
            Build();
            Loaded += delegate { RefreshView(); };
        }

        public void RefreshView()
        {
            if (_status == null) return;
            _status.Text = "正在读取当前店铺的图片知识…";
            var selectedId = _selected == null ? string.Empty : _selected.EntityId;
            Task.Run(() => VisualImageKnowledgeService.Load(_shopKey, _seller))
                .ContinueWith(t => Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (t.IsFaulted)
                    {
                        _status.Text = "读取图片知识失败：" + t.Exception.GetBaseException().Message;
                        return;
                    }

                    _suppressSelection = true;
                    _items.Clear();
                    foreach (var item in t.Result ?? new System.Collections.Generic.List<VisualImageKnowledgeEntry>())
                        _items.Add(item);
                    _suppressSelection = false;

                    var selected = _items.FirstOrDefault(x =>
                        string.Equals(x.EntityId, selectedId, StringComparison.Ordinal));
                    _grid.SelectedItem = selected;
                    Select(selected);
                    _status.Text = "共 " + _items.Count + " 条图片知识；仅当前店铺可用。";
                })));
        }

        private void Build()
        {
            var root = new Grid { Background = Brushes.White };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Image knowledge uses one focused editor: upload above the preview,
            // automatic OCR after upload, and save directly below the description.
            // No separate recognize toolbar or five-button command strip is shown.

            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40, GridUnitType.Star) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60, GridUnitType.Star) });
            Grid.SetRow(body, 1);
            root.Children.Add(body);

            _grid = new DataGrid
            {
                AutoGenerateColumns = false,
                IsReadOnly = true,
                CanUserAddRows = false,
                ItemsSource = _items,
                SelectionMode = DataGridSelectionMode.Single,
                Margin = new Thickness(0, 0, 8, 0),
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal
            };
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = "图片名称 / 说明",
                Binding = new System.Windows.Data.Binding("ImageName"),
                Width = new DataGridLength(1, DataGridLengthUnitType.Star)
            });
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = "OCR 内容预览",
                Binding = new System.Windows.Data.Binding("OcrText"),
                Width = new DataGridLength(1, DataGridLengthUnitType.Star)
            });
            _grid.SelectionChanged += delegate
            {
                if (!_suppressSelection) Select(_grid.SelectedItem as VisualImageKnowledgeEntry);
            };
            Grid.SetColumn(_grid, 0);
            body.Children.Add(_grid);

            var editor = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
            var uploadRow = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 6) };
            _uploadButton = Button("上传图片");
            _uploadButton.Click += delegate { UploadImage(); };
            DockPanel.SetDock(_uploadButton, Dock.Left);
            uploadRow.Children.Add(_uploadButton);
            _imageName = new TextBox
            {
                Height = 28,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = "图片名称；留空时自动使用上传文件名"
            };
            uploadRow.Children.Add(_imageName);
            editor.Children.Add(uploadRow);
            editor.Children.Add(Label("图片预览"));
            var previewBorder = new Border
            {
                Height = 190,
                BorderBrush = new SolidColorBrush(Color.FromRgb(220, 226, 232)),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Color.FromRgb(248, 250, 252)),
                Padding = new Thickness(6)
            };
            _preview = new Image { Stretch = Stretch.Uniform, MaxHeight = 176, MaxWidth = 720 };
            previewBorder.Child = _preview;
            editor.Children.Add(previewBorder);

            editor.Children.Add(Label("OCR 识别结果（可编辑、可删除文字）"));
            _ocrText = Multi(220);
            editor.Children.Add(_ocrText);

            editor.Children.Add(Label("图片说明（用于后续图片匹配和上下文理解）"));
            _description = Multi(88);
            editor.Children.Add(_description);

            var saveRow = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            _saveButton = Button("保存");
            _saveButton.Click += async delegate { await SaveAsync(); };
            _deleteButton = Button("删除图片知识");
            _deleteButton.Click += async delegate { await DeleteAsync(); };
            _deleteButton.IsEnabled = false;
            saveRow.Children.Add(_saveButton);
            saveRow.Children.Add(_deleteButton);
            editor.Children.Add(saveRow);

            var editorScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = editor
            };
            Grid.SetColumn(editorScroll, 1);
            body.Children.Add(editorScroll);

            _status = new TextBlock
            {
                Margin = new Thickness(0, 8, 0, 0),
                Foreground = Brushes.DimGray,
                Text = "上传图片后自动调用服务端 OCR；核对识别结果、填写图片说明，然后保存。"
            };
            Grid.SetRow(_status, 2);
            root.Children.Add(_status);
            Content = root;
        }

        private async void UploadImage()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择要建立图片知识的图片",
                Filter = "图片文件 (*.jpg;*.jpeg;*.png;*.webp;*.gif)|*.jpg;*.jpeg;*.png;*.webp;*.gif"
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

            try
            {
                var info = new FileInfo(dialog.FileName);
                if (info.Length <= 0 || info.Length > VisualImageKnowledgeService.MaxImageBytes)
                    throw new InvalidOperationException("图片大小必须在 1 字节至 8 MB 之间。");

                var extension = (Path.GetExtension(dialog.FileName) ?? string.Empty).ToLowerInvariant();
                if (extension != ".jpg" && extension != ".jpeg" && extension != ".png"
                    && extension != ".webp" && extension != ".gif")
                    throw new InvalidOperationException("仅支持 JPG、PNG、WEBP、GIF 图片。");

                _suppressSelection = true;
                _grid.SelectedItem = null;
                _suppressSelection = false;
                _selected = null;
                _currentImagePath = dialog.FileName;
                _imageName.Text = Path.GetFileName(dialog.FileName);
                _currentImageSha256 = string.Empty;
                _ocrRecognized = false;
                _ocrText.Clear();
                _description.Clear();
                LoadPreview(_currentImagePath);
                _status.Text = "图片已上传，正在自动调用服务端 OCR…";
                await RecognizeImageAsync();
            }
            catch (Exception ex)
            {
                _status.Text = "选择图片失败：" + ex.Message;
            }
        }

        private async Task RecognizeImageAsync()
        {
            if (string.IsNullOrWhiteSpace(_currentImagePath) || !File.Exists(_currentImagePath))
            {
                _status.Text = "请先上传或选择一张图片。";
                return;
            }

            if (_uploadButton != null) _uploadButton.IsEnabled = false;
            _status.Text = "正在上传图片并调用服务端 OCR，请稍候…";
            try
            {
                var result = await LocalOcrService.TryRecognizeAsync(
                    _currentImagePath, _seller, _shopKey, CancellationToken.None);
                if (result == null || !result.Success)
                {
                    _ocrRecognized = false;
                    _status.Text = "服务端 OCR 识别失败：" + (result == null ? "未返回结果" : result.Error);
                    return;
                }

                _ocrText.Text = result.Text ?? string.Empty;
                _currentImageSha256 = result.ImageSha256 ?? string.Empty;
                _ocrRecognized = true;
                _status.Text = "OCR 识别成功：共 " + _ocrText.Text.Length + " 个字符，置信度 "
                    + result.Confidence.ToString("0.000")
                    + "，引擎 " + (result.Engine ?? "服务端 OCR")
                    + (result.CacheHit ? "（缓存命中）" : string.Empty)
                    + "。可修改识别文字后填写图片说明并保存。";
            }
            catch (Exception ex)
            {
                _ocrRecognized = false;
                _status.Text = "服务端 OCR 调用异常：" + ex.Message;
            }
            finally
            {
                if (_uploadButton != null) _uploadButton.IsEnabled = true;
            }
        }

        private async Task SaveAsync()
        {
            if (string.IsNullOrWhiteSpace(_currentImagePath) || !File.Exists(_currentImagePath))
            {
                _status.Text = "请先上传一张有效图片。";
                return;
            }
            if (!_ocrRecognized)
            {
                _status.Text = "图片尚未完成服务端 OCR，识别成功后才能保存。";
                return;
            }
            if (string.IsNullOrWhiteSpace(_description.Text))
            {
                _status.Text = "请填写图片说明，例如：这是手机端的账户界面截图。";
                _description.Focus();
                return;
            }

            var item = new VisualImageKnowledgeEntry
            {
                EntityId = _selected == null ? string.Empty : _selected.EntityId,
                ImageSha256 = _currentImageSha256,
                ImageName = _imageName == null ? string.Empty : _imageName.Text,
                OcrText = _ocrText.Text,
                Description = _description.Text
            };
            _saveButton.IsEnabled = false;
            _status.Text = "正在保存图片、OCR 结果和图片说明…";
            try
            {
                var saved = await Task.Run(() =>
                    VisualImageKnowledgeService.Save(_shopKey, _seller, item, _currentImagePath));
                _selected = saved;
                _currentImagePath = saved.ImagePath;
                _currentImageSha256 = saved.ImageSha256;
                _ocrRecognized = true;
                RefreshView();
                _status.Text = "图片知识已保存，后续买家发送相似图片时会结合当前对话和知识库进行判断。";
            }
            catch (Exception ex)
            {
                _status.Text = "保存图片知识失败：" + ex.Message;
            }
            finally
            {
                _saveButton.IsEnabled = true;
            }
        }

        private async Task DeleteAsync()
        {
            if (_selected == null) return;
            if (MessageBox.Show(
                Window.GetWindow(this),
                "确定删除选中的图片知识及其本地图片文件吗？",
                "删除图片知识",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes) return;

            var id = _selected.EntityId;
            _deleteButton.IsEnabled = false;
            try
            {
                var deleted = await Task.Run(() =>
                    VisualImageKnowledgeService.Delete(_shopKey, _seller, id));
                _status.Text = deleted ? "图片知识已删除。" : "图片知识不存在或已被删除。";
                Select(null);
                _grid.SelectedItem = null;
                RefreshView();
            }
            catch (Exception ex)
            {
                _status.Text = "删除图片知识失败：" + ex.Message;
            }
            finally
            {
                _deleteButton.IsEnabled = true;
            }
        }

        internal void ImportImageFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            try
            {
                var info = new FileInfo(path);
                if (info.Length <= 0 || info.Length > VisualImageKnowledgeService.MaxImageBytes)
                    throw new InvalidOperationException("图片大小必须在 1 字节至 8 MB 之间。");
                var extension = (Path.GetExtension(path) ?? string.Empty).ToLowerInvariant();
                if (extension != ".jpg" && extension != ".jpeg" && extension != ".png"
                    && extension != ".webp" && extension != ".gif")
                    throw new InvalidOperationException("仅支持 JPG、PNG、WEBP、GIF 图片。");
                _suppressSelection = true;
                _grid.SelectedItem = null;
                _suppressSelection = false;
                _selected = null;
                _currentImagePath = path;
                _currentImageSha256 = string.Empty;
                _ocrRecognized = false;
                _imageName.Text = Path.GetFileName(path);
                _ocrText.Clear();
                _description.Clear();
                LoadPreview(path);
                _status.Text = "已从买家消息导入图片，正在自动执行服务端 OCR…";
                _ = RecognizeImageAsync();
            }
            catch (Exception ex)
            {
                _status.Text = "导入买家图片失败：" + ex.Message;
            }
        }

        private void Select(VisualImageKnowledgeEntry item)
        {
            _selected = item;
            _currentImagePath = item == null ? string.Empty : item.ImagePath ?? string.Empty;
            _currentImageSha256 = item == null ? string.Empty : item.ImageSha256 ?? string.Empty;
            _ocrRecognized = item != null;
            _ocrText.Text = item == null ? string.Empty : item.OcrText ?? string.Empty;
            _description.Text = item == null ? string.Empty : item.Description ?? string.Empty;
            _imageName.Text = item == null ? string.Empty
                : (!string.IsNullOrWhiteSpace(item.ImageName) ? item.ImageName : Path.GetFileName(item.ImagePath ?? string.Empty));
            LoadPreview(_currentImagePath);
            _deleteButton.IsEnabled = item != null;
            _status.Text = item == null
                ? "上传图片后点击“识别图片”，编辑 OCR 结果并填写图片说明。"
                : "已载入图片知识。可修改 OCR 文字或图片说明后保存。";
        }

        private void LoadPreview(string path)
        {
            _preview.Source = null;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            try
            {
                using (var stream = File.OpenRead(path))
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    _preview.Source = bitmap;
                }
            }
            catch (Exception ex)
            {
                _status.Text = "图片预览加载失败：" + ex.Message;
            }
        }

        private static Button Button(string text)
        {
            return new Button
            {
                Content = text,
                MinWidth = 86,
                Height = 28,
                Margin = new Thickness(0, 0, 7, 0),
                Padding = new Thickness(8, 2, 8, 2)
            };
        }

        private static TextBlock Label(string text)
        {
            return new TextBlock
            {
                Text = text,
                Margin = new Thickness(0, 8, 0, 4),
                Foreground = Brushes.DimGray
            };
        }

        private static TextBox Multi(double height)
        {
            return new TextBox
            {
                Height = height,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(6),
                FontFamily = new FontFamily("Microsoft YaHei UI")
            };
        }
    }
}
