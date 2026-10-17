using System.Text.RegularExpressions;

namespace YetAnotherGameLauncher.Core.Utilities;

/// <summary>分卷集合形态分类（<see cref="SplitVolumeNaming.Classify"/> 的结果）。</summary>
public enum SplitVolumeSet
{
    /// <summary>无序号形态：条目各自独立（如多条独立 .zip）。</summary>
    NotVolumes,

    /// <summary>同基名且序号恰为 1..N 的一套分卷。</summary>
    Ordered,

    /// <summary>像分卷但序号不连续/多基名：服务器数据自相矛盾（拼接结果必定不是合法归档）。</summary>
    Broken,
}

/// <summary>
/// 分卷命名规则（终末地 packs 与 v2 载荷实测形态，2026-10-17）：<c>基名.三位以上序号</c>。
/// 渠道（下载前识别/排序/拒收）与安装器（旧暂存清单复算，见 PackageInstallerService）共用同一实现，
/// 避免两份规则各自漂移。
/// </summary>
public static partial class SplitVolumeNaming
{
    /// <summary>
    /// 识别分卷集合：全部条目同基名且序号恰为 1..N 时返回按序号升序的下标；
    /// 完全无序号形态 = 各自独立；像分卷但不连续/多基名 = 数据自相矛盾。
    /// </summary>
    public static (SplitVolumeSet Kind, int[] Order) Classify(IReadOnlyList<string> names)
    {
        if (names.Count == 0)
        {
            return (SplitVolumeSet.NotVolumes, []);
        }

        var volumes = new List<(string BaseName, int Number, int Index)>(names.Count);
        for (var i = 0; i < names.Count; i++)
        {
            var match = SplitVolumeNameRegex().Match(names[i]);
            if (!match.Success || !int.TryParse(match.Groups["number"].Value, out var number))
            {
                return (SplitVolumeSet.NotVolumes, []);
            }

            volumes.Add((match.Groups["baseName"].Value, number, i));
        }

        var baseNames = volumes.Select(v => v.BaseName).Distinct(StringComparer.Ordinal).ToArray();
        var ordered = volumes.Select(v => v.Number).Order().ToArray();
        if (baseNames.Length != 1 || !ordered.SequenceEqual(Enumerable.Range(1, volumes.Count)))
        {
            return (SplitVolumeSet.Broken, []);
        }

        volumes.Sort(static (left, right) => left.Number.CompareTo(right.Number));
        return (SplitVolumeSet.Ordered, [.. volumes.Select(v => v.Index)]);
    }

    /// <summary>分卷文件名形态：<c>基名.三位以上序号</c>（如 Beyond_…official.zip.001）。</summary>
    [GeneratedRegex(@"^(?<baseName>.+)\.(?<number>\d{3,})$")]
    private static partial Regex SplitVolumeNameRegex();
}
