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

        /// <summary>媒体的本地数据库二进制内容；小于大小限制的媒体直接存入 SQLite。</summary>
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
