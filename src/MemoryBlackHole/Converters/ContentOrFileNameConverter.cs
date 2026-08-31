using System;
using System.Globalization;
using System.Windows.Data;
using MemoryBlackHole.Models;

namespace MemoryBlackHole.Converters
{
    /// <summary>
    /// 根据记忆类型选择主行显示文本(v3.0.3 重打):
    ///   - Text 类型 → 返回 Content 原文(由 XAML TextTrimming="CharacterEllipsis" 截断)
    ///   - Link 类型 → 返回 Content 原文
    ///   - 其它类型(图片/音频/视频/文件) → 返回 OriginalFileName 或 FilePath
    /// 不再固定截 10 字 + "..." — 自适应宽度由 TextTrimming 接管。
    /// </summary>
    public class ContentOrFileNameConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is not MemoryItem item) return value;

            if (item.Type == "Text" || item.Type == "Link")
            {
                var t = item.Content?.Trim() ?? "";
                return string.IsNullOrEmpty(t) ? "(无内容)" : t;
            }
            return item.OriginalFileName ?? item.FilePath ?? "(无)";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
