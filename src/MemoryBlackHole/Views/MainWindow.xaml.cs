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

            Loaded += (_, _) =>
            {
                // 从程序集自动读取版本号
                var ver = Assembly.GetExecutingAssembly().GetName().Version;
                if (ver != null)
                    VersionText.Text = $"v{ver.Major}.{ver.Minor}.{ver.Build}";
                RefreshSearchResults();
                CompositionTarget.Rendering += OnRendering;

                // 反转探索页面滚动方向（匹配 Windows 标准行为）
                ResultsScrollViewer.PreviewMouseWheel += (s, e) =>
                {
                    ResultsScrollViewer.ScrollToVerticalOffset(
                        ResultsScrollViewer.VerticalOffset - e.Delta);
                    e.Handled = true;
                };
            };
            Closed += (_, _) =>
            {
                CompositionTarget.Rendering -= OnRendering;
            };
        }

        private void WindowFrame_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (WindowFrame.ActualWidth > 0 && WindowFrame.ActualHeight > 0)
                WindowFrame.Clip = new RectangleGeometry(
                    new Rect(0, 0, WindowFrame.ActualWidth, WindowFrame.ActualHeight), 18, 18);
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
        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (_service == null) return;
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var files = (string[]?)e.Data.GetData(DataFormats.FileDrop);
            if (files == null || files.Length == 0) return;

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
                _service.AddFile(new MemoryItem
                {
                    Type = dialog.SelectedType,
                    Title = dialog.OriginalFileNames[i],
                    Content = dialog.OriginalFileNames[i],
                    Note = null,
                    Tags = dialog.Tags,
                    OriginalFileName = dialog.OriginalFileNames[i]
                }, dialog.FilePaths[i]);
            }

            _frontSpace.PlayInward();
            RefreshSearchResults();
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
                    _service.AddFile(new MemoryItem
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

            _frontSpace.PlayInward();
            RefreshSearchResults();
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
                    RefreshSearchResults();
                }
                else if (preview.EditRequested)
                {
                    var edit = new EditMemoryDialog(item) { Owner = this };
                    if (edit.ShowDialog() == true)
                    {
                        _service?.Update(item);
                        RefreshSearchResults();
                    }
                }
            }
        }

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                RefreshSearchResults();
        }

        /// <summary>点击标签→按标签过滤搜索。</summary>
        private void TagItem_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is KeyValuePair<string, int> kv)
                        {
                            if (kv.Key == "全部标签")
                                _activeTag = null;
                            else
                                _activeTag = kv.Key;
                            SearchBox.Text = "";
                            RefreshSearchResults();
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
                        : $"黑洞吐出了 {results.Count} 条记忆" +
                          (!string.IsNullOrEmpty(_activeTag) ? $"（标签：{_activeTag}）" : "");

                if (results.Count > 0)
                {
                    _backSpace.PlayOutward();
                    ResultsList.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(420)));
                }

                // 刷新标签侧栏
                RefreshSidebar();
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
        /// 2.5D 风格化黑洞:引力透镜式椭圆吸积盘 + 光子球 + 双极喷流 + 池化粒子。
        /// 视觉由 4 层独立旋转的椭圆环(冷热渐变描边)、事件视界暗球、辉光、双极喷流、
        /// 以及绕轨道运行的彩色粒子组成。
        ///
        /// 性能策略:
        /// - 所有 Brush / Pen / Gradient 在 Build() 阶段 Freeze(),跨线程共享且无 per-frame 分配。
        /// - 80 颗粒子预分配在池中(对象复用),Update() 只改 Canvas 位置与 Opacity,不创建对象。
        /// - 不使用额外的 CompositionTarget hook;由 MainWindow 统一驱动 OnRendering → Update(delta)。
        /// - 喷发特效"借用"池中已 dead 节点,粒子死亡后回到池,GC 压力为零。
        /// </summary>
        private sealed class SpaceCore
        {
            // 整体尺寸缩放(v3.0.4):1.25x 适度放大,所有元素按此基准矢量缩放。
            // 关键:绝对不用 BlurEffect(1.5x 那次用户嫌糊,根因就是 BlurEffect 位图后处理)。
            // 辉光用 RadialGradientBrush 即可,主体轮廓保持矢量锐利。
            private const double SizeScale = 1.25;

            // 事件视界(纯黑实心,无 BlurEffect / 无渐变)
            private const double EventW    = 180 * SizeScale;
            private const double EventH    = 180 * SizeScale;
            // 中心光晕(暖/冷径向渐变,无模糊 — 渐变本身就是软光晕)
            private const double HaloW     = 460 * SizeScale;
            private const double HaloH     = 460 * SizeScale;
            // 光子球(锐利细亮环,无模糊,描边清晰)
            private const double PhotonW   = 220 * SizeScale;
            private const double PhotonH   = 220 * SizeScale;
            // 单层柔光吸积盘(无旋转、无模糊)
            private const double DiskW     = 600 * SizeScale;
            private const double DiskH     = 600 * SizeScale;

            // 吸积粒子轨道半径(放大 1.25x,范围更广)
            private const double OrbitRInner = 110 * SizeScale;     // 视界外缘
            private const double OrbitROuter = 360 * SizeScale;     // 外圈出生半径
            // 喷发粒子(吸/吐)轨道(放大 1.25x)
            private const double BurstRInner = 110 * SizeScale;
            private const double BurstROuter = 340 * SizeScale;
            // 喷发拖尾节点数
            private const int BurstTrailLen = 12;

            // 吸积粒子数量(60→100,密度提升 + 范围更大)
            private const int OrbitPoolSize = 100;
            // 喷发粒子(吸/吐)池大小
            private const int BurstPoolSize = 18;

            private readonly Canvas _canvas;
            private readonly bool _warm;

            // 中心结构
            private Ellipse _halo = null!;
            private Ellipse _disk = null!;
            private Ellipse _eventHorizon = null!;
            private Ellipse _photonRing = null!;
            private Ellipse _shockwave = null!;

            // 吸积粒子池(100 颗,对数螺线向内 + 开普勒式角速度)
            private readonly List<Ellipse> _orbitPool = new();
            private readonly double[] _orbitAngle = new double[OrbitPoolSize];
            private readonly double[] _orbitRadius = new double[OrbitPoolSize];
            private readonly double[] _orbitSpeed = new double[OrbitPoolSize];
            private readonly double[] _orbitSize = new double[OrbitPoolSize];
            private readonly double[] _orbitBaseAlpha = new double[OrbitPoolSize];
            // 螺旋收缩率(每颗不同,产生层次感)
            private readonly double[] _orbitShrink = new double[OrbitPoolSize];

            // 喷发粒子池(吸/吐各 9 颗)
            // 每颗:头部 Ellipse(亮点) + Polyline(弧线拖尾,12 节点)
            // 拖尾用 LinearGradientBrush(头部浓、尾部淡)模拟被引力拉长的光带
            private readonly List<Ellipse> _burstPool = new();
            private readonly List<Polyline> _burstTrails = new();
            private readonly double[] _burstAge = new double[BurstPoolSize];
            private readonly double[] _burstLife = new double[BurstPoolSize];
            // 位置 (bx, by) — 每帧从 Update 写入
            private readonly double[] _burstX = new double[BurstPoolSize];
            private readonly double[] _burstY = new double[BurstPoolSize];
            // 起始参数
            private readonly double[] _burstStartR = new double[BurstPoolSize];
            private readonly double[] _burstStartAngle = new double[BurstPoolSize];
            private readonly double[] _burstBaseSize = new double[BurstPoolSize];
            // 吸入用:对数螺线 b
            private readonly double[] _burstSpiralB = new double[BurstPoolSize];
            // 吐出用:方向 + 切向/径向
            private readonly double[] _burstDirX = new double[BurstPoolSize];
            private readonly double[] _burstDirY = new double[BurstPoolSize];
            private readonly double[] _burstSpeed = new double[BurstPoolSize];
            private readonly double[] _burstTangentRatio = new double[BurstPoolSize];
            // 拖尾位置环形缓冲
            private readonly double[] _burstTrailX = new double[BurstPoolSize * BurstTrailLen];
            private readonly double[] _burstTrailY = new double[BurstPoolSize * BurstTrailLen];
            private readonly int[] _burstTrailHead = new int[BurstPoolSize];
            private readonly bool[] _burstAlive = new bool[BurstPoolSize];
            private readonly bool[] _burstIsInward = new bool[BurstPoolSize];

            private double _time;
            private double _pulse;
            private double _pulseDecay;
            private double _shockwaveAge;
            private double _shockwaveLife;

            public SpaceCore(Canvas canvas, bool warm)
            {
                _canvas = canvas; _warm = warm; Build();
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
                    ? Color.FromArgb(150, 0xFF, 0xA0, 0x40)
                    : Color.FromArgb(140, 0x80, 0xC0, 0xFF),
                    Color.FromArgb(0, 0, 0, 0));
                _canvas.Children.Add(_disk);

                // 2) 中心光晕(暖色径向,无 BlurEffect)
                _halo = new Ellipse
                {
                    Width = HaloW, Height = HaloH,
                    IsHitTestVisible = false,
                    Opacity = 0.55,
                };
                _halo.Fill = MakeRadialGlow(_warm
                    ? Color.FromArgb(200, 0xFF, 0x7A, 0x28)
                    : Color.FromArgb(190, 0x60, 0xB8, 0xFF),
                    Color.FromArgb(0, 0, 0, 0));
                _canvas.Children.Add(_halo);

                // 3) 事件视界 — 纯黑实心
                _eventHorizon = new Ellipse
                {
                    Width = EventW, Height = EventH,
                    IsHitTestVisible = false,
                    Fill = Freeze(new SolidColorBrush(Color.FromRgb(0, 0, 0))),
                };
                _canvas.Children.Add(_eventHorizon);

                // 4) 光子球 — 锐利细亮环(描边粗细随 SizeScale 同步,保持锐利)
                _photonRing = new Ellipse
                {
                    Width = PhotonW, Height = PhotonH,
                    Stroke = Freeze(new SolidColorBrush(
                        _warm ? Color.FromArgb(220, 0xFF, 0xC8, 0x78)
                              : Color.FromArgb(220, 0xB0, 0xE0, 0xFF))),
                    StrokeThickness = 2.0 * SizeScale,   // 与 SizeScale 同步
                    IsHitTestVisible = false,
                    Opacity = 0.78,
                };
                _canvas.Children.Add(_photonRing);

                // 5) 中心涟漪(吸/吐时扩散,初始不可见,无模糊)
                _shockwave = new Ellipse
                {
                    Width = EventW, Height = EventH,
                    Stroke = Freeze(new SolidColorBrush(
                        _warm ? Color.FromArgb(200, 0xFF, 0xD2, 0x80)
                              : Color.FromArgb(200, 0xC8, 0xE8, 0xFF))),
                    StrokeThickness = 2.5 * SizeScale,
                    IsHitTestVisible = false,
                    Opacity = 0,
                };
                _canvas.Children.Add(_shockwave);

                // 6) 100 颗稳定吸积粒子 — 从最外圈出发,沿对数螺线向内,达视界后重生
                // 全部无 BlurEffect(清晰亮点),Doppler 增亮保留
                // 关键:角速度按 r^(-2) 加速(比开普勒 r^(-1.5) 更猛,产生"最后瞬间被吸入"的加速度感)
                var rng = new Random(_warm ? 17 : 113);
                for (int i = 0; i < OrbitPoolSize; i++)
                {
                    var dot = new Ellipse
                    {
                        IsHitTestVisible = false,
                    };
                    _orbitPool.Add(dot);
                    _canvas.Children.Add(dot);
                    _orbitAngle[i]     = rng.NextDouble() * TwoPi;
                    // 出生:最外圈 ± 抖动
                    _orbitRadius[i]    = OrbitROuter * (0.92 + rng.NextDouble() * 0.08);
                    // 基础角速度:0.18~0.73(外圈慢)
                    _orbitSpeed[i]     = 0.18 + rng.NextDouble() * 0.55;
                    // 粒子大小
                    _orbitSize[i]      = 1.6 + rng.NextDouble() * 2.6;
                    _orbitBaseAlpha[i] = 0.40 + rng.NextDouble() * 0.45;
                    // 螺旋收缩率:每颗 6~14 px/s(随机),产生层次感
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

                // 7) 喷发粒子池(18 颗,每颗一个 Ellipse 头部 + 一条 Polyline 拖尾)
                // 拖尾用 LinearGradientBrush(头部浓、尾部淡)模拟"被引力拉长的光带"
                for (int i = 0; i < BurstPoolSize; i++)
                {
                    var head = new Ellipse { IsHitTestVisible = false, Opacity = 0 };
                    head.Fill = Freeze(new RadialGradientBrush
                    {
                        // 头部立体感:中心白热 → 边缘暖色
                        GradientStops = FreezeStops(new (Color, double)[]
                        {
                            (Color.FromArgb(255, 255, 250, 220), 0.0),
                            (_warm ? Color.FromArgb(255, 0xFF, 0xC0, 0x70)
                                  : Color.FromArgb(255, 0xA0, 0xD8, 0xFF), 0.6),
                            (_warm ? Color.FromArgb(180, 0xFF, 0x80, 0x20)
                                  : Color.FromArgb(180, 0x60, 0xB0, 0xFF), 1.0),
                        })
                    });
                    _burstPool.Add(head);

                    var trail = new Polyline
                    {
                        IsHitTestVisible = false,
                        Opacity = 0,
                        StrokeThickness = 1.8 * SizeScale,    // 描边随 SizeScale 同步
                        StrokeLineJoin = PenLineJoin.Round,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round,
                        // 拖尾 gradient(头部浓、尾部淡) — 沿 polyline 长度方向
                        Stroke = MakeTrailGradient(_warm),
                    };
                    var pts = new PointCollection();
                    for (int k = 0; k < BurstTrailLen; k++) pts.Add(new Point(0, 0));
                    trail.Points = pts;
                    _burstTrails.Add(trail);

                    _canvas.Children.Add(trail);
                    _canvas.Children.Add(head);
                    _burstAlive[i] = false;
                    _burstTrailHead[i] = 0;
                }
            }

            /// <summary>
            /// 拖尾专用渐变:沿 polyline 方向 alpha 0→220,头部浓、尾部淡。
            /// </summary>
            private static Brush MakeTrailGradient(bool warm)
            {
                var lg = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0.5),
                    EndPoint   = new Point(1, 0.5),
                };
                Color hot = warm ? Color.FromRgb(0xFF, 0xB0, 0x60)
                                 : Color.FromRgb(0xA0, 0xC8, 0xFF);
                lg.GradientStops.Add(new GradientStop(Color.FromArgb(0,   hot.R, hot.G, hot.B), 0.0));
                lg.GradientStops.Add(new GradientStop(Color.FromArgb(80,  hot.R, hot.G, hot.B), 0.5));
                lg.GradientStops.Add(new GradientStop(Color.FromArgb(220, hot.R, hot.G, hot.B), 1.0));
                Freeze(lg);
                return lg;
            }

            private static Brush MakeRadialGlow(Color inner, Color outer)
            {
                var brush = new RadialGradientBrush(inner, outer);
                brush.Freeze();
                return brush;
            }

            private static GradientStopCollection FreezeStops((Color color, double offset)[] items)
            {
                var col = new GradientStopCollection();
                foreach (var (c, o) in items) col.Add(new GradientStop(c, o));
                col.Freeze();
                return col;
            }

            private static T Freeze<T>(T f) where T : Freezable
            {
                if (f.CanFreeze) f.Freeze();
                return f;
            }

            public void PlayInward() => StartBurst(inward: true);
            public void PlayOutward() => StartBurst(inward: false);

            /// <summary>
            /// 触发吸/吐特效(问题 3 重做):
            ///
            /// 吸入(9 颗"光带",写实引力捕获感):
            ///   - 起点:外圈 BurstROuter(9 颗均分角,带小扰动)
            ///   - 路径:对数螺线 r = R₀ · exp(-b·t)  b=2.4~3.2, **前 60% 时间走完 30% 距离,后 40% 走 70%(ease-in 加速旋入)**
            ///   - 角速度按 r^(-2) 加速(比开普勒 r^(-1.5) 更猛,产生"最后瞬间被切向卷入"的强烈引力感)
            ///   - 头部 size 随 t 增大(临近视界时被拉长成弧线)
            ///   - 头部 alpha 按多普勒蓝移:越靠中心越亮(0.6→1.0)
            ///   - 0~0.10 渐入, 0.92~1.00 渐出(在视界边被吞没)
            ///
            /// 吐出(9 颗"光粒",反向 + 切向):
            ///   - 起点:视界外缘 BurstRInner(9 颗均分角,带小扰动)
            ///   - 切向为主(切向 / 径向 = 1.3 / 0.25),模仿吸积盘物质溢出而非极向喷流
            ///   - ease-out cubic(前快后慢 — 初始柔和涌出,远端自然消散)
            ///   - 0~0.12 渐入, 0.85~1.00 渐出
            /// </summary>
            private void StartBurst(bool inward)
            {
                _pulse = 1.0;
                _pulseDecay = 1.4;
                _shockwaveAge = 0;
                _shockwaveLife = 1.0;

                const int target = 9;
                int spawned = 0;
                for (int i = 0; i < BurstPoolSize && spawned < target; i++)
                {
                    if (_burstAlive[i]) continue;
                    _burstAlive[i] = true;
                    _burstIsInward[i] = inward;
                    _burstAge[i] = 0;
                    _burstLife[i] = 1.6 + (spawned * 0.05);   // 1.6 ~ 2.0 秒
                    _burstBaseSize[i] = 4.0 + (spawned % 3) * 1.5;   // 4/5.5/7 px
                    // 起始位置:均分角 + 扰动
                    double baseAngle = (spawned / (double)target) * TwoPi;
                    double angleJitter = ((i % 3) - 1) * 0.08;
                    double ang = baseAngle + angleJitter;

                    if (inward)
                    {
                        // 吸入:对数螺线 + 强加速
                        _burstStartR[i]     = BurstROuter * (0.95 + (i % 4) * 0.02);
                        _burstStartAngle[i] = ang;
                        // b 越大越快旋入:2.4~3.2
                        _burstSpiralB[i]    = 2.4 + (i % 4) * 0.2;
                        _burstDirX[i] = 0; _burstDirY[i] = 0; _burstSpeed[i] = 0; _burstTangentRatio[i] = 0;
                    }
                    else
                    {
                        // 吐出:切向为主 + 径向少量
                        // 切向单位向量(椭圆压扁 0.55)
                        double tx = -Math.Sin(ang);
                        double ty =  Math.Cos(ang) * 0.55;
                        // 径向单位向量
                        double rx =  Math.Cos(ang);
                        double ry =  Math.Sin(ang) * 0.55;
                        // 切向 1.3 + 径向 0.25
                        double tangent = 1.3;
                        double radial  = 0.25;
                        double vx = tx * tangent + rx * radial;
                        double vy = ty * tangent + ry * radial;
                        // 速度:380 ~ 540 px/s
                        double speed = 380 + (spawned % 4) * 50;
                        _burstStartR[i]       = BurstRInner;
                        _burstStartAngle[i]   = ang;
                        _burstDirX[i]         = vx;
                        _burstDirY[i]         = vy;
                        _burstSpeed[i]        = speed;
                        _burstTangentRatio[i] = tangent;
                        _burstSpiralB[i]      = 0;
                    }

                    // 头部 / 拖尾
                    var head = _burstPool[i];
                    head.Width = head.Height = _burstBaseSize[i];
                    head.Opacity = 0;
                    var trail = _burstTrails[i];
                    var pts = trail.Points;
                    for (int k = 0; k < BurstTrailLen; k++) pts[k] = new Point(0, 0);
                    trail.Opacity = 0;
                    _burstTrailHead[i] = 0;
                    spawned++;
                }
            }

            public void Update(double delta)
            {
                _time += delta;
                if (_pulse > 0) _pulse = Math.Max(0, _pulse - _pulseDecay * delta);
                double coreGlowOpacity = 0.55 + _pulse * 0.35;
                double photonRingOpacity = 0.78 + _pulse * 0.18;
                double diskOpacity = 0.30 + _pulse * 0.15;

                double cx = _canvas.ActualWidth  > 0 ? _canvas.ActualWidth  / 2 : 640;
                double cy = _canvas.ActualHeight > 0 ? _canvas.ActualHeight / 2 : 370;

                // 1) 吸积盘(慢呼吸)
                double breath = 1.0 + Math.Sin(_time * 0.6) * 0.025;
                LayoutCentered(_disk, cx, cy, breath * (1.0 + _pulse * 0.10));
                _disk.Opacity = diskOpacity;

                // 2) 中心光晕
                LayoutCentered(_halo, cx, cy, 1.0 + _pulse * 0.10);
                _halo.Opacity = coreGlowOpacity;

                // 3) 事件视界 + 光子球
                double scale = 1.0 + _pulse * 0.08;
                LayoutCentered(_photonRing,   cx, cy, scale);
                LayoutCentered(_eventHorizon, cx, cy, scale);
                _photonRing.Opacity   = Math.Min(1.0, photonRingOpacity);
                _eventHorizon.Opacity = 1.0;

                // 4) 中心涟漪
                if (_shockwaveAge < _shockwaveLife)
                {
                    _shockwaveAge += delta;
                    double sw = Math.Clamp(_shockwaveAge / _shockwaveLife, 0, 1);
                    double swScale = 1.0 + (1 - Math.Pow(1 - sw, 2)) * 0.95;
                    LayoutCentered(_shockwave, cx, cy, swScale);
                    _shockwave.Opacity = (1.0 - sw) * 0.50;
                }
                else
                {
                    _shockwave.Opacity = 0;
                }

                // 5) 100 颗稳定吸积粒子 — 对数螺线向心 + r^(-2) 角速度加速
                // 关键:外圈慢,越靠近视界越快,最后瞬间被切向卷入
                for (int i = 0; i < OrbitPoolSize; i++)
                {
                    // 角速度按 r^(-2) 加速(比开普勒 r^(-1.5) 更猛)
                    double omega = _orbitSpeed[i] * Math.Pow(OrbitROuter / _orbitRadius[i], 2.0);
                    _orbitAngle[i] += omega * delta;
                    // 半径每颗按 _orbitShrink[i] 持续线性收缩
                    _orbitRadius[i] -= _orbitShrink[i] * delta;
                    // 到达视界外缘 → 立即从外圈重生(永远不穿越)
                    if (_orbitRadius[i] <= OrbitRInner)
                    {
                        _orbitRadius[i] = OrbitROuter * (0.92 + (i % 6) * 0.015);
                        _orbitAngle[i]  += (i % 5) * 0.15;
                    }
                    double r = _orbitRadius[i];
                    // 椭圆压扁 0.52 模拟盘面
                    double x = Math.Cos(_orbitAngle[i]) * r;
                    double y = Math.Sin(_orbitAngle[i]) * r * 0.52;
                    // Doppler 增亮:右半侧(向观察者来)更亮
                    double dop = 0.5 + 0.5 * Math.Cos(_orbitAngle[i]);
                    double a = _orbitBaseAlpha[i] * (0.45 + dop * 0.65);
                    var dot = _orbitPool[i];
                    dot.Opacity = a;
                    Canvas.SetLeft(dot, cx + x - dot.Width / 2);
                    Canvas.SetTop(dot,  cy + y - dot.Height / 2);
                }

                // 6) 9 颗喷发光带/光粒(吸/吐 — 自由发挥,见 StartBurst 注释)
                for (int i = 0; i < BurstPoolSize; i++)
                {
                    if (!_burstAlive[i]) continue;
                    _burstAge[i] += delta;
                    double t = _burstAge[i] / _burstLife[i];
                    if (t >= 1.0)
                    {
                        _burstAlive[i] = false;
                        _burstPool[i].Opacity = 0;
                        _burstTrails[i].Opacity = 0;
                        continue;
                    }

                    double bx, by;
                    if (_burstIsInward[i])
                    {
                        // 吸入:对数螺线 + r^(-2) 角速度加速(强引力感)
                        // 半径:r = R₀ · exp(-b·t)
                        double r = _burstStartR[i] * Math.Exp(-_burstSpiralB[i] * t);
                        if (r < 4) r = 4;
                        // 角速度:基线 + r^(-2) 加速(已收缩的圆周转得更快)
                        double omegaAccel = Math.Pow(_burstStartR[i] / r, 2.0);
                        // 当前累积相位:在 Update 中累加(原 StartBurst 中已给 _burstStartAngle)
                        // 这里用本地 phase 变量更清晰,但代码已用 _burstX/Y 存位置,直接用数组
                        // 简化:角速度积分 = _burstStartAngle + ∫ω dt
                        // 用闭式:角速度按 r^(-2), r 指数衰减, ∫ω dt 的解析式比较复杂
                        // 改用数值:每帧累加 omega = baseOmega * (R0/r)^2
                        // 但需要状态变量存 phase — 借用 _burstX[?] 不合适,直接定义 phase 累加
                        // 实际方案:用 _burstStartAngle 作为初始 phase, 每帧 phase += omega * delta
                        // 但 _burstStartAngle 是 const-ish(只在 StartBurst 设一次),
                        // 改成 _burstAge 同时累加 phase 字段会破坏现有数组;
                        // 替代:用 _burstDirX 存 phase 累加值
                        // 已经在 StartBurst 中设 _burstDirX=0,这里累加
                        double baseOmega = 4.5;    // 基础角速度(rad/s),R0 处
                        _burstDirX[i] += baseOmega * omegaAccel * delta;
                        double phase = _burstStartAngle[i] + _burstDirX[i];
                        bx = Math.Cos(phase) * r;
                        by = Math.Sin(phase) * r * 0.55;
                    }
                    else
                    {
                        // 吐出:切向初速度 + ease-out cubic 减速
                        // 距离 = speed * ease * life * 0.55
                        double ease = 1.0 - Math.Pow(1.0 - t, 3.0);
                        double travel = _burstSpeed[i] * ease * _burstLife[i] * 0.55;
                        bx = _burstDirX[i] * travel;
                        by = _burstDirY[i] * travel;
                    }
                    _burstX[i] = bx;
                    _burstY[i] = by;

                    // 头部透明度
                    double headOpacity;
                    if (_burstIsInward[i])
                    {
                        // 吸入:0~0.10 渐入, 0.92~1.00 渐出
                        // 越靠近中心越亮(多普勒蓝移):0.6→1.0
                        if (t < 0.10) headOpacity = t / 0.10;
                        else if (t < 0.92) headOpacity = 1.0;
                        else headOpacity = Math.Max(0, 1.0 - (t - 0.92) / 0.08);
                        headOpacity = Math.Min(1.0, headOpacity * (0.6 + 0.4 * t));
                        // 头部放大:临近视界时被拉长
                        double s = _burstBaseSize[i] * (1.0 + t * 1.4);
                        _burstPool[i].Width = _burstPool[i].Height = s;
                    }
                    else
                    {
                        // 吐出:0~0.12 渐入(中心亮闪), 0.85~1.00 渐出
                        if (t < 0.12) headOpacity = t / 0.12;
                        else if (t < 0.85) headOpacity = 1.0;
                        else headOpacity = Math.Max(0, 1.0 - (t - 0.85) / 0.15);
                    }

                    // 拖尾环形缓冲
                    int head = _burstTrailHead[i];
                    int baseIdx = i * BurstTrailLen;
                    _burstTrailX[baseIdx + head] = bx;
                    _burstTrailY[baseIdx + head] = by;
                    _burstTrailHead[i] = (head + 1) % BurstTrailLen;
                    // Polyline 写入(从最旧到最新)
                    var pts = _burstTrails[i].Points;
                    for (int k = 0; k < BurstTrailLen; k++)
                    {
                        int srcIdx = (head + 1 + k) % BurstTrailLen;
                        pts[k] = new Point(
                            cx + _burstTrailX[baseIdx + srcIdx],
                            cy + _burstTrailY[baseIdx + srcIdx]);
                    }
                    _burstTrails[i].Opacity = headOpacity * 0.85;
                    Canvas.SetLeft(_burstPool[i], cx + bx - _burstPool[i].Width / 2);
                    Canvas.SetTop(_burstPool[i],  cy + by - _burstPool[i].Height / 2);
                    _burstPool[i].Opacity = headOpacity;
                }
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
