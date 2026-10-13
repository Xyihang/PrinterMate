using System.Windows;

namespace PrinterMate.App.Dialogs;

/// <summary>纯展示型对话框:标题 + 可滚动的长文本 + 确认/取消。</summary>
public partial class MessageDialog : Window
{
    public MessageDialog(string title, string message, string confirmText, string? cancelText, bool showCancel)
    {
        InitializeComponent();

        Title = string.IsNullOrWhiteSpace(title) ? "PrinterMate" : title;
        TitleText.Text = Title;
        BodyText.Text = message ?? string.Empty;
        ConfirmButton.Content = string.IsNullOrWhiteSpace(confirmText) ? "确认" : confirmText;

        if (showCancel)
        {
            CancelButton.Content = string.IsNullOrWhiteSpace(cancelText) ? "取消" : cancelText;
            CancelButton.Visibility = Visibility.Visible;
        }
        else
        {
            CancelButton.Visibility = Visibility.Collapsed;
        }
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
