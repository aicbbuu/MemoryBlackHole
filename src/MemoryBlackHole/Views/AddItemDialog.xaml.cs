using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Shell;
using Microsoft.Win32;

namespace MemoryBlackHole.Views
{
    public partial class AddItemDialog : Window
    {
        public string ContentText { get; private set; } = "";
        public string? ItemTitle { get; private set; }
        public string? Note { get; private set; }
        public string? Tags { get; private set; }
        public string SelectedType { get; private set; } = "Text";

        // v3.1.0: 固定类型词(标签框左侧显示,用户不可删,确认时拼到 Tags 最前面)
        // TagsBox 只装"用户在固定词后追加的部分",最终 Tags = "{_fixedTag}{用户词非空时 ,+用户词}"
        // FixedTagLabel 由 XAML x:Name 自动生成同名字段,无需手写声明。
        private string _fixedTag = "文本";

        /// <summary>多文件支持：选中的文件路径列表。</summary>
        public List<string> FilePaths { get; private set; } = new();
        /// <summary>多文件支持：原始文件名列表。</summary>
        public List<string> OriginalFileNames { get; private set; } = new();
        /// <summary>多文件支持：文件大小列表。</summary>
        public List<long> FileSizes { get; private set; } = new();

        // v3.0.3 重打(问题1): 无边框窗口最大化用 WM_GETMINMAXINFO(见 NativeWindow)+ 最大化时
        // WindowChrome.ResizeBorderThickness=0(消除内容区内缩),Normal 还原为原值 _resizeBorder。
        private readonly Thickness _resizeBorder;
        private HwndSource? _hwndSource;

        // v3.0.9: 移除了未使用的 MaxFileSize / MaxTotalSize / MaxFileCount 三个常量

        public AddItemDialog()
        {
            InitializeComponent();
            _resizeBorder = WindowChrome.GetWindowChrome(this)?.ResizeBorderThickness ?? new Thickness(0);
            SourceInitialized += AddItemDialog_SourceInitialized;
            Closed += AddItemDialog_Closed;
            StateChanged += Window_StateChanged;
            // v3.0.9: 拖入文件模式时 FilePanel 可见,ContentBox 是 Collapsed,
            // 直接 Focus ContentBox 无效,应 Focus FileListBox(用户可立刻按方向键/Enter 选)
            Loaded += (_, _) =>
            {
                if (FilePanel != null && FilePanel.Visibility == Visibility.Visible)
                    FileListBox?.Focus();
                else
                    ContentBox.Focus();
            };
        }

        /// <summary>预选文件的构造函数（拖拽添加用）。</summary>
        public AddItemDialog(string[] filePaths) : this()
        {
            if (filePaths == null || filePaths.Length == 0) return;

            // 根据文件自动选择类型
            var first = filePaths[0].ToLowerInvariant();
            SelectedType = first switch
            {
                string f when f.EndsWith(".png") || f.EndsWith(".jpg") || f.EndsWith(".jpeg")
                    || f.EndsWith(".gif") || f.EndsWith(".bmp") || f.EndsWith(".webp") => "Image",
                string f when f.EndsWith(".mp4") || f.EndsWith(".mkv") || f.EndsWith(".avi")
                    || f.EndsWith(".mov") || f.EndsWith(".webm") => "Video",
                string f when f.EndsWith(".mp3") || f.EndsWith(".wav") || f.EndsWith(".flac")
                    || f.EndsWith(".m4a") || f.EndsWith(".ogg") => "Audio",
                _ => "File"
            };
            // v3.0.9: 改用 x:Name="TypePanel" 直接引用,替代反射式 ((WrapPanel)RText.Parent).Children
            // 选中对应的 radio
            foreach (var child in TypePanel.Children)
            {
                if (child is System.Windows.Controls.RadioButton rb && rb.Tag?.ToString() == SelectedType)
                    rb.IsChecked = true;
            }

            // 填入文件
            var displayItems = new List<string>();
            long totalSize = 0;
            foreach (var fp in filePaths)
            {
                var info = new FileInfo(fp);
                FilePaths.Add(fp);
                OriginalFileNames.Add(info.Name);
                FileSizes.Add(info.Length);
                totalSize += info.Length;
                displayItems.Add($"{info.Name}  ({FormatSize(info.Length)})");
            }

            ContentBox.Visibility = Visibility.Collapsed;
            FilePanel.Visibility = Visibility.Visible;
            SelectedFileText.Text = $"拖入了 {filePaths.Length} 个文件（共 {FormatSize(totalSize)}）";
            FileListBox.ItemsSource = displayItems;
            FileListBox.Visibility = Visibility.Visible;
        }

        private void Type_Changed(object sender, RoutedEventArgs e)
        {
            if (ContentBox == null || FilePanel == null || sender is not RadioButton radio) return;
            SelectedType = radio.Tag?.ToString() ?? "Text";
            bool text = SelectedType == "Text" || SelectedType == "Link";
            ContentBox.Visibility = text ? Visibility.Visible : Visibility.Collapsed;
            FilePanel.Visibility = text ? Visibility.Collapsed : Visibility.Visible;
            if (SelectedType == "Link")
                ContentBox.ToolTip = "输入链接地址（如 https://github.com/）";
            else
                ContentBox.ToolTip = "输入文本内容";
            // v3.1.0: 固定类型词(_fixedTag)随类型切换,左侧 FixedTagLabel 实时显示
            // TagsBox 不再被清空/覆盖 — 用户之前追加的标签全部保留
            _fixedTag = SelectedType switch
            {
                "Text"  => "文本",
                "Image" => "图片",
                "Video" => "视频",
                "Audio" => "音频",
                "File"  => "文件",
                "Link"  => "链接",
                _       => "",
            };
            if (FixedTagLabel != null) FixedTagLabel.Text = _fixedTag;
        }

        private void ChooseFile_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择要保存的文件",
                Multiselect = true, // 启用多选！
                Filter = SelectedType switch
                {
                    "Image" => "图片文件|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp|所有文件|*.*",
                    "Video" => "视频文件|*.mp4;*.mkv;*.avi;*.mov;*.webm|所有文件|*.*",
                    "Audio" => "音频文件|*.mp3;*.wav;*.flac;*.m4a;*.ogg|所有文件|*.*",
                    _ => "所有文件|*.*"
                }
            };
            if (dialog.ShowDialog() != true) return;

            // 清空之前的选中
            FilePaths.Clear();
            OriginalFileNames.Clear();
            FileSizes.Clear();

            var displayItems = new List<string>();
            long totalSize = 0;

            foreach (var fileName in dialog.FileNames)
            {
                var info = new FileInfo(fileName);

                FilePaths.Add(fileName);
                OriginalFileNames.Add(info.Name);
                FileSizes.Add(info.Length);
                totalSize += info.Length;

                string sizeStr = FormatSize(info.Length);
                displayItems.Add($"{info.Name}  ({sizeStr})");
            }

            // 更新 UI
            int count = displayItems.Count;
            SelectedFileText.Text = $"已选择 {count} 个文件（共 {FormatSize(totalSize)}）";
            FileListBox.ItemsSource = displayItems;
            FileListBox.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private static string FormatSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB" };
            double value = bytes; int unit = 0;
            while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
            return $"{value:0.##} {units[unit]}";
        }

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedType == "Text")
            {
                ContentText = ContentBox.Text?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(ContentText))
                {
                    ConfirmDialog.ShowInfo("丢进黑洞", "请输入文本内容", this);
                    return;
                }
            }
            else if (SelectedType == "Link")
            {
                ContentText = ContentBox.Text?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(ContentText))
                {
                    ConfirmDialog.ShowInfo("丢进黑洞", "请输入链接地址", this);
                    return;
                }
                if (!ContentText.StartsWith("http://") && !ContentText.StartsWith("https://"))
                {
                    ConfirmDialog.ShowInfo("丢进黑洞", "链接必须以 http:// 或 https:// 开头", this);
                    return;
                }
            }
            else if (FilePaths.Count == 0)
            {
                ConfirmDialog.ShowInfo("丢进黑洞", "请先点击「选择本地文件」按钮", this);
                return;
            }

            // 使用第一个文件的原始文件名作为对话框标题
            ItemTitle = OriginalFileNames.Count > 0 ? OriginalFileNames[0] : null;
            // v3.1.0: Tags = 固定类型词 + (用户词非空?"," + 用户词:"")
            var userTags = string.IsNullOrWhiteSpace(TagsBox.Text) ? "" : TagsBox.Text.Trim();
            Tags = string.IsNullOrEmpty(_fixedTag) ? userTags :
                   string.IsNullOrEmpty(userTags) ? _fixedTag :
                   _fixedTag + "," + userTags;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        // v3.0.3: 自定义标题栏 — 拖动 / 最小化 / 最大化切换 / 关闭
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

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        // v3.0.3 重打(问题1): 无边框窗口最大化的标准做法,与主窗口/查看弹窗完全一致:
        //   - WM_GETMINMAXINFO 钩子(NativeWindow)按"窗口当前所在显示器工作区"接管最大化尺寸与位置;
        //   - StateChanged 切换 WindowChrome.ResizeBorderThickness(最大化=0 消除内容区内缩,Normal 还原);
        //   - 不再用负 Margin / 手动设 Left/Top/Width/Height / MaxWidth/MaxHeight / RestoreBounds。
        private void AddItemDialog_SourceInitialized(object? sender, EventArgs e)
        {
            _hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _hwndSource?.AddHook(NativeWindow.WndProc);
        }

        private void AddItemDialog_Closed(object? sender, EventArgs e)
        {
            _hwndSource?.RemoveHook(NativeWindow.WndProc);
            _hwndSource = null;
        }

        private void Window_StateChanged(object? sender, EventArgs e)
        {
            NativeWindow.ApplyMaximizeState(this, _resizeBorder);
        }
    }
}