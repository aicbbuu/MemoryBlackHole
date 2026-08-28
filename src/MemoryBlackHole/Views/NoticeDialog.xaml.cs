using System.Windows;

namespace MemoryBlackHole.Views
{
    public partial class NoticeDialog : Window
    {
        public NoticeDialog(string title, string message)
        {
            InitializeComponent();
            TitleText.Text = title;
            MessageText.Text = message;
        }

        private void Ok_Click(object sender, RoutedEventArgs e) => Close();
    }
}
