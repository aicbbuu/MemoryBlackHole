using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace MemoryBlackHole.Views
{
    /// <summary>暗色主题确认对话框，替代系统 MessageBox。</summary>
    public partial class ConfirmDialog : Window
    {
        /// <summary>用户是否确认（按了确认按钮）。</summary>
        public bool Confirmed { get; private set; }

        /// <summary>创建确认对话框。</summary>
        /// <param name="title">标题</param>
        /// <param name="message">消息内容</param>
        /// <param name="confirmText">确认按钮文字（默认"确定"）</param>
        /// <param name="cancelText">取消按钮文字（默认"取消"）</param>
        /// <param name="isWarning">是否警告样式（确认按钮红色）</param>
        public ConfirmDialog(string title, string message,
                             string confirmText = "确定", string cancelText = "取消",
                             bool isWarning = false)
        {
            InitializeComponent();
            TitleText.Text = title;
            MessageText.Text = message;
            ConfirmButton.Content = confirmText;
            CancelButton.Content = cancelText;

            // 信息提示只有一个按钮
            if (cancelText == "")
            {
                CancelButton.Visibility = Visibility.Collapsed;
            }

            if (isWarning)
            {
                var bg = new SolidColorBrush(Color.FromRgb(0x8B, 0x1A, 0x1A));
                var fg = new SolidColorBrush(Color.FromRgb(0xFF, 0x7E, 0x9E));
                if (bg.CanFreeze) bg.Freeze();
                if (fg.CanFreeze) fg.Freeze();
                ConfirmButton.Background = bg;
                ConfirmButton.Foreground = fg;
            }

            // v3.0.9: Enter 确认 / Esc 取消
            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter && ConfirmButton.Visibility == Visibility.Visible)
                {
                    Confirm_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    Cancel_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                }
            };
        }

        /// <summary>创建信息提示对话框（只有一个"知道了"按钮）。</summary>
        public static void ShowInfo(string title, string message, Window? owner = null)
        {
            var dialog = new ConfirmDialog(title, message, "知道了", "");
            if (owner != null) dialog.Owner = owner;
            dialog.ShowDialog();
        }

        /// <summary>创建确认对话框，返回是否确认。</summary>
        public static bool ShowConfirm(string title, string message, Window? owner = null, bool isWarning = false)
        {
            var dialog = new ConfirmDialog(title, message, isWarning ? "确认删除" : "确定", "取消", isWarning);
            if (owner != null) dialog.Owner = owner;
            return dialog.ShowDialog() == true && dialog.Confirmed;
        }

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            Confirmed = true;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}