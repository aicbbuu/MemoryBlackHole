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

        /// <summary>媒体的本地数据库二进制内容；≤800MB（十进制）的文件存入 SQLite，超出阈值时为 null。</summary>
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
        /// v3.1.4: 缩略图 BLOB(约 100x100 PNG,KB 级,仅 Image 类型在 AddFile 时生成)。
        /// Search 时 SELECT 此字段(小 BLOB 不耗内存),MainWindow 列表行优先显示此缩略图。
        /// 旧数据(无缩略图)或非 Image 类型此字段为 null,列表回退显示占位 emoji(🖼)。
        /// </summary>
        public byte[]? Thumbnail { get; set; }

        /// <summary>
        /// 缩略图 ImageSource(懒加载:有 Thumbnail BLOB 才解码为 BitmapImage,缓存)。
        /// 用 StreamSource + BitmapCacheOption.OnLoad 释放 native 内存。
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public System.Windows.Media.ImageSource? ThumbnailImage
        {
            get
            {
                if (Thumbnail == null || Thumbnail.Length == 0) return null;
                try
                {
                    var img = new System.Windows.Media.Imaging.BitmapImage();
                    using var ms = new System.IO.MemoryStream(Thumbnail);
                    img.BeginInit();
                    img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    img.StreamSource = ms;
                    img.EndInit();
                    img.Freeze();
                    return img;
                }
                catch { return null; }
            }
        }

        /// <summary>缩略图占位图标可见性(无缩略图或非 Image 时显示)。</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public System.Windows.Visibility ThumbnailIconVisibility
        {
            get
            {
                // v3.1.4: Thumbnail 非空且类型为 Image 时,占位图标隐藏(由缩略图替代)
                return Type == "Image" && Thumbnail != null && Thumbnail.Length > 0
                    ? System.Windows.Visibility.Collapsed
                    : System.Windows.Visibility.Visible;
            }
        }

        /// <summary>缩略图图片可见性(有缩略图且 Image 时显示)。</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public System.Windows.Visibility ThumbnailImageVisibility
        {
            get
            {
                return Type == "Image" && Thumbnail != null && Thumbnail.Length > 0
                    ? System.Windows.Visibility.Visible
                    : System.Windows.Visibility.Collapsed;
            }
        }

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
                    "Audio" => "音频",
                    "Video" => "视频",
                    _ => "文本"
                };
            }
        }
    }
}
