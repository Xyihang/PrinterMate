using System.ComponentModel;
using System.Text.RegularExpressions;

namespace PrinterMate.Core.Discovery;

/// <summary>从设备自述文本(sysDescr / hrDeviceDescr / mDNS TXT)中识别打印机品牌与型号。纯函数,可离线测试。</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class PrinterBrandParser
{
    private sealed record BrandRule(string Brand, Regex Pattern);

    // 顺序即优先级:同一条自述里出现多个品牌关键词时,取更具体的(如 “Fuji Xerox” 优先于 “Xerox”)。
    private static readonly BrandRule[] Rules =
    [
        Rule("Konica Minolta", @"\bkonica\b|\bminolta\b|\bbizhub\b|\bmagicolor\b"),
        Rule("Fujifilm", @"\bfujifilm\b|\bfuji\s*xerox\b|\bfujixerox\b|\bxerox\b"),
        Rule("HP", @"\bhewlett\b|\bhp\b|\blaserjet\b|\bofficejet\b|\bdeskjet\b|\bdesignjet\b|\bpagewide\b|\benvy\b"),
        Rule("Canon", @"\bcanon\b|\bi-?sensys\b|\bimagerunner\b|\bpixma\b|\bselphy\b|\bimageclass\b|\blbp\b"),
        Rule("Epson", @"\bepson\b|\bworkforce\b|\becotank\b|\bstylus\b|\bsurecolor\b"),
        Rule("Brother", @"\bbrother\b|\bmfc[-\s]?\d|\bdcp[-\s]?\d|\bhl[-\s]?\d"),
        Rule("Kyocera", @"\bkyocera\b|\becosys\b|\btaskalfa\b|\bfs-c\d|\bfs-\d{3,4}"),
        Rule("Ricoh", @"\bricoh\b|\baficio\b|\bnashuatec\b|\bsavin\b|\bgestetner\b|\blanier\b"),
        Rule("Lexmark", @"\blexmark\b|\boptra\b"),
        Rule("Sharp", @"\bsharp\b|\bar-\d|\bmx-\d"),
        Rule("Toshiba", @"\btoshiba\b|\be-?studio\b"),
        Rule("OKI", @"\boki\b|\bokidata\b|\bmicroline\b"),
        Rule("Samsung", @"\bsamsung\b|\bscx[-\s]?\d|\bclx[-\s]?\d|\bxpress\b"),
        Rule("Pantum", @"\bpantum\b"),
        Rule("Lenovo", @"\blenovo\b"),
        Rule("Deli", @"\bdeli\b"),
        Rule("Jolimark", @"\bjolimark\b"),
    ];

    private static readonly Regex NoiseSuffix = new(@"[\r\n\t]+", RegexOptions.Compiled);

    private static BrandRule Rule(string brand, string pattern) =>
        new(brand, new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant));

    /// <summary>识别品牌规范名(如 "HP");无法识别时返回 null(Panasonic、通用 "Printer" 等)。</summary>
    public static string? Identify(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (var rule in Rules)
        {
            if (rule.Pattern.IsMatch(text))
            {
                return rule.Brand;
            }
        }

        return null;
    }

    /// <summary>在多个候选文本中按顺序识别品牌。</summary>
    public static string? IdentifyFrom(params string?[] texts)
    {
        foreach (var text in texts)
        {
            var brand = Identify(text);
            if (brand is not null)
            {
                return brand;
            }
        }

        return null;
    }

    /// <summary>从自述文本中提取型号:去掉品牌关键词及其之前的噪声,保留剩余描述。</summary>
    public static string ExtractModel(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var cleaned = NoiseSuffix.Replace(text, " ").Trim();

        foreach (var rule in Rules)
        {
            var match = rule.Pattern.Match(cleaned);
            if (!match.Success)
            {
                continue;
            }

            var tail = cleaned[(match.Index + match.Length)..].Trim(' ', '-', ',', ';', ':', '.', '/');
            if (tail.Length == 0)
            {
                break; // 自述里只有品牌,退回整串。
            }

            return Truncate(tail, 120);
        }

        return Truncate(cleaned, 120);
    }

    private static string Truncate(string value, int max)
    {
        var cut = value.IndexOfAny(['\r', '\n', ';', ',']);
        if (cut >= 0)
        {
            value = value[..cut].Trim();
        }

        return value.Length <= max ? value : value[..max].Trim();
    }
}
