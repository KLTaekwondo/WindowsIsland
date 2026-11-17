using System.IO;
using System.Text.RegularExpressions;

namespace WindowsIsland;

public sealed class LrcLine
{
    public TimeSpan Time { get; init; }

    public string Text { get; init; } = string.Empty;
}

/// <summary>
/// 本地 LRC 歌词：在指定目录里按「文件名包含歌名」找 .lrc，解析时间轴后取当前行。
/// 完全离线，找不到就返回 null，界面继续显示歌手，不会影响其它功能。
/// </summary>
public static class LrcLyrics
{
    private static readonly Regex TimeTag = new(@"\[(\d{1,2}):(\d{1,2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);

    public static List<LrcLine>? TryLoad(string? title, string folder)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return null;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*.lrc", SearchOption.AllDirectories))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (!name.Contains(title.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var lines = Parse(file);
                if (lines.Count > 0)
                {
                    return lines;
                }
            }
        }
        catch
        {
            // 目录读不了就当作没有歌词
        }

        return null;
    }

    public static List<LrcLine> Parse(string path)
    {
        var result = new List<LrcLine>();

        foreach (var raw in File.ReadAllLines(path))
        {
            var matches = TimeTag.Matches(raw);
            if (matches.Count == 0)
            {
                continue;
            }

            var text = TimeTag.Replace(raw, string.Empty).Trim();
            if (text.Length == 0)
            {
                continue;
            }

            foreach (Match match in matches)
            {
                var minutes = int.Parse(match.Groups[1].Value);
                var seconds = int.Parse(match.Groups[2].Value);
                var fraction = match.Groups[3].Success ? match.Groups[3].Value : "0";

                var milliseconds = fraction.Length switch
                {
                    1 => int.Parse(fraction) * 100,
                    2 => int.Parse(fraction) * 10,
                    _ => int.Parse(fraction)
                };

                result.Add(new LrcLine
                {
                    Time = new TimeSpan(0, 0, minutes, seconds, milliseconds),
                    Text = text
                });
            }
        }

        result.Sort((a, b) => a.Time.CompareTo(b.Time));
        return result;
    }

    public static string? LineAt(List<LrcLine> lines, TimeSpan position)
    {
        string? current = null;

        foreach (var line in lines)
        {
            if (line.Time <= position)
            {
                current = line.Text;
            }
            else
            {
                break;
            }
        }

        return current;
    }
}
