using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;
using MemoryBlackHole.Models;
using MemoryBlackHole.Services;

namespace MemoryBlackHole.Views
{
    public partial class MainWindow : Window
    {
        private readonly DataService? _service;
        private readonly SpaceCore _frontSpace;
        private readonly SpaceCore _backSpace;
        private bool _flipping;
        private DateTime _lastFrame;
        private string? _activeTag;
        // v3.0.7: 搜索防抖 — 连续输入 300ms 内只触发一次真实搜索
        // (连续点标签 / 连续打字 / 快速按 Enter 都被合并,避免无谓的 DB 查询)
        private readonly DispatcherTimer _searchDebouncer = new() { Interval = TimeSpan.FromMilliseconds(300) };
        private bool _searchPending;
        // v3.0.8: 搜索范围提示预创建 brush(SolidColorBrush 创建后 Freeze 即可安全全局共享)
        private static readonly Brush _globalScopeBrush = CreateFrozenBrush(Color.FromRgb(0x9D, 0x8B, 0xFF));
        private static readonly Brush _tagScopeBrush    = CreateFrozenBrush(Color.FromRgb(0xFF, 0xB0, 0x60));
        // v3.0.9: WindowFrame Clip 缓存 — resize 时只改 Rect,不 new RectangleGeometry
        private RectangleGeometry? _windowFrameClip;
        // v3.1.0(建议12): 最大化时把窗口四角圆角/裁剪半径置 0(消除四角小缺口),Normal 还原为 18。
        private readonly double _frameCornerRadius = 18;
        private double _clipRadius = 18;
        // v3.0.3 重打(问题1): 无边框窗口最大化用 WM_GETMINMAXINFO(见 NativeWindow)+ 最大化时
        // WindowChrome.ResizeBorderThickness=0(消除内容区内缩),Normal 还原为原值 _resizeBorder。
        private readonly Thickness _resizeBorder;
        private HwndSource? _hwndSource;

        // ---- v3.1.2 左下角背景音乐播放器 ----
        // 音频用 System.Windows.Media.MediaPlayer(透明窗口只影响视频帧合成,音频不受影响)。
        private MediaPlayer? _musicPlayer;
        private List<MemoryItem>? _musicItems;   // Audio 类型记忆表(元数据,懒加载)
        private int _musicIndex = -1;
        private bool _musicQueueBuilt;
        private bool _musicPlaying;
        private bool _musicReady;                 // 当前曲目是否 Open 完成(拿到总时长)
        private bool _musicDragging;              // 进度条拖动防回弹
        private bool _musicSeekFromTimer;
        private double _musicDurationSeconds;
        private string? _musicCurTemp;            // 当前曲目临时文件(用于回退/关闭时清理)
        private string? _musicTempToDelete;       // 上一首临时文件,等新曲 Open 完成后删除
        private int _musicConsFailures;           // 连续不可播计数,防止整列表都坏时无限切歌
        private readonly DispatcherTimer _musicPollTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };

        private static Brush CreateFrozenBrush(Color c)
        {
            var b = new SolidColorBrush(c);
            if (b.CanFreeze) b.Freeze();
            return b;
        }

public MainWindow()
        {
            InitializeComponent();
            // v3.0.3 重打(问题1): 无边框窗口最大化标准做法 — WM_GETMINMAXINFO 接管尺寸/位置(NativeWindow),
            // StateChanged 里按状态切换 WindowChrome.ResizeBorderThickness(最大化=0,还原=原值)。
            _resizeBorder = WindowChrome.GetWindowChrome(this)?.ResizeBorderThickness ?? new Thickness(0);
            SourceInitialized += MainWindow_SourceInitialized;
            Closed += MainWindow_Closed;
            StateChanged += Window_StateChanged;
            _frontSpace = new SpaceCore(FrontCanvas, warm: true);
            _backSpace = new SpaceCore(BackCanvas, warm: false);
            _lastFrame = DateTime.UtcNow;

            try { _service = new DataService(); }
            catch (Exception ex)
            {
                _service = null;
                ConfirmDialog.ShowInfo("数据库初始化失败", "数据库初始化失败：" + ex.Message, this);
            }

            // v3.0.7: 搜索防抖 — Tick 触发后才真正执行 RefreshSearchResults
            _searchDebouncer.Tick += (_, _) =>
            {
                _searchDebouncer.Stop();
                _searchPending = false;
                DoSearch();
            };

Loaded += (_, _) =>
            {
                // 从程序集自动读取版本号
                var ver = Assembly.GetExecutingAssembly().GetName().Version;
                if (ver != null)
                    VersionText.Text = $"v{ver.Major}.{ver.Minor}.{ver.Build}";
                // 默认加载全部记忆(进入探索页即看到列表)
                UpdateSearchScope();
                DoSearch();
                // v3.0.9: 启动时初始化侧栏(标签列表+统计),后续只在 Add/Delete 触发
                RefreshSidebar();
                CompositionTarget.Rendering += OnRendering;
                // 注意:不再自定义 PreviewMouseWheel,WPF ScrollViewer 的默认滚轮方向
                // (滚轮上=内容上、滚轮下=内容下)即与 Windows 文件管理器一致。
                // 之前手动 `-e.Delta` 在某些嵌套/触摸板下会反向。
            };
            _musicPollTimer.Tick += (_, _) => PollMusicPosition();

            Closed += (_, _) =>
            {
                CompositionTarget.Rendering -= OnRendering;
                // v3.0.9: 停止搜索防抖 timer,避免 Tick 在窗口关闭后访问已释放 UI
                _searchDebouncer.Stop();
                // v3.1.2: 停止音乐轮询、关闭播放器,并清理提取出的临时文件
                _musicPollTimer.Stop();
                try { _musicPlayer?.Close(); } catch { }
                _musicPlayer = null;
                DeleteMusicTemp(_musicCurTemp);
                DeleteMusicTemp(_musicTempToDelete);
                // v3.1.0: 退出时释放长连接 + WAL checkpoint,防止库文件只增不减
                try { _service?.Checkpoint(); } catch { }
                _service?.Dispose();
            };
        }

        // v3.0.3: 记忆列表滚轮方向修正(用户实测 WPF 默认在嵌套布局下反向,与 Windows 习惯相反)
        // 滚轮上 → 内容上(VerticalOffset 减小);用 e.Handled = true 阻止 ScrollViewer 默认再处理
        private void ResultsScrollViewer_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            if (sender is not System.Windows.Controls.ScrollViewer sv) return;
            sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta);
            e.Handled = true;
        }

        /// <summary>
        /// v3.0.3 重打(问题1): 无边框窗口最大化的标准做法(主窗口 / 新增弹窗 / 查看弹窗一致):
        ///   - 窗口句柄创建后,把 WM_GETMINMAXINFO 钩子挂到 HwndSource(NativeWindow.WndProc),
        ///     由系统按"窗口当前所在显示器的工作区"接管最大化尺寸与位置(物理像素,多屏/DPI 正确)。
        ///   - StateChanged 里按状态切换 WindowChrome.ResizeBorderThickness:最大化=0(消除内容区内缩,不再留边),
        ///     Normal 还原为原值。
        ///   - 不再用负 Margin、不再手动设 Left/Top/Width/Height、不再用 MaxWidth/MaxHeight / RestoreBounds,
        ///     普通状态可拖拽上限由 WM_GETMINMAXINFO 的 ptMaxTrackSize 保证。
        /// </summary>
        private void MainWindow_SourceInitialized(object? sender, EventArgs e)
        {
            _hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _hwndSource?.AddHook(NativeWindow.WndProc);
        }

        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            _hwndSource?.RemoveHook(NativeWindow.WndProc);
            _hwndSource = null;
        }

        private void Window_StateChanged(object? sender, EventArgs e)
        {
            NativeWindow.ApplyMaximizeState(this, _resizeBorder);
        }

        private void WindowFrame_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            double w = WindowFrame.ActualWidth, h = WindowFrame.ActualHeight;
            if (w <= 0 || h <= 0) return;
            // v3.1.0(建议12): 最大化时圆角/裁剪半径置 0,消除四角小缺口;Normal 还原 18。
            double r = WindowState == WindowState.Maximized ? 0 : _frameCornerRadius;
            if (WindowFrame.CornerRadius.TopLeft != r)
                WindowFrame.CornerRadius = new CornerRadius(r);
            // v3.0.9: 复用缓存的 RectangleGeometry,只改 Rect,避免每次 resize new 对象触发 GC
            if (_windowFrameClip == null || _clipRadius != r)
            {
                _windowFrameClip = new RectangleGeometry(new Rect(0, 0, w, h), r, r);
                WindowFrame.Clip = _windowFrameClip;
                _clipRadius = r;
            }
            else
            {
                _windowFrameClip.Rect = new Rect(0, 0, w, h);
            }
        }

        /// <summary>全局键盘快捷键。</summary>
        private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                switch (e.Key)
                {
                    case Key.N: // Ctrl+N: 新增记忆
                        e.Handled = true;
                        if (FrontFace.Visibility == Visibility.Visible)
                            OpenAddDialog();
                        else
                        {
                            new NoticeDialog("提示", "请在黑洞正面使用 Ctrl+N 新增记忆。")
                                { Owner = this }.ShowDialog();
                        }
                        break;
                    case Key.F: // Ctrl+F: 搜索框聚焦
                        e.Handled = true;
                        if (BackFace.Visibility == Visibility.Visible)
                        {
                            SearchBox?.Focus();
                            SearchBox?.SelectAll();
                        }
                        else if (await EnsureAccess())
                            FlipToBack();
                        break;
                    case Key.W: // Ctrl+W: 关闭窗口
                        e.Handled = true;
                        Close();
                        break;
                }
            }
        }

        // v3.1.0(性能): CompositionTarget.Rendering 每帧(随屏幕刷新率,高刷更高)触发。
        // 原实现每帧同时更新 _frontSpace 与 _backSpace(两套各 100 颗粒子,共 200)。
        // 优化:仅当窗口可见且非最小化时才渲染;且只更新"当前可见的面"——
        //   FrontFace/BackFace 之一被 Collapsed 时 WPF 本就不渲染它,空转纯属白耗 CPU/GPU。
        // 效果保持:可见面仍按原帧率驱动同一套动画;隐藏面因不可见,暂停更新无感。
        private void OnRendering(object? sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized || !IsVisible) return;
            bool frontVisible = FrontFace.Visibility == Visibility.Visible;
            bool backVisible = BackFace.Visibility == Visibility.Visible;
            if (!frontVisible && !backVisible) return;

            var now = DateTime.UtcNow;
            double delta = Math.Clamp((now - _lastFrame).TotalSeconds, 0, 0.05);
            _lastFrame = now;
            if (frontVisible) _frontSpace.Update(delta);
            if (backVisible) _backSpace.Update(delta);
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                ToggleMaximize();
                return;
            }
            if (e.LeftButton == MouseButtonState.Pressed)
                DragMove();
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

        private void ToggleMaximize()
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            MaximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void OpenGitHub_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://github.com/aicbbuu/MemoryBlackHole")
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                new NoticeDialog("打开失败", $"无法打开浏览器。\n{ex.Message}") { Owner = this }.ShowDialog();
            }
        }

        /// <summary>添加文件(非文本/链接):v3.1.0 查重 + 询问 + 后台异步 AddFile(避免大文件写入冻结 UI)。</summary>
        /// <returns><c>true</c> 完成添加;某个文件被跳过/失败由内部处理。</returns>
        private async Task AddFileWithDupCheckAsync(MemoryItem item, string sourcePath)
        {
            if (_service is not { } service) return;
            // v3.1.0: 先用真实文件大小,否则查重时 FileSizeBytes=0 永远查不到重复。
            try { item.FileSizeBytes = new FileInfo(sourcePath).Length; }
            catch (Exception ex) { App.Log("读取文件大小失败: " + sourcePath + " " + ex.Message); return; }

            // 查重:同 OriginalFileName + FileSizeBytes + IsDeleted=0
            var dup = service.FindDuplicate(item.OriginalFileName, item.FileSizeBytes);
            if (dup != null)
            {
                bool stillAdd = ConfirmDialog.ShowConfirm("记忆可能重复",
                    $"已存在同名同大小文件:\n「{dup.OriginalFileName}」({FormatBytes(item.FileSizeBytes)})\n\n是否仍要添加?",
                    this, isWarning: false);
                if (!stillAdd) return;
            }

            // v3.1.0: 缩略图依赖 WPF imaging(VerticalImage/BitmapImage 需 STA),不能在 UI 线程解码大图(BitmapImage 解码
            // + RenderTargetBitmap + PNG 编码会卡屏),也不能在 MTA 的线程池线程上跑(会抛)。改在专用后台 STA 线程生成,
            // 结果(byte[])再回 UI 线程交给 AddFile。文件复制/大 BLOB 写入依旧走 Task.Run,避免界面冻结。
            byte[]? thumb = item.Type == "Image" ? await GenerateThumbnailBackgroundAsync(sourcePath) : null;

            try
            {
                await Task.Run(() => service.AddFile(item, sourcePath, thumb));
            }
            catch (Exception ex)
            {
                App.Log("AddFile 失败: " + sourcePath + " " + ex);
                new NoticeDialog("添加失败", $"无法保存文件。\n{ex.Message}") { Owner = this }.ShowDialog();
            }
        }

        /// <summary>设置忙碌态:显示等待光标,防止文件写入期间重复触发。</summary>
        private bool _busy;
        private void SetBusy(bool busy)
        {
            _busy = busy;
            Mouse.OverrideCursor = busy ? Cursors.Wait : null;
        }

        /// <summary>
        /// v3.1.0(性能): 缩略图生成依赖 WPF 成像(需 STA 线程)。线程池线程是 MTA,直接 Task.Run 会抛;
        /// 这里在专用后台 STA 线程上跑 DataService.GenerateThumbnail,字节数组返回给调用方,
        /// 从而把大图解码+缩放+PNG 编码移出 UI 线程,避免添加大图记忆时界面冻结。
        /// </summary>
        private static Task<byte[]?> GenerateThumbnailBackgroundAsync(string path)
        {
            return Task.Run(() =>
            {
                byte[]? result = null;
                var thread = new Thread(() =>
                {
                    try { result = DataService.GenerateThumbnail(path, 100); }
                    catch { result = null; }
                });
                thread.SetApartmentState(ApartmentState.STA);
                thread.IsBackground = true;
                thread.Start();
                thread.Join();
                return result;
            });
        }

        /// <summary>v3.0.9: 字节数 → 友好字符串(B/KB/MB/GB)。</summary>
        private static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB" };
            double value = bytes; int unit = 0;
            while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
            return $"{value:0.##} {units[unit]}";
        }

        private async void Window_Drop(object sender, DragEventArgs e)
        {
            if (_service == null || _busy) return;
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var files = (string[]?)e.Data.GetData(DataFormats.FileDrop);
            if (files == null || files.Length == 0) return;

            // v3.0.9: 整个拖入流程 try-catch(剪贴板/权限异常/对话框异常统一拦截)
            try
            {
                // 拖入文本：按文字内容处理；拖入文件：弹出预填对话框
                if (files.Length == 1 && string.IsNullOrEmpty(System.IO.Path.GetExtension(files[0])))
                {
                    // 无扩展名视为文本拖入，暂不支持
                    new NoticeDialog("拖入文件", "要添加文本记忆，请在正面点击✦按钮或使用 Ctrl+N。")
                        { Owner = this }.ShowDialog();
                    return;
                }

                var dialog = new AddItemDialog(files) { Owner = this };
                if (dialog.ShowDialog() != true) return;

                SetBusy(true);
                try
                {
                    for (int i = 0; i < dialog.FilePaths.Count; i++)
                    {
                        await AddFileWithDupCheckAsync(new MemoryItem
                        {
                            Type = dialog.SelectedType,
                            Title = dialog.OriginalFileNames[i],
                            Content = dialog.OriginalFileNames[i],
                            Note = null,
                            Tags = dialog.Tags,
                            OriginalFileName = dialog.OriginalFileNames[i]
                        }, dialog.FilePaths[i]);
                    }

                    // 问题 2:新增记忆成功 → 黑洞光晕变蓝(5 秒后回默认)
                    _frontSpace.FlashBlue();
                    ScheduleSearch();
                    // v3.0.9: 新增记忆后刷新侧栏(标签计数/统计)
                    RefreshSidebar();
                }
                finally
                {
                    SetBusy(false);
                }
            }
            catch (Exception ex)
            {
                App.Log("Window_Drop 处理失败: " + ex);
                new NoticeDialog("拖入文件失败", $"无法读取拖入的文件。\n{ex.Message}")
                    { Owner = this }.ShowDialog();
            }
        }

        private async void FrontCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2 && await EnsureAccess())
                FlipToBack();
        }

        private async Task<bool> EnsureAccess()
        {
            if (_service == null) return false;
            bool passed;
            if (!_service.HasPassword())
            {
                var setup = new PasswordDialog(true) { Owner = this };
                if (setup.ShowDialog() != true) return false;
                // v3.1.0: PBKDF2 异步派生,避免 UI 卡顿
                await _service.SetPassword(setup.Password);
                passed = true;
            }
            else
            {
                var verify = new PasswordDialog(false) { Owner = this };
                passed = verify.ShowDialog() == true && await _service.VerifyPassword(verify.Password);
                if (!passed)
                {
                    new NoticeDialog("访问被拒绝", "密码不正确，无法进入记忆空间。") { Owner = this }.ShowDialog();
                    return false;
                }
            }
            return passed;
        }

        private void FlipToBack()
        {
            if (_flipping) return;
            _flipping = true;
            AnimateFlip(FlipScale, () =>
            {
                FrontFace.Visibility = Visibility.Collapsed;
                BackFace.Visibility = Visibility.Visible;
                AnimateFlip(BackFlipScale, () => _flipping = false);
            });
        }

        private void BackToFront(object sender, RoutedEventArgs e)
        {
            if (_flipping) return;
            _flipping = true;
            AnimateFlip(BackFlipScale, () =>
            {
                BackFace.Visibility = Visibility.Collapsed;
                FrontFace.Visibility = Visibility.Visible;
                AnimateFlip(FlipScale, () => _flipping = false);
            });
        }

        private static void AnimateFlip(ScaleTransform scale, Action onDone)
        {
            var collapse = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(260))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            collapse.Completed += (_, _) =>
            {
                onDone();
                var expand = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, expand);
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, collapse);
        }

        private void FrontAdd_Click(object sender, RoutedEventArgs e) => OpenAddDialog();

        private async void OpenAddDialog()
        {
            if (_service == null || _busy) return;
            var dialog = new AddItemDialog { Owner = this };
            if (dialog.ShowDialog() != true) return;

            // 文本类型：直接保存内容
            if (dialog.SelectedType == "Text")
            {
                _service.Add(new MemoryItem
                {
                    Type = "Text",
                    Title = null,
                    Content = dialog.ContentText,
                    Note = null,
                    Tags = dialog.Tags
                });
            }
            else if (dialog.SelectedType == "Link")
            {
                _service.Add(new MemoryItem
                {
                    Type = "Link",
                    Title = dialog.ContentText,
                    Content = dialog.ContentText,
                    Note = null,
                    Tags = dialog.Tags
                });
            }
            else
            {
                // 非文本：流式写入 SQLite BLOB 或外部文件（避免 OOM）— v3.1.0 移到后台避免冻结
                SetBusy(true);
                try
                {
                    for (int i = 0; i < dialog.FilePaths.Count; i++)
                    {
                        await AddFileWithDupCheckAsync(new MemoryItem
                        {
                            Type = dialog.SelectedType,
                            Title = dialog.OriginalFileNames[i],
                            Content = dialog.OriginalFileNames[i],
                            Note = null,
                            Tags = dialog.Tags,
                            OriginalFileName = dialog.OriginalFileNames[i]
                        }, dialog.FilePaths[i]);
                    }
                }
                finally
                {
                    SetBusy(false);
                }
            }

            // 问题 2:新增记忆成功 → 黑洞光晕变蓝
            _frontSpace.FlashBlue();
            ScheduleSearch();
            // v3.0.9: 新增记忆后刷新侧栏
            RefreshSidebar();
        }

        private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject source)
            {
                var item = ItemsControl.ContainerFromElement(ResultsList, source) is ContentPresenter presenter
                    ? presenter.Content as MemoryItem
                    : (source as FrameworkElement)?.DataContext as MemoryItem;
                if (item == null) return;
                var preview = new PreviewMemoryDialog(item, _service) { Owner = this };
                preview.ShowDialog();
                if (preview.DeleteRequested)
                {
                    _service?.Delete(item.Id);
                    ScheduleSearch();
                    // v3.0.9: 删除记忆后刷新侧栏
                    RefreshSidebar();
                }
                else if (preview.EditRequested)
                {
                    var edit = new EditMemoryDialog(item) { Owner = this };
                    if (edit.ShowDialog() == true)
                    {
                        _service?.Update(item);
                        ScheduleSearch();
                    }
                }
            }
        }

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                ScheduleSearch();
        }

        /// <summary>点击标签→按标签过滤搜索。</summary>
        private void TagItem_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is KeyValuePair<string, int> kv)
            {
                _activeTag = kv.Key == "全部标签" ? null : kv.Key;
                SearchBox.Text = "";
                ScheduleSearch();
            }
        }

        /// <summary>请求一次搜索(会被 300ms 防抖合并)。</summary>
        private void ScheduleSearch()
        {
            if (_searchPending) return;
            _searchPending = true;
            _searchDebouncer.Stop();
            _searchDebouncer.Start();
        }

        /// <summary>真正执行搜索(防抖 Tick 后调用)。</summary>
        private void DoSearch()
        {
            UpdateSearchScope();
            RefreshSearchResults();
        }

        /// <summary>v3.0.8: 更新搜索范围提示(全局 / 标签:xxx)。使用预创建 brush 避免 GC 压力。</summary>
        private void UpdateSearchScope()
        {
            if (SearchScopeText == null) return;
            if (!string.IsNullOrEmpty(_activeTag))
            {
                SearchScopeText.Text = $"标签:{_activeTag}";
                SearchScopeText.Foreground = _tagScopeBrush;
            }
            else
            {
                SearchScopeText.Text = "全局搜索";
                SearchScopeText.Foreground = _globalScopeBrush;
            }
        }

        private void RefreshSearchResults()
        {
            if (_service == null)
            {
                SearchStatus.Text = "本地数据库尚未就绪";
                ResultsList.Visibility = Visibility.Collapsed;
                return;
            }

            try
            {
                var keyword = SearchBox?.Text?.Trim() ?? "";

                // 搜索（带标签过滤）
                var results = _service.Search(keyword, tag: _activeTag);

                ResultsList.ItemsSource = results;
                bool hasResults = results.Count > 0;
                bool hasQuery = !string.IsNullOrWhiteSpace(keyword) || !string.IsNullOrEmpty(_activeTag);

                ResultsList.Visibility = hasResults ? Visibility.Visible : Visibility.Collapsed;
                SearchStatus.Text = !hasQuery
                    ? "请输入关键词开始搜索"
                    : results.Count == 0
                        ? "没有找到这段记忆"
                        : $"找到了 {results.Count} 条记忆" +
                          (!string.IsNullOrEmpty(_activeTag) ? $"（标签：{_activeTag}）" : "");

                if (results.Count > 0)
                {
                    // 问题 2:搜索命中 → 光晕变红;问题 4:结果半透明(0.60)不挡黑洞
                    _backSpace.FlashRed();
                    ResultsList.Opacity = 0.60;
                }
                // v3.0.9: 搜索不再刷新侧栏(标签/统计);侧栏只在 Loaded/Add/Delete 时刷
            }
            catch (Exception ex)
            {
                SearchStatus.Text = "搜索暂时不可用：" + ex.Message;
                ResultsList.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>刷新标签列表和统计面板。</summary>
        private void RefreshSidebar()
        {
            if (_service == null) return;
            try
            {
                // 标签（前面加「全部标签」项）
                var tags = _service.GetTagCounts();
                var allTags = new List<KeyValuePair<string, int>> { new("全部标签", 0) };
                allTags.AddRange(tags);
                TagsList.ItemsSource = allTags;

                // 统计
                var stats = _service.GetStats();
                string sizeStr = stats.TotalSizeBytes switch
                {
                    < 1024L => $"{stats.TotalSizeBytes} B",
                    < 1024L * 1024 => $"{stats.TotalSizeBytes / 1024.0:F1} KB",
                    < 1024L * 1024 * 1024 => $"{stats.TotalSizeBytes / 1024.0 / 1024.0:F1} MB",
                    _ => $"{stats.TotalSizeBytes / 1024.0 / 1024.0 / 1024.0:F1} GB"
                };
                StatsText.Text = $"📊 共 {stats.Total} 条记忆\n" +
                                 $"📝 文本 {stats.Text}  ·  🖼 图片 {stats.Image}\n" +
                                 $"🎵 音频 {stats.Audio}  ·  🎬 视频 {stats.Video}\n" +
                                 $"📄 文件 {stats.File}  ·  占用 {sizeStr}";
            }
            catch (Exception ex) { App.Log("RefreshSidebar 失败: " + ex.Message); }
        }

        // ---- v3.1.2 左下角背景音乐播放器 ----

        /// <summary>展开/收起音乐面板(小圆钮 或 面板内的收起按钮共用)。</summary>
        private void MusicToggle_Click(object sender, RoutedEventArgs e)
        {
            bool expand = MusicPanel.Visibility != Visibility.Visible;
            MusicPanel.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
            MusicToggle.Visibility = expand ? Visibility.Collapsed : Visibility.Visible;
            if (expand)
            {
                BuildMusicQueue();
                if (_musicItems != null && _musicItems.Count > 0)
                    MusicTitle.Text = _musicTitleForIndex(_musicIndex);
                else
                    MusicTitle.Text = "暂无背景音乐";
            }
        }

        /// <summary>懒加载音频记忆列表(仅元数据,Search 走索引;真正提取 BLOB 到文件在播放时才后台做)。</summary>
        private void BuildMusicQueue()
        {
            if (_musicQueueBuilt) return;
            _musicQueueBuilt = true;
            try
            {
                _musicItems = _service?.Search("", type: "Audio") ?? new List<MemoryItem>();
            }
            catch (Exception ex)
            {
                App.Log("构建背景音乐列表失败: " + ex.Message);
                _musicItems = new List<MemoryItem>();
            }
        }

        private async void MusicPlayPause_Click(object sender, RoutedEventArgs e)
        {
            BuildMusicQueue();
            if (_musicItems == null || _musicItems.Count == 0) { MusicTitle.Text = "暂无背景音乐"; return; }
            if (_musicPlaying)
            {
                _musicPlayer?.Pause();
                _musicPlaying = false;
                MusicPlayPause.Content = "▶";
                return;
            }
            // 播放:若无当前曲先定位;若已加载(暂停后恢复)直接 Play,否则加载并播放
            if (_musicIndex < 0) { _musicIndex = 0; _musicReady = false; }
            await PlayMusicAsync(resume: _musicReady && _musicPlayer != null);
        }

        private async Task PlayMusicAsync(bool resume)
        {
            if (resume) { _musicPlayer!.Play(); _musicPlaying = true; MusicPlayPause.Content = "⏸"; return; }
            await LoadTrackAndPlayAsync(_musicIndex);
        }

        private async void MusicPrev_Click(object sender, RoutedEventArgs e) => await ChangeMusicAsync(-1);
        private async void MusicNext_Click(object sender, RoutedEventArgs e) => await ChangeMusicAsync(1);

        private async Task ChangeMusicAsync(int delta)
        {
            BuildMusicQueue();
            if (_musicItems == null || _musicItems.Count == 0) { MusicTitle.Text = "暂无背景音乐"; return; }
            NextMusicIndex(delta);
            _musicReady = false;
            await LoadTrackAndPlayAsync(_musicIndex);
        }

        /// <summary>循环切歌:上一首(-1)/下一首(+1),负索引回绕到列表末尾,超尾回绕到开头。</summary>
        private void NextMusicIndex(int delta)
        {
            if (_musicItems == null || _musicItems.Count == 0) return;
            int start = _musicIndex < 0 ? 0 : _musicIndex;
            _musicIndex = (start + delta + _musicItems.Count) % _musicItems.Count;
        }

        /// <summary>加载第 idx 首并播放:后台提取可播文件(不阻塞 UI),再在 UI 线程 Open+Play。</summary>
        private async Task LoadTrackAndPlayAsync(int idx)
        {
            if (_musicItems == null || idx < 0 || idx >= _musicItems.Count || _service == null)
            {
                MusicTitle.Text = "暂无背景音乐";
                return;
            }
            var item = _musicItems[idx];
            MusicTitle.Text = _musicTitleForIndex(idx);

            // 提取:SQLite BLOB → 临时文件;外部副本 → 直接库内路径。放后台,避免 UI 卡顿。
            var extracted = await Task.Run(() => _service.ExtractMediaFile(item));
            if (extracted == null)
            {
                App.Log("背景音乐不可播(无可用文件): " + (item.OriginalFileName ?? item.Title));
                // 连续失败计数:超过列表长度说明整轮都不可播,停止切歌,避免无限循环。
                _musicConsFailures++;
                if (_musicConsFailures >= _musicItems.Count)
                {
                    _musicConsFailures = 0;
                    _musicPlaying = false;
                    MusicPlayPause.Content = "▶";
                    return;
                }
                NextMusicIndex(1);
                await LoadTrackAndPlayAsync(_musicIndex);
                return;
            }
            (string path, bool isTemp) = extracted.Value;

            _musicTempToDelete = _musicCurTemp;   // 上一首的临时文件,等这首 Open 完成后删
            _musicCurTemp = isTemp ? path : null;

            var player = _musicPlayer;
            if (player == null)
            {
                player = _musicPlayer = new MediaPlayer();
                player.MediaOpened += MusicPlayer_MediaOpened;
                player.MediaEnded += MusicPlayer_MediaEnded;
                player.MediaFailed += MusicPlayer_MediaFailed;
            }
            _musicReady = false;
            _musicDurationSeconds = 0;
            _musicSeekFromTimer = false;
            MusicProgress.Value = 0;
            MusicTime.Text = "00:00 / 00:00";
            player.Volume = Math.Clamp(MusicVolume.Value / 100.0, 0.0, 1.0);
            player.Open(ToMediaUri(path));
            player.Play();
            _musicPlaying = true;
            MusicPlayPause.Content = "⏸";
            if (!_musicPollTimer.IsEnabled) _musicPollTimer.Start();
        }

        /// <summary>每 ~400ms 轮询 Position 更新进度条;仅播放且未拖动时,防回弹。</summary>
        private void PollMusicPosition()
        {
            if (!_musicReady || _musicPlayer == null || _musicDurationSeconds <= 0) return;
            if (_musicDragging || MusicProgress.IsMouseCaptureWithin) return;
            if (!_musicPlaying) return;
            double pos = Math.Clamp(_musicPlayer.Position.TotalSeconds, 0, _musicDurationSeconds);
            _musicSeekFromTimer = true;
            MusicProgress.Value = pos;
            _musicSeekFromTimer = false;
        }

        private void MusicProgress_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _musicDragging = true;

        private void MusicProgress_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _musicDragging = false;
            if (_musicReady && _musicPlayer != null && _musicDurationSeconds > 0)
                _musicPlayer.Position = TimeSpan.FromSeconds(Math.Clamp(MusicProgress.Value, 0, _musicDurationSeconds));
            MusicTime.Text = FormatMusicTime(MusicProgress.Value, _musicDurationSeconds);
        }

        private void MusicProgress_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            MusicTime.Text = FormatMusicTime(e.NewValue, _musicDurationSeconds);
            if (_musicSeekFromTimer) return;
            if (_musicDragging) return;
            if (_musicReady && _musicPlayer != null && _musicDurationSeconds > 0)
                _musicPlayer.Position = TimeSpan.FromSeconds(Math.Clamp(e.NewValue, 0, _musicDurationSeconds));
        }

        private void MusicVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_musicPlayer != null)
                _musicPlayer.Volume = Math.Clamp(MusicVolume.Value / 100.0, 0.0, 1.0);
        }

        private void MusicPlayer_MediaOpened(object? sender, EventArgs e)
        {
            _musicReady = true;
            _musicConsFailures = 0;   // 成功打开,重置连续失败计数
            var dur = _musicPlayer!.NaturalDuration;
            if (dur.HasTimeSpan && dur.TimeSpan > TimeSpan.Zero)
            {
                _musicDurationSeconds = dur.TimeSpan.TotalSeconds;
                MusicProgress.Maximum = _musicDurationSeconds;
            }
            else
            {
                _musicDurationSeconds = 0;
                MusicProgress.Maximum = 1;
            }
            MusicPlayPause.Content = _musicPlaying ? "⏸" : "▶";
            MusicTime.Text = FormatMusicTime(_musicPlayer.Position.TotalSeconds, _musicDurationSeconds);
            // 上一首已切走且新曲 Open 完成 → 可安全删除其临时文件
            DeleteMusicTemp(_musicTempToDelete);
            _musicTempToDelete = null;
        }

        private void MusicPlayer_MediaEnded(object? sender, EventArgs e) => _ = ChangeMusicAsync(1);   // 播完自动下一首,循环

        private void MusicPlayer_MediaFailed(object? sender, MediaFailedEventArgs e)
        {
            App.Log("背景音乐播放失败: " + e.ErrorException?.Message);
            _musicReady = false;
            _musicPlaying = false;
            MusicPlayPause.Content = "▶";
            _musicConsFailures++;
            if (_musicConsFailures < (_musicItems?.Count ?? 1))
                _ = ChangeMusicAsync(1);   // 跳过不可播的,播放下一首(带计数上限防无限循环)
        }

        private string _musicTitleForIndex(int idx)
        {
            if (_musicItems == null || idx < 0 || idx >= _musicItems.Count) return "背景音乐";
            var it = _musicItems[idx];
            return it.OriginalFileName ?? it.Title ?? it.DisplayText ?? "背景音乐";
        }

        private static void DeleteMusicTemp(string? p)
        {
            if (string.IsNullOrEmpty(p)) return;
            try { if (File.Exists(p)) File.Delete(p); } catch { }
        }

        /// <summary>把本地路径转成 MediaPlayer 可解析的 file:// Uri,转义 # % & 等保留字符。</summary>
        private static Uri ToMediaUri(string path)
        {
            var full = System.IO.Path.GetFullPath(path).Replace('\\', '/');
            full = full.Replace("%", "%25").Replace("#", "%23").Replace("&", "%26");
            if (!full.StartsWith("/", StringComparison.Ordinal))
                full = "/" + full;
            return new Uri("file://" + full, UriKind.Absolute);
        }

        private static string FormatMusicTime(double seconds, double totalSeconds)
        {
            var cur = TimeSpan.FromSeconds(Math.Max(0, seconds));
            var total = TimeSpan.FromSeconds(Math.Max(0, totalSeconds));
            return ((int)cur.TotalMinutes).ToString("00") + ":" + cur.Seconds.ToString("00")
                 + " / " + ((int)total.TotalMinutes).ToString("00") + ":" + total.Seconds.ToString("00");
        }

        /// <summary>
        /// v3.1.2 回退 2.5D 风格化黑洞:暖色柔光环 + 平面视界(纯黑实心) + 池化粒子。
        /// 视觉由 halo 径向渐变(可 Flash 临时染色为蓝/红) + 事件视界纯黑实心 + 100 颗正圆对数螺线向心粒子组成。
        /// halo 仍支持 FlashBlue/FlashRed(新增记忆 / 搜索命中反馈)。
        ///
        /// 性能策略:
        /// - 所有 Brush / Pen / Gradient 在 Build() 阶段 Freeze(),跨线程共享且无 per-frame 分配。
        /// - 100 颗粒子预分配在池中(对象复用),Update() 只改 Canvas 位置与 Opacity,不创建对象。
        /// - 由 MainWindow 的 CompositionTarget.Rendering 驱动 Update(delta)。
        /// </summary>
        private sealed class SpaceCore
        {
            // 整体尺寸缩放(v3.0.5):1.25x,所有元素按此基准矢量缩放。
            // 关键:绝不用 BlurEffect(1.5x 那次用户嫌糊,根因是位图后处理)。
            private const double SizeScale = 1.25;

            // 2π 常量(粒子角度初始化)
            private const double TwoPi = Math.PI * 2.0;

            // 事件视界(纯黑实心)
            private const double EventW    = 180 * SizeScale;
            private const double EventH    = 180 * SizeScale;
            // 中心光晕(可被 Flash 临时染色)
            private const double HaloW     = 460 * SizeScale;
            private const double HaloH     = 460 * SizeScale;
            // 单层柔光吸积盘(无旋转)
            private const double DiskW     = 600 * SizeScale;
            private const double DiskH     = 600 * SizeScale;

            // 吸积粒子轨道半径
            private const double OrbitRInner = 110 * SizeScale;
            private const double OrbitROuter = 360 * SizeScale;

            // 吸积粒子数量
            private const int OrbitPoolSize = 100;

            // Halo 闪烁参数(v3.0.6):总时长 5s→10s,颜色加深加亮
            private const double FlashInSec  = 0.30;   // 进入动画 0.3s
            private const double FlashHoldSec = 9.40;  // 保持 9.4s(v3.0.6:4.4→9.4)
            private const double FlashOutSec  = 0.30;   // 退出动画 0.3s
            private const double FlashTotalSec = FlashInSec + FlashHoldSec + FlashOutSec; // 10.0s

            private readonly Canvas _canvas;
            private readonly bool _warm;

            // 中心结构
            private Ellipse _halo = null!;
            private RadialGradientBrush _haloBrush = null!;  // 不 freeze,运行时改色
            private Ellipse _disk = null!;
            private Ellipse _eventHorizon = null!;
            // Halo 默认色(暖/冷)与 flash 目标色(v3.1.3:提亮到极限,RGB 接近 255,alpha 255)
            private readonly Color _haloDefault;
            // 深亮蓝(提亮版):R/G 极低、B 满;alpha 255
            private readonly Color _haloBlue = Color.FromArgb(255, 0x40, 0x80, 0xFF);
            // 深红(提亮版):R 满、G/B 极低;alpha 255
            private readonly Color _haloRed  = Color.FromArgb(255, 0xFF, 0x40, 0x60);
            // 当前闪烁状态
            private Color _haloTarget;
            private double _haloT;   // 已流逝秒
            private bool _haloFlashing;

            // 吸积粒子池
            private readonly List<Ellipse> _orbitPool = new();
            private readonly double[] _orbitAngle = new double[OrbitPoolSize];
            private readonly double[] _orbitRadius = new double[OrbitPoolSize];
            private readonly double[] _orbitSpeed = new double[OrbitPoolSize];
            private readonly double[] _orbitSize = new double[OrbitPoolSize];
            private readonly double[] _orbitBaseAlpha = new double[OrbitPoolSize];
            private readonly double[] _orbitShrink = new double[OrbitPoolSize];

            private double _time;

            public SpaceCore(Canvas canvas, bool warm)
            {
                _canvas = canvas; _warm = warm;
                _haloDefault = warm
                    ? Color.FromArgb(255, 0xFF, 0xE0, 0x90)   // 暖橙提亮:全饱和亮橙,alpha 255
                    : Color.FromArgb(255, 0xA0, 0xD8, 0xFF);  // 冷蓝紫提亮:alpha 255
                _haloTarget = _haloDefault;
                _haloT = 0;
                _haloFlashing = false;
                Build();
            }

            private void Build()
            {
                // 1) 单层吸积盘(无旋转、慢呼吸,无模糊)
                _disk = new Ellipse
                {
                    Width = DiskW, Height = DiskH,
                    IsHitTestVisible = false,
                    Opacity = 0.30,
                };
                _disk.Fill = MakeRadialGlow(_warm
                    ? Color.FromArgb(200, 0xFF, 0xC8, 0x80)  // 暖橙提亮
                    : Color.FromArgb(200, 0xB0, 0xD8, 0xFF),  // 冷蓝紫提亮
                    Color.FromArgb(0, 0, 0, 0));
                Freeze(_disk.Fill);
                _canvas.Children.Add(_disk);

                // 2) 中心光晕(可被 Flash 临时染色 — brush 不 freeze,运行时改色)
                _haloBrush = new RadialGradientBrush(_haloDefault, Color.FromArgb(0, 0, 0, 0));
                _halo = new Ellipse
                {
                    Width = HaloW, Height = HaloH,
                    IsHitTestVisible = false,
                    Opacity = 0.55,
                    Fill = _haloBrush,
                };
                _canvas.Children.Add(_halo);

                // 3) 事件视界 — v3.1.2 回退 2.5D:纯黑实心,无 3D 立体 RadialGradient
                _eventHorizon = new Ellipse
                {
                    Width = EventW, Height = EventH,
                    IsHitTestVisible = false,
                    Fill = Freeze(new SolidColorBrush(Color.FromRgb(0, 0, 0))),
                };
                _canvas.Children.Add(_eventHorizon);

                // (v3.1.2 移除 v3.1.1 的 3D 高光斑点 + RotateTransform 慢转:回退 2.5D 风格)

                // 5) 100 颗稳定吸积粒子 — 正圆轨道,启动随机分布
                // 关键改动(问题 1):
                //   - 启动时 _orbitRadius[i] 在 [OrbitRInner, OrbitROuter] 内随机分布(不堆外圈)
                //   - 角度随机(已为 TwoPi * NextDouble)
                //   - 运动:正圆(x/y 半径相同,不再 ×0.52 压扁)
                //   - 仍保持引力感:角速度按 r^(-2) 加速,达视界外缘重生
                var rng = new Random(_warm ? 17 : 113);
                for (int i = 0; i < OrbitPoolSize; i++)
                {
                    var dot = new Ellipse { IsHitTestVisible = false };
                    _orbitPool.Add(dot);
                    _canvas.Children.Add(dot);
                    _orbitAngle[i]     = rng.NextDouble() * TwoPi;
                    // 启动分布:在 [OrbitRInner, OrbitROuter] 内随机
                    _orbitRadius[i]    = OrbitRInner + rng.NextDouble() * (OrbitROuter - OrbitRInner);
                    // 基础角速度:0.18~0.73
                    _orbitSpeed[i]     = 0.18 + rng.NextDouble() * 0.55;
                    _orbitSize[i]      = 1.6 + rng.NextDouble() * 2.6;
                    _orbitBaseAlpha[i] = 0.40 + rng.NextDouble() * 0.45;
                    // 螺旋收缩率:6~14 px/s,产生层次感
                    _orbitShrink[i]    = 6.0 + rng.NextDouble() * 8.0;
                    // 离视界近的偏白热,远的偏冷
                    double t = 1.0 - (_orbitRadius[i] - OrbitRInner) / (OrbitROuter - OrbitRInner);
                    byte r = (byte)(180 + 75 * t);
                    byte g = (byte)(_warm ? (200 - t * 70) : (180 + t * 50));
                    byte b = (byte)(_warm ? (90 + t * 70)  : (255 - t * 60));
                    var brush = Freeze(new SolidColorBrush(Color.FromArgb(230, r, g, b)));
                    dot.Fill = brush;
                    dot.Width = dot.Height = _orbitSize[i];
                }
            }

            /// <summary>新增记忆成功 — 光晕变深亮蓝,10 秒后平滑回默认。</summary>
            public void FlashBlue()
            {
                _haloTarget = _haloBlue;
                _haloT = 0;
                _haloFlashing = true;
            }

            /// <summary>搜索命中 — 光晕变深红,10 秒后平滑回默认。</summary>
            public void FlashRed()
            {
                _haloTarget = _haloRed;
                _haloT = 0;
                _haloFlashing = true;
            }

            private static Brush MakeRadialGlow(Color inner, Color outer)
            {
                var brush = new RadialGradientBrush(inner, outer);
                brush.Freeze();
                return brush;
            }

            private static T Freeze<T>(T f) where T : Freezable
            {
                if (f.CanFreeze) f.Freeze();
                return f;
            }

            public void Update(double delta)
            {
                _time += delta;
                double cx = _canvas.ActualWidth  > 0 ? _canvas.ActualWidth  / 2 : 640;
                double cy = _canvas.ActualHeight > 0 ? _canvas.ActualHeight / 2 : 370;

                // 1) 吸积盘(慢呼吸)
                double breath = 1.0 + Math.Sin(_time * 0.6) * 0.025;
                LayoutCentered(_disk, cx, cy, breath);
                _disk.Opacity = 0.30;

                // 2) 中心光晕 — Halo 闪烁(平滑过渡,v3.0.6 总时长 10 秒回默认)
                if (_haloFlashing)
                {
                    _haloT += delta;
                    double tIn, tOut;
                    if (_haloT < FlashInSec)
                    {
                        // 进入:0 → 1(smoothstep)
                        double u = _haloT / FlashInSec;
                        tIn = u * u * (3 - 2 * u);
                        tOut = 0;
                    }
                    else if (_haloT < FlashInSec + FlashHoldSec)
                    {
                        tIn = 1; tOut = 0;
                    }
                    else if (_haloT < FlashTotalSec)
                    {
                        // 退出:1 → 0(smoothstep)
                        double u = (_haloT - FlashInSec - FlashHoldSec) / FlashOutSec;
                        tIn = 1;
                        tOut = u * u * (3 - 2 * u);
                    }
                    else
                    {
                        _haloFlashing = false;
                        _haloT = 0;
                        tIn = 0; tOut = 0;
                    }
                    // 0=default, 1=target(lerp)
                    double mix = tIn * (1 - tOut);
                    var mixed = LerpColor(_haloDefault, _haloTarget, mix);
                    _haloBrush.GradientStops[0].Color = mixed;
                    if (!_haloFlashing) _haloBrush.GradientStops[0].Color = _haloDefault;
                }
                LayoutCentered(_halo, cx, cy, 1.0);
                _halo.Opacity = 0.55;

                // 3) 事件视界(居中)
                LayoutCentered(_eventHorizon, cx, cy, 1.0);
                _eventHorizon.Opacity = 1.0;

                // 4) 100 颗稳定吸积粒子 — 正圆 + 引力向心
                // 关键(问题 1):x/y 半径相同 — 不再 ×0.52 压扁
                for (int i = 0; i < OrbitPoolSize; i++)
                {
                    // 角速度按 r^(-2) 加速
                    double omega = _orbitSpeed[i] * Math.Pow(OrbitROuter / _orbitRadius[i], 2.0);
                    _orbitAngle[i] += omega * delta;
                    // 半径持续向心收缩
                    _orbitRadius[i] -= _orbitShrink[i] * delta;
                    // 达视界外缘 → 从外圈随机半径重生
                    if (_orbitRadius[i] <= OrbitRInner)
                    {
                        // v3.0.9: 死代码 `_warm ? 0.5 : 0.5` 简化为常量 0.5
                        _orbitRadius[i] = OrbitRInner + 0.5 +
                            (OrbitROuter - OrbitRInner) * (0.5 + ((i * 37) % 50) / 50.0);
                        _orbitAngle[i]  += (i % 5) * 0.15;
                    }
                    double r = _orbitRadius[i];
                    // 正圆:x/y 半径相同(无压扁)
                    double x = Math.Cos(_orbitAngle[i]) * r;
                    double y = Math.Sin(_orbitAngle[i]) * r;
                    // Doppler 增亮:右半侧更亮
                    double dop = 0.5 + 0.5 * Math.Cos(_orbitAngle[i]);
                    double a = _orbitBaseAlpha[i] * (0.45 + dop * 0.65);
                    var dot = _orbitPool[i];
                    dot.Opacity = a;
                    Canvas.SetLeft(dot, cx + x - dot.Width / 2);
                    Canvas.SetTop(dot,  cy + y - dot.Height / 2);
                }
            }

            /// <summary>线性插值两个 ARGB 颜色(每通道 0~255)。</summary>
            private static Color LerpColor(Color a, Color b, double t)
            {
                t = Math.Clamp(t, 0, 1);
                return Color.FromArgb(
                    (byte)(a.A + (b.A - a.A) * t),
                    (byte)(a.R + (b.R - a.R) * t),
                    (byte)(a.G + (b.G - a.G) * t),
                    (byte)(a.B + (b.B - a.B) * t));
            }

            private static void LayoutCentered(FrameworkElement el, double cx, double cy, double scale)
            {
                double w = el.Width * scale;
                double h = el.Height * scale;
                Canvas.SetLeft(el, cx - w / 2);
                Canvas.SetTop(el,  cy - h / 2);
            }
        }





    }
}
