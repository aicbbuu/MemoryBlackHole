using System;
using System.Windows;
using MemoryBlackHole.Models;

namespace MemoryBlackHole.Views
{
    public partial class EditMemoryDialog : Window
    {
        private readonly MemoryItem _item;
        // v3.1.0 兼容 AddItemDialog：标签框只装自定义标签，类型词由左侧 FixedTagLabel 固定显示。
        // FixedTagLabel / TagsBox 由 XAML x:Name 自动生成字段，无需手写声明。
        private readonly string _fixedTag;

        public EditMemoryDialog(MemoryItem item)
        {
            InitializeComponent();
            _item = item;
            _fixedTag = item.Type switch
            {
                "Text"  => "文本",
                "Image" => "图片",
                "Video" => "视频",
                "Audio" => "音频",
                "File"  => "文件",
                "Link"  => "链接",
                _       => "",
            };
            TypeText.Text = $"类型：{item.TypeName}";
            ContentBox.Text = item.Type == "Text" ? item.Content ?? "" : item.OriginalFileName ?? "";
            ContentBox.IsReadOnly = item.Type != "Text";
            if (FixedTagLabel != null) FixedTagLabel.Text = _fixedTag;
            TagsBox.Text = StripTypeTag(item.Tags);
            FileText.Text = string.IsNullOrWhiteSpace(item.OriginalFileName) ? "" : $"文件：{item.OriginalFileName}";
        }

        /// <summary>打开时把"类型词"从完整 Tags 里剥掉，TagsBox 只留自定义部分；兼容旧数据。</summary>
        private string StripTypeTag(string? tags)
        {
            if (string.IsNullOrWhiteSpace(tags)) return "";
            var trimmed = tags.Trim();
            // 以"固定类型词 + 逗号(中/英文)"开头 → 剥掉类型词
            if (!string.IsNullOrEmpty(_fixedTag) &&
                (trimmed.StartsWith(_fixedTag + ",", StringComparison.Ordinal) ||
                 trimmed.StartsWith(_fixedTag + "，", StringComparison.Ordinal)))
                return trimmed.Substring(_fixedTag.Length + 1).TrimStart();
            // 完全就是类型词（无自定义标签）→ 返回空
            if (!string.IsNullOrEmpty(_fixedTag) && trimmed == _fixedTag)
                return "";
            // 兼容旧数据：不以类型词开头 → 原样放进 TagsBox（去掉可能的前导逗号）
            return trimmed.TrimStart(',', '，');
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            // v3.0.9: Text/Link 都允许编辑 Content;非文本类型只改 Tags(Content 是文件名,不可改)
            if (_item.Type == "Text" || _item.Type == "Link")
                _item.Content = string.IsNullOrWhiteSpace(ContentBox.Text) ? null : ContentBox.Text.Trim();
            // v3.1.0: 保存时拼回 "类型词,自定义"（与 AddItemDialog 规则一致）
            var userTags = string.IsNullOrWhiteSpace(TagsBox.Text) ? "" : TagsBox.Text.Trim();
            _item.Tags = string.IsNullOrEmpty(_fixedTag)
                ? (string.IsNullOrEmpty(userTags) ? null : userTags)
                : string.IsNullOrEmpty(userTags) ? _fixedTag : _fixedTag + "," + userTags;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
