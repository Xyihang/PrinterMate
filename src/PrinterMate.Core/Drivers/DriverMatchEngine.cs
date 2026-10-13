using System.Runtime.InteropServices;
using PrinterMate.Catalog;
using PrinterMate.Core.Infrastructure;

namespace PrinterMate.Core.Drivers;

/// <summary>L0 免驱通道所需的系统类驱动名称。</summary>
public static class InboxClassDrivers
{
    public const string Ipp = "Microsoft IPP Class Driver";
    public const string Ps = "Microsoft PS Class Driver";
    public const string Xps = "Microsoft XPS Class Driver";
    public const string Pdf = "Microsoft Print To PDF";
    public const string UniversalPrint = "Microsoft Universal Print Class Driver";
}

/// <summary>
/// 驱动匹配引擎(L0 → L1 → L2 分层决策)。
/// 这是本产品的核心:先判断免驱,再用内置 UPD,最后才考虑在线补齐。
/// </summary>
public sealed class DriverMatchEngine
{
    private readonly DriverLibrary _library;
    private readonly WppModeDetector _wpp;

    public DriverMatchEngine(DriverLibrary library, WppModeDetector? wpp = null)
    {
        _library = library;
        _wpp = wpp ?? new WppModeDetector();
    }

    /// <summary>当前进程架构标签:x64 / arm64 / x86。</summary>
    public static string CurrentArchitecture => RuntimeInformation.OSArchitecture switch
    {
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "x86",
        _ => "x64",
    };

    /// <summary>当前系统标签:win11 / win10。</summary>
    public static string CurrentOsTag => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) ? "win11" : "win10";

    public DriverLibrary Library => _library;

    /// <summary>按 L0 → L1 → L2 顺序给出驱动决策。</summary>
    public DriverDecision Decide(DiscoveredPrinter printer, bool preferDriverFree = true)
    {
        var wpp = _wpp.Detect();
        var candidates = new List<DriverCandidate>();

        // ── L0:系统免驱 ─────────────────────────────
        var classDriver = BuildClassDriverCandidate(printer);
        if (classDriver is not null)
        {
            candidates.Add(classDriver);
        }

        // WPP 模式下系统拒绝第三方驱动,只保留 L0。
        if (wpp.Enabled)
        {
            var selected = candidates.FirstOrDefault(c => c.Installable);
            var message = selected is not null
                ? "系统已启用 Windows Protected Print Mode,自动切换为 IPP 免驱模式。"
                : "系统已启用 Windows Protected Print Mode,且该设备不支持免驱,无法安装第三方驱动。";
            var suggestion = selected is not null
                ? "免驱模式下可正常打印,但部分高级功能(装订、双面等)不可用。"
                : "请改用支持 Mopria / IPP Everywhere 的打印机,或在企业策略中关闭 WPP 后重试。";

            return new DriverDecision(
                selected is not null ? OperationStatus.Success : OperationStatus.NotSupported,
                selected is not null ? "L0" : "-",
                selected,
                candidates,
                message,
                suggestion,
                RequiresDownload: false);
        }

        if (classDriver is not null && preferDriverFree && (printer.IsMopria || printer.SupportsIpp))
        {
            var selected = candidates.First(c => ReferenceEquals(c, classDriver));
            return new DriverDecision(
                OperationStatus.Success,
                "L0",
                selected,
                candidates,
                $"设备支持 IPP / Mopria,使用系统内置的「{selected.DriverName}」建立队列,无需安装任何驱动。",
                "若打印出现排版异常,可在驱动管理页手动改用厂商通用驱动。",
                RequiresDownload: false);
        }

        // ── L1:内置 UPD ─────────────────────────────
        var matches = CatalogMatcher.Match(_library, printer.ToMatchInput());
        var builtin = new List<(CatalogMatch Match, DriverCandidate Candidate)>();

        foreach (var match in matches)
        {
            var availability = _library.CheckAvailability(match.Entry, CurrentArchitecture, CurrentOsTag);
            var blocked = availability.Available ? null : availability.Reason;

            builtin.Add((match, new DriverCandidate(
                match.Entry.DriverName,
                DriverSource.BuiltinUpd,
                match.Entry.Brand,
                match.Entry.Family,
                match.Entry.Pdl,
                availability.InfPath,
                availability.Sha256,
                match.Confidence,
                match.Reason,
                availability.Available,
                blocked)));
        }

        candidates.AddRange(builtin.Select(b => b.Candidate));

        var installable = builtin.Where(b => b.Candidate.Installable).ToList();
        if (installable.Count > 0)
        {
            // 规格:同品牌 PCL6/PS 并存时默认选 PCL6;只有跨家族并列时才让用户人工选择。
            var ordered = installable
                .OrderByDescending(b => b.Match.Score)
                .ThenBy(b => PdlRank(b.Candidate.Pdl))
                .ThenByDescending(b => b.Match.Entry.Priority)
                .ToList();

            var topScore = ordered[0].Match.Score;
            var topGroup = ordered.Where(b => b.Match.Score == topScore).ToList();
            var confident = ordered[0].Match.Confidence == MatchConfidence.High;
            var families = topGroup.Select(b => b.Candidate.Family).Distinct(StringComparer.OrdinalIgnoreCase).Count();

            if (confident && families == 1)
            {
                var chosen = ordered[0].Candidate;
                return new DriverDecision(
                    OperationStatus.Success,
                    "L1",
                    chosen,
                    candidates,
                    $"已从内置驱动库匹配到「{chosen.DriverName}」({chosen.PdlText}),可离线安装,无需联网。",
                    "安装前会校验驱动数字签名,请在弹出的提权确认框中选择「是」。",
                    RequiresDownload: false);
            }

            return new DriverDecision(
                OperationStatus.NeedsUserAction,
                "L1",
                null,
                candidates,
                "匹配到多个不同家族的驱动,需要你确认使用哪一个。",
                "通常优先选择 PCL6 通用驱动;若打印出现字体缺失或乱码,可改用 PostScript 驱动。",
                RequiresDownload: false);
        }

        // ── L2:在线补齐 ─────────────────────────────
        // 规格:同品牌 PCL6/PS 并存时默认选 PCL6,不能被 priority 覆盖。
        var bestBuiltin = builtin
            .OrderByDescending(b => b.Match.Score)
            .ThenBy(b => PdlRank(b.Candidate.Pdl))
            .ThenByDescending(b => b.Match.Entry.Priority)
            .FirstOrDefault();

        if (builtin.Count > 0)
        {
            var entry = bestBuiltin.Match.Entry;
            var onlineCandidate = new DriverCandidate(
                entry.DriverName,
                DriverSource.Online,
                entry.Brand,
                entry.Family,
                entry.Pdl,
                null,
                null,
                bestBuiltin.Match.Confidence,
                $"{bestBuiltin.Match.Reason};内置驱动包不可用:{bestBuiltin.Candidate.BlockReason}",
                true,
                null);

            candidates.Add(onlineCandidate);

            return new DriverDecision(
                OperationStatus.Success,
                "L2",
                onlineCandidate,
                candidates,
                $"内置驱动库无法直接使用「{entry.Brand} {entry.Family}」,需要从厂商官方源在线获取。",
                "下载前会校验 HTTPS 证书、SHA256 与数字签名,任一不通过都会拒绝安装。",
                RequiresDownload: true);
        }

        // ── 完全未识别 ──────────────────────────────
        // 注意:仅当类驱动对该设备真的可用(设备具备 IPP/WSD 能力)时才降级到 L0;
        // 只有 USB 能力的老设备不能"假装"能用 IPP 类驱动。
        if (classDriver is { Installable: true })
        {
            return new DriverDecision(
                OperationStatus.Success,
                "L0",
                classDriver,
                candidates,
                "未能识别具体型号,但设备可通过系统内置类驱动以通用方式打印。",
                "通用类驱动可能不支持设备全部功能,建议手动选择正确型号后重试。",
                RequiresDownload: false);
        }

        return DriverDecision.Unresolved(
            string.IsNullOrWhiteSpace(printer.Model)
                ? "无法识别该打印机的型号与厂商,内置驱动库中没有可用候选。"
                : $"内置驱动库中未找到匹配「{printer.Model}」的驱动。",
            "请在驱动管理页手动选择品牌与型号,或使用厂商官方安装包。",
            candidates);
    }

    /// <summary>PDL 优先级:PCL6 优于 PS(规格:同品牌并存时默认 PCL6)。</summary>
    private static int PdlRank(PrintDescriptionLanguage? pdl) => pdl switch
    {
        PrintDescriptionLanguage.PCL6 => 0,
        PrintDescriptionLanguage.PS => 1,
        PrintDescriptionLanguage.GDI => 2,
        PrintDescriptionLanguage.ClassDriver => 3,
        _ => 4,
    };

    /// <summary>构造 L0 免驱候选。</summary>
    private DriverCandidate? BuildClassDriverCandidate(DiscoveredPrinter printer)
    {
        var entry = _library.Entries.FirstOrDefault(e => e.Pdl == PrintDescriptionLanguage.ClassDriver
            && string.Equals(e.DriverName, InboxClassDrivers.Ipp, StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            return null;
        }

        // 只有设备明确支持 IPP 才能用类驱动,USB Only 的老设备不适用。
        var usable = printer.IsMopria || printer.SupportsIpp || printer.Connection is ConnectionKind.Wsd or ConnectionKind.LocalQueue;
        var blockReason = usable ? null : "该设备未上报 IPP / Mopria 能力,系统 IPP 类驱动不适用。";

        return new DriverCandidate(
            entry.DriverName,
            DriverSource.ClassDriver,
            entry.Brand,
            entry.Family,
            entry.Pdl,
            null,
            null,
            printer.IsMopria ? MatchConfidence.High : MatchConfidence.Medium,
            printer.IsMopria ? "设备上报支持 Mopria / IPP Everywhere,可免驱直连。" : "可通过系统 IPP 类驱动免驱使用。",
            usable,
            blockReason);
    }

    /// <summary>列出内置驱动库中全部品牌,供人工选择驱动时使用。</summary>
    public IReadOnlyList<DriverCandidate> ListManualCandidates(string? brand = null, string? modelKeyword = null)
    {
        var result = new List<DriverCandidate>();

        foreach (var entry in _library.Entries)
        {
            if (brand is not null && !string.Equals(entry.Brand, brand, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (modelKeyword is not null && !entry.DriverName.Contains(modelKeyword, StringComparison.OrdinalIgnoreCase)
                && !entry.Family.Contains(modelKeyword, StringComparison.OrdinalIgnoreCase)
                && !entry.ModelPatterns.Any(p => p.Contains(modelKeyword, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var availability = _library.CheckAvailability(entry, CurrentArchitecture, CurrentOsTag);
            result.Add(new DriverCandidate(
                entry.DriverName,
                entry.Pdl == PrintDescriptionLanguage.ClassDriver ? DriverSource.ClassDriver : DriverSource.BuiltinUpd,
                entry.Brand,
                entry.Family,
                entry.Pdl,
                availability.InfPath,
                availability.Sha256,
                MatchConfidence.High,
                "人工选择",
                availability.Available,
                availability.Available ? null : availability.Reason));
        }

        return result.OrderBy(c => c.Brand, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.DriverName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
