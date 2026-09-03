using System;
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
            ContentBox.Text = item.Content ?? "";
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            _item.Content = string.IsNullOrWhiteSpace(ContentBox.Text) ? null : ContentBox.Text.Trim();
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
