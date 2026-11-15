using System.Globalization;
using System.Text.RegularExpressions;

namespace ShipRight.Modules.Database;

/// <summary>A size said the way a person would say it: "900 bytes", "2.0 KB", "13.0 MB", "5.5 GB".</summary>
public static class ByteSize
{
    public static string Format(long bytes)
    {
        if (bytes < 1024) return $"{bytes} bytes";
        string[] units = ["KB", "MB", "GB", "TB"];
        var value = bytes / 1024.0;
        var i = 0;
        while (value >= 1024 && i < units.Length - 1) { value /= 1024; i++; }
        return $"{value.ToString("0.0", CultureInfo.InvariantCulture)} {units[i]}";
    }
}

/// <summary>
/// Turns a stream of "this many bytes so far" into a few readable updates: in steps (not every chunk), every few seconds when
/// slow, never the same percent twice, and always the end. Used for the upload of a script and for watching it run.
/// </summary>
public sealed class ProgressReporter
{
    private readonly string _stage;
    private readonly long _total;
    private readonly Func<DateTime> _now;
    private readonly Action<string, string, int, long, long> _emit;
    private readonly int _step;
    private readonly TimeSpan _interval;
    private readonly DateTime _started;
    private int _lastPercent = -1;
    private DateTime _lastEmit;

    /// <param name="emit">stage, message, percent, bytes done, bytes total.</param>
    public ProgressReporter(string stage, long total, Func<DateTime> now, Action<string, string, int, long, long> emit,
        int stepPercent = 5, int minIntervalSeconds = 2)
    {
        _stage = stage; _total = total; _now = now; _emit = emit;
        _step = stepPercent; _interval = TimeSpan.FromSeconds(minIntervalSeconds);
        _started = _lastEmit = now();
    }

    public void Report(long done)
    {
        var percent = _total <= 0 ? 100 : (int)Math.Min(100, done * 100 / _total);
        if (percent == _lastPercent) return;
        var now = _now();
        var due = percent == 100 || percent >= _lastPercent + _step || now - _lastEmit >= _interval;
        if (!due) return;

        _lastPercent = percent; _lastEmit = now;
        var seconds = Math.Max((now - _started).TotalSeconds, 0.001);
        var amount = $"{ByteSize.Format(done)} of {ByteSize.Format(_total)}";
        var message = _stage == "upload"
            ? $"Uploading… {percent}% ({amount}, {ByteSize.Format((long)(done / seconds))}/s)"
            : $"Running… {percent}% ({amount} of the script read)";
        _emit(_stage, message, percent, done, _total);
    }
}

/// <summary>
/// Watches a script while the database runs it. The client reads the script from a file on the server, so how far into that file it
/// has got (its read position, from /proc) is how far along the run is. Only commands that read the file that way can be watched;
/// any other command is left exactly as it was.
/// </summary>
public static class QueryProgress
{
    public const string Prefix = "[progress] ";
    private static readonly Regex PlainPath = new(@"^/[A-Za-z0-9._/-]+$", RegexOptions.Compiled);

    public static string Wrap(string command, string remoteFile)
    {
        if (!PlainPath.IsMatch(remoteFile)) throw new ArgumentException($"'{remoteFile}' is not a plain path.", nameof(remoteFile));
        if (!command.TrimEnd().EndsWith("< " + remoteFile, StringComparison.Ordinal)) return command;

        return $"{command.TrimEnd()} & p=$!; t=$(wc -c < {remoteFile} | tr -d ' '); " +
               "while kill -0 $p 2>/dev/null; do " +
               "pos=$(awk '/^pos:/ {print $2}' /proc/$p/fdinfo/0 2>/dev/null); " +
               $"[ -n \"$pos\" ] && echo \"{Prefix}$pos $t\" >&2; sleep 3; done; wait $p";
    }

    public static bool TryParse(string line, out long position, out long total)
    {
        position = total = 0;
        var t = (line ?? "").Trim();
        if (!t.StartsWith(Prefix.Trim(), StringComparison.Ordinal)) return false;
        var parts = t[Prefix.Trim().Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && long.TryParse(parts[0], out position) && long.TryParse(parts[1], out total);
    }
}
