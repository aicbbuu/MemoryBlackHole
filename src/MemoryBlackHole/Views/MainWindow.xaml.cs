using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
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
        private Color _accentColor = Color.FromRgb(0x6D, 0x5D, 0xF7);
        // v3.0.7: 搜索防抖 — 连续输入 300ms 内只触发一次真实搜索
        // (连续点标签 / 连续打字 / 快速按 Enter 都被合并,避免无谓的 DB 查询)
        private readonly DispatcherTimer _searchDebouncer = new() { Interval = TimeSpan.FromMilliseconds(300) };
        private bool _searchPending;
        // v3.0.8: 搜索范围提示预创建 brush(SolidColorBrush 创建后 Freeze 即可安全全局共享)
        private static readonly Brush _globalScopeBrush = CreateFrozenBrush(Color.FromRgb(0x9D, 0x8B, 0xFF));
        private static readonly Brush _tagScopeBrush    = CreateFrozenBrush(Color.FromRgb(0xFF, 0xB0, 0x60));
        // v3.0.9: WindowFrame Clip 缓存 — resize 时只改 Rect,不 new RectangleGeometry
        private RectangleGeometry? _windowFrameClip;
        private static Brush CreateFrozenBrush(Color c)
        {
            var b = new SolidColorBrush(c);
            if (b.CanFreeze) b.Freeze();
            return b;
        }

public MainWindow()
        {
            InitializeComponent();
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
                // v3.0.3 重打: 不依赖 WorkArea(Win11 任务栏自隐藏时含整屏),用 PrimaryScreen 直接拿主屏分辨率
                // WindowChrome.ResizeBorderThickness=6 + WPF 内部额外边距 + DWM 7-8px 非客户区
                // + 1 像素余量 → 主窗口减 17 像素才能贴满
                MaxWidth = SystemParameters.PrimaryScreenWidth - 17;
                MaxHeight = SystemParameters.PrimaryScreenHeight - 17;
                // v3.0.3 重打: BackFace(探索页)同样补一次(避免探索页最大化时右边/底部露边)
                BackFace.MaxWidth = SystemParameters.PrimaryScreenWidth - 17;
                BackFace.MaxHeight = SystemParameters.PrimaryScreenHeight - 17;
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
            Closed += (_, _) =>
            {
                CompositionTarget.Rendering -= OnRendering;
                // v3.0.9: 停止搜索防抖 timer,避免 Tick 在窗口关闭后访问已释放 UI
                _searchDebouncer.Stop();
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

        private void WindowFrame_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            double w = WindowFrame.ActualWidth, h = WindowFrame.ActualHeight;
            if (w <= 0 || h <= 0) return;
            // v3.0.9: 复用缓存的 RectangleGeometry,只改 Rect,避免每次 resize new 对象触发 GC
            if (_windowFrameClip == null)
            {
                _windowFrameClip = new RectangleGeometry(new Rect(0, 0, w, h), 18, 18);
                WindowFrame.Clip = _windowFrameClip;
            }
            else
            {
                _windowFrameClip.Rect = new Rect(0, 0, w, h);
            }
        }

        /// <summary>全局键盘快捷键。</summary>
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
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
                        else if (EnsureAccess())
                            FlipToBack();
                        break;
                    case Key.W: // Ctrl+W: 关闭窗口
                        e.Handled = true;
                        Close();
                        break;
                }
            }
        }

        private void OnRendering(object? sender, EventArgs e)
        {
            var now = DateTime.UtcNow;
            double delta = Math.Clamp((now - _lastFrame).TotalSeconds, 0, 0.05);
            _lastFrame = now;
            _frontSpace.Update(delta);
            _backSpace.Update(delta);
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

        /// <summary>拖拽文件到窗口 → 弹出新增对话框预填文件。</summary>
        /// <summary>添加文件(非文本/链接):v3.0.9 查重 + 询问 + 实际 AddFile</summary>
        private void AddFileWithDupCheck(MemoryItem item, string sourcePath)
        {
            // 查重:同 OriginalFileName + FileSizeBytes + IsDeleted=0
            var dup = _service?.FindDuplicate(item.OriginalFileName, item.FileSizeBytes);
            if (dup != null)
            {
                bool stillAdd = ConfirmDialog.ShowConfirm("记忆可能重复",
                    $"已存在同名同大小文件:\n「{dup.OriginalFileName}」({FormatBytes(item.FileSizeBytes)})\n\n是否仍要添加?",
                    this, isWarning: false);
                if (!stillAdd) return;
            }
            _service?.AddFile(item, sourcePath);
        }

        /// <summary>v3.0.9: 字节数 → 友好字符串(B/KB/MB/GB)。</summary>
        private static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB" };
            double value = bytes; int unit = 0;
            while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
            return $"{value:0.##} {units[unit]}";
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (_service == null) return;
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

                for (int i = 0; i < dialog.FilePaths.Count; i++)
                {
                    AddFileWithDupCheck(new MemoryItem
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
            catch (Exception ex)
            {
                App.Log("Window_Drop 处理失败: " + ex);
                new NoticeDialog("拖入文件失败", $"无法读取拖入的文件。\n{ex.Message}")
                    { Owner = this }.ShowDialog();
            }
        }

        private void FrontCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2 && EnsureAccess())
                FlipToBack();
        }

        private bool EnsureAccess()
        {
            if (_service == null) return false;
            bool passed;
            if (!_service.HasPassword())
            {
                var setup = new PasswordDialog(true) { Owner = this };
                if (setup.ShowDialog() != true) return false;
                _service.SetPassword(setup.Password);
                passed = true;
            }
            else
            {
                var verify = new PasswordDialog(false) { Owner = this };
                passed = verify.ShowDialog() == true && _service.VerifyPassword(verify.Password);
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

        private void OpenAddDialog()
        {
            if (_service == null) return;
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
                // 非文本：流式写入 SQLite BLOB 或外部文件（避免 OOM）
                for (int i = 0; i < dialog.FilePaths.Count; i++)
                {
                    AddFileWithDupCheck(new MemoryItem
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
            catch { /* 静默 */ }
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

            /// <summary>立即重置为默认色(不等待)。</summary>
            public void ResetHalo()
            {
                _haloFlashing = false;
                _haloT = 0;
                _haloBrush.GradientStops[0].Color = _haloDefault;
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
