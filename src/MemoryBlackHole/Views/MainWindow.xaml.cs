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
            private const int OrbitPoolSize = 60;
            private const int BurstPoolSize = 24;
            private const double TwoPi = Math.PI * 2.0;

            // 整体尺寸缩放系数(v3.0.1):所有视觉元素长宽/半径都按此基准。
            // 修改这一个常量即可统一调整黑洞大小。
            private const double SizeScale = 1.5;

            // 吸积盘统一使用 SizeScale 后的基准尺寸
            private const double CoreGlowW  = 480 * SizeScale;   // 中心辉光
            private const double CoreGlowH  = 360 * SizeScale;
            private const double EventW     = 200 * SizeScale;   // 事件视界
            private const double EventH     = 190 * SizeScale;
            private const double PhotonW    = 244 * SizeScale;   // 光子球
            private const double PhotonH    = 232 * SizeScale;
            private const double DiskHaloW  = 720 * SizeScale;   // 替代 4 环的单层柔光环
            private const double DiskHaloH  = 460 * SizeScale;
            private const double JetHalfW    = 40  * SizeScale;   // 双极喷流
            private const double JetLength   = 220 * SizeScale;

            // 粒子轨道半径基准(放大 1.5x)
            private const double OrbitRInner = 95  * SizeScale;
            private const double OrbitROuter = 330 * SizeScale;
            private const double OrbitResetR = 138;              // < 此值重置回外圈
            private const double SpawnRBase  = 470 * SizeScale;  // 喷发起始半径

            // 拖尾节点数
            private const int TrailLen = 10;

            private readonly Canvas _canvas;
            private readonly bool _warm;
            private System.Windows.Shapes.Path _jetTop = null!;
            private System.Windows.Shapes.Path _jetBottom = null!;
            private Ellipse _diskHalo = null!;   // 替代 4 环的单层柔和吸积盘
            private Ellipse _photonRing = null!;
            private Ellipse _eventHorizon = null!;
            private Ellipse _coreGlow = null!;
            private Ellipse _shockwave = null!;   // 中心涟漪(吸/吐时扩散)

            private readonly List<Ellipse> _orbitPool = new();
            private readonly double[] _orbitAngle = new double[OrbitPoolSize];
            private readonly double[] _orbitRadius = new double[OrbitPoolSize];
            private readonly double[] _orbitSpeed = new double[OrbitPoolSize];
            private readonly double[] _orbitSize = new double[OrbitPoolSize];
            private readonly double[] _orbitBaseAlpha = new double[OrbitPoolSize];

            // 喷发粒子:每颗一个 Ellipse(头部亮点) + 一条 Polyline(拖尾)
            // Polyline 改用 LinearGradientBrush 沿拖尾做 alpha 渐变(头部浓、尾部淡)
            // 才能模拟"被引力拉长的光带",而非生硬的实线。
            private readonly List<Ellipse> _burstPool = new();
            private readonly List<Polyline> _burstTrails = new();
            private readonly double[] _burstAge = new double[BurstPoolSize];
            private readonly double[] _burstLife = new double[BurstPoolSize];
            // 吸入:开普勒式轨道参数
            private readonly double[] _burstSemiMajor = new double[BurstPoolSize];  // 半长轴 a(像素)
            private readonly double[] _burstEccentricity = new double[BurstPoolSize];// 离心率 e∈[0,0.7]
            private readonly double[] _burstPhase0 = new double[BurstPoolSize];    // 初始相位(角度)
            private readonly double[] _burstOmega0 = new double[BurstPoolSize];    // 初始角速度
            private readonly double[] _burstBaseSize = new double[BurstPoolSize];
            // 吐出:切向溢出参数
            private readonly double[] _burstDirX = new double[BurstPoolSize];      // 切向初速度方向
            private readonly double[] _burstDirY = new double[BurstPoolSize];
            private readonly double[] _burstSpeed = new double[BurstPoolSize];
            private readonly double[] _burstTangentRatio = new double[BurstPoolSize];// 切向/径向 比(1.0=纯切向,0=纯径向)
            private readonly double[] _burstBaseR = new double[BurstPoolSize];     // 粒子起始半径(吸/吐都可能是 0)
            // 拖尾位置环形缓冲
            private readonly double[] _burstTrailX = new double[BurstPoolSize * TrailLen];
            private readonly double[] _burstTrailY = new double[BurstPoolSize * TrailLen];
            private readonly int[] _burstTrailHead = new int[BurstPoolSize];
            private readonly bool[] _burstAlive = new bool[BurstPoolSize];

            // 区分"吸入粒子"和"吐出粒子"(共享同一池):用 isInward 数组存
            private readonly bool[] _burstIsInward = new bool[BurstPoolSize];

            private double _time;
            private double _pulse;
            private double _pulseDecay;
            private double _shockwaveAge;     // 涟漪年龄
            private double _shockwaveLife;    // 涟漪寿命

            public SpaceCore(Canvas canvas, bool warm)
            {
                _canvas = canvas; _warm = warm; Build();
            }

            private void Build()
            {
                // 1) 单层柔和吸积盘(替代原 4 环):大椭圆,统一暖/冷单色调,无旋转,慢呼吸
                _diskHalo = new Ellipse
                {
                    Width = DiskHaloW,
                    Height = DiskHaloH,
                    IsHitTestVisible = false,
                    Opacity = 0.32,
                };
                _diskHalo.Fill = MakeRadialGlow(_warm
                    ? Color.FromArgb(170, 0xFF, 0xA1, 0x4A)
                    : Color.FromArgb(160, 0x8E, 0xC8, 0xFF),
                    Color.FromArgb(0, 0, 0, 0));
                _diskHalo.Effect = new BlurEffect { Radius = 32 * SizeScale, KernelType = KernelType.Gaussian };
                _canvas.Children.Add(_diskHalo);

                // 2) 中心辉光
                _coreGlow = new Ellipse
                {
                    Width = CoreGlowW, Height = CoreGlowH,
                    IsHitTestVisible = false,
                    Opacity = 0.55,
                };
                _coreGlow.Fill = MakeRadialGlow(_warm
                    ? Color.FromArgb(220, 0xFF, 0x86, 0x2E)
                    : Color.FromArgb(210, 0x6B, 0xC8, 0xFF),
                    Color.FromArgb(0, 0, 0, 0));
                _coreGlow.Effect = new BlurEffect { Radius = 48 * SizeScale, KernelType = KernelType.Gaussian };
                _canvas.Children.Add(_coreGlow);

                // 3) 事件视界
                _eventHorizon = new Ellipse
                {
                    Width = EventW, Height = EventH, IsHitTestVisible = false,
                };
                _eventHorizon.Fill = Freeze(new RadialGradientBrush
                {
                    GradientStops = FreezeStops(new (Color, double)[]
                    {
                        (Color.FromRgb(0, 0, 0),         0.00),
                        (Color.FromRgb(0, 0, 0),         0.78),
                        (_warm ? Color.FromRgb(0xFF, 0x6A, 0x18)
                              : Color.FromRgb(0x3E, 0xB4, 0xFF), 0.92),
                        (Color.FromArgb(0, 0, 0, 0),    1.00),
                    })
                });
                _canvas.Children.Add(_eventHorizon);

                // 4) 光子球 — 描边宽度随 SizeScale 同步,保持锐利
                _photonRing = new Ellipse
                {
                    Width = PhotonW, Height = PhotonH,
                    Stroke = Freeze(new SolidColorBrush(
                        _warm ? Color.FromArgb(220, 0xFF, 0xC2, 0x70)
                              : Color.FromArgb(220, 0xB8, 0xE8, 0xFF))),
                    StrokeThickness = 2.0 * SizeScale,   // 关键:与 SizeScale 同步
                    IsHitTestVisible = false,
                    Opacity = 0.82,
                };
                _canvas.Children.Add(_photonRing);

                // 5) 双极喷流(放大 1.5x)— 用梯形 Path 而非矩形:
                //    根部 alpha=0(中心不画出竖线),中部 alpha 达峰,顶端 alpha=0
                //    + 用 PathGeometry 自己画 4 个点形成"颈部收窄"的视觉
                _jetTop = MakeJetTrapezoid(JetHalfW, -JetLength, _warm, _isTop: true);
                _jetBottom = MakeJetTrapezoid(JetHalfW,  JetLength, _warm, _isTop: false);
                _canvas.Children.Add(_jetBottom);
                _canvas.Children.Add(_jetTop);

                // 5.5) 中心涟漪(吸/吐时扩散,初始不可见)
                _shockwave = new Ellipse
                {
                    Width = EventW, Height = EventH,
                    Stroke = Freeze(new SolidColorBrush(
                        _warm ? Color.FromArgb(180, 0xFF, 0xD2, 0x80)
                              : Color.FromArgb(180, 0xCC, 0xEC, 0xFF))),
                    StrokeThickness = 2.5 * SizeScale,
                    IsHitTestVisible = false,
                    Opacity = 0,
                };
                _canvas.Children.Add(_shockwave);

                // 6) 稳定吸积粒子池(60 颗,1.5x 半径)— Blur 与 SizeScale 同步
                var rng = new Random(_warm ? 17 : 113);
                for (int i = 0; i < OrbitPoolSize; i++)
                {
                    var dot = new Ellipse
                    {
                        IsHitTestVisible = false,
                        Effect = new BlurEffect { Radius = 5 * SizeScale, KernelType = KernelType.Gaussian },
                    };
                    _orbitPool.Add(dot);
                    _canvas.Children.Add(dot);
                    _orbitAngle[i]     = rng.NextDouble() * TwoPi;
                    _orbitRadius[i]    = OrbitRInner + rng.NextDouble() * (OrbitROuter - OrbitRInner);
                    _orbitSpeed[i]     = 0.18 + rng.NextDouble() * 0.55;
                    _orbitSize[i]      = 1.8 + rng.NextDouble() * 3.8;
                    _orbitBaseAlpha[i] = 0.35 + rng.NextDouble() * 0.55;
                    // 离视界近的偏白热,远的偏冷
                    double t = (_orbitRadius[i] - OrbitRInner) / (OrbitROuter - OrbitRInner);
                    byte r = (byte)(255 * (1 - t * 0.55));
                    byte g = (byte)(_warm ? (200 - t * 60) : (180 + t * 40));
                    byte b = (byte)(_warm ? (90 + t * 60)  : (255 - t * 30));
                    var brush = Freeze(new SolidColorBrush(Color.FromArgb(220, r, g, b)));
                    dot.Fill = brush;
                    dot.Width = dot.Height = _orbitSize[i];
                }

                // 7) 喷发粒子池(24 颗:每颗一个 Ellipse 头部 + 一条 Polyline 拖尾)
                // 拖尾 stroke 用 LinearGradientBrush(沿 polyline 长度方向 alpha 渐变),
                // 头部浓(1.0) → 尾部淡(0.0),才能模拟"被引力拉长的光带"而非生硬实线。
                for (int i = 0; i < BurstPoolSize; i++)
                {
                    var head = new Ellipse { IsHitTestVisible = false, Opacity = 0 };
                    head.Effect = new BlurEffect { Radius = 6 * SizeScale, KernelType = KernelType.Gaussian };
                    _burstPool.Add(head);

                    var trail = new Polyline
                    {
                        IsHitTestVisible = false,
                        Opacity = 0,
                        StrokeThickness = 2.2 * SizeScale,
                        StrokeLineJoin = PenLineJoin.Round,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round,
                        Stroke = MakeTrailGradient(_warm),   // 沿拖尾方向 alpha 渐变
                    };
                    // 拖尾自身轻微模糊(更柔和)— 与 SizeScale 同步
                    trail.Effect = new BlurEffect { Radius = 2.5 * SizeScale, KernelType = KernelType.Gaussian };
                    var pts = new PointCollection();
                    for (int k = 0; k < TrailLen; k++) pts.Add(new Point(0, 0));
                    trail.Points = pts;
                    _burstTrails.Add(trail);

                    _canvas.Children.Add(trail);
                    _canvas.Children.Add(head);
                    _burstAlive[i] = false;
                    _burstTrailHead[i] = 0;
                }
            }

            /// <summary>
            /// 构造一条双极喷流 Path,形态为"梯形"(根部 0、中部最宽、顶端 0),
            /// 渐变 alpha 从 0(根部)→ 峰值 0.5 附近 → 0(顶端),
            /// 避免在中心叠加形成竖直光带。
            /// </summary>
            private static System.Windows.Shapes.Path MakeJetTrapezoid(double halfWidth, double tipY, bool warm, bool _isTop)
            {
                double h = Math.Abs(tipY) * 2;
                // 梯形:根部坐标(0, h/2),沿顶端(tipY)从根部出发
                // 因为 Path 用 Stretch.None,内部坐标系以 (0,0) 为左上角
                // tipY < 0 表示朝上,Path 内部坐标让 root 在 y=h/2,top 在 y=0
                // 反之 tipY > 0,root 在 y=0,top 在 y=h
                double rootY = tipY < 0 ? h : 0;
                double topY  = tipY < 0 ? 0 : h;
                double dir   = tipY < 0 ? -1 : 1;

                // 顶点(梯形 4 个):root-left, root-right, top-left, top-right
                // 根部 width=0(收成一点),中部 width=halfWidth*2(展宽)
                // 用 PathGeometry + 4 个 Bezier 控制点构造"颈部"
                // 简化:用三角形(root=0)代替梯形
                // 三角形 3 个点:(0, rootY), (-halfWidth, rootY+dir*h*0.5), (+halfWidth, rootY+dir*h*0.5)
                // 这样根部是一点 0,中段最宽,顶端收成一点 0
                var pg = new System.Windows.Shapes.Path
                {
                    Width = halfWidth * 2,
                    Height = h,
                    IsHitTestVisible = false,
                    Stretch = Stretch.None,
                };
                var geom = new PathGeometry();
                // 根部中点(rootX = halfWidth,因为内部 x∈[0, 2*halfWidth])
                double rootX = halfWidth;
                // 三角形 3 个点(相对于内部 Rect(0,0,2*halfWidth,h))
                var fig = new PathFigure { IsClosed = true, StartPoint = new Point(rootX, rootY) };
                // 中段最宽点(在中点高度处,收向顶端)
                double midY = rootY + dir * h * 0.55;
                fig.Segments.Add(new LineSegment(new Point(0, midY), isStroked: false));
                fig.Segments.Add(new LineSegment(new Point(2 * halfWidth, midY), isStroked: false));
                fig.Segments.Add(new LineSegment(new Point(rootX, topY), isStroked: false));
                geom.Figures.Add(fig);
                Freeze(geom);
                pg.Data = geom;

                // 渐变:alpha 根部 0 → 中段 0.55 位置 0.5 处 alpha 峰值 → 顶端 0
                var gradient = new LinearGradientBrush
                {
                    StartPoint = new Point(0.5, tipY < 0 ? 1.0 : 0.0),
                    EndPoint   = new Point(0.5, tipY < 0 ? 0.0 : 1.0),
                };
                Color hot = warm ? Color.FromArgb(0xFF, 0x9A, 0x40)
                                 : Color.FromArgb(0x7A, 0xC8, 0xFF);
                // 关键:根部 alpha 0(不画竖条),中段 alpha 100(柔和),顶端 alpha 0
                gradient.GradientStops.Add(new GradientStop(Color.FromArgb(0,   hot.R, hot.G, hot.B), 0.0));
                gradient.GradientStops.Add(new GradientStop(Color.FromArgb(0,   hot.R, hot.G, hot.B), 0.20));
                gradient.GradientStops.Add(new GradientStop(Color.FromArgb(110, hot.R, hot.G, hot.B), 0.55));
                gradient.GradientStops.Add(new GradientStop(Color.FromArgb(0,   hot.R, hot.G, hot.B), 1.0));
                Freeze(gradient);
                pg.Fill = gradient;
                return pg;
            }

            /// <summary>
            /// 拖尾专用渐变:沿 polyline 方向(从尾部到尾端)alpha 1.0 → 0.0,
            /// 让光带像被引力拉长、自然淡出,而不是生硬实线。
            /// 由于 Polyline 没有内置 "StartPoint/EndPoint 沿自身长度" 概念,
            /// 用 X 轴 0→1 渐变,视觉上因为 polyline 是连续线段也能看出头尾深浅。
            /// </summary>
            private Brush MakeTrailGradient(bool warm)
            {
                var lg = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0.5),
                    EndPoint   = new Point(1, 0.5),
                };
                Color hot = warm ? Color.FromRgb(0xFF, 0xB0, 0x60)
                                 : Color.FromRgb(0x9C, 0xD8, 0xFF);
                lg.GradientStops.Add(new GradientStop(Color.FromArgb(0,   hot.R, hot.G, hot.B), 0.0));
                lg.GradientStops.Add(new GradientStop(Color.FromArgb(60,  hot.R, hot.G, hot.B), 0.5));
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
            /// 触发吸入/吐出的喷发动画。
            ///
            /// 吸入(12 颗)——"被引力捕获、加速旋入":
            ///   每颗初始化为一条开普勒式椭圆轨道(半长轴 a, 离心率 e, 初始相位 θ₀, 初始角速度 ω₀)。
            ///   在 Update 中用开普勒第三定律 ω ∝ a^(-3/2) 演化角速度,越靠近视界 a 越小、ω 越大,
            ///   视觉上:外圈慢旋转, 越靠中心越快被切向"卷"入, 拖尾被拉成弧线。
            ///   亮度按多普勒蓝移: 越靠中心 cos(θ) 越正向 → 越亮。
            ///
            /// 吐出(16 颗)——"吸积盘物质溢出":
            ///   每颗从事件视界边缘沿吸积盘平面切向抛出(切向 / 径向 = 1.2),
            ///   角速度按 r^(-1.5) 缓慢衰减(像开普勒运动自然减速),
            ///   远离视界后 ease-out 减速变淡, 不是直接弹射飞出。
            /// </summary>
            private void StartBurst(bool inward)
            {
                _pulse = 1.0;
                _pulseDecay = 1.6;
                // 中心涟漪(吸/吐共用)
                _shockwaveAge = 0;
                _shockwaveLife = 1.2;

                int target = inward ? 12 : 16;
                int spawned = 0;
                for (int i = 0; i < BurstPoolSize && spawned < target; i++)
                {
                    if (_burstAlive[i]) continue;
                    _burstAlive[i] = true;
                    _burstIsInward[i] = inward;
                    _burstAge[i] = 0;
                    _burstLife[i] = inward
                        ? 1.2 + (spawned * 0.06)               // 1.2 ~ 1.86 秒
                        : 1.6 + (spawned * 0.05);              // 1.6 ~ 2.35 秒
                    _burstBaseSize[i] = 3.2 + (spawned % 4) * 1.4;

                    if (inward)
                    {
                        // 吸入:开普勒椭圆轨道
                        // 12 颗均匀分布在外圈 705~759 半径处,初始相位错开形成"被引力的有机轨道"
                        double phase0 = (spawned / 12.0) * TwoPi + (i % 3) * 0.32;
                        // 半长轴从 0.85*SpawnRBase 到 1.15*SpawnRBase
                        double a = SpawnRBase * (0.85 + (spawned % 4) * 0.10);
                        // 离心率 0.35~0.65(椭圆)
                        double e = 0.35 + (spawned % 3) * 0.10;
                        // 初始角速度:按开普勒 ω₀ = k * a^(-3/2),单位 rad/s
                        // 选 k 使外圈 a=SpawnRBase=705 时 ω₀ ≈ 0.7 rad/s(约 12 秒一圈)
                        double k = 0.7 * Math.Pow(a, 1.5);
                        double omega0 = k / Math.Pow(a, 1.5);
                        _burstSemiMajor[i]     = a;
                        _burstEccentricity[i]  = e;
                        _burstPhase0[i]        = phase0;
                        _burstOmega0[i]        = omega0;
                        // 初始半径: 椭圆远心点 r_max = a*(1+e)
                        _burstBaseR[i]         = a * (1 + e);
                        _burstDirX[i] = 0; _burstDirY[i] = 0; _burstSpeed[i] = 0; _burstTangentRatio[i] = 0;
                    }
                    else
                    {
                        // 吐出:吸积盘物质溢出 — 沿盘面切向抛出
                        // 16 颗分成 4 组, 每组 4 颗近似同向
                        int group = spawned / 4;
                        int inGroup = spawned % 4;
                        // 每组 4 颗:基向 + 在切向两侧展开 ±0.12, ±0.04 rad
                        double groupAngle = (group / 4.0) * TwoPi + 0.15;
                        double localSpread = (inGroup - 1.5) * 0.06;   // -0.09 ~ 0.09
                        double theta = groupAngle + localSpread;
                        // 切向单位向量(在椭圆压扁盘面内)
                        double tx = -Math.Sin(theta);                  // 切向 = 角度导数方向
                        double ty =  Math.Cos(theta) * 0.55;           // 椭圆压扁
                        // 径向单位向量
                        double rx =  Math.Cos(theta);
                        double ry =  Math.Sin(theta) * 0.55;
                        // 初速度:切向为主, 略加径向向外(模拟盘面风)
                        double tangentRatio = 1.15;                     // 切向 1.15 + 径向 0.30
                        double radialMag = 0.30;
                        double vx = tx * tangentRatio + rx * radialMag;
                        double vy = ty * tangentRatio + ry * radialMag;
                        // 速度模(像素/秒):远端粒子稍快,模拟"内边缘黏滞多,外缘风更猛"
                        double speed = 360 + inGroup * 40;
                        _burstDirX[i] = vx; _burstDirY[i] = vy;
                        _burstSpeed[i] = speed;
                        _burstTangentRatio[i] = tangentRatio;
                        // 起始半径:在视界外缘
                        _burstBaseR[i] = EventW * 0.55;       // 视界外缘
                        // 开普勒相关字段:不参与更新,但保持默认值
                        _burstSemiMajor[i] = 0; _burstEccentricity[i] = 0;
                        _burstPhase0[i] = 0; _burstOmega0[i] = 0;
                    }

                    // 头部颜色 / 拖尾
                    var head = _burstPool[i];
                    head.Width = head.Height = _burstBaseSize[i];
                    head.Fill = Freeze(new SolidColorBrush(_warm
                        ? Color.FromRgb(0xFF, 0xD8, 0x88)
                        : Color.FromRgb(0xCC, 0xEC, 0xFF)));
                    head.Opacity = 0.0;

                    var trail = _burstTrails[i];
                    var pts = trail.Points;
                    for (int k = 0; k < TrailLen; k++) pts[k] = new Point(0, 0);
                    trail.Opacity = 0.0;
                    _burstTrailHead[i] = 0;
                    spawned++;
                }
            }

            public void Update(double delta)
            {
                _time += delta;
                if (_pulse > 0) _pulse = Math.Max(0, _pulse - _pulseDecay * delta);
                double coreGlowOpacity   = 0.55 + _pulse * 0.40;
                double photonRingOpacity = 0.82 + _pulse * 0.18;
                double eventGlow         = 0.85 + _pulse * 0.20;
                double diskOpacity       = 0.32 + _pulse * 0.18;
                double jetOpacity        = 0.14 + _pulse * 0.50;     // 喷流常驻 alpha 略降

                double cx = _canvas.ActualWidth  > 0 ? _canvas.ActualWidth  / 2 : 640;
                double cy = _canvas.ActualHeight > 0 ? _canvas.ActualHeight / 2 : 370;

                // 1) 吸积盘柔光环(无旋转,慢呼吸)
                double breath = 1.0 + Math.Sin(_time * 0.6) * 0.025;
                LayoutCentered(_diskHalo, cx, cy, breath * (1.0 + _pulse * 0.10));
                _diskHalo.Opacity = diskOpacity;

                // 2) 中心组件居中,吸入/吐出时辉光短暂放大
                double scale = 1.0 + _pulse * 0.10;
                LayoutCentered(_photonRing,   cx, cy, scale);
                LayoutCentered(_eventHorizon, cx, cy, scale);
                _photonRing.Opacity   = Math.Min(1.0, photonRingOpacity);
                _eventHorizon.Opacity = eventGlow;
                LayoutCentered(_coreGlow, cx, cy, 1.0 + _pulse * 0.12);
                _coreGlow.Opacity = coreGlowOpacity;

                // 3) 双极喷流 — 关键改动:根部已收成 0 + 渐变根部 alpha=0,不再形成竖条
                _jetTop.Opacity    = jetOpacity;
                _jetBottom.Opacity = jetOpacity;
                Canvas.SetLeft(_jetTop,    cx - _jetTop.Width    / 2);
                Canvas.SetTop(_jetTop,     cy - _jetTop.Height);
                Canvas.SetLeft(_jetBottom, cx - _jetBottom.Width / 2);
                Canvas.SetTop(_jetBottom,  cy);

                // 3.5) 中心涟漪 — 吸/吐瞬间从视界外缘向外扩散,比直接抬升 opacity 柔和
                if (_shockwaveAge < _shockwaveLife)
                {
                    _shockwaveAge += delta;
                    double sw = Math.Clamp(_shockwaveAge / _shockwaveLife, 0, 1);
                    // 半径从 1.0 扩散到 1.85, ease-out
                    double swScale = 1.0 + (1 - Math.Pow(1 - sw, 2)) * 0.85;
                    LayoutCentered(_shockwave, cx, cy, swScale);
                    // alpha 从 0.55 渐衰到 0
                    _shockwave.Opacity = (1.0 - sw) * 0.55;
                }
                else
                {
                    _shockwave.Opacity = 0;
                }

                // 4) 稳定吸积粒子(公转 + 极慢向心 + Doppler 增亮,半径 1.5x)
                for (int i = 0; i < OrbitPoolSize; i++)
                {
                    _orbitAngle[i] += _orbitSpeed[i] * delta;
                    _orbitRadius[i] -= delta * (4 + i % 5) * 0.7;
                    if (_orbitRadius[i] < OrbitResetR)
                    {
                        _orbitRadius[i] = OrbitROuter - 30 + (i % 7) * 6;
                    }
                    double r = _orbitRadius[i];
                    double x = Math.Cos(_orbitAngle[i]) * r;
                    double y = Math.Sin(_orbitAngle[i]) * r * 0.52;
                    double dop = 0.5 + 0.5 * Math.Cos(_orbitAngle[i]);
                    double a = _orbitBaseAlpha[i] * (0.45 + dop * 0.65);
                    var dot = _orbitPool[i];
                    dot.Opacity = a;
                    Canvas.SetLeft(dot, cx + x - dot.Width / 2);
                    Canvas.SetTop(dot,  cy + y - dot.Height / 2);
                }

                // 5) 喷发粒子
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
                        // 吸入:开普勒式椭圆轨道 — 用真椭圆极坐标
                        // 极径 r(θ) = a*(1-e²) / (1 + e*cos(θ - θ_peri))
                        // 这里 θ 是"从远心点算起"的相位, 实际演化用"角速度按 a 衰减"
                        // 简化模型: a(t) 随时间从 a0 缓慢收缩到 0(被视界吞没),
                        //          角速度按开普勒 ω = ω₀ * (a₀/a)^(3/2) 增大
                        double a0 = _burstSemiMajor[i];
                        // 轨道半长轴随时间收缩:ease-in(向心加速度递增)
                        double at = a0 * (1.0 - t * t * 0.98);
                        if (at < 8) at = 8;
                        double e = _burstEccentricity[i];
                        // 当前累积相位
                        // 用梯形积分:近似每帧相位增 = ω0*(a0/at)^1.5 * delta
                        // 简化:用闭式 phase = θ₀ + 2π * (1 - (at/a0)^(-1/2)) 的近似
                        // 更简单:phase 累加, 但每帧按 ω 变化
                        _burstPhase0[i] += _burstOmega0[i] * Math.Pow(a0 / at, 1.5) * delta;
                        double theta = _burstPhase0[i];
                        // 极径(从焦点)
                        double r = at * (1.0 - e * e) / (1.0 + e * Math.Cos(theta));
                        // 椭圆压扁 0.55(盘面倾角)
                        bx = Math.Cos(theta) * r;
                        by = Math.Sin(theta) * r * 0.55;
                    }
                    else
                    {
                        // 吐出:从视界切向抛出 → 开普勒式减速(切向速度按 r^(-0.5) 衰减, 像真引力)
                        double r = _burstBaseR[i];
                        // 当前 r: 起点 r0, 速度方向已定, 距离原点距离 r 随时间累积
                        // 用线性近似: r(t) = r0 + |v| * ease-out(t) * 0.5 * life
                        double ease = 1.0 - Math.Pow(1.0 - t, 3.0);   // ease-out cubic
                        double travel = _burstSpeed[i] * ease * _burstLife[i] * 0.55;
                        bx = _burstDirX[i] * travel;
                        by = _burstDirY[i] * travel;
                    }

                    // 头部透明度 + 尺寸
                    double headOpacity;
                    if (_burstIsInward[i])
                    {
                        // 吸入:0~0.25 渐入(被引力捕获), 0.25~1.0 保持亮, 临近结束 0.85~1.0 渐出
                        // 越靠近视界越亮(多普勒蓝移)
                        if (t < 0.25) headOpacity = t / 0.25;
                        else if (t < 0.85) headOpacity = 1.0;
                        else headOpacity = Math.Max(0, 1.0 - (t - 0.85) / 0.15);
                        headOpacity = Math.Min(1.0, headOpacity * (0.55 + 0.45 * t));
                        // 头部放大:越近越大
                        double s = _burstBaseSize[i] * (1.0 + t * 1.0);
                        _burstPool[i].Width = _burstPool[i].Height = s;
                    }
                    else
                    {
                        // 吐出:0~0.20 渐入(中心亮闪), 0.20~0.80 保持亮, 0.80~1.0 渐出
                        if (t < 0.20) headOpacity = t / 0.20;
                        else if (t < 0.80) headOpacity = 1.0;
                        else headOpacity = Math.Max(0, 1.0 - (t - 0.80) / 0.20);
                    }

                    // 拖尾环形缓冲
                    int head = _burstTrailHead[i];
                    int baseIdx = i * TrailLen;
                    _burstTrailX[baseIdx + head] = bx;
                    _burstTrailY[baseIdx + head] = by;
                    _burstTrailHead[i] = (head + 1) % TrailLen;

                    // Polyline 写入:从最旧到最新
                    var pts = _burstTrails[i].Points;
                    for (int k = 0; k < TrailLen; k++)
                    {
                        int srcIdx = (head + 1 + k) % TrailLen;
                        double px = _burstTrailX[baseIdx + srcIdx];
                        double py = _burstTrailY[baseIdx + srcIdx];
                        pts[k] = new Point(cx + px, cy + py);
                    }
                    // 拖尾 alpha:吸入的拖尾在末尾更亮(被拉长), 吐出的拖尾在头部亮(被甩出)
                    // 简单做法:头尾一起渐变
                    double trailOpacity = headOpacity * 0.75;
                    _burstTrails[i].Opacity = trailOpacity;

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
