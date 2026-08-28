using System.Windows;

namespace MemoryBlackHole.Views
{
    public partial class PasswordDialog : Window
    {
        private readonly bool _setup;
        public string Password => PasswordBox.Password;

        public PasswordDialog(bool setup)
        {
            InitializeComponent();
            _setup = setup;
            if (setup)
            {
                HeaderText.Text = "设置空间密码";
                DescriptionText.Text = "首次进入前，请设置一个本地密码";
                ConfirmLabel.Visibility = Visibility.Visible;
                ConfirmBox.Visibility = Visibility.Visible;
                OkButton.Content = "保存密码";
            }
            else
            {
                ConfirmLabel.Visibility = Visibility.Collapsed;
                ConfirmBox.Visibility = Visibility.Collapsed;
                OkButton.Content = "验证";
                Loaded += (_, _) => PasswordBox.Focus();
            }
        }

        private void PasswordBox_Changed(object sender, RoutedEventArgs e) { }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            // 仅"首次设置密码"时才校验长度与两次一致；验证模式下任何输入都交给密码校验判断
            if (_setup && Password.Length < 6)
            {
                new NoticeDialog("密码设置失败", "密码至少需要 6 位。") { Owner = this }.ShowDialog();
                PasswordBox.Clear();
                ConfirmBox.Clear();
                PasswordBox.Focus();
                return;
            }
            if (_setup && Password != ConfirmBox.Password)
            {
                new NoticeDialog("密码设置失败", "两次输入的密码不一致，请重新输入。") { Owner = this }.ShowDialog();
                ConfirmBox.Clear();
                ConfirmBox.Focus();
                return;
            }
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
