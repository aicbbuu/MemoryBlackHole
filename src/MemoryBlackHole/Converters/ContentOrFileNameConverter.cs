using System;
using System.Globalization;
using System.Text.RegularExpressions;
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
    /// v3.0.3(问题3): Text 正文常含硬换行 \r\n / \n,WPF TextBlock 在 TextWrapping="NoWrap" 下仍把硬换行
    /// 渲染成真实换行,导致多行正文被全部展开。这里把换行/制表符规范化为空格、压缩连续空白为单空格,
    /// 让文本类型与链接类型一样保持单行 + 超出以 … 结尾。
    /// </summary>
    public class ContentOrFileNameConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is not MemoryItem item) return value;

            if (item.Type == "Text" || item.Type == "Link")
            {
                var t = item.Content?.Trim() ?? "";
                if (string.IsNullOrEmpty(t)) return "(无内容)";
                // \s 含换行/制表符;压缩连续空白为单空格,保证单行
                return Regex.Replace(t, @"\s+", " ").Trim();
            }
            return item.OriginalFileName ?? item.FilePath ?? "(无)";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
