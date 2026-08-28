using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
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

        /// <summary>多文件支持：选中的文件路径列表。</summary>
        public List<string> FilePaths { get; private set; } = new();
        /// <summary>多文件支持：原始文件名列表。</summary>
        public List<string> OriginalFileNames { get; private set; } = new();
        /// <summary>多文件支持：文件大小列表。</summary>
        public List<long> FileSizes { get; private set; } = new();

        /// <summary>文件校验：不限大小、不限数量。</summary>
        private const long MaxFileSize = 10L * 1024 * 1024 * 1024; // 10 GB
        private const long MaxTotalSize = 50L * 1024 * 1024 * 1024; // 50 GB
        private const int MaxFileCount = 200;

        public AddItemDialog()
        {
            InitializeComponent();
            Loaded += (_, _) => ContentBox.Focus();
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
            // 选中对应的 radio
            foreach (var child in ((WrapPanel)RText.Parent).Children)
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
            bool text = SelectedType == "Text";
            ContentBox.Visibility = text ? Visibility.Visible : Visibility.Collapsed;
            FilePanel.Visibility = text ? Visibility.Collapsed : Visibility.Visible;
            // 不自动打开文件选择窗口，必须由用户点击按钮。
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
                    MessageBox.Show("请输入文本内容", "丢进黑洞", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
            }
            else if (FilePaths.Count == 0)
            {
                MessageBox.Show("请先点击\u201C选择本地文件\u201D", "丢进黑洞", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 使用第一个文件的原始文件名作为对话框标题
            ItemTitle = OriginalFileNames.Count > 0 ? OriginalFileNames[0] : null;
            Tags = string.IsNullOrWhiteSpace(TagsBox.Text) ? null : TagsBox.Text.Trim();
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}