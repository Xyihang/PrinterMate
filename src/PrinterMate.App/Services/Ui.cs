using System.Windows;

namespace PrinterMate.App.Services;

/// <summary>把后台线程的更新安全地投递到 UI 线程。</summary>
public static class Ui
{
    public static void Post(Action action)
    {
        if (action is null)
        {
            return;
        }

        try
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                action();
            }
            else
            {
                dispatcher.BeginInvoke(action);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("ui-post", ex.ToString());
        }
    }
}
