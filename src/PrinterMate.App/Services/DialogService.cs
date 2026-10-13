using System.Windows;
using PrinterMate.App.Dialogs;

namespace PrinterMate.App.Services;

/// <summary>基于 WPF 窗口的确认/提示实现。所有失败都收敛为"取消",不抛出异常。</summary>
public sealed class DialogService : IDialogService
{
    public bool Confirm(string title, string message, string confirmText = "确认", string cancelText = "取消")
    {
        try
        {
            var dialog = new MessageDialog(title, message, confirmText, cancelText, showCancel: true)
            {
                Owner = ResolveOwner(),
            };

            return dialog.ShowDialog() == true;
        }
        catch (Exception ex)
        {
            AppLog.Write("dialog", ex.ToString());
            return false;
        }
    }

    public void Info(string title, string message)
    {
        try
        {
            var dialog = new MessageDialog(title, message, "知道了", null, showCancel: false)
            {
                Owner = ResolveOwner(),
            };

            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            AppLog.Write("dialog", ex.ToString());
        }
    }

    private static Window? ResolveOwner()
    {
        var main = Application.Current?.MainWindow;
        return main is { IsLoaded: true } ? main : null;
    }
}
