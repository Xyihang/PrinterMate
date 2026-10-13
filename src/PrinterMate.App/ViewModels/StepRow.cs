using PrinterMate.Core;

namespace PrinterMate.App.ViewModels;

/// <summary>一行安装步骤(步骤名 / 状态 / 命令 / 返回码 / 输出),用于实时展示执行过程。</summary>
public sealed class StepRow
{
    public StepRow(InstallStepResult result)
    {
        StepText = result.StepText;
        StateText = result.State switch
        {
            InstallStepState.Succeeded => "成功",
            InstallStepState.Failed => "失败",
            InstallStepState.Skipped => "跳过",
            InstallStepState.Running => "执行中",
            _ => "等待",
        };

        IsFailed = result.State == InstallStepState.Failed;
        Message = result.Message;
        Command = result.Command;
        ExitCodeText = result.ExitCode.ToString();
        Output = result.Output;
        DurationText = result.Duration > TimeSpan.Zero ? $"{result.Duration.TotalSeconds:F1} 秒" : "-";
    }

    public string StepText { get; }

    public string StateText { get; }

    public bool IsFailed { get; }

    public string Message { get; }

    public string Command { get; }

    public string ExitCodeText { get; }

    public string Output { get; }

    public string DurationText { get; }

    public string CommandText => string.IsNullOrWhiteSpace(Command) ? "-" : Command;

    public string OutputText => string.IsNullOrWhiteSpace(Output) ? string.Empty : Output;
}
