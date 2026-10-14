using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using PrinterMate.App.Services;

namespace PrinterMate.App.Startup;

/// <summary>
/// 启动动画窗口(约 3.5 秒,可跳过)。
///
/// 关键约束:
///   · 动画只改 Opacity 与 RenderTransform,避免逐帧重排(见 XAML 内注释);
///   · 跳过与正常结束都必须只关闭一次 —— 用 <see cref="_closing"/> 防重入,
///     否则用户跳过时 Storyboard 的 Completed 仍会触发,导致 Close() 被调用两次。
///   · 本窗口【不参与】任何业务初始化,关闭失败也不允许影响主流程。
/// </summary>
public partial class SplashWindow : Window
{
    private bool _closing;

    public SplashWindow()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        PreviewMouseDown += OnSkipRequested;
        PreviewKeyDown += OnSkipRequested;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (FindResource("Intro") is Storyboard storyboard)
            {
                storyboard.Completed += (_, _) => CloseOnce();
                storyboard.Begin(this);
            }
            else
            {
                // 资源缺失时不要卡住启动,直接进入后续流程
                CloseOnce();
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("splash", ex.ToString());
            CloseOnce();
        }
    }

    private void OnSkipRequested(object sender, InputEventArgs e)
    {
        e.Handled = true;
        CloseOnce();
    }

    private void CloseOnce()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;

        try
        {
            Close();
        }
        catch (Exception ex)
        {
            AppLog.Write("splash-close", ex.ToString());
        }
    }
}
