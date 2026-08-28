using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
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
                MessageBox.Show("数据库初始化失败：" + ex.Message,
                    "记忆黑洞", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            Loaded += (_, _) =>
            {
                // 从程序集自动读取版本号
                var ver = Assembly.GetExecutingAssembly().GetName().Version;
                if (ver != null)
                    VersionText.Text = $"v{ver.Major}.{ver.Minor}.{ver.Build}";
                RefreshSearchResults();
                CompositionTarget.Rendering += OnRendering;
            };
            Closed += (_, _) => CompositionTarget.Rendering -= OnRendering;
        }

        private void WindowFrame_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (WindowFrame.ActualWidth > 0 && WindowFrame.ActualHeight > 0)
                WindowFrame.Clip = new RectangleGeometry(
                    new Rect(0, 0, WindowFrame.ActualWidth, WindowFrame.ActualHeight), 18, 18);
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

            // v1.0.1: 用 StoreMedia 流式复制文件到 media 目录（File.Copy，不占内存），
            //         不再读取 FileData BLOB。单文件 >= 100MB 走原始路径。
            for (int i = 0; i < dialog.FilePaths.Count; i++)
            {
                string? storedPath = _service.StoreMedia(dialog.FilePaths[i]);
                string? note = storedPath == null ? "文件超过 100 MB，仅保存原始路径" : null;

                _service.Add(new MemoryItem
                {
                    Type = dialog.SelectedType,
                    Title = dialog.OriginalFileNames[i],
                    Content = dialog.OriginalFileNames[i],
                    FilePath = storedPath ?? dialog.FilePaths[i],
                    FileData = null, // v1.0.1: 不再使用 BLOB 存储
                    Note = note,
                    Tags = dialog.Tags,
                    OriginalFileName = dialog.OriginalFileNames[i],
                    FileSizeBytes = dialog.FileSizes[i]
                });
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
                var preview = new PreviewMemoryDialog(item) { Owner = this };
                preview.ShowDialog();
                if (preview.EditRequested)
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
                var results = _service.Search(keyword);
                ResultsList.ItemsSource = results;
                bool hasKeyword = !string.IsNullOrWhiteSpace(keyword);
                ResultsList.Visibility = hasKeyword && results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                SearchStatus.Text = !hasKeyword
                    ? "请输入关键词开始搜索"
                    : results.Count == 0 ? "没有找到这段记忆" : $"黑洞吐出了 {results.Count} 条记忆";

                if (results.Count > 0)
                {
                    _backSpace.PlayOutward();
                    ResultsList.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(420)));
                }
            }
            catch (Exception ex)
            {
                SearchStatus.Text = "搜索暂时不可用：" + ex.Message;
                ResultsList.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>现代化 2.5D 黑洞视觉：引力透镜式椭圆吸积盘、热光环和粒子轨道。</summary>
        private sealed class SpaceCore
        {
            private sealed class BurstParticle
            {
                public FrameworkElement Shape { get; }
                public double Angle { get; }
                public double StartRadius { get; }
                public double EndRadius { get; }
                public double Age { get; set; }
                public BurstParticle(FrameworkElement shape, double angle, double startRadius, double endRadius)
                {
                    Shape = shape; Angle = angle; StartRadius = startRadius; EndRadius = endRadius;
                }
            }

            private readonly Canvas _canvas;
            private readonly List<(Ellipse Ring, RotateTransform Rotate, double Speed)> _rings = new();
            private readonly List<BurstParticle> _bursts = new();
            private readonly bool _warm;
            private Ellipse _core = null!;
            private Ellipse _coreGlow = null!;
            private double _time;

            public SpaceCore(Canvas canvas, bool warm)
            {
                _canvas = canvas; _warm = warm; Build();
            }

            private void Build()
            {
                AddRing(680, 158, -8, _warm ? "#FFD18A" : "#D8E7FF", 0.18, 7);
                AddRing(610, 126, 16, _warm ? "#FF8A3D" : "#A8C7FF", -0.34, 10);
                AddRing(535, 96, -24, _warm ? "#FFB52E" : "#E7F0FF", 0.52, 6);
                AddRing(455, 70, 36, _warm ? "#FFF4D0" : "#B9D7FF", -0.78, 4);

                _coreGlow = new Ellipse
                {
                    Width = 390, Height = 300,
                    Fill = new RadialGradientBrush(Color.FromArgb(145, _warm ? (byte)255 : (byte)75, _warm ? (byte)74 : (byte)120, 255), Color.FromArgb(0, 0, 0, 0)),
                    Opacity = 0.42, IsHitTestVisible = false
                };
                _canvas.Children.Add(_coreGlow);

                _core = new Ellipse
                {
                    Width = 220, Height = 210,
                    Fill = new RadialGradientBrush(Color.FromRgb(0, 0, 1), Color.FromRgb(3, 5, 17)),
                    Stroke = new SolidColorBrush(Color.FromArgb(210, 8, 10, 24)),
                    StrokeThickness = 5, IsHitTestVisible = false
                };
                _canvas.Children.Add(_core);

                // 不显示持续环绕的彩点；黑洞只保留吸积盘和交互星星特效。
            }

            private void AddRing(double width, double height, double rotation, string color, double speed, double thickness)
            {
                var ring = new Ellipse
                {
                    Width = width, Height = height,
                    Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)),
                    StrokeThickness = thickness, Opacity = 0.38, IsHitTestVisible = false,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = new RotateTransform(rotation)
                };
                ring.Effect = new DropShadowEffect { Color = ((SolidColorBrush)ring.Stroke).Color, BlurRadius = 26, ShadowDepth = 0, Opacity = 0.72 };
                _canvas.Children.Add(ring);
                _rings.Add((ring, (RotateTransform)ring.RenderTransform, speed));
            }

            public void PlayInward() => StartBurst(true);
            public void PlayOutward() => StartBurst(false);

            private void StartBurst(bool inward)
            {
                var color = _warm ? Color.FromRgb(0xFF, 0xD4, 0x72) : Color.FromRgb(0xB8, 0xE8, 0xFF);
                var star = new TextBlock
                {
                    Text = "✦",
                    FontSize = 42,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(color),
                    Width = 58,
                    Height = 58,
                    TextAlignment = TextAlignment.Center,
                    IsHitTestVisible = false,
                    Effect = new DropShadowEffect
                    { Color = color, BlurRadius = 28, ShadowDepth = 0, Opacity = 1 }
                };
                _canvas.Children.Add(star);
                _bursts.Add(new BurstParticle(star, inward ? 0.15 : -0.25,
                    inward ? 600 : 34, inward ? 34 : 620));
            }

            public void Update(double delta)
            {
                _time += delta;
                double cx = _canvas.ActualWidth > 0 ? _canvas.ActualWidth / 2 : 640;
                double cy = _canvas.ActualHeight > 0 ? _canvas.ActualHeight / 2 : 370;
                foreach (var item in _rings)
                {
                    item.Rotate.Angle += item.Speed * delta * 30;
                    Canvas.SetLeft(item.Ring, cx - item.Ring.Width / 2);
                    Canvas.SetTop(item.Ring, cy - item.Ring.Height / 2 + Math.Sin(_time * 0.6) * 3);
                }
                Canvas.SetLeft(_coreGlow, cx - _coreGlow.Width / 2);
                Canvas.SetTop(_coreGlow, cy - _coreGlow.Height / 2);
                Canvas.SetLeft(_core, cx - _core.Width / 2);
                Canvas.SetTop(_core, cy - _core.Height / 2);
                for (int i = _bursts.Count - 1; i >= 0; i--)
                {
                    var burst = _bursts[i];
                    burst.Age += delta;
                    double progress = Math.Clamp(burst.Age / 1.5, 0, 1);
                    double eased = progress * progress * (3 - 2 * progress);
                    double radius = burst.StartRadius + (burst.EndRadius - burst.StartRadius) * eased;
                    double x = Math.Cos(burst.Angle + _time * 0.8) * radius;
                    double y = Math.Sin(burst.Angle + _time * 0.8) * radius * 0.52;
                    burst.Shape.Opacity = 1 - progress;
                    Canvas.SetLeft(burst.Shape, cx + x - burst.Shape.Width / 2);
                    Canvas.SetTop(burst.Shape, cy + y - burst.Shape.Height / 2);
                    if (progress >= 1)
                    {
                        _canvas.Children.Remove(burst.Shape);
                        _bursts.RemoveAt(i);
                    }
                }
            }
        }
    }
}
