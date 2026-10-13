using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PrinterMate.Interop;

/// <summary>把 Win32 错误码翻译成可直接展示给用户的中文说明。</summary>
public static class NativeError
{
    public static int LastError() => Marshal.GetLastWin32Error();

    public static string Describe(int code) => code switch
    {
        0 => "操作成功。",
        5 => "拒绝访问:需要管理员权限,或当前账户无权操作该打印机。",
        6 => "句柄无效。",
        8 => "内存不足。",
        13 => "数据无效。",
        14 => "内存不足。",
        87 => "参数错误。",
        122 => "缓冲区不足(API 需要更大的缓冲区)。",
        123 => "文件名或扩展名语法错误。",
        267 => "目录名无效。",
        1150 => "打印处理器未知。",
        1801 => "打印机名称无效。",
        1804 => "指定的数据类型无效。",
        1805 => "指定的打印监视器无效。",
        1905 => "打印机已被删除。",
        1906 => "打印机状态无效。",
        1907 => "用户必须在打印前更改密码。",
        1908 => "找不到该打印机的驱动程序。",
        1909 => "打印机当前处于错误状态。",
        1910 => "找不到打印机端口。",
        1911 => "找不到打印机。",
        1912 => "找不到打印后台处理程序。",
        1913 => "打印作业被取消。",
        1914 => "打印作业已暂停。",
        1915 => "打印作业正在删除。",
        1916 => "打印作业已完成。",
        1917 => "打印作业正在后台处理。",
        1918 => "打印作业正在打印。",
        1919 => "打印作业已脱机。",
        3003 => "打印队列已满。",
        3004 => "打印机正在打印。",
        _ => new Win32Exception(code).Message,
    };

    public static string DescribeLastError() => Describe(LastError());

    public static InvalidOperationException Exception(string action, string? target = null, int? code = null)
    {
        var c = code ?? LastError();
        var where = string.IsNullOrWhiteSpace(target) ? string.Empty : $"[{target}]";
        return new InvalidOperationException($"{action}失败{where}:{Describe(c)}(错误码 {c})");
    }
}
