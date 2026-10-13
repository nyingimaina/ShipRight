namespace ShipRight.Modules.Identity;

public static class AppRoles
{
    public const string Admin = "Admin";
    public const string Operator = "Operator";
    public const string Viewer = "Viewer";

    public static readonly string[] All = [Admin, Operator, Viewer];

    public static bool IsValid(string? role) => All.Contains(role, StringComparer.Ordinal);

    // Higher number = more access.
    public static int Rank(string? role) => role switch
    {
        Admin => 3,
        Operator => 2,
        Viewer => 1,
        _ => 0,
    };
}

// One person: exactly one company and exactly one role.
public sealed class AppUser
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public string Email { get; set; } = "";
    public string NormalizedEmail { get; set; } = "";
    public string Name { get; set; } = "";
    public string? PasswordHash { get; set; }
    public string Role { get; set; } = AppRoles.Viewer;
    public bool IsActive { get; set; } = true;
    public bool MustChangePassword { get; set; }
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime? LockoutEnd { get; set; }
    public int AccessFailedCount { get; set; }
    public DateTime Created { get; set; } = DateTime.UtcNow;
    public DateTime Modified { get; set; } = DateTime.UtcNow;
}
