using System.Net.Mail;

namespace ShipRight.Modules.Identity;

public record RegisterRequest(string? Email, string? Password, string? Name, string? CompanyName);
public record LoginRequest(string? Email, string? Password);
public record ForgotPasswordRequest(string? Email);
public record ResetPasswordRequest(string? Token, string? NewPassword);
public record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

public record CreateTeammateRequest(string? Email, string? Name, string? TemporaryPassword, string? Role);
public record ChangeRoleRequest(string? Role, string? ExpectedRole);

public record SessionUserDto(Guid Id, string Email, string Name, Guid CompanyId, string Role, bool MustChangePassword);
public record SessionDto(SessionUserDto User);
public record ProvidersDto(bool Google);

public record TeamMemberDto(Guid Id, string Email, string Name, string Role, bool IsActive, DateTime Created);
public record TeamPageDto(IReadOnlyList<TeamMemberDto> Items, int Total, int Page, int PageSize);

// Field errors for RFC 7807 validation problems; the key is the field name the screen shows the message beside.
public static class IdentityValidation
{
    public const int MinPasswordLength = 8;
    public const int MaxNameLength = 200;
    public const int MaxEmailLength = 255;

    public static Dictionary<string, string[]> Register(RegisterRequest r)
    {
        var errors = new Dictionary<string, string[]>();
        Email(errors, r.Email);
        Password(errors, "password", r.Password);
        Name(errors, "name", r.Name, required: true);
        Name(errors, "companyName", r.CompanyName, required: false);
        return errors;
    }

    public static Dictionary<string, string[]> Teammate(CreateTeammateRequest r)
    {
        var errors = new Dictionary<string, string[]>();
        Email(errors, r.Email);
        Name(errors, "name", r.Name, required: true);
        Password(errors, "temporaryPassword", r.TemporaryPassword);
        RoleField(errors, r.Role);
        return errors;
    }

    public static Dictionary<string, string[]> Role(ChangeRoleRequest r)
    {
        var errors = new Dictionary<string, string[]>();
        RoleField(errors, r.Role);
        return errors;
    }

    public static Dictionary<string, string[]> NewPassword(string field, string? value)
    {
        var errors = new Dictionary<string, string[]>();
        Password(errors, field, value);
        return errors;
    }

    public static string Trimmed(string? value) => (value ?? "").Trim();

    public static void Email(Dictionary<string, string[]> errors, string? value)
    {
        var email = Trimmed(value);
        if (email.Length == 0) errors["email"] = ["Enter an email address."];
        else if (email.Length > MaxEmailLength || !IsEmail(email)) errors["email"] = ["Enter a valid email address."];
    }

    private static void Password(Dictionary<string, string[]> errors, string field, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) errors[field] = ["Enter a password."];
        else if (value.Length < MinPasswordLength) errors[field] = [$"Use at least {MinPasswordLength} characters."];
    }

    private static void Name(Dictionary<string, string[]> errors, string field, string? value, bool required)
    {
        var name = Trimmed(value);
        if (name.Length == 0 && required) errors[field] = ["Enter a name."];
        else if (name.Length > MaxNameLength) errors[field] = [$"Use at most {MaxNameLength} characters."];
    }

    private static void RoleField(Dictionary<string, string[]> errors, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) errors["role"] = ["Choose a role."];
        else if (!AppRoles.IsValid(value)) errors["role"] = ["Choose Admin, Operator or Viewer."];
    }

    private static bool IsEmail(string value) =>
        MailAddress.TryCreate(value, out var parsed) && parsed.Address == value && value.Contains('@') && value.Split('@')[1].Contains('.');
}
