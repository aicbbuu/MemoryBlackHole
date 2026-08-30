using System;

namespace MemoryBlackHole.Models
{
    /// <summary>记忆黑洞中的一条记录（一句话、一个文件、一张图、一段语音/视频）。</summary>
    public class MemoryItem
    {
        public long Id { get; set; }

        /// <summary>内容类型：Text / File / Image / Audio / Video</summary>
        public string Type { get; set; } = "Text";

        /// <summary>标题（默认可为空，自动取首行/文件名）</summary>
        public string? Title { get; set; }

        /// <summary>文本内容（仅 Text 类型有；其余类型为备注/说明）</summary>
        public string? Content { get; set; }

        /// <summary>文件/媒体对应的本地存储路径（库内副本的路径）</summary>
        public string? FilePath { get; set; }

        /// <summary>媒体的本地数据库二进制内容；≤1GiB 的文件存入 SQLite，超出阈值时为 null。</summary>
        public byte[]? FileData { get; set; }

        /// <summary>原始文件名（入纳入时）</summary>
        public string? OriginalFileName { get; set; }

        /// <summary>媒体/文件大小（字节）</summary>
        public long FileSizeBytes { get; set; }

        /// <summary>用户备注（媒体类主要靠这个可搜）</summary>
        public string? Note { get; set; }

        /// <summary>标签（逗号分隔，如 "合同,重要"）</summary>
        public string? Tags { get; set; }

        /// <summary>创建时间</summary>
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        /// <summary>是否收藏</summary>
        public bool IsFavorite { get; set; }

        /// <summary>是否已删除（回收站）</summary>
        public bool IsDeleted { get; set; }

        /// <summary>删除时间</summary>
        public DateTime? DeletedAt { get; set; }

        /// <summary>
        /// 缩略图源(从 FileData BLOB 加载,仅 Image 类型使用)。
        /// v3.0.9 标 [Obsolete]:Search 永远 FileData=null,此 getter 永远返回 null,
        /// MainWindow 列表行 ThumbnailImageVisibility 也永远 Collapsed,缩略图功能实际未生效。
        /// 保留是为了不破坏 XAML 绑定(编译期 0 警告)。如未来要重新启用,见
        /// PreviewMemoryDialog.xaml.cs 的 ExtractBlobToFile 流式加载方案。
        /// </summary>
        [System.Obsolete("ThumbnailSource 在搜索结果中永远为 null(MainWindow.xaml L142 绑定仍无害)。如要启用,改用 DataService.ExtractBlobToFile 流式加载缩略图。")]
        [System.Text.Json.Serialization.JsonIgnore]
        public System.Windows.Media.ImageSource? ThumbnailSource
        {
            get
            {
                if (Type != "Image" || FileData == null || FileData.Length == 0) return null;
                try
                {
                    var img = new System.Windows.Media.Imaging.BitmapImage();
                    using var ms = new System.IO.MemoryStream(FileData);
                    img.BeginInit();
                    img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    img.StreamSource = ms;
                    img.DecodePixelWidth = 84;
                    img.EndInit();
                    img.Freeze();
                    return img;
                }
                catch { return null; }
            }
        }

        /// <summary>
        /// 缩略图图标可见性。v3.0.9 标 [Obsolete]:与 ThumbnailSource 联动,搜索结果中永远 Collapsed。
        /// </summary>
        [System.Obsolete("见 ThumbnailSource 注释。")]
        [System.Text.Json.Serialization.JsonIgnore]
        public System.Windows.Visibility ThumbnailVisibility =>
            Type == "Image" && ThumbnailSource != null ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

        /// <summary>
        /// 缩略图图片可见性。v3.0.9 标 [Obsolete]:与 ThumbnailSource 联动,搜索结果中永远 Collapsed。
        /// </summary>
        [System.Obsolete("见 ThumbnailSource 注释。")]
        [System.Text.Json.Serialization.JsonIgnore]
        public System.Windows.Visibility ThumbnailImageVisibility =>
            Type == "Image" && ThumbnailSource != null ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        /// <summary>供搜索用的摘要/预览文本。</summary>
        public string DisplayText
        {
            get
            {
                if (Type == "Text" && !string.IsNullOrWhiteSpace(Content))
                    return Content.Trim().Length > 80 ? Content.Trim()[..80] + "…" : Content.Trim();
                if (!string.IsNullOrWhiteSpace(Note)) return Note;
                if (!string.IsNullOrWhiteSpace(Title)) return Title;
                return OriginalFileName ?? FilePath ?? "(无内容)";
            }
        }

        /// <summary>类型图标（Material 图标名）。</summary>
        public string TypeIcon
        {
            get
            {
                return Type switch
                {
                    "File" => "FileOutline",
                    "Image" => "ImageOutline",
                    "Audio" => "MusicNoteOutline",
                    "Video" => "MovieOpenOutline",
                    _ => "TextRecognition"
                };
            }
        }

        /// <summary>类型中文名。</summary>
        public string TypeName
        {
            get
            {
                return Type switch
                {
                    "File" => "文件",
                    "Image" => "图片",
                    "Audio" => "语音",
                    "Video" => "视频",
                    _ => "文本"
                };
            }
        }
    }
}
