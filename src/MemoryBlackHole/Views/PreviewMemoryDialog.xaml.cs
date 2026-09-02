using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using MemoryBlackHole.Models;
using MemoryBlackHole.Services;

namespace MemoryBlackHole.Views
{
    public partial class PreviewMemoryDialog : Window
    {
        private readonly MemoryItem _item;
        private readonly DataService? _service;
        private string? _tempPreview;
        // v3.0.9: 图片预览改 StreamSource 后,需要持续持有 FileStream(不能 using)
        private FileStream? _imageStream;
        public bool EditRequested { get; private set; }
        public bool DeleteRequested { get; private set; }

        // v3.1.0: 媒体播放控制状态。_mediaPollTimer 每 ~400ms 轮询 Position 更新进度条,
        // 仅"播放中且未拖动"时更新;用 _isDraggingProgress 区分用户拖动与程序更新防回弹。
        private readonly DispatcherTimer _mediaPollTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
        private bool _mediaReady;
        private double _mediaDurationSeconds;
        private bool _isDraggingProgress;
        private bool _seekFromTimer;

        // v3.0.3 重打(问题1): 无边框窗口最大化用 WM_GETMINMAXINFO(见 NativeWindow)+ 最大化时
        // WindowChrome.ResizeBorderThickness=0(消除内容区内缩),Normal 还原为原值 _resizeBorder。
        private readonly Thickness _resizeBorder;
        private HwndSource? _hwndSource;

        public PreviewMemoryDialog(MemoryItem item, DataService? service = null)
        {
            InitializeComponent();
            _resizeBorder = WindowChrome.GetWindowChrome(this)?.ResizeBorderThickness ?? new Thickness(0);
            SourceInitialized += PreviewMemoryDialog_SourceInitialized;
            Closed += PreviewMemoryDialog_Closed;
            StateChanged += Window_StateChanged;
            _item = item;
            _service = service;
            TitleText.Text = string.IsNullOrWhiteSpace(item.Title) ? item.DisplayText : item.Title;
            MetaText.Text = $"{item.TypeName}  ·  {item.CreatedAt:yyyy-MM-dd HH:mm}";
            // v3.0.3 重打: 文字/链接类型不显示上方 Title 区域(内容本身就是 Title,显示重复)
            bool showTitle = _item.Type != "Text" && _item.Type != "Link";
            TitleText.Visibility = showTitle ? Visibility.Visible : Visibility.Collapsed;
            MetaText.Visibility = showTitle ? Visibility.Visible : Visibility.Collapsed;
            ShowContent();
            // v3.0.9: ESC 退出全屏预览(双击图片后用 ESC 回到普通窗口大小)
            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape && WindowState == WindowState.Maximized)
                {
                    WindowState = WindowState.Normal;
                    e.Handled = true;
                }
            };
            Closed += (_, _) => CleanupTemp();
            _mediaPollTimer.Tick += (_, _) => PollMediaPosition();
        }

        // v3.0.3 重打(问题1): 无边框窗口最大化的标准做法,与主窗口/新增弹窗完全一致:
        //   - WM_GETMINMAXINFO 钩子(NativeWindow)按"窗口当前所在显示器工作区"接管最大化尺寸与位置;
        //   - StateChanged 切换 WindowChrome.ResizeBorderThickness(最大化=0 消除内容区内缩,Normal 还原);
        //   - 不再用负 Margin / 手动设 Left/Top/Width/Height / MaxWidth/MaxHeight / RestoreBounds。
        private void PreviewMemoryDialog_SourceInitialized(object? sender, EventArgs e)
        {
            _hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _hwndSource?.AddHook(NativeWindow.WndProc);
        }

        private void PreviewMemoryDialog_Closed(object? sender, EventArgs e)
        {
            _hwndSource?.RemoveHook(NativeWindow.WndProc);
            _hwndSource = null;
        }

        private void Window_StateChanged(object? sender, EventArgs e)
        {
            NativeWindow.ApplyMaximizeState(this, _resizeBorder);
        }

        private void ShowContent()
        {
            // v3.1.0: 默认关闭媒体控制条与轮询定时器;仅 Video/Audio 再开启。
            _mediaPollTimer.Stop();
            _mediaReady = false;
            _isDraggingProgress = false;
            if (MediaControls != null) MediaControls.Visibility = Visibility.Collapsed;

            if (_item.Type == "Text")
            {
                // 文本：支持 Markdown 渲染
                MarkdownViewer.Visibility = Visibility.Visible;
                var doc = MarkdownHelper.ToFlowDocument(_item.Content ?? "");
                MarkdownViewer.Document = doc;
                return;
            }

            if (_item.Type == "Link")
            {
                // 链接：显示可点击链接
                LinkPanel.Visibility = Visibility.Visible;
                LinkUrlText.Text = _item.Content ?? _item.Title ?? "";
                return;
            }

            string? path = GetPreviewPath();
            if (_item.Type == "Image" && path != null)
            {
                // v3.0.9: 改用 StreamSource 加载图片,绕开 Uri 解析(支持含 # % & 等特殊字符的路径)
                try
                {
                    var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    _imageStream = fs;  // 持有引用,避免 GC 释放
                    var img = new BitmapImage();
                    img.BeginInit();
                    img.CacheOption = BitmapCacheOption.OnLoad;
                    img.StreamSource = fs;
                    img.EndInit();
                    img.Freeze();
                    ImagePreview.Source = img;
                    ImagePreview.Visibility = Visibility.Visible;
                    // v3.0.9: 双击图片全屏预览(ESC 还原);v3.0.3(任务B): 与视频/音频共用,切全屏后 BringIntoView 确保可见。
                    ImagePreview.MouseLeftButtonDown -= PreviewElement_MouseLeftButtonDown;
                    ImagePreview.MouseLeftButtonDown += PreviewElement_MouseLeftButtonDown;
                }
                catch
                {
                    // 加载失败回退到 Uri 方式(极端情况)
                    try { ImagePreview.Source = new BitmapImage(new Uri(path)); ImagePreview.Visibility = Visibility.Visible; } catch { }
                }
            }
            else if ((_item.Type == "Video" || _item.Type == "Audio") && path != null)
            {
                // v3.1.0: 用 file:// 转义后的 Uri,避免含 # % & 等字符的路径被当作 fragment/转义/参数解析失败。
                ConfigureMediaEvents();
                ResetMediaState();
                MediaControls.Visibility = Visibility.Visible;
                MediaPreview.Source = ToMediaUri(path);
                MediaPreview.Play();
                _mediaPollTimer.Start();
                // v3.0.3(任务B): 视频/音频支持双击全屏预览,与图片一致(ESC 还原),切全屏后 BringIntoView 确保可见。
                MediaPreview.MouseLeftButtonDown -= PreviewElement_MouseLeftButtonDown;
                MediaPreview.MouseLeftButtonDown += PreviewElement_MouseLeftButtonDown;
            }
            else
            {
                FileInfoPanel.Visibility = Visibility.Visible;
                FileInfoText.Text = $"{_item.OriginalFileName ?? "未命名文件"}\n{App.FormatSize(_item.FileSizeBytes)}";
                OpenFileButton.Visibility = string.IsNullOrWhiteSpace(path) ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        // ---- v3.1.0 媒体播放控制 ----

        private void ConfigureMediaEvents()
        {
            MediaPreview.MediaOpened -= MediaPreview_MediaOpened;
            MediaPreview.MediaOpened += MediaPreview_MediaOpened;
            MediaPreview.MediaEnded -= MediaPreview_MediaEnded;
            MediaPreview.MediaEnded += MediaPreview_MediaEnded;
            MediaPreview.MediaFailed -= MediaPreview_MediaFailed;
            MediaPreview.MediaFailed += MediaPreview_MediaFailed;
        }

        private void ResetMediaState()
        {
            _mediaReady = false;
            _mediaDurationSeconds = 0;
            _isDraggingProgress = false;
            _seekFromTimer = false;
            ProgressSlider.Value = 0;
            ProgressSlider.Maximum = 1;
            ProgressSlider.IsEnabled = true;
            TimeText.Text = "00:00 / 00:00";
            PlayPauseButton.Content = "⏸";
        }

        /// <summary>每 ~400ms 轮询一次播放位置;仅播放且未拖动时更新进度条,防止拖动回弹。</summary>
        private void PollMediaPosition()
        {
            if (!_mediaReady || _mediaDurationSeconds <= 0) return;
            // _isDraggingProgress = 用户按下鼠标(拖动/点轨道);IsMouseCaptureWithin = 缩略圈仍持有鼠标捕获(松手在外部也拦截),
            // 两者任一成立都视为"用户正在操作进度条",暂停轮询避免把进度值拉回去。
            if (_isDraggingProgress || ProgressSlider.IsMouseCaptureWithin) return;
            if (!MediaPreview.IsPlaying) return;
            double pos = Math.Clamp(MediaPreview.Position.TotalSeconds, 0, _mediaDurationSeconds);
            _seekFromTimer = true;
            ProgressSlider.Value = pos;
            _seekFromTimer = false;
        }

        private void PlayPause_Click(object sender, RoutedEventArgs e)
        {
            if (!_mediaReady) return;
            if (MediaPreview.IsPlaying)
            {
                MediaPreview.Pause();
                PlayPauseButton.Content = "▶";
            }
            else
            {
                // 已到尾末则从头播放
                if (_mediaDurationSeconds > 0 &&
                    MediaPreview.NaturalDuration.HasTimeSpan &&
                    MediaPreview.Position >= MediaPreview.NaturalDuration.TimeSpan)
                    MediaPreview.Position = TimeSpan.Zero;
                MediaPreview.Play();
                PlayPauseButton.Content = "⏸";
            }
        }

        // Slider 没有 ThumbDragStarted/ThumbDragCompleted(那是 Thumb 的事件);改用 Slider 支持的
        // PreviewMouseLeftButtonDown/Up 标记"用户正在操作进度条",防止 PollMediaPosition 回弹。
        private void ProgressSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isDraggingProgress = true;
        }

        private void ProgressSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isDraggingProgress = false;
            // 松手后真正跳转(拖动或点轨道都从这里定位)
            if (_mediaReady && _mediaDurationSeconds > 0)
                MediaPreview.Position = TimeSpan.FromSeconds(Math.Clamp(ProgressSlider.Value, 0, _mediaDurationSeconds));
            TimeText.Text = FormatTime(ProgressSlider.Value, _mediaDurationSeconds);
        }

        private void ProgressSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            TimeText.Text = FormatTime(e.NewValue, _mediaDurationSeconds);
            // 程序更新(轮询定时器)→ 只反映,不 seek
            if (_seekFromTimer) return;
            // 拖动过程中→ 不实时 seek,等 ThumbDragCompleted
            if (_isDraggingProgress) return;
            // 直接点击进度条/键盘改值 → 立即跳转
            if (_mediaReady && _mediaDurationSeconds > 0)
                MediaPreview.Position = TimeSpan.FromSeconds(Math.Clamp(e.NewValue, 0, _mediaDurationSeconds));
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            MediaPreview.Volume = Math.Clamp(VolumeSlider.Value / 100.0, 0.0, 1.0);
        }

        private void MediaPreview_MediaOpened(object? sender, RoutedEventArgs e)
        {
            _mediaReady = true;
            var dur = MediaPreview.NaturalDuration;
            if (dur.HasTimeSpan && dur.TimeSpan > TimeSpan.Zero)
            {
                _mediaDurationSeconds = dur.TimeSpan.TotalSeconds;
                ProgressSlider.Maximum = _mediaDurationSeconds;
                ProgressSlider.IsEnabled = true;
            }
            else
            {
                // 无限时长(直播流等):无总时长可跳转,禁用进度条
                _mediaDurationSeconds = 0;
                ProgressSlider.Maximum = 1;
                ProgressSlider.IsEnabled = false;
            }
            PlayPauseButton.Content = MediaPreview.IsPlaying ? "⏸" : "▶";
            TimeText.Text = FormatTime(MediaPreview.Position.TotalSeconds, _mediaDurationSeconds);
        }

        private void MediaPreview_MediaEnded(object? sender, RoutedEventArgs e)
        {
            PlayPauseButton.Content = "▶";
            if (_mediaDurationSeconds > 0)
                TimeText.Text = FormatTime(_mediaDurationSeconds, _mediaDurationSeconds);
        }

        private void MediaPreview_MediaFailed(object? sender, ExceptionRoutedEventArgs e)
        {
            _mediaReady = false;
            _mediaPollTimer.Stop();
            MediaControls.Visibility = Visibility.Collapsed;
        }

        /// <summary>把本地路径转成 MediaElement 可解析的 file:// Uri,转义 # % & 等保留字符。</summary>
        private static Uri ToMediaUri(string path)
        {
            var full = Path.GetFullPath(path).Replace('\\', '/');
            full = full.Replace("%", "%25").Replace("#", "%23").Replace("&", "%26");
            if (!full.StartsWith("/", StringComparison.Ordinal))
                full = "/" + full;
            return new Uri("file://" + full, UriKind.Absolute);
        }

        private static string FormatTime(double seconds, double totalSeconds)
        {
            var cur = TimeSpan.FromSeconds(Math.Max(0, seconds));
            var total = TimeSpan.FromSeconds(Math.Max(0, totalSeconds));
            var curStr = ((int)cur.TotalMinutes).ToString("00") + ":" + cur.Seconds.ToString("00");
            var totalStr = ((int)total.TotalMinutes).ToString("00") + ":" + total.Seconds.ToString("00");
            return curStr + " / " + totalStr;
        }

        /// <summary>
        /// v3.0.3(任务B): 双击预览元素(图片/视频/音频)切换全屏,并滚动到该元素可见位置(BringIntoView),
        /// 避免切全屏后画面仍在视口外;ESC 还原逻辑在 PreviewKeyDown(已保留)。
        /// </summary>
        private void PreviewElement_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount != 2) return;
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            (sender as FrameworkElement)?.BringIntoView();
            e.Handled = true;
        }

        private string? GetPreviewPath()
        {
            // v3.0.9: 优先用 service 流式提取,避免把 800MB+ BLOB 一次加载到 .NET 内存
            if (_service != null && _item.Id > 0 && _service.HasBlobData(_item.Id))
            {
                string ext = Path.GetExtension(_item.OriginalFileName ?? "") ?? ".bin";
                _tempPreview = Path.Combine(Path.GetTempPath(), $"MemoryBlackHole_{Guid.NewGuid():N}{ext}");
                try
                {
                    _service.ExtractBlobToFile(_item.Id, _tempPreview);
                    return _tempPreview;
                }
                catch
                {
                    // ExtractBlobToFile 失败时退回 FileData(若已加载)
                    if (_item.FileData != null && _item.FileData.Length > 0)
                    {
                        using var fs = File.Create(_tempPreview);
                        fs.Write(_item.FileData, 0, _item.FileData.Length);
                        return _tempPreview;
                    }
                    return null;
                }
            }

            // 情况 A:FileData 已在内存(刚 Add 完立刻 Preview 的场景)
            if (_item.FileData != null && _item.FileData.Length > 0)
            {
                string ext = Path.GetExtension(_item.OriginalFileName ?? "") ?? ".bin";
                _tempPreview = Path.Combine(Path.GetTempPath(), $"MemoryBlackHole_{Guid.NewGuid():N}{ext}");
                using (var fs = File.Create(_tempPreview))
                {
                    fs.Write(_item.FileData, 0, _item.FileData.Length);
                }
                return _tempPreview;
            }

            // 情况 B:外部文件(超过 SQLite BLOB 阈值的存储方式)
            if (_item.FilePath != null && File.Exists(_item.FilePath))
                return _item.FilePath;

            return null;
        }

        private void OpenFile_Click(object sender, RoutedEventArgs e)
        {
            // 优先使用已生成的临时预览文件（BLOB 存库的情况），其次用本地路径
            string? target = (File.Exists(_tempPreview)) ? _tempPreview : null;
            if (target == null && File.Exists(_item.FilePath))
                target = _item.FilePath;

            if (target == null) return;

            try
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                new NoticeDialog("打开失败", $"无法使用系统程序打开该文件。\n{ex.Message}") { Owner = this }.ShowDialog();
            }
        }

        /// <summary>打开链接。</summary>
        private void OpenLink_Click(object sender, RoutedEventArgs e)
        {
            var url = _item.Content ?? _item.Title;
            if (string.IsNullOrEmpty(url)) return;
            // v3.1.0: 仅允许 http/https,避免经 UseShellExecute 唤起其它协议处理器(dangerous scheme)。
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                new NoticeDialog("无法打开", "仅支持 http:// 或 https:// 链接。") { Owner = this }.ShowDialog();
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                new NoticeDialog("打开失败", $"无法打开链接。\n{ex.Message}") { Owner = this }.ShowDialog();
            }
        }

        private void Edit_Click(object sender, RoutedEventArgs e)
        {
            EditRequested = true;
            Close();
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            bool confirmed = ConfirmDialog.ShowConfirm("确认删除",
                $"确定删除这条{_item.TypeName}记忆吗？\n此操作不可恢复。", this, isWarning: true);
            if (confirmed)
            {
                DeleteRequested = true;
                Close();
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        // v3.0.3: 自定义标题栏 — 拖动 / 最小化 / 最大化切换
        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                ToggleMaximize();
                return;
            }
            if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
                DragMove();
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

        private void ToggleMaximize()
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            if (MaximizeButton != null)
                MaximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
        }

        private void CleanupTemp()
        {
            // v3.1.0: 先停止轮询定时器,防止窗口关闭后 Tick 访问已卸载的 UI。
            _mediaPollTimer.Stop();
            // v3.1.0: 先显式停止/关闭媒体,释放其对临时文件的锁定,再删临时文件,避免 %TEMP% 残留。
            try { if (MediaPreview != null) { MediaPreview.Stop(); MediaPreview.Close(); } } catch (Exception ex) { App.Log("MediaPreview 释放失败: " + ex.Message); }
            // v3.0.9: 再释放图片 FileStream(若存在)
            try { _imageStream?.Dispose(); } catch (Exception ex) { App.Log("_imageStream 释放失败: " + ex.Message); }
            _imageStream = null;
            // v3.1.0: 删除失败不再静默,记录日志
            try
            {
                if (_tempPreview != null && File.Exists(_tempPreview))
                    File.Delete(_tempPreview);
            }
            catch (Exception ex)
            {
                App.Log("删除临时文件失败: " + _tempPreview + " " + ex.Message);
            }
        }
    }
}
