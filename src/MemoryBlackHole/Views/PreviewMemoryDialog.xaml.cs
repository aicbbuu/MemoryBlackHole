using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media.Imaging;
using MemoryBlackHole.Models;
using MemoryBlackHole.Services;

namespace MemoryBlackHole.Views
{
    public partial class PreviewMemoryDialog : Window
    {
        private readonly MemoryItem _item;
        private string? _tempPreview;
        public bool EditRequested { get; private set; }
        public bool DeleteRequested { get; private set; }

        public PreviewMemoryDialog(MemoryItem item)
        {
            InitializeComponent();
            _item = item;
            TitleText.Text = string.IsNullOrWhiteSpace(item.Title) ? item.DisplayText : item.Title;
            MetaText.Text = $"{item.TypeName}  ·  {item.CreatedAt:yyyy-MM-dd HH:mm}";
            ShowContent();
            Closed += (_, _) => CleanupTemp();
        }

        private void ShowContent()
        {
            if (_item.Type == "Text")
            {
                // 文本：支持 Markdown 渲染
                MarkdownViewer.Visibility = Visibility.Visible;
                var doc = MarkdownHelper.ToFlowDocument(_item.Content ?? "");
                MarkdownViewer.Document = doc;
                return;
            }

            if (_item.Type == "Link")
            {
                // 链接：显示可点击链接
                LinkPanel.Visibility = Visibility.Visible;
                LinkUrlText.Text = _item.Content ?? _item.Title ?? "";
                return;
            }

            string? path = GetPreviewPath();
            if (_item.Type == "Image" && path != null)
            {
                ImagePreview.Source = new BitmapImage(new Uri(path));
                ImagePreview.Visibility = Visibility.Visible;
            }
            else if ((_item.Type == "Video" || _item.Type == "Audio") && path != null)
            {
                MediaPreview.Source = new Uri(path);
                MediaPreview.Visibility = Visibility.Visible;
                MediaPreview.Play();
            }
            else
            {
                FileInfoPanel.Visibility = Visibility.Visible;
                FileInfoText.Text = $"{_item.OriginalFileName ?? "未命名文件"}\n{FormatSize(_item.FileSizeBytes)}";
                OpenFileButton.Visibility = string.IsNullOrWhiteSpace(path) ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        private string? GetPreviewPath()
        {
            if (_item.FileData != null && _item.FileData.Length > 0)
            {
                string ext = Path.GetExtension(_item.OriginalFileName ?? "") ?? ".bin";
                _tempPreview = Path.Combine(Path.GetTempPath(), $"MemoryBlackHole_{Guid.NewGuid():N}{ext}");
                File.WriteAllBytes(_tempPreview, _item.FileData);
                return _tempPreview;
            }
            return File.Exists(_item.FilePath) ? _item.FilePath : null;
        }

        private void OpenFile_Click(object sender, RoutedEventArgs e)
        {
            // 优先使用已生成的临时预览文件（BLOB 存库的情况），其次用本地路径
            string? target = (File.Exists(_tempPreview)) ? _tempPreview : null;
            if (target == null && File.Exists(_item.FilePath))
                target = _item.FilePath;

            if (target == null) return;

            try
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                new NoticeDialog("打开失败", $"无法使用系统程序打开该文件。\n{ex.Message}") { Owner = this }.ShowDialog();
            }
        }

        /// <summary>打开链接。</summary>
        private void OpenLink_Click(object sender, RoutedEventArgs e)
        {
            var url = _item.Content ?? _item.Title;
            if (!string.IsNullOrEmpty(url))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    new NoticeDialog("打开失败", $"无法打开链接。\n{ex.Message}") { Owner = this }.ShowDialog();
                }
            }
        }

        private void Edit_Click(object sender, RoutedEventArgs e)
        {
            EditRequested = true;
            Close();
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show($"确定删除这条{_item.TypeName}记忆吗？\n此操作不可恢复。",
                "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes)
            {
                DeleteRequested = true;
                Close();
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void CleanupTemp()
        {
            try { if (_tempPreview != null && File.Exists(_tempPreview)) File.Delete(_tempPreview); } catch { }
        }

        private static string FormatSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB" };
            double value = bytes; int unit = 0;
            while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
            return $"{value:0.##} {units[unit]}";
        }
    }
}
