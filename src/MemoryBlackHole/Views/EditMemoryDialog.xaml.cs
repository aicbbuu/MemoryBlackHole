using System.Windows;
using MemoryBlackHole.Models;

namespace MemoryBlackHole.Views
{
    public partial class EditMemoryDialog : Window
    {
        private readonly MemoryItem _item;
        public EditMemoryDialog(MemoryItem item)
        {
            InitializeComponent();
            _item = item;
            TypeText.Text = $"类型：{item.TypeName}";
            ContentBox.Text = item.Type == "Text" ? item.Content ?? "" : item.OriginalFileName ?? "";
            ContentBox.IsReadOnly = item.Type != "Text";
            TagsBox.Text = item.Tags ?? "";
            FileText.Text = string.IsNullOrWhiteSpace(item.OriginalFileName) ? "" : $"文件：{item.OriginalFileName}";
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            // v3.0.9: Text/Link 都允许编辑 Content;非文本类型只改 Tags(Content 是文件名,不可改)
            if (_item.Type == "Text" || _item.Type == "Link")
                _item.Content = string.IsNullOrWhiteSpace(ContentBox.Text) ? null : ContentBox.Text.Trim();
            _item.Tags = string.IsNullOrWhiteSpace(TagsBox.Text) ? null : TagsBox.Text.Trim();
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
