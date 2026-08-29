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
            private const int TwoPi = Math.PI * 2.0;

            // 整体尺寸缩放系数(v3.0.1):1.0 是基线,放大后 BlurEffect 仍会糊。
            // 用户在第 4 轮反馈"放大 1.5x 后还是很糊",这里固定 1.0,
            // 所有视觉元素按 1:1 像素级清晰渲染。
            private const double SizeScale = 1.0;

            // 事件视界(纯黑实心球 — 不透出任何背景,不加 BlurEffect,不加渐变)
            private const double EventW    = 180;
            private const double EventH    = 180;
            // 中心光晕(暖/冷径向渐变,无模糊)
            private const double HaloW     = 460;
            private const double HaloH     = 460;
            // 光子球(锐利细亮环,无模糊)
            private const double PhotonW   = 220;
            private const double PhotonH   = 220;
            // 单层柔光吸积盘(无旋转,无模糊)
            private const double DiskW     = 600;
            private const double DiskH     = 600;

            // 吸积粒子轨道半径
            private const double OrbitRInner = 90;        // 视界外缘(粒子消失半径)
            private const double OrbitROuter = 290;       // 粒子出生半径
            // 行星(三行星)轨道
            private const double PlanetRInner = 100;
            private const double PlanetROuter = 260;
            // 行星拖尾节点数
            private const int PlanetTrailLen = 36;        // 3 圈 × 12 节点/圈 ≈ 3 圈轨迹

            // 喷发粒子(吸/吐临时) — 池大小
            private const int BurstPoolSize = 18;

            private readonly Canvas _canvas;
            private readonly bool _warm;

            // 中心结构:halo 在最底,事件视界纯黑在上,光子球最上(描边不模糊)
            private Ellipse _halo = null!;
            private Ellipse _disk = null!;
            private Ellipse _eventHorizon = null!;
            private Ellipse _photonRing = null!;
            private Ellipse _shockwave = null!;

            // 吸积粒子池(60 颗,向内螺旋,无模糊)
            private readonly List<Ellipse> _orbitPool = new();
            private readonly double[] _orbitAngle = new double[OrbitPoolSize];
            private readonly double[] _orbitRadius = new double[OrbitPoolSize];
            private readonly double[] _orbitSpeed = new double[OrbitPoolSize];
            private readonly double[] _orbitSize = new double[OrbitPoolSize];
            private readonly double[] _orbitBaseAlpha = new double[OrbitPoolSize];

            // 三行星:每颗一个头部 Ellipse + 一条拖尾 Polyline
            // 行星有"立体感" — 头部用 RadialGradientBrush(中心白→边缘暗)模拟受光面
            private const int PlanetCount = 3;
            private readonly Ellipse[] _planetHeads = new Ellipse[PlanetCount];
            private readonly Polyline[] _planetTrails = new Polyline[PlanetCount];
            // 每颗行星的状态:alive / t / startR / endR / omega0 / phase0 / r0 / sizeBase
            private readonly bool[] _planetAlive = new bool[PlanetCount];
            private readonly double[] _planetT = new double[PlanetCount];
            private readonly double[] _planetLife = new double[PlanetCount];
            private readonly double[] _planetStartR = new double[PlanetCount];
            private readonly double[] _planetEndR = new double[PlanetCount];
            private readonly double[] _planetOmega0 = new double[PlanetCount];
            private readonly double[] _planetPhase0 = new double[PlanetCount];
            private readonly double[] _planetCurrentR = new double[PlanetCount];
            private readonly double[] _planetSize = new double[PlanetCount];
            // 行星拖尾位置(每颗 36 个点)
            private readonly double[,] _planetTrailX = new double[PlanetCount, PlanetTrailLen];
            private readonly double[,] _planetTrailY = new double[PlanetCount, PlanetTrailLen];
            private readonly int[] _planetTrailHead = new int[PlanetCount];

            // 喷发粒子池(只在吸/吐瞬间产生 — 模拟"被吸入消失的碎片"或"被吐出的星尘")
            // 用于增强吸/吐的真实感(三行星主轨迹 + 一些碎片伴随)
            private readonly List<Ellipse> _burstPool = new();
            private readonly List<Polyline> _burstTrails = new();
            private readonly double[] _burstAge = new double[BurstPoolSize];
            private readonly double[] _burstLife = new double[BurstPoolSize];
            private readonly double[] _burstSemiMajor = new double[BurstPoolSize];
            private readonly double[] _burstEccentricity = new double[BurstPoolSize];
            private readonly double[] _burstPhase0 = new double[BurstPoolSize];
            private readonly double[] _burstOmega0 = new double[BurstPoolSize];
            private readonly double[] _burstBaseR = new double[BurstPoolSize];
            private readonly double[] _burstBaseSize = new double[BurstPoolSize];
            private readonly double[] _burstTrailX = new double[BurstPoolSize * PlanetTrailLen];
            private readonly double[] _burstTrailY = new double[BurstPoolSize * PlanetTrailLen];
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
                // 1) 单层吸积盘(无旋转、慢呼吸,无模糊 — 纯渐变提供质感)
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

                // 2) 中心光晕(暖色径向,无 BlurEffect — 渐变就是"软"光晕)
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

                // 3) 事件视界 — 纯黑实心,无模糊,无渐变
                // (问题 5:必须纯黑不透明,绝不能透出背景)
                _eventHorizon = new Ellipse
                {
                    Width = EventW, Height = EventH,
                    IsHitTestVisible = false,
                    Fill = Freeze(new SolidColorBrush(Color.FromRgb(0, 0, 0))),
                };
                _canvas.Children.Add(_eventHorizon);

                // 4) 光子球 — 锐利细亮环(无 BlurEffect,描边清晰)
                _photonRing = new Ellipse
                {
                    Width = PhotonW, Height = PhotonH,
                    Stroke = Freeze(new SolidColorBrush(
                        _warm ? Color.FromArgb(220, 0xFF, 0xC8, 0x78)
                              : Color.FromArgb(220, 0xB0, 0xE0, 0xFF))),
                    StrokeThickness = 2.0,
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
                    StrokeThickness = 2.5,
                    IsHitTestVisible = false,
                    Opacity = 0,
                };
                _canvas.Children.Add(_shockwave);

                // 6) 60 颗稳定吸积粒子 — 从最外圈出发,沿对数螺线向内,达视界后重生
                // 全部无 BlurEffect(清晰亮点),用 Doppler 增亮
                var rng = new Random(_warm ? 17 : 113);
                for (int i = 0; i < OrbitPoolSize; i++)
                {
                    var dot = new Ellipse
                    {
                        IsHitTestVisible = false,
                        // 不加 BlurEffect — 1.0x 下矢量边缘已清晰
                    };
                    _orbitPool.Add(dot);
                    _canvas.Children.Add(dot);
                    _orbitAngle[i]     = rng.NextDouble() * TwoPi;
                    _orbitRadius[i]    = OrbitRInner + rng.NextDouble() * (OrbitROuter - OrbitRInner);
                    _orbitSpeed[i]     = 0.18 + rng.NextDouble() * 0.55;     // 外圈慢
                    _orbitSize[i]      = 1.6 + rng.NextDouble() * 3.0;
                    _orbitBaseAlpha[i] = 0.40 + rng.NextDouble() * 0.45;
                    // 离视界近的偏白热,远的偏冷
                    double t = (_orbitRadius[i] - OrbitRInner) / (OrbitROuter - OrbitRInner);
                    byte r = (byte)(255 * (1 - t * 0.55));
                    byte g = (byte)(_warm ? (200 - t * 60) : (180 + t * 40));
                    byte b = (byte)(_warm ? (90 + t * 60)  : (255 - t * 30));
                    var brush = Freeze(new SolidColorBrush(Color.FromArgb(230, r, g, b)));
                    dot.Fill = brush;
                    dot.Width = dot.Height = _orbitSize[i];
                }

                // 7) 三行星(预创建,初始不可见)
                for (int p = 0; p < PlanetCount; p++)
                {
                    var head = new Ellipse
                    {
                        IsHitTestVisible = false,
                        Opacity = 0,
                    };
                    // 行星立体感:径向渐变(中心白热 → 边缘暗),无 BlurEffect
                    head.Fill = Freeze(new RadialGradientBrush
                    {
                        GradientStops = FreezeStops(new (Color, double)[]
                        {
                            (Color.FromArgb(255, 255, 250, 220), 0.0),
                            (_warm ? Color.FromArgb(255, 0xFF, 0xB0, 0x50)
                                  : Color.FromArgb(255, 0x9C, 0xD8, 0xFF), 0.55),
                            (_warm ? Color.FromArgb(255, 0xC0, 0x40, 0x10)
                                  : Color.FromArgb(255, 0x40, 0x80, 0xC0), 1.0),
                        })
                    });
                    _planetHeads[p] = head;
                    _canvas.Children.Add(head);

                    var trail = new Polyline
                    {
                        IsHitTestVisible = false,
                        Opacity = 0,
                        StrokeThickness = 1.6,
                        StrokeLineJoin = PenLineJoin.Round,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round,
                        Stroke = Freeze(new SolidColorBrush(
                            _warm ? Color.FromArgb(180, 0xFF, 0xC0, 0x70)
                                  : Color.FromArgb(180, 0xA0, 0xC8, 0xFF))),
                    };
                    // 拖尾自身不加 BlurEffect(清晰描边)— 1.0x 下保持锐利
                    var pts = new PointCollection();
                    for (int k = 0; k < PlanetTrailLen; k++) pts.Add(new Point(0, 0));
                    trail.Points = pts;
                    _planetTrails[p] = trail;
                    _canvas.Children.Add(trail);
                    _planetAlive[p] = false;
                }

                // 8) 喷发粒子池(吸/吐时伴生碎片,增强真实感)
                for (int i = 0; i < BurstPoolSize; i++)
                {
                    var head = new Ellipse { IsHitTestVisible = false, Opacity = 0 };
                    head.Fill = Freeze(new SolidColorBrush(
                        _warm ? Color.FromRgb(0xFF, 0xD8, 0x88)
                              : Color.FromRgb(0xCC, 0xEC, 0xFF)));
                    _burstPool.Add(head);

                    var trail = new Polyline
                    {
                        IsHitTestVisible = false,
                        Opacity = 0,
                        StrokeThickness = 1.2,
                        StrokeLineJoin = PenLineJoin.Round,
                        Stroke = Freeze(new SolidColorBrush(
                            _warm ? Color.FromArgb(200, 0xFF, 0xB0, 0x60)
                                  : Color.FromArgb(200, 0x9C, 0xD8, 0xFF))),
                    };
                    var pts = new PointCollection();
                    for (int k = 0; k < PlanetTrailLen; k++) pts.Add(new Point(0, 0));
                    trail.Points = pts;
                    _burstTrails.Add(trail);

                    _canvas.Children.Add(trail);
                    _canvas.Children.Add(head);
                    _burstAlive[i] = false;
                    _burstTrailHead[i] = 0;
                }
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

            public void PlayInward() => StartPlanetsAndBurst(inward: true);
            public void PlayOutward() => StartPlanetsAndBurst(inward: false);

            /// <summary>
            /// 触发吸/吐动画:三行星(主轨迹)+ 15 颗碎片(伴生)。
            ///
            /// 吸入(三行星,问题 6 思路):
            ///   - 起点:最外圈 PlanetROuter, 终:视界外缘 PlanetRInner
            ///   - 相位累加 6π(3 圈), 角速度按 r^(-1.5) 加速(引力感)
            ///   - 半径 ease-in 向心收缩(越近越快)
            ///   - 3 颗错开 120° 起始相位
            ///   - t=0 不可见, t=0.1~0.2 渐入, t=0.95 后渐出(在视界边"消失")
            ///
            /// 吐出(三行星,反向):
            ///   - 起点:视界外缘 PlanetRInner, 终:最外圈 PlanetROuter
            ///   - 同样的 6π(3 圈),但半径 ease-out 向外扩张(初速慢,后段加速离场)
            ///   - t=0.85~1.0 渐出(远离后自然消散)
            /// </summary>
            private void StartPlanetsAndBurst(bool inward)
            {
                _pulse = 1.0;
                _pulseDecay = 1.4;
                _shockwaveAge = 0;
                _shockwaveLife = 1.0;

                // 三行星参数
                for (int p = 0; p < PlanetCount; p++)
                {
                    _planetAlive[p] = true;
                    _planetT[p] = 0;
                    _planetLife[p] = 2.6;       // 2.6 秒走完 3 圈
                    // 半径:吸 vs 吐
                    if (inward)
                    {
                        _planetStartR[p] = PlanetROuter;
                        _planetEndR[p]   = PlanetRInner;
                    }
                    else
                    {
                        _planetStartR[p] = PlanetRInner;
                        _planetEndR[p]   = PlanetROuter;
                    }
                    // 起始相位错开 120°
                    _planetPhase0[p] = (p * TwoPi / PlanetCount) + (_warm ? 0.0 : Math.PI);
                    // 角速度按起始 r 反 1.5 次方 — 外圈慢、内圈快
                    // 选 k 使 r=PlanetROuter=260 时 ω₀ ≈ 0.85 rad/s(约 7.4 秒一圈)
                    // 整体走 3 圈 = 6π rad 用 2.6 秒 → 平均 ω ≈ 7.25 rad/s
                    // 但因 r 在 t 中变化, 我们让 ω₀ = 7.25 rad/s 在外圈
                    _planetOmega0[p] = 7.25;
                    _planetCurrentR[p] = _planetStartR[p];
                    // 行星大小:内圈稍大(近看更大,符合透视)
                    _planetSize[p] = 6 + p * 1.0;  // 6/7/8 px
                    // 拖尾清零
                    for (int k = 0; k < PlanetTrailLen; k++)
                    {
                        _planetTrailX[p, k] = 0;
                        _planetTrailY[p, k] = 0;
                    }
                    _planetTrailHead[p] = 0;
                }

                // 15 颗伴生碎片(增强真实感)
                int burstTarget = 15;
                int spawned = 0;
                for (int i = 0; i < BurstPoolSize && spawned < burstTarget; i++)
                {
                    if (_burstAlive[i]) continue;
                    _burstAlive[i] = true;
                    _burstIsInward[i] = inward;
                    _burstAge[i] = 0;
                    _burstLife[i] = 1.6 + (spawned * 0.06);
                    _burstBaseSize[i] = 2.0 + (spawned % 3) * 0.8;
                    if (inward)
                    {
                        // 碎片开普勒式椭圆轨道
                        double a = PlanetROuter * (0.9 + (spawned % 4) * 0.08);
                        double e = 0.30 + (spawned % 3) * 0.12;
                        double k = 4.5 * Math.Pow(a, 1.5);
                        double omega0 = k / Math.Pow(a, 1.5);
                        _burstSemiMajor[i]    = a;
                        _burstEccentricity[i] = e;
                        _burstPhase0[i]       = (spawned / (double)burstTarget) * TwoPi + (i % 3) * 0.4;
                        _burstOmega0[i]       = omega0;
                        _burstBaseR[i]        = a * (1 + e);
                    }
                    else
                    {
                        // 吐出碎片:从视界切向抛出,ease-out 远离
                        double a = (spawned / (double)burstTarget) * TwoPi;
                        double tx = -Math.Sin(a);
                        double ty =  Math.Cos(a) * 0.55;
                        double speed = 280 + (spawned % 3) * 50;
                        _burstSemiMajor[i]    = 0; _burstEccentricity[i] = 0;
                        _burstPhase0[i]       = 0; _burstOmega0[i]       = 0;
                        _burstBaseR[i]        = PlanetRInner;
                        // 用 _burstPhase0[i] 当 vx,_burstOmega0[i] 当 vy 的复用空间
                        // 实际上 vx/vy 临时存:用 _burstBaseSize[i] 不够 — 用 _burstSemiMajor/_burstEccentricity
                        _burstSemiMajor[i]    = tx * speed;
                        _burstEccentricity[i] = ty * speed;
                    }
                    var head = _burstPool[i];
                    head.Width = head.Height = _burstBaseSize[i];
                    head.Opacity = 0;
                    var trail = _burstTrails[i];
                    var pts = trail.Points;
                    for (int k = 0; k < PlanetTrailLen; k++) pts[k] = new Point(0, 0);
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

                // 2) 中心光晕(暖/冷渐变,无模糊)
                LayoutCentered(_halo, cx, cy, 1.0 + _pulse * 0.10);
                _halo.Opacity = coreGlowOpacity;

                // 3) 事件视界 + 光子球(吸/吐时抬升)
                double scale = 1.0 + _pulse * 0.08;
                LayoutCentered(_photonRing,   cx, cy, scale);
                LayoutCentered(_eventHorizon, cx, cy, scale);
                _photonRing.Opacity   = Math.Min(1.0, photonRingOpacity);
                _eventHorizon.Opacity = 1.0;     // 永远不透明,纯黑

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

                // 5) 60 颗稳定吸积粒子 — 从最外圈出发,向内螺旋,达视界后重生
                // (问题 4 整改:禁止向外漂、禁止穿过视界、禁止匀速转圈)
                // 用"半径随时间线性收缩 + 角速度按 r^(-1.5) 开普勒式"
                for (int i = 0; i < OrbitPoolSize; i++)
                {
                    // 角速度:外圈慢,内圈快
                    double omega = _orbitSpeed[i] * Math.Pow(OrbitROuter / _orbitRadius[i], 1.5);
                    _orbitAngle[i] += omega * delta;
                    // 半径持续向内收缩(被引力吸入,不复反弹)
                    _orbitRadius[i] -= delta * (5 + (i % 4) * 1.5);
                    // 到达视界外缘 → 立即从最外圈重生(永远不穿越)
                    if (_orbitRadius[i] <= OrbitRInner)
                    {
                        _orbitRadius[i] = OrbitROuter - 4 + (i % 6) * 1.0;
                        _orbitAngle[i]  += (i % 5) * 0.15;  // 重生时相位稍偏移,避免同位
                    }
                    double r = _orbitRadius[i];
                    // 椭圆压扁 0.52 模拟盘面倾角
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

                // 6) 三行星(吸/吐主轨迹)
                for (int p = 0; p < PlanetCount; p++)
                {
                    if (!_planetAlive[p]) continue;
                    _planetT[p] += delta;
                    double t = _planetT[p] / _planetLife[p];
                    if (t >= 1.0)
                    {
                        _planetAlive[p] = false;
                        _planetHeads[p].Opacity = 0;
                        _planetTrails[p].Opacity = 0;
                        continue;
                    }
                    // 半径:吸 = start→end ease-in(t²);吐 = start→end ease-out(1-(1-t)²)
                    double r;
                    if (_planetEndR[p] < _planetStartR[p])
                    {
                        // 吸入:ease-in(向心加速)
                        double e = t * t;
                        r = _planetStartR[p] + (_planetEndR[p] - _planetStartR[p]) * e;
                    }
                    else
                    {
                        // 吐出:ease-out(初速慢,后段加速离场)
                        double e = 1.0 - (1.0 - t) * (1.0 - t);
                        r = _planetStartR[p] + (_planetEndR[p] - _planetStartR[p]) * e;
                    }
                    _planetCurrentR[p] = r;
                    // 角速度按 r^(-1.5) 加速(引力感)
                    double omega = _planetOmega0[p] * Math.Pow(_planetStartR[p] / Math.Max(r, 30), 1.5);
                    _planetPhase0[p] += omega * delta;
                    double a = _planetPhase0[p];
                    // 椭圆压扁 0.52 模拟盘面
                    double bx = Math.Cos(a) * r;
                    double by = Math.Sin(a) * r * 0.52;

                    // 头部透明度:0~0.1 渐入, 0.90~1.0 渐出
                    double headOpacity;
                    if (t < 0.10) headOpacity = t / 0.10;
                    else if (t < 0.90) headOpacity = 1.0;
                    else headOpacity = Math.Max(0, 1.0 - (t - 0.90) / 0.10);

                    // 头部大小:近看稍大(随 r 减小线性增大)
                    double sizeMul = 1.0 + (PlanetROuter - r) / PlanetROuter * 0.6;
                    _planetHeads[p].Width = _planetHeads[p].Height = _planetSize[p] * sizeMul;

                    // 拖尾环形缓冲
                    int head = _planetTrailHead[p];
                    _planetTrailX[p, head] = bx;
                    _planetTrailY[p, head] = by;
                    _planetTrailHead[p] = (head + 1) % PlanetTrailLen;
                    // 写入 Polyline
                    var pts = _planetTrails[p].Points;
                    for (int k = 0; k < PlanetTrailLen; k++)
                    {
                        int srcIdx = (head + 1 + k) % PlanetTrailLen;
                        pts[k] = new Point(
                            cx + _planetTrailX[p, srcIdx],
                            cy + _planetTrailY[p, srcIdx]);
                    }
                    _planetTrails[p].Opacity = headOpacity * 0.85;
                    Canvas.SetLeft(_planetHeads[p], cx + bx - _planetHeads[p].Width / 2);
                    Canvas.SetTop(_planetHeads[p],  cy + by - _planetHeads[p].Height / 2);
                    _planetHeads[p].Opacity = headOpacity;
                }

                // 7) 15 颗伴生碎片(吸:开普勒轨道 / 吐:切向 ease-out)
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
                        double a0 = _burstSemiMajor[i];
                        double at = a0 * (1.0 - t * t * 0.98);
                        if (at < 8) at = 8;
                        double e = _burstEccentricity[i];
                        _burstPhase0[i] += _burstOmega0[i] * Math.Pow(a0 / at, 1.5) * delta;
                        double theta = _burstPhase0[i];
                        double r = at * (1.0 - e * e) / (1.0 + e * Math.Cos(theta));
                        bx = Math.Cos(theta) * r;
                        by = Math.Sin(theta) * r * 0.55;
                    }
                    else
                    {
                        // 吐出碎片:用 vx=SemiMajor, vy=Eccentricity
                        double ease = 1.0 - Math.Pow(1.0 - t, 3.0);
                        double travel = ease * _burstLife[i] * 0.55;
                        bx = _burstSemiMajor[i] * travel;
                        by = _burstEccentricity[i] * travel;
                    }
                    // 头/尾
                    double headOpacity = t < 0.20 ? (t / 0.20) : Math.Max(0, 1.0 - (t - 0.20) / 0.80);
                    // 环形拖尾
                    int head = _burstTrailHead[i];
                    int baseIdx = i * PlanetTrailLen;
                    _burstTrailX[baseIdx + head] = bx;
                    _burstTrailY[baseIdx + head] = by;
                    _burstTrailHead[i] = (head + 1) % PlanetTrailLen;
                    var pts = _burstTrails[i].Points;
                    for (int k = 0; k < PlanetTrailLen; k++)
                    {
                        int srcIdx = (head + 1 + k) % PlanetTrailLen;
                        pts[k] = new Point(cx + _burstTrailX[baseIdx + srcIdx],
                                          cy + _burstTrailY[baseIdx + srcIdx]);
                    }
                    _burstTrails[i].Opacity = headOpacity * 0.70;
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
