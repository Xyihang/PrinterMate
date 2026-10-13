using System.IO;
using System.Text;

namespace PrinterMate.App.Services;

/// <summary>界面层日志。任何写入失败都不得影响主流程。</summary>
public static class AppLog
{
    private static readonly object Gate = new();

    public static string Directory
    {
        get
        {
            try
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrWhiteSpace(local))
                {
                    local = Path.GetTempPath();
                }

                return Path.Combine(local, "PrinterMate", "logs");
            }
            catch (Exception)
            {
                return Path.GetTempPath();
            }
        }
    }

    public static void Write(string category, string message)
    {
        try
        {
            var directory = Directory;
            System.IO.Directory.CreateDirectory(directory);
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{category}] {message}";
            lock (Gate)
            {
                File.AppendAllText(
                    Path.Combine(directory, $"ui-{DateTime.Now:yyyyMMdd}.log"),
                    line + Environment.NewLine,
                    new UTF8Encoding(false));
            }
        }
        catch (Exception)
        {
            // 日志写入失败时静默忽略,避免二次异常。
        }
    }
}
