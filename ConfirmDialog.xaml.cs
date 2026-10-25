using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace musicPlayer
{
    /// <summary>
    /// 应用风格的确认弹窗（替代系统 MessageBox）：
    /// 深色圆角卡片，危险操作显示红色确认按钮，普通操作显示金色。
    /// Esc = 取消，Enter = 确认（确认按钮初始聚焦）。
    /// 用法：ConfirmDialog.Confirm(owner, 标题, 正文, 按钮文字, 是否危险操作)。
    /// </summary>
    public partial class ConfirmDialog : Window
    {
        private ConfirmDialog()
        {
            InitializeComponent();
        }

        /// <summary>弹出确认框并阻塞等待，返回用户是否点了确认。</summary>
        public static bool Confirm(Window? owner, string title, string message,
            string confirmText = "确定", bool danger = true)
        {
            var dlg = new ConfirmDialog
            {
                Owner = owner
            };
            dlg.txtTitle.Text = title;
            dlg.txtMessage.Text = message;
            dlg.btnConfirm.Content = confirmText;

            if (danger)
            {
                // 危险操作：红色警示徽章 + 红色确认按钮（XAML 默认即红色，无需改）
                dlg.txtIcon.Text = "⚠";
            }
            else
            {
                // 普通操作：金色主题
                dlg.txtIcon.Text = "♪";
                dlg.iconBadge.Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xD7, 0x00));
                dlg.txtIcon.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00));
                dlg.btnConfirm.Background = new SolidColorBrush(Color.FromRgb(0xD9, 0xA5, 0x21));
            }

            // 确认按钮初始聚焦：直接回车 = 确认
            dlg.Loaded += (_, _) => dlg.btnConfirm.Focus();
            dlg.ShowDialog();
            return dlg.DialogResult == true;
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                DialogResult = false;
                e.Handled = true;
            }
        }

        private void btnConfirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;

        private void btnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
