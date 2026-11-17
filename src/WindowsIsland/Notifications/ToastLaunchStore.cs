using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace WindowsIsland;

internal sealed record ToastLaunch(string AppId, string Launch, string ActivationType, string Title, string Body);

/// <summary>
/// Reads a notification's activation payload from Windows' wpndatabase.db.
///
/// Why: UserNotificationListener only exposes title/body, not the toast's launch
/// arguments. wpndatabase.db keeps the full toast XML in Notification.Payload,
/// which includes launch + activationType (e.g. QQ uses
/// "ntqq-notification://externalCommand?..." with activationType=protocol).
///
/// How we read it:
///  - the DB is held by the WPN service, so we copy it (plus -wal/-shm) to temp first;
///  - we query through the inbox winsqlite3.dll (present since Win10 1709),
///    so no NuGet dependency is needed.
/// </summary>
internal static class ToastLaunchStore
{
    private static readonly object Gate = new();
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(3);
    private static DateTime _loadedAt = DateTime.MinValue;
    private static List<ToastLaunch> _cache = new();

    public static ToastLaunch? Find(string appId, string title, string body)
    {
        var items = Load();

        if (items.Count == 0)
        {
            return null;
        }

        var sameApp = items
            .Where(i => string.Equals(i.AppId, appId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (sameApp.Count == 0)
        {
            return null;
        }

        // exact title first, then "contains", then body
        var match = sameApp.FirstOrDefault(i => string.Equals(i.Title, title, StringComparison.Ordinal))
                    ?? sameApp.FirstOrDefault(i => !string.IsNullOrEmpty(title)
                                                   && (i.Title.Contains(title, StringComparison.Ordinal)
                                                       || title.Contains(i.Title, StringComparison.Ordinal)))
                    ?? sameApp.FirstOrDefault(i => !string.IsNullOrEmpty(body)
                                                   && i.Body.Contains(body, StringComparison.Ordinal));

        return match;
    }

    /// <summary>Lists recent entries (diagnostics only).</summary>
    public static List<string> DebugDump(int max)
    {
        return Load()
            .Take(max)
            .Select(i => $"{i.AppId} | type={i.ActivationType} | title={i.Title} | launch={i.Launch}")
            .ToList();
    }

    /// <summary>Activates a notification using its own launch payload when possible.</summary>
    public static bool TryActivate(IslandNotification notification)
    {
        var entry = Find(notification.Aumid, notification.Title, notification.Body);

        if (entry is null || entry.Launch.Length == 0)
        {
            return false;
        }

        try
        {
            // protocol activation: the launch value is a URI the shell can hand to the app
            if (entry.ActivationType.Contains("protocol", StringComparison.OrdinalIgnoreCase)
                || entry.Launch.Contains("://", StringComparison.Ordinal))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = entry.Launch,
                    UseShellExecute = true
                });

                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static List<ToastLaunch> Load()
    {
        lock (Gate)
        {
            if (DateTime.UtcNow - _loadedAt < CacheLifetime)
            {
                return _cache;
            }

            _cache = ReadAll();
            _loadedAt = DateTime.UtcNow;
            return _cache;
        }
    }

    private static List<ToastLaunch> ReadAll()
    {
        var result = new List<ToastLaunch>();

        try
        {
            var source = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "Windows", "Notifications", "wpndatabase.db");

            if (!File.Exists(source))
            {
                return result;
            }

            var work = Path.Combine(Path.GetTempPath(), "island-wpn");
            Directory.CreateDirectory(work);

            // copy the WAL as well, otherwise the newest rows may be missing
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                try
                {
                    var from = source + suffix;
                    var to = Path.Combine(work, "wpndatabase.db" + suffix);

                    if (File.Exists(from))
                    {
                        File.Copy(from, to, overwrite: true);
                    }
                }
                catch
                {
                    // keep going with whatever we have
                }
            }

            var dbPath = Path.Combine(work, "wpndatabase.db");
            if (SqlOpen(dbPath, out var db) != 0)
            {
                return result;
            }

            try
            {
                const string Sql =
                    "SELECT h.PrimaryId, n.Payload FROM Notification n " +
                    "JOIN NotificationHandler h ON h.RecordId = n.HandlerId " +
                    "ORDER BY n.ArrivalTime DESC LIMIT 60";

                if (SqlPrepare(db, Sql, out var stmt) != 0)
                {
                    return result;
                }

                try
                {
                    while (SqlStep(stmt) == 100)
                    {
                        try
                        {
                            var appId = SqlText(stmt, 0);
                            var payload = SqlText(stmt, 1);

                            if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(payload))
                            {
                                continue;
                            }

                            var parsed = ParsePayload(appId, payload);

                            if (parsed is not null)
                            {
                                result.Add(parsed);
                            }
                        }
                        catch
                        {
                            // one bad row must not break the rest
                        }
                    }
                }
                finally
                {
                    SqlFinalize(stmt);
                }
            }
            finally
            {
                SqlClose(db);
            }
        }
        catch
        {
            // treat any failure as "no data"
        }

        return result;
    }

    private static ToastLaunch? ParsePayload(string appId, string payload)
    {
        try
        {
            var doc = XDocument.Parse(payload);
            var root = doc.Root;

            if (root is null)
            {
                return null;
            }

            var launch = (string?)root.Attribute("launch") ?? string.Empty;
            var activationType = (string?)root.Attribute("activationType") ?? string.Empty;

            var texts = root.Descendants()
                .Where(e => e.Name.LocalName == "text")
                .Select(e => e.Value.Trim())
                .Where(v => v.Length > 0)
                .ToList();

            if (launch.Length == 0 && texts.Count == 0)
            {
                return null;
            }

            return new ToastLaunch(
                appId,
                launch,
                activationType,
                texts.ElementAtOrDefault(0) ?? string.Empty,
                texts.ElementAtOrDefault(1) ?? string.Empty);
        }
        catch
        {
            return null;
        }
    }

    // ---------------- minimal winsqlite3 wrapper ----------------

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_open_v2", CharSet = CharSet.Ansi)]
    private static extern int SqlOpenRaw(string path, out IntPtr db, int flags, IntPtr vfs);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_prepare_v2", CharSet = CharSet.Ansi)]
    private static extern int SqlPrepareRaw(IntPtr db, string sql, int byteCount, out IntPtr stmt, IntPtr tail);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_step")]
    private static extern int SqlStepRaw(IntPtr stmt);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_text")]
    private static extern IntPtr SqlColumnTextRaw(IntPtr stmt, int column);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_finalize")]
    private static extern int SqlFinalizeRaw(IntPtr stmt);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_close")]
    private static extern int SqlCloseRaw(IntPtr db);

    private static int SqlOpen(string path, out IntPtr db) => SqlOpenRaw(path, out db, 1, IntPtr.Zero);

    private static int SqlPrepare(IntPtr db, string sql, out IntPtr stmt)
        => SqlPrepareRaw(db, sql, -1, out stmt, IntPtr.Zero);

    private static int SqlStep(IntPtr stmt) => SqlStepRaw(stmt);

    private static string SqlText(IntPtr stmt, int column)
    {
        var pointer = SqlColumnTextRaw(stmt, column);
        return pointer == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(pointer) ?? string.Empty;
    }

    private static void SqlFinalize(IntPtr stmt) => SqlFinalizeRaw(stmt);

    private static void SqlClose(IntPtr db) => SqlCloseRaw(db);
}
