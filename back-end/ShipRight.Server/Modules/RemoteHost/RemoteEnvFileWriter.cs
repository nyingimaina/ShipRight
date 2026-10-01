using System.Text;

namespace ShipRight.Modules.RemoteHost;

/// <summary>
/// Builds a command that writes a .env file on a remote host. Content is base64-encoded
/// so no quoting/escaping issues arise and the secret never appears in plain text in the
/// command string (or in any log of it).
/// </summary>
public static class RemoteEnvFileWriter
{
    public static string BuildWriteEnvFileCommand(string workingDir, string content)
    {
        var normalised = content.Replace("\r\n", "\n");
        if (!normalised.EndsWith('\n')) normalised += "\n";
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(normalised));
        var dir = workingDir.TrimEnd('/');
        return $"mkdir -p '{dir}' && umask 077 && echo '{b64}' | base64 -d > '{dir}/.env' && chmod 600 '{dir}/.env'";
    }
}
