using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using PrinterMate.Core.Elevation;
using PrinterMate.Core.Infrastructure;

namespace PrinterMate.Core.Elevation;

/// <summary>
/// 提权辅助进程的启动器。主程序始终保持普通用户权限,
/// 只有驱动安装/卸载这些必须提权的操作才通过 ShellExecute(runas) 拉起辅助进程。
/// </summary>
public sealed class ElevatedLauncher
{
    private const int ErrorCancelled = 1223;

    private readonly TimeSpan _timeout;

    public ElevatedLauncher(TimeSpan? timeout = null)
    {
        _timeout = timeout ?? TimeSpan.FromMinutes(10);
    }

    /// <summary>辅助进程可执行文件路径(与主程序同目录的 elevated 子目录)。</summary>
    public static string HelperPath => Path.Combine(AppContext.BaseDirectory, "elevated", "PrinterMate.Elevated.exe");

    public static bool IsHelperAvailable => File.Exists(HelperPath);

    /// <summary>当前进程是否已经具备管理员权限。</summary>
    public static bool IsProcessElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public OperationResult<ElevatedInstallResponse> Execute(ElevatedInstallRequest request)
    {
        if (!IsHelperAvailable)
        {
            return OperationResult<ElevatedInstallResponse>.Fail(
                $"未找到提权辅助进程:{HelperPath}",
                "请重新安装 PrinterMate,或确认安装目录完整。",
                OperationStatus.NotSupported);
        }

        var workDir = Path.Combine(Path.GetTempPath(), "PrinterMate");
        Directory.CreateDirectory(workDir);

        var tag = Guid.NewGuid().ToString("N");
        var requestPath = Path.Combine(workDir, $"req-{tag}.json");
        var resultPath = Path.Combine(workDir, $"res-{tag}.json");

        try
        {
            File.WriteAllText(requestPath, ElevatedJson.Serialize(request), new UTF8Encoding(false));
            if (File.Exists(resultPath))
            {
                File.Delete(resultPath);
            }
        }
        catch (Exception ex)
        {
            return ex.ToFailure<ElevatedInstallResponse>("准备提权请求");
        }

        var psi = new ProcessStartInfo
        {
            FileName = HelperPath,
            // UseShellExecute + runas 是唯一的提权方式,不做任何 UAC 绕过。
            UseShellExecute = true,
            Verb = "runas",
            Arguments = $"--request \"{requestPath}\" --result \"{resultPath}\"",
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return OperationResult<ElevatedInstallResponse>.Fail(
                    "无法启动提权辅助进程。",
                    "请确认系统 UAC 服务正常,并手动以管理员身份运行安装包。",
                    OperationStatus.NotPermitted);
            }

            var exited = process.WaitForExit((int)_timeout.TotalMilliseconds);
            if (!exited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                    // 已自行退出
                }

                return OperationResult<ElevatedInstallResponse>.Fail(
                    $"提权安装超过 {_timeout.TotalMinutes:F0} 分钟未完成,已终止。",
                    "请打开「驱动管理」确认驱动是否已入库,或查看系统事件日志中的驱动安装记录。");
            }

            var response = ElevatedJson.DeserializeFile<ElevatedInstallResponse>(resultPath);
            if (response is null)
            {
                return OperationResult<ElevatedInstallResponse>.Fail(
                    $"提权辅助进程未返回结果(退出码 {process.ExitCode})。",
                    "请检查是否被安全软件拦截,或导出诊断报告后反馈。");
            }

            return response.Success
                ? OperationResult<ElevatedInstallResponse>.Ok(response, response.Summary)
                : OperationResult<ElevatedInstallResponse>.Fail(
                    string.IsNullOrWhiteSpace(response.Error) ? response.Summary : response.Error,
                    "请根据上一步的错误信息处理;若为权限或策略限制,请改用免驱通道。");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return OperationResult<ElevatedInstallResponse>.Fail(
                "你取消了管理员权限请求,安装未执行。",
                "如需安装驱动,请在弹出确认框时选择「是」;也可以改用免驱通道直接连接。",
                OperationStatus.NeedsUserAction);
        }
        catch (Exception ex)
        {
            return ex.ToFailure<ElevatedInstallResponse>("启动提权辅助进程");
        }
        finally
        {
            TryDelete(requestPath);
            TryDelete(resultPath);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // 临时文件清理失败不影响主流程。
        }
    }
}
