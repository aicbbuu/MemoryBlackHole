using System;
using System.Globalization;
using System.Linq;
using System.Windows.Data;

namespace MemoryBlackHole.Converters
{
    /// <summary>
    /// 列表副行用:把 Tags 字段里的"类型词"前缀剥掉,只返回用户实际追加的标签。
    /// Tags 存储格式(v3.1.0): "{固定类型词},{用户词1},{用户词2}..."
    /// 例: "图片,工作,2024" → 剥掉 "图片" 第一个 → 返回 "工作,2024"
    /// 用户没加额外标签时(只有 "图片" 一个) → 返回空串
    /// </summary>
    public class StripFixedTagConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is not string tags || string.IsNullOrWhiteSpace(tags)) return "";
            // 取第一段(类型词),剩余就是用户词
            var parts = tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length <= 1) return ""; // 只有类型词,没用户词
            return string.Join(", ", parts.Skip(1)); // 跳过类型词
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
