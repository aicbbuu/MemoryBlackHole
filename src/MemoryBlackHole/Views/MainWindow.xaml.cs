1|using System;
2|using System.Collections.Generic;
3|using System.Diagnostics;
4|using System.IO;
5|using System.Linq;
6|using System.Reflection;
7|using System.Text.Json;
8|using System.Windows;
9|using System.Windows.Controls;
10|using System.Windows.Input;
11|using System.Windows.Media;
12|using System.Windows.Media.Animation;
13|using System.Windows.Media.Effects;
14|using System.Windows.Shapes;
15|using MemoryBlackHole.Models;
16|using MemoryBlackHole.Services;
17|
18|namespace MemoryBlackHole.Views
19|{
20|    public partial class MainWindow : Window
21|    {
22|        private readonly DataService? _service;
23|        private readonly SpaceCore _frontSpace;
24|        private readonly SpaceCore _backSpace;
25|        private bool _flipping;
26|        private DateTime _lastFrame;
27|        private string? _activeTag;
28|        private Color _accentColor = Color.FromRgb(0x6D, 0x5D, 0xF7);
29|
30|        public MainWindow()
31|        {
32|            InitializeComponent();
33|            _frontSpace = new SpaceCore(FrontCanvas, warm: true);
34|            _backSpace = new SpaceCore(BackCanvas, warm: false);
35|            _lastFrame = DateTime.UtcNow;
36|
37|            try { _service = new DataService(); }
38|            catch (Exception ex)
39|            {
40|                _service = null;
41|                MessageBox.Show("数据库初始化失败：" + ex.Message,
42|                    "记忆黑洞", MessageBoxButton.OK, MessageBoxImage.Warning);
43|            }
44|
45|            Loaded += (_, _) =>
46|            {
47|                // 从程序集自动读取版本号
48|                var ver = Assembly.GetExecutingAssembly().GetName().Version;
49|                if (ver != null)
50|                    VersionText.Text = $"v{ver.Major}.{ver.Minor}.{ver.Build}";
51|                RefreshSearchResults();
52|                CompositionTarget.Rendering += OnRendering;
53|            };
54|            Closed += (_, _) =>
55|            {
56|                CompositionTarget.Rendering -= OnRendering;
57|            };
58|        }
59|
60|        private void WindowFrame_SizeChanged(object sender, SizeChangedEventArgs e)
61|        {
62|            if (WindowFrame.ActualWidth > 0 && WindowFrame.ActualHeight > 0)
63|                WindowFrame.Clip = new RectangleGeometry(
64|                    new Rect(0, 0, WindowFrame.ActualWidth, WindowFrame.ActualHeight), 18, 18);
65|        }
66|
67|        /// <summary>全局键盘快捷键。</summary>
68|        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
69|        {
70|            if (Keyboard.Modifiers == ModifierKeys.Control)
71|            {
72|                switch (e.Key)
73|                {
74|                    case Key.N: // Ctrl+N: 新增记忆
75|                        e.Handled = true;
76|                        if (FrontFace.Visibility == Visibility.Visible)
77|                            OpenAddDialog();
78|                        else
79|                        {
80|                            new NoticeDialog("提示", "请在黑洞正面使用 Ctrl+N 新增记忆。")
81|                                { Owner = this }.ShowDialog();
82|                        }
83|                        break;
84|                    case Key.F: // Ctrl+F: 搜索框聚焦
85|                        e.Handled = true;
86|                        if (BackFace.Visibility == Visibility.Visible)
87|                        {
88|                            SearchBox?.Focus();
89|                            SearchBox?.SelectAll();
90|                        }
91|                        else if (EnsureAccess())
92|                            FlipToBack();
93|                        break;
94|                    case Key.W: // Ctrl+W: 关闭窗口
95|                        e.Handled = true;
96|                        Close();
97|                        break;
98|                }
99|            }
100|        }
101|
102|        private void OnRendering(object? sender, EventArgs e)
103|        {
104|            var now = DateTime.UtcNow;
105|            double delta = Math.Clamp((now - _lastFrame).TotalSeconds, 0, 0.05);
106|            _lastFrame = now;
107|            _frontSpace.Update(delta);
108|            _backSpace.Update(delta);
109|        }
110|
111|        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
112|        {
113|            if (e.ClickCount == 2)
114|            {
115|                ToggleMaximize();
116|                return;
117|            }
118|            if (e.LeftButton == MouseButtonState.Pressed)
119|                DragMove();
120|        }
121|
122|        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
123|
124|        private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
125|
126|        private void ToggleMaximize()
127|        {
128|            WindowState = WindowState == WindowState.Maximized
129|                ? WindowState.Normal
130|                : WindowState.Maximized;
131|            MaximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
132|        }
133|
134|        private void Close_Click(object sender, RoutedEventArgs e) => Close();
135|
136|        private void OpenGitHub_Click(object sender, RoutedEventArgs e)
137|        {
138|            try
139|            {
140|                Process.Start(new ProcessStartInfo("https://github.com/aicbbuu/MemoryBlackHole")
141|                {
142|                    UseShellExecute = true
143|                });
144|            }
145|            catch (Exception ex)
146|            {
147|                new NoticeDialog("打开失败", $"无法打开浏览器。\n{ex.Message}") { Owner = this }.ShowDialog();
148|            }
149|        }
150|
151|        /// <summary>拖拽文件到窗口 → 弹出新增对话框预填文件。</summary>
152|        private void Window_Drop(object sender, DragEventArgs e)
153|        {
154|            if (_service == null) return;
155|            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
156|            var files = (string[]?)e.Data.GetData(DataFormats.FileDrop);
157|            if (files == null || files.Length == 0) return;
158|
159|            // 拖入文本：按文字内容处理；拖入文件：弹出预填对话框
160|            if (files.Length == 1 && string.IsNullOrEmpty(System.IO.Path.GetExtension(files[0])))
161|            {
162|                // 无扩展名视为文本拖入，暂不支持
163|                new NoticeDialog("拖入文件", "要添加文本记忆，请在正面点击✦按钮或使用 Ctrl+N。")
164|                    { Owner = this }.ShowDialog();
165|                return;
166|            }
167|
168|            var dialog = new AddItemDialog(files) { Owner = this };
169|            if (dialog.ShowDialog() != true) return;
170|
171|            for (int i = 0; i < dialog.FilePaths.Count; i++)
172|            {
173|                byte[] fileData = File.ReadAllBytes(dialog.FilePaths[i]);
174|                _service.Add(new MemoryItem
175|                {
176|                    Type = dialog.SelectedType,
177|                    Title = dialog.OriginalFileNames[i],
178|                    Content = dialog.OriginalFileNames[i],
179|                    FilePath = null,
180|                    FileData = fileData,
181|                    Note = null,
182|                    Tags = dialog.Tags,
183|                    OriginalFileName = dialog.OriginalFileNames[i],
184|                    FileSizeBytes = dialog.FileSizes[i]
185|                });
186|            }
187|
188|            _frontSpace.PlayInward();
189|            RefreshSearchResults();
190|        }
191|
192|        private void FrontCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
193|        {
194|            if (e.ClickCount == 2 && EnsureAccess())
195|                FlipToBack();
196|        }
197|
198|        private bool EnsureAccess()
199|        {
200|            if (_service == null) return false;
201|            bool passed;
202|            if (!_service.HasPassword())
203|            {
204|                var setup = new PasswordDialog(true) { Owner = this };
205|                if (setup.ShowDialog() != true) return false;
206|                _service.SetPassword(setup.Password);
207|                passed = true;
208|            }
209|            else
210|            {
211|                var verify = new PasswordDialog(false) { Owner = this };
212|                passed = verify.ShowDialog() == true && _service.VerifyPassword(verify.Password);
213|                if (!passed)
214|                {
215|                    new NoticeDialog("访问被拒绝", "密码不正确，无法进入记忆空间。") { Owner = this }.ShowDialog();
216|                    return false;
217|                }
218|            }
219|            return passed;
220|        }
221|
222|        private void FlipToBack()
223|        {
224|            if (_flipping) return;
225|            _flipping = true;
226|            AnimateFlip(FlipScale, () =>
227|            {
228|                FrontFace.Visibility = Visibility.Collapsed;
229|                BackFace.Visibility = Visibility.Visible;
230|                AnimateFlip(BackFlipScale, () => _flipping = false);
231|            });
232|        }
233|
234|        private void BackToFront(object sender, RoutedEventArgs e)
235|        {
236|            if (_flipping) return;
237|            _flipping = true;
238|            AnimateFlip(BackFlipScale, () =>
239|            {
240|                BackFace.Visibility = Visibility.Collapsed;
241|                FrontFace.Visibility = Visibility.Visible;
242|                AnimateFlip(FlipScale, () => _flipping = false);
243|            });
244|        }
245|
246|        private static void AnimateFlip(ScaleTransform scale, Action onDone)
247|        {
248|            var collapse = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(260))
249|            {
250|                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
251|            };
252|            collapse.Completed += (_, _) =>
253|            {
254|                onDone();
255|                var expand = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260))
256|                {
257|                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
258|                };
259|                scale.BeginAnimation(ScaleTransform.ScaleXProperty, expand);
260|            };
261|            scale.BeginAnimation(ScaleTransform.ScaleXProperty, collapse);
262|        }
263|
264|        private void FrontAdd_Click(object sender, RoutedEventArgs e) => OpenAddDialog();
265|
266|        private void OpenAddDialog()
267|        {
268|            if (_service == null) return;
269|            var dialog = new AddItemDialog { Owner = this };
270|            if (dialog.ShowDialog() != true) return;
271|
272|            // 文本类型：直接保存内容
273|            if (dialog.SelectedType == "Text")
274|            {
275|                _service.Add(new MemoryItem
276|                {
277|                    Type = "Text",
278|                    Title = null,
279|                    Content = dialog.ContentText,
280|                    Note = null,
281|                    Tags = dialog.Tags
282|                });
283|            }
284|            else if (dialog.SelectedType == "Link")
285|            {
286|                _service.Add(new MemoryItem
287|                {
288|                    Type = "Link",
289|                    Title = dialog.ContentText,
290|                    Content = dialog.ContentText,
291|                    Note = null,
292|                    Tags = dialog.Tags
293|                });
294|            }
295|            else
296|            {
297|                // 非文本：读取文件字节直接存入 SQLite BLOB
298|                for (int i = 0; i < dialog.FilePaths.Count; i++)
299|                {
300|                    byte[] fileData = File.ReadAllBytes(dialog.FilePaths[i]);
301|
302|                    _service.Add(new MemoryItem
303|                    {
304|                        Type = dialog.SelectedType,
305|                        Title = dialog.OriginalFileNames[i],
306|                        Content = dialog.OriginalFileNames[i],
307|                        FilePath = null,
308|                        FileData = fileData,
309|                        Note = null,
310|                        Tags = dialog.Tags,
311|                        OriginalFileName = dialog.OriginalFileNames[i],
312|                        FileSizeBytes = dialog.FileSizes[i]
313|                    });
314|                }
315|            }
316|
317|            _frontSpace.PlayInward();
318|            RefreshSearchResults();
319|        }
320|
321|        private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
322|        {
323|            if (e.OriginalSource is DependencyObject source)
324|            {
325|                var item = ItemsControl.ContainerFromElement(ResultsList, source) is ContentPresenter presenter
326|                    ? presenter.Content as MemoryItem
327|                    : (source as FrameworkElement)?.DataContext as MemoryItem;
328|                if (item == null) return;
329|                var preview = new PreviewMemoryDialog(item) { Owner = this };
330|                preview.ShowDialog();
331|                if (preview.DeleteRequested)
332|                {
333|                    _service?.Delete(item.Id);
334|                    RefreshSearchResults();
335|                }
336|                else if (preview.EditRequested)
337|                {
338|                    var edit = new EditMemoryDialog(item) { Owner = this };
339|                    if (edit.ShowDialog() == true)
340|                    {
341|                        _service?.Update(item);
342|                        RefreshSearchResults();
343|                    }
344|                }
345|            }
346|        }
347|
348|        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
349|        {
350|            if (e.Key == Key.Enter)
351|                RefreshSearchResults();
352|        }
353|
354|        /// <summary>点击标签→按标签过滤搜索。</summary>
355|        private void TagItem_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
356|        {
357|            if (sender is FrameworkElement fe && fe.DataContext is KeyValuePair<string, int> kv)
358|                        {
359|                            if (kv.Key == "全部标签")
360|                                _activeTag = null;
361|                            else
362|                                _activeTag = kv.Key;
363|                            SearchBox.Text = "";
364|                            RefreshSearchResults();
365|                        }
366|        }
367|
368|        private void RefreshSearchResults()
369|        {
370|            if (_service == null)
371|            {
372|                SearchStatus.Text = "本地数据库尚未就绪";
373|                ResultsList.Visibility = Visibility.Collapsed;
374|                return;
375|            }
376|
377|            try
378|            {
379|                var keyword = SearchBox?.Text?.Trim() ?? "";
380|
381|                // 搜索（带标签过滤）
382|                var results = _service.Search(keyword, tag: _activeTag);
383|
384|                ResultsList.ItemsSource = results;
385|                bool hasResults = results.Count > 0;
386|                bool hasQuery = !string.IsNullOrWhiteSpace(keyword) || !string.IsNullOrEmpty(_activeTag);
387|
388|                ResultsList.Visibility = hasResults ? Visibility.Visible : Visibility.Collapsed;
389|                SearchStatus.Text = !hasQuery
390|                    ? "请输入关键词开始搜索"
391|                    : results.Count == 0
392|                        ? "没有找到这段记忆"
393|                        : $"黑洞吐出了 {results.Count} 条记忆" +
394|                          (!string.IsNullOrEmpty(_activeTag) ? $"（标签：{_activeTag}）" : "");
395|
396|                if (results.Count > 0)
397|                {
398|                    _backSpace.PlayOutward();
399|                    ResultsList.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(420)));
400|                }
401|
402|                // 刷新标签侧栏
403|                RefreshSidebar();
404|            }
405|            catch (Exception ex)
406|            {
407|                SearchStatus.Text = "搜索暂时不可用：" + ex.Message;
408|                ResultsList.Visibility = Visibility.Collapsed;
409|            }
410|        }
411|
412|        /// <summary>刷新标签列表和统计面板。</summary>
413|        private void RefreshSidebar()
414|        {
415|            if (_service == null) return;
416|            try
417|            {
418|                // 标签（前面加「全部标签」项）
419|                var tags = _service.GetTagCounts();
420|                var allTags = new List<KeyValuePair<string, int>> { new("全部标签", 0) };
421|                allTags.AddRange(tags);
422|                TagsList.ItemsSource = allTags;
423|
424|                // 统计
425|                var stats = _service.GetStats();
426|                string sizeStr = stats.TotalSizeBytes switch
427|                {
428|                    < 1024L => $"{stats.TotalSizeBytes} B",
429|                    < 1024L * 1024 => $"{stats.TotalSizeBytes / 1024.0:F1} KB",
430|                    < 1024L * 1024 * 1024 => $"{stats.TotalSizeBytes / 1024.0 / 1024.0:F1} MB",
431|                    _ => $"{stats.TotalSizeBytes / 1024.0 / 1024.0 / 1024.0:F1} GB"
432|                };
433|                StatsText.Text = $"📊 共 {stats.Total} 条记忆\n" +
434|                                 $"📝 文本 {stats.Text}  ·  🖼 图片 {stats.Image}\n" +
435|                                 $"🎵 音频 {stats.Audio}  ·  🎬 视频 {stats.Video}\n" +
436|                                 $"📄 文件 {stats.File}  ·  占用 {sizeStr}";
437|            }
438|            catch { /* 静默 */ }
439|        }
440|
441|        /// <summary>现代化 2.5D 黑洞视觉：引力透镜式椭圆吸积盘、热光环和粒子轨道。</summary>
533|        private sealed class SpaceCore
534|        {
535|            private sealed class BurstParticle
536|            {
537|                public FrameworkElement Shape { get; }
538|                public double Angle { get; }
539|                public double StartRadius { get; }
540|                public double EndRadius { get; }
541|                public double Age { get; set; }
542|                public BurstParticle(FrameworkElement shape, double angle, double startRadius, double endRadius)
543|                {
544|                    Shape = shape; Angle = angle; StartRadius = startRadius; EndRadius = endRadius;
545|                }
546|            }
547|
548|            private readonly Canvas _canvas;
549|            private readonly List<(Ellipse Ring, RotateTransform Rotate, double Speed)> _rings = new();
550|            private readonly List<BurstParticle> _bursts = new();
551|            private readonly bool _warm;
552|            private Ellipse _core = null!;
553|            private Ellipse _coreGlow = null!;
554|            private double _time;
555|
556|            public SpaceCore(Canvas canvas, bool warm)
557|            {
558|                _canvas = canvas; _warm = warm; Build();
559|            }
560|
561|            private void Build()
562|            {
563|                AddRing(680, 158, -8, _warm ? "#FFD18A" : "#D8E7FF", 0.18, 7);
564|                AddRing(610, 126, 16, _warm ? "#FF8A3D" : "#A8C7FF", -0.34, 10);
565|                AddRing(535, 96, -24, _warm ? "#FFB52E" : "#E7F0FF", 0.52, 6);
566|                AddRing(455, 70, 36, _warm ? "#FFF4D0" : "#B9D7FF", -0.78, 4);
567|
568|                _coreGlow = new Ellipse
569|                {
570|                    Width = 390, Height = 300,
571|                    Fill = new RadialGradientBrush(Color.FromArgb(145, _warm ? (byte)255 : (byte)75, _warm ? (byte)74 : (byte)120, 255), Color.FromArgb(0, 0, 0, 0)),
572|                    Opacity = 0.42, IsHitTestVisible = false
573|                };
574|                _canvas.Children.Add(_coreGlow);
575|
576|                _core = new Ellipse
577|                {
578|                    Width = 220, Height = 210,
579|                    Fill = new RadialGradientBrush(Color.FromRgb(0, 0, 1), Color.FromRgb(3, 5, 17)),
580|                    Stroke = new SolidColorBrush(Color.FromArgb(210, 8, 10, 24)),
581|                    StrokeThickness = 5, IsHitTestVisible = false
582|                };
583|                _canvas.Children.Add(_core);
584|
585|                // 不显示持续环绕的彩点；黑洞只保留吸积盘和交互星星特效。
586|            }
587|
588|            private void AddRing(double width, double height, double rotation, string color, double speed, double thickness)
589|            {
590|                var ring = new Ellipse
591|                {
592|                    Width = width, Height = height,
593|                    Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)),
594|                    StrokeThickness = thickness, Opacity = 0.38, IsHitTestVisible = false,
595|                    RenderTransformOrigin = new Point(0.5, 0.5),
596|                    RenderTransform = new RotateTransform(rotation)
597|                };
598|                ring.Effect = new DropShadowEffect { Color = ((SolidColorBrush)ring.Stroke).Color, BlurRadius = 26, ShadowDepth = 0, Opacity = 0.72 };
599|                _canvas.Children.Add(ring);
600|                _rings.Add((ring, (RotateTransform)ring.RenderTransform, speed));
601|            }
602|
603|            public void PlayInward() => StartBurst(true);
604|            public void PlayOutward() => StartBurst(false);
605|
606|            private void StartBurst(bool inward)
607|            {
608|                var color = _warm ? Color.FromRgb(0xFF, 0xD4, 0x72) : Color.FromRgb(0xB8, 0xE8, 0xFF);
609|                var star = new TextBlock
610|                {
611|                    Text = "✦",
612|                    FontSize = 42,
613|                    FontWeight = FontWeights.Bold,
614|                    Foreground = new SolidColorBrush(color),
615|                    Width = 58,
616|                    Height = 58,
617|                    TextAlignment = TextAlignment.Center,
618|                    IsHitTestVisible = false,
619|                    Effect = new DropShadowEffect
620|                    { Color = color, BlurRadius = 28, ShadowDepth = 0, Opacity = 1 }
621|                };
622|                _canvas.Children.Add(star);
623|                _bursts.Add(new BurstParticle(star, inward ? 0.15 : -0.25,
624|                    inward ? 600 : 34, inward ? 34 : 620));
625|            }
626|
627|            public void Update(double delta)
628|            {
629|                _time += delta;
630|                double cx = _canvas.ActualWidth > 0 ? _canvas.ActualWidth / 2 : 640;
631|                double cy = _canvas.ActualHeight > 0 ? _canvas.ActualHeight / 2 : 370;
632|                foreach (var item in _rings)
633|                {
634|                    item.Rotate.Angle += item.Speed * delta * 30;
635|                    Canvas.SetLeft(item.Ring, cx - item.Ring.Width / 2);
636|                    Canvas.SetTop(item.Ring, cy - item.Ring.Height / 2 + Math.Sin(_time * 0.6) * 3);
637|                }
638|                Canvas.SetLeft(_coreGlow, cx - _coreGlow.Width / 2);
639|                Canvas.SetTop(_coreGlow, cy - _coreGlow.Height / 2);
640|                Canvas.SetLeft(_core, cx - _core.Width / 2);
641|                Canvas.SetTop(_core, cy - _core.Height / 2);
642|                for (int i = _bursts.Count - 1; i >= 0; i--)
643|                {
644|                    var burst = _bursts[i];
645|                    burst.Age += delta;
646|                    double progress = Math.Clamp(burst.Age / 1.5, 0, 1);
647|                    double eased = progress * progress * (3 - 2 * progress);
648|                    double radius = burst.StartRadius + (burst.EndRadius - burst.StartRadius) * eased;
649|                    double x = Math.Cos(burst.Angle + _time * 0.8) * radius;
650|                    double y = Math.Sin(burst.Angle + _time * 0.8) * radius * 0.52;
651|                    burst.Shape.Opacity = 1 - progress;
652|                    Canvas.SetLeft(burst.Shape, cx + x - burst.Shape.Width / 2);
653|                    Canvas.SetTop(burst.Shape, cy + y - burst.Shape.Height / 2);
654|                    if (progress >= 1)
655|                    {
656|                        _canvas.Children.Remove(burst.Shape);
657|                        _bursts.RemoveAt(i);
658|                    }
659|                }
660|            }
661|        }
662|    }
663|}
664|