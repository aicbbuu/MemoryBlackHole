using System;
using System.Globalization;
using System.Windows.Data;
using MemoryBlackHole.Models;

namespace MemoryBlackHole.Converters
{
    /// <summary>
    /// 根据记忆类型选择主行显示文本(v3.0.3):
    ///   - Text 类型 → 取 Content 前 10 字(超过则追加 "...")
    ///   - Link 类型 → 取 Content 或 OriginalFileName 前 10 字(超过则追加 "...");空就 "(无内容)"
    ///   - 其它类型(图片/音频/视频/文件) → 取 OriginalFileName
    /// 注意:不再用 Title(避免和内容/文件名重复显示)。
    /// </summary>
    public class TypeAwareDisplayConverter : IValueConverter
    {
        private const int PreviewLen = 10;

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is not MemoryItem item) return value;

            if (item.Type == "Text")
            {
                var t = item.Content?.Trim() ?? "";
                return t.Length > PreviewLen ? t[..PreviewLen] + "..." : t;
            }
            if (item.Type == "Link")
            {
                var t = !string.IsNullOrWhiteSpace(item.Content) ? item.Content.Trim()
                       : (!string.IsNullOrWhiteSpace(item.OriginalFileName) ? item.OriginalFileName.Trim() : "");
                if (string.IsNullOrEmpty(t)) return "(无内容)";
                return t.Length > PreviewLen ? t[..PreviewLen] + "..." : t;
            }
            return item.OriginalFileName ?? item.FilePath ?? "(无内容)";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
