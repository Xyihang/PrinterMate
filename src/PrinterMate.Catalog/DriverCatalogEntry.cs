using System.Text.Json.Serialization;

namespace PrinterMate.Catalog;

/// <summary>页面描述语言。ClassDriver 表示系统内置类驱动(无需分发驱动文件)。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PrintDescriptionLanguage>))]
public enum PrintDescriptionLanguage
{
    /// <summary>未指定。作为默认值,避免漏填 pdl 的条目被误判为系统内置类驱动。</summary>
    None = 0,

    ClassDriver,
    GDI,
    PCL6,
    PS,
}

/// <summary>驱动包按架构分列的 SHA256。空字符串代表该架构尚未入库。</summary>
public sealed record DriverPackageHashes
{
    [JsonPropertyName("x64")]
    public string? X64 { get; init; }

    [JsonPropertyName("arm64")]
    public string? Arm64 { get; init; }

    public string? For(string arch) => arch.Equals("arm64", StringComparison.OrdinalIgnoreCase) ? Arm64 : X64;
}

/// <summary>内置驱动库中的一条记录,对应 Catalog/drivers.json 的 entries 元素。</summary>
public sealed class DriverCatalogEntry
{
    [JsonPropertyName("brand")]
    public string Brand { get; init; } = string.Empty;

    [JsonPropertyName("family")]
    public string Family { get; init; } = string.Empty;

    [JsonPropertyName("pdl")]
    public PrintDescriptionLanguage Pdl { get; init; }

    [JsonPropertyName("driverName")]
    public string DriverName { get; init; } = string.Empty;

    [JsonPropertyName("infRelPath")]
    public string InfRelPath { get; init; } = string.Empty;

    [JsonPropertyName("arch")]
    public IReadOnlyList<string> Arch { get; init; } = Array.Empty<string>();

    [JsonPropertyName("os")]
    public IReadOnlyList<string> Os { get; init; } = Array.Empty<string>();

    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("signerSubject")]
    public string SignerSubject { get; init; } = string.Empty;

    /// <summary>是否已完成再分发授权核对。false 时禁止从本地磁盘安装该驱动。</summary>
    [JsonPropertyName("redistributable")]
    public bool Redistributable { get; init; }

    [JsonPropertyName("modelPatterns")]
    public IReadOnlyList<string> ModelPatterns { get; init; } = Array.Empty<string>();

    [JsonPropertyName("hardwareIdPatterns")]
    public IReadOnlyList<string> HardwareIdPatterns { get; init; } = Array.Empty<string>();

    [JsonPropertyName("sha256")]
    public DriverPackageHashes Sha256 { get; init; } = new();

    [JsonPropertyName("priority")]
    public int Priority { get; init; } = 100;

    /// <summary>是否为系统内置类驱动,不需要任何驱动文件。</summary>
    public bool IsInboxDriver => Pdl == PrintDescriptionLanguage.ClassDriver;

    public override string ToString() => $"{Brand} {Family} [{Pdl}] {DriverName}";
}

/// <summary>drivers.json 的根对象。</summary>
public sealed class DriverManifest
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = "1.0";

    [JsonPropertyName("libraryVersion")]
    public string LibraryVersion { get; set; } = string.Empty;

    [JsonPropertyName("notes")]
    public string Notes { get; init; } = string.Empty;

    [JsonPropertyName("entries")]
    public List<DriverCatalogEntry> Entries { get; init; } = new();
}
