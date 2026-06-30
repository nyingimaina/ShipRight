using System.Collections.Concurrent;

namespace ShipRight.Shared.SshRunner;

public static class SshSessionStore
{
    private static readonly ConcurrentDictionary<string, string> _cwd = new();

    public static string GetCwd(string sessionKey) =>
        _cwd.GetOrAdd(sessionKey, _ => "~");

    public static void SetCwd(string sessionKey, string path) =>
        _cwd[sessionKey] = string.IsNullOrEmpty(path) ? "~" : path;
}
