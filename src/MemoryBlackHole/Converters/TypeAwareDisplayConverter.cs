using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using MemoryBlackHole.Models;

namespace MemoryBlackHole.Converters
{
    /// <summary>
    /// 根据记忆类型选择主行显示文本：
    ///   - Text 类型 → 取 Content(正文)前 60 字
    ///   - 其它类型(图片/音频/视频/文件/链接) → 取 OriginalFileName
    /// 注意：v3.0.2 起彻底不用 Title(避免和内容/文件名重复显示)。
    /// </summary>
    public class TypeAwareDisplayConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is not MemoryItem item) return value;
            if (item.Type == "Text" && !string.IsNullOrWhiteSpace(item.Content))
            {
                var t = item.Content.Trim();
                return t.Length > 60 ? t[..60] + "…" : t;
            }
            return item.OriginalFileName ?? item.FilePath ?? "(无内容)";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
