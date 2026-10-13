using System.Collections;
using System.ComponentModel;

namespace PrinterMate.Core.Discovery;

/// <summary>IPP Get-Printer-Attributes 的结果(与 PrinterMate.Printing.IppDeviceInfo 字段一一对应)。</summary>
public sealed record IppDeviceAttributes(
    string Uri,
    string MakeAndModel,
    string Manufacturer,
    string PrinterState,
    IReadOnlyList<string> DocumentFormats,
    IReadOnlyList<string> UrfSupported,
    string Uuid,
    bool SupportsIppEverywhere,
    string Info);

/// <summary>
/// IPP 属性来源抽象。发现层不直接依赖 PrinterMate.Printing,便于在打印模块尚未就绪时解耦与单元测试。
/// </summary>
public interface IIppAttributeSource
{
    /// <summary>读取 IPP 属性;不可用时返回 null(不得抛异常)。</summary>
    Task<IppDeviceAttributes?> GetAttributesAsync(Uri ippUri, CancellationToken ct);
}

/// <summary>
/// 默认实现:通过反射桥接到 PrinterMate.Printing.IppClient。
/// 之所以用反射,是因为 IppClient 由打印模块独立交付,发现模块必须能先独立编译;
/// 一旦 IppClient 落地且构建稳定,应改为直接引用并删除本类中的反射代码。
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class PrintingIppAttributeSource : IIppAttributeSource
{
    private const string AssemblyName = "PrinterMate.Printing";
    private const string TypeName = "PrinterMate.Printing.IppClient";

    private readonly object? _client;

    public PrintingIppAttributeSource(TimeSpan? timeout = null)
    {
        _client = CreateClient(timeout);
    }

    /// <summary>PrinterMate.Printing.IppClient 是否存在(不存在时探测自动降级为纯端口探测)。</summary>
    public static bool IsAvailable => ResolveClientType() is not null;

    /// <inheritdoc />
    public async Task<IppDeviceAttributes?> GetAttributesAsync(Uri ippUri, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ippUri);

        var client = _client;
        if (client is null)
        {
            return null;
        }

        try
        {
            var method = client.GetType().GetMethod("GetAttributesAsync", [typeof(Uri), typeof(CancellationToken)]);
            if (method is null)
            {
                return null;
            }

            if (method.Invoke(client, [ippUri, ct]) is not Task task)
            {
                return null;
            }

            await task.ConfigureAwait(false);

            var result = task.GetType().GetProperty("Result")?.GetValue(task);
            return result is null ? null : Map(result);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static object? CreateClient(TimeSpan? timeout)
    {
        var type = ResolveClientType();
        if (type is null)
        {
            return null;
        }

        try
        {
            var constructor = type.GetConstructor([typeof(TimeSpan?)]) ?? type.GetConstructor(Type.EmptyTypes);
            return constructor?.Invoke(constructor.GetParameters().Length == 0 ? null : [timeout]);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Type? ResolveClientType()
    {
        var type = Type.GetType($"{TypeName}, {AssemblyName}", throwOnError: false);
        if (type is not null)
        {
            return type;
        }

        try
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!string.Equals(assembly.GetName().Name, AssemblyName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                type = assembly.GetType(TypeName, throwOnError: false);
                if (type is not null)
                {
                    return type;
                }
            }
        }
        catch (Exception)
        {
            // 反射枚举失败时视为不可用。
        }

        return null;
    }

    private static IppDeviceAttributes Map(object info)
    {
        var type = info.GetType();

        string GetText(string name) => type.GetProperty(name)?.GetValue(info) as string ?? string.Empty;

        IReadOnlyList<string> GetList(string name)
        {
            var value = type.GetProperty(name)?.GetValue(info);
            if (value is null || value is string)
            {
                return [];
            }

            if (value is not IEnumerable enumerable)
            {
                return [];
            }

            var items = new List<string>();
            foreach (var item in enumerable)
            {
                if (item?.ToString() is { Length: > 0 } text)
                {
                    items.Add(text);
                }
            }

            return items;
        }

        var flag = type.GetProperty("SupportsIppEverywhere")?.GetValue(info) as bool? ?? false;

        return new IppDeviceAttributes(
            GetText("Uri"),
            GetText("MakeAndModel"),
            GetText("Manufacturer"),
            GetText("PrinterState"),
            GetList("DocumentFormats"),
            GetList("UrfSupported"),
            GetText("Uuid"),
            flag,
            GetText("Info"));
    }
}
