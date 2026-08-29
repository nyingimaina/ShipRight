using System.Net;
using System.Net.Mail;

namespace ShipRight.Modules.Auth.Services;

public static class PasswordResetEmail
{
    public static string BuildResetLink(string publicUrl, string token)
    {
        if (!Uri.TryCreate(publicUrl, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Password reset public URL must use HTTPS.");

        return $"{publicUrl.TrimEnd('/')}/reset-password?token={Uri.EscapeDataString(token)}";
    }
}

public interface IPasswordResetEmailSender
{
    Task SendAsync(string recipient, string token, CancellationToken cancellationToken = default);
}

public sealed class SmtpPasswordResetEmailSender : IPasswordResetEmailSender
{
    private readonly string _host = Required("SHIPRIGHT__SMTP_HOST");
    private readonly int _port = int.TryParse(Environment.GetEnvironmentVariable("SHIPRIGHT__SMTP_PORT"), out var port) ? port : 587;
    private readonly string _username = Required("SHIPRIGHT__SMTP_USERNAME");
    private readonly string _password = Required("SHIPRIGHT__SMTP_PASSWORD");
    private readonly string _from = Required("SHIPRIGHT__SMTP_FROM");
    private readonly string _publicUrl = Required("SHIPRIGHT__PUBLIC_URL");

    public async Task SendAsync(string recipient, string token, CancellationToken cancellationToken = default)
    {
        using var message = new MailMessage(_from, recipient)
        {
            Subject = "Reset your ShipRight password",
            Body = $"Reset your password using this link:\n\n{PasswordResetEmail.BuildResetLink(_publicUrl, token)}\n\nThis link expires in one hour.",
        };
        using var client = new SmtpClient(_host, _port)
        {
            EnableSsl = true,
            Credentials = new NetworkCredential(_username, _password),
        };
        await client.SendMailAsync(message, cancellationToken);
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{name} is required for password reset email delivery.");
}
