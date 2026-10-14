using System.Windows;
using PrinterMate.App.Services;

namespace PrinterMate.App.Startup;

/// <summary>
/// 启动协议窗口。
///
/// 行为约定(按使用者要求):
///   · 必须显式勾选后才能点「同意并进入」;
///   · 不同意 -> <see cref="DialogResult"/> 不为 true,由 App 负责立即退出,不执行任何业务操作;
///   · 同意   -> 写入本机日志作为凭据,然后进入主界面;
///   · 窗口右上角关闭按钮等同于「不同意」(DialogResult 保持 null)。
/// </summary>
public partial class AgreementWindow : Window
{
    public AgreementWindow()
    {
        InitializeComponent();

        // 正文从小节集合渲染,便于排版与维护(文案在 AgreementDocument 里)
        SectionList.ItemsSource = AgreementDocument.Sections;
        LeadText.Text = AgreementDocument.Lead;
    }

    private void OnAgreeChanged(object sender, RoutedEventArgs e)
    {
        if (AgreeButton is not null)
        {
            AgreeButton.IsEnabled = AgreeBox.IsChecked == true;
        }
    }

    private void OnAgreeClick(object sender, RoutedEventArgs e)
    {
        if (AgreeBox.IsChecked != true)
        {
            return;
        }

        AppLog.Write("agreement", $"使用者已同意启动协议(版本 {AgreementDocument.Title})。");
        DialogResult = true;
    }

    private void OnDeclineClick(object sender, RoutedEventArgs e)
    {
        AppLog.Write("agreement", "使用者不同意启动协议,程序退出。");
        DialogResult = false;
    }
}
