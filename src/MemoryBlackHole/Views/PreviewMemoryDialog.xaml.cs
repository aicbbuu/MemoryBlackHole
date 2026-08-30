using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using MemoryBlackHole.Models;
using MemoryBlackHole.Services;

namespace MemoryBlackHole.Views
{
    public partial class PreviewMemoryDialog : Window
    {
        private readonly MemoryItem _item;
        private readonly DataService? _service;
        private string? _tempPreview;
        // v3.0.9: 图片预览改 StreamSource 后,需要持续持有 FileStream(不能 using)
        private FileStream? _imageStream;
        public bool EditRequested { get; private set; }
        public bool DeleteRequested { get; private set; }

        public PreviewMemoryDialog(MemoryItem item, DataService? service = null)
        {
            InitializeComponent();
            _item = item;
            _service = service;
            TitleText.Text = string.IsNullOrWhiteSpace(item.Title) ? item.DisplayText : item.Title;
            MetaText.Text = $"{item.TypeName}  ·  {item.CreatedAt:yyyy-MM-dd HH:mm}";
            ShowContent();
            // v3.0.9: ESC 退出全屏预览(双击图片后用 ESC 回到普通窗口大小)
            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape && WindowState == WindowState.Maximized)
                {
                    WindowState = WindowState.Normal;
                    e.Handled = true;
                }
            };
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
                // v3.0.9: 改用 StreamSource 加载图片,绕开 Uri 解析(支持含 # % & 等特殊字符的路径)
                try
                {
                    var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    _imageStream = fs;  // 持有引用,避免 GC 释放
                    var img = new BitmapImage();
                    img.BeginInit();
                    img.CacheOption = BitmapCacheOption.OnLoad;
                    img.StreamSource = fs;
                    img.EndInit();
                    img.Freeze();
                    ImagePreview.Source = img;
                    ImagePreview.Visibility = Visibility.Visible;
                    // v3.0.9: 双击图片全屏预览(ESC 还原)
                    ImagePreview.MouseLeftButtonDown += (_, e) =>
                    {
                        if (e.ClickCount == 2)
                        {
                            WindowState = WindowState == WindowState.Maximized
                                ? WindowState.Normal
                                : WindowState.Maximized;
                            e.Handled = true;
                        }
                    };
                }
                catch
                {
                    // 加载失败回退到 Uri 方式(极端情况)
                    try { ImagePreview.Source = new BitmapImage(new Uri(path)); ImagePreview.Visibility = Visibility.Visible; } catch { }
                }
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
            // v3.0.9: 优先用 service 流式提取,避免把 800MB+ BLOB 一次加载到 .NET 内存
            if (_service != null && _item.Id > 0 && _service.HasBlobData(_item.Id))
            {
                string ext = Path.GetExtension(_item.OriginalFileName ?? "") ?? ".bin";
                _tempPreview = Path.Combine(Path.GetTempPath(), $"MemoryBlackHole_{Guid.NewGuid():N}{ext}");
                try
                {
                    _service.ExtractBlobToFile(_item.Id, _tempPreview);
                    return _tempPreview;
                }
                catch
                {
                    // ExtractBlobToFile 失败时退回 FileData(若已加载)
                    if (_item.FileData != null && _item.FileData.Length > 0)
                    {
                        using var fs = File.Create(_tempPreview);
                        fs.Write(_item.FileData, 0, _item.FileData.Length);
                        return _tempPreview;
                    }
                    return null;
                }
            }

            // 情况 A:FileData 已在内存(刚 Add 完立刻 Preview 的场景)
            if (_item.FileData != null && _item.FileData.Length > 0)
            {
                string ext = Path.GetExtension(_item.OriginalFileName ?? "") ?? ".bin";
                _tempPreview = Path.Combine(Path.GetTempPath(), $"MemoryBlackHole_{Guid.NewGuid():N}{ext}");
                using (var fs = File.Create(_tempPreview))
                {
                    fs.Write(_item.FileData, 0, _item.FileData.Length);
                }
                return _tempPreview;
            }

            // 情况 B:外部文件(超过 SQLite BLOB 阈值的存储方式)
            if (_item.FilePath != null && File.Exists(_item.FilePath))
                return _item.FilePath;

            return null;
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
            bool confirmed = ConfirmDialog.ShowConfirm("确认删除",
                $"确定删除这条{_item.TypeName}记忆吗？\n此操作不可恢复。", this, isWarning: true);
            if (confirmed)
            {
                DeleteRequested = true;
                Close();
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void CleanupTemp()
        {
            // v3.0.9: 先释放图片 FileStream(若存在),再删临时文件
            try { _imageStream?.Dispose(); } catch { }
            _imageStream = null;
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
