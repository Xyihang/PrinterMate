namespace PrinterMate.App.Services;

/// <summary>对话框抽象,便于在无窗口环境下安全降级。</summary>
public interface IDialogService
{
    /// <summary>二次确认。任何异常都按"用户取消"处理。</summary>
    bool Confirm(string title, string message, string confirmText = "确认", string cancelText = "取消");

    void Info(string title, string message);
}
