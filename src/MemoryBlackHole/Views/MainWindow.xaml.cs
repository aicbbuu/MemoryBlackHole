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

            private readonly Canvas _canvas;
            private readonly bool _warm;
            private readonly List<Ellipse> _rings = new();
            private readonly List<RotateTransform> _ringRotations = new();
            private readonly List<double> _ringSpeeds = new();
            private Ellipse _photonRing = null!;
            private Ellipse _eventHorizon = null!;
            private Ellipse _coreGlow = null!;
            private Path _jetTop = null!;
            private Path _jetBottom = null!;

            private readonly List<Ellipse> _orbitPool = new();
            private readonly double[] _orbitAngle = new double[OrbitPoolSize];
            private readonly double[] _orbitRadius = new double[OrbitPoolSize];
            private readonly double[] _orbitSpeed = new double[OrbitPoolSize];
            private readonly double[] _orbitSize = new double[OrbitPoolSize];
            private readonly double[] _orbitBaseAlpha = new double[OrbitPoolSize];

            private readonly List<Ellipse> _burstPool = new();
            private readonly double[] _burstAge = new double[BurstPoolSize];
            private readonly double[] _burstLife = new double[BurstPoolSize];
            private readonly double[] _burstVX = new double[BurstPoolSize];
            private readonly double[] _burstVY = new double[BurstPoolSize];
            private readonly double[] _burstStartX = new double[BurstPoolSize];
            private readonly double[] _burstStartY = new double[BurstPoolSize];
            private readonly double[] _burstBaseSize = new double[BurstPoolSize];
            private readonly bool[] _burstAlive = new bool[BurstPoolSize];

            private double _time;
            private double _pulse;
            private double _pulseDecay;

            public SpaceCore(Canvas canvas, bool warm)
            {
                _canvas = canvas; _warm = warm; Build();
            }

            private void Build()
            {
                // 1) 中心辉光(大软光晕,内核外侧)
                _coreGlow = new Ellipse
                {
                    Width = 480, Height = 360,
                    IsHitTestVisible = false,
                    Opacity = 0.55,
                };
                _coreGlow.Fill = MakeRadialGlow(_warm
                    ? Color.FromArgb(220, 0xFF, 0x86, 0x2E)
                    : Color.FromArgb(210, 0x6B, 0xC8, 0xFF),
                    Color.FromArgb(0, 0, 0, 0));
                _coreGlow.Effect = new BlurEffect { Radius = 38, KernelType = KernelType.Gaussian };
                _canvas.Children.Add(_coreGlow);

                // 2) 事件视界(中心深黑球,边缘透出 Doppler 增亮)
                _eventHorizon = new Ellipse { Width = 200, Height = 190, IsHitTestVisible = false };
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

                // 3) 光子球(最内圈细亮环,模拟弯曲光强反射)
                _photonRing = new Ellipse
                {
                    Width = 244, Height = 232,
                    Stroke = Freeze(new SolidColorBrush(
                        _warm ? Color.FromArgb(210, 0xFF, 0xC2, 0x70)
                              : Color.FromArgb(210, 0xB8, 0xE8, 0xFF))),
                    StrokeThickness = 1.6,
                    IsHitTestVisible = false,
                    Opacity = 0.75,
                };
                _canvas.Children.Add(_photonRing);

                // 4) 4 层吸积盘(独立旋转,椭圆压扁模拟相对论倾角)
                AddDiskRing(680, 168,   6, _warm ? "#FFD18A" : "#D8E7FF",  0.10, 6);
                AddDiskRing(560, 124,  12, _warm ? "#FF8A3D" : "#A8C7FF", -0.22, 7);
                AddDiskRing(440,  86, -18, _warm ? "#FFB52E" : "#E7F0FF",  0.36, 5);
                AddDiskRing(330,  58,  24, _warm ? "#FFF4D0" : "#B9D7FF", -0.55, 3);

                // 5) 双极喷流(常驻但低不透明度,吸/吐时抬升)
                _jetTop = MakeJetPath(40, -200, _warm);
                _jetBottom = MakeJetPath(40, 200, _warm);
                _canvas.Children.Add(_jetBottom);
                _canvas.Children.Add(_jetTop);

                // 6) 稳定吸积粒子池(全部预创建,运行期零分配)
                var rng = new Random(_warm ? 17 : 113);
                for (int i = 0; i < OrbitPoolSize; i++)
                {
                    var dot = new Ellipse
                    {
                        IsHitTestVisible = false,
                        Effect = new BlurEffect { Radius = 4, KernelType = KernelType.Gaussian },
                    };
                    _orbitPool.Add(dot);
                    _canvas.Children.Add(dot);
                    _orbitAngle[i]     = rng.NextDouble() * TwoPi;
                    _orbitRadius[i]    = 95 + rng.NextDouble() * 235;
                    _orbitSpeed[i]     = 0.18 + rng.NextDouble() * 0.55;
                    _orbitSize[i]      = 1.6 + rng.NextDouble() * 3.4;
                    _orbitBaseAlpha[i] = 0.35 + rng.NextDouble() * 0.55;
                    // 离视界近的偏白热,远的偏冷
                    double t = (_orbitRadius[i] - 95.0) / 235.0;
                    byte r = (byte)(255 * (1 - t * 0.55));
                    byte g = (byte)(_warm ? (200 - t * 60) : (180 + t * 40));
                    byte b = (byte)(_warm ? (90 + t * 60)  : (255 - t * 30));
                    var brush = Freeze(new SolidColorBrush(Color.FromArgb(220, r, g, b)));
                    dot.Fill = brush;
                    dot.Width = dot.Height = _orbitSize[i];
                }

                // 7) 喷发池(预创建,初始不可见)
                for (int i = 0; i < BurstPoolSize; i++)
                {
                    var p = new Ellipse { IsHitTestVisible = false, Opacity = 0 };
                    p.Effect = new BlurEffect { Radius = 6, KernelType = KernelType.Gaussian };
                    _burstPool.Add(p);
                    _canvas.Children.Add(p);
                    _burstAlive[i] = false;
                }
            }

            private void AddDiskRing(double width, double height, double rotation, string color, double speed, double thickness)
            {
                var brush = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)));
                var ring = new Ellipse
                {
                    Width = width, Height = height,
                    Stroke = brush,
                    StrokeThickness = thickness,
                    IsHitTestVisible = false,
                    Opacity = 0.42,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = new RotateTransform(rotation),
                };
                ring.Effect = new DropShadowEffect
                {
                    Color = ((SolidColorBrush)ring.Stroke).Color,
                    BlurRadius = 28,
                    ShadowDepth = 0,
                    Opacity = 0.55,
                };
                _canvas.Children.Add(ring);
                _rings.Add(ring);
                _ringRotations.Add((RotateTransform)ring.RenderTransform);
                _ringSpeeds.Add(speed);
            }

            private static Path MakeJetPath(double halfWidth, double tipY, bool warm)
            {
                double h = Math.Abs(tipY) * 2;
                var pg = new Path
                {
                    Width = halfWidth * 2,
                    Height = h,
                    IsHitTestVisible = false,
                    Stretch = Stretch.None,
                    Data = new RectangleGeometry(new Rect(0, 0, halfWidth * 2, h)),
                };
                var gradient = new LinearGradientBrush
                {
                    StartPoint = new Point(0.5, tipY < 0 ? 1.0 : 0.0),
                    EndPoint   = new Point(0.5, tipY < 0 ? 0.0 : 1.0),
                };
                Color hot = warm ? Color.FromRgb(0xFF, 0x9A, 0x40)
                                 : Color.FromRgb(0x7A, 0xC8, 0xFF);
                gradient.GradientStops.Add(new GradientStop(Color.FromArgb(180, hot.R, hot.G, hot.B), 0.0));
                gradient.GradientStops.Add(new GradientStop(Color.FromArgb(0,   hot.R, hot.G, hot.B), 1.0));
                Freeze(gradient);
                pg.Fill = gradient;
                return pg;
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

            private void StartBurst(bool inward)
            {
                // 抬升中心视界亮度
                _pulse = 1.0;
                _pulseDecay = 1.4;

                int spawned = 0;
                for (int i = 0; i < BurstPoolSize && spawned < 12; i++)
                {
                    if (_burstAlive[i]) continue;
                    _burstAlive[i] = true;
                    _burstAge[i] = 0;
                    _burstLife[i] = 0.9 + (spawned * 0.04);
                    if (inward)
                    {
                        // 吸入:从外向中心螺旋坍缩
                        double a = (spawned / 12.0) * TwoPi;
                        double r = 320 + (spawned % 3) * 18;
                        _burstStartX[i] = Math.Cos(a) * r;
                        _burstStartY[i] = Math.Sin(a) * r * 0.55;
                        _burstVX[i] = -Math.Cos(a) * 380;
                        _burstVY[i] = -Math.Sin(a) * 380 * 0.55;
                    }
                    else
                    {
                        // 吐出:从中心沿双极喷流方向爆发
                        bool up = (spawned % 2) == 0;
                        double spread = ((spawned % 6) / 6.0 - 0.5) * 0.35;
                        _burstStartX[i] = spread * 40;
                        _burstStartY[i] = up ? -8 : 8;
                        _burstVX[i] = spread * 80;
                        _burstVY[i] = up ? -560 : 560;
                    }
                    _burstBaseSize[i] = 3.0 + (spawned % 4) * 1.4;
                    var p = _burstPool[i];
                    p.Width = p.Height = _burstBaseSize[i];
                    p.Fill = Freeze(new SolidColorBrush(_warm
                        ? Color.FromRgb(0xFF, 0xC2, 0x70)
                        : Color.FromRgb(0xB8, 0xE8, 0xFF)));
                    p.Opacity = 0.95;
                    spawned++;
                }
            }

            public void Update(double delta)
            {
                _time += delta;
                if (_pulse > 0) _pulse = Math.Max(0, _pulse - _pulseDecay * delta);
                double coreGlowOpacity   = 0.55 + _pulse * 0.40;
                double photonRingOpacity = 0.75 + _pulse * 0.25;
                double eventGlow         = 0.85 + _pulse * 0.20;

                double cx = _canvas.ActualWidth  > 0 ? _canvas.ActualWidth  / 2 : 640;
                double cy = _canvas.ActualHeight > 0 ? _canvas.ActualHeight / 2 : 370;

                // 1) 吸积盘 4 层独立旋转 + 上下轻微正弦漂移
                for (int i = 0; i < _rings.Count; i++)
                {
                    _ringRotations[i].Angle += _ringSpeeds[i] * delta * 50.0;
                    var ring = _rings[i];
                    Canvas.SetLeft(ring, cx - ring.Width / 2);
                    Canvas.SetTop(ring,  cy - ring.Height / 2 + Math.Sin(_time * 0.6 + i) * 2.4);
                }

                // 2) 中心组件居中,吸入/吐出时辉光短暂放大
                double scale = 1.0 + _pulse * 0.10;
                LayoutCentered(_photonRing,   cx, cy, scale);
                LayoutCentered(_eventHorizon, cx, cy, scale);
                _photonRing.Opacity   = Math.Min(1.0, photonRingOpacity);
                _eventHorizon.Opacity = eventGlow;
                LayoutCentered(_coreGlow, cx, cy, 1.0 + _pulse * 0.12);
                _coreGlow.Opacity = coreGlowOpacity;

                // 3) 双极喷流
                double jetOpacity = 0.18 + _pulse * 0.55;
                _jetTop.Opacity    = jetOpacity;
                _jetBottom.Opacity = jetOpacity;
                Canvas.SetLeft(_jetTop,    cx - _jetTop.Width    / 2);
                Canvas.SetTop(_jetTop,     cy - _jetTop.Height);
                Canvas.SetLeft(_jetBottom, cx - _jetBottom.Width / 2);
                Canvas.SetTop(_jetBottom,  cy);

                // 4) 稳定吸积粒子(公转 + 极慢向心 + Doppler 增亮)
                for (int i = 0; i < OrbitPoolSize; i++)
                {
                    _orbitAngle[i] += _orbitSpeed[i] * delta;
                    _orbitRadius[i] -= delta * (4 + i % 5) * 0.7;
                    if (_orbitRadius[i] < 92)
                    {
                        _orbitRadius[i] = 320 + (i % 7) * 6;
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
                        continue;
                    }
                    double ease = 1 - Math.Pow(1 - t, 2);
                    double bx = _burstStartX[i] + _burstVX[i] * ease * (0.55 * _burstLife[i]);
                    double by = _burstStartY[i] + _burstVY[i] * ease * (0.55 * _burstLife[i]);
                    _burstPool[i].Opacity = (1 - t) * 0.95;
                    Canvas.SetLeft(_burstPool[i], cx + bx - _burstPool[i].Width / 2);
                    Canvas.SetTop(_burstPool[i],  cy + by - _burstPool[i].Height / 2);
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
