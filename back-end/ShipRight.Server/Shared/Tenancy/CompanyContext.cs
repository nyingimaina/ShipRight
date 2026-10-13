using System.Security.Claims;

namespace ShipRight.Shared.Tenancy;

public static class AppClaims
{
    public const string CompanyId = "company_id";
}

// The company of whoever is making the current call. Taken only from the signed-in person's claim,
// never from a header, route or body. Stores fail closed when no company is known.
public interface ICompanyContext
{
    // The signed-in person's company, or null when there is none (not signed in, or system work).
    Guid? CompanyId { get; }

    // True inside SystemScope: background work that legitimately spans every company.
    bool IsSystem { get; }

    // Returns the company to filter by, or throws when the call has no company and is not system work.
    Guid RequireCompanyId();

    // Runs background work across all companies (watch-branch start-up, scheduler).
    IDisposable BeginSystemScope();
}

public sealed class CompanyContext : ICompanyContext
{
    // AsyncLocal so the company flows into work started from inside a request (builds run after the response).
    private static readonly AsyncLocal<Guid?> Current = new();
    private static readonly AsyncLocal<bool> System = new();

    public Guid? CompanyId => Current.Value;
    public bool IsSystem => System.Value;

    public Guid RequireCompanyId() =>
        Current.Value ?? throw new CompanyScopeException("No company is associated with this call.");

    // Scopes nest: leaving an inner one puts back whatever the outer one had set.
    public IDisposable BeginSystemScope()
    {
        var previous = System.Value;
        System.Value = true;
        return new Scope(() => System.Value = previous);
    }

    internal static void Set(Guid? companyId) => Current.Value = companyId;

    public static Guid? FromPrincipal(ClaimsPrincipal? principal) =>
        Guid.TryParse(principal?.FindFirst(AppClaims.CompanyId)?.Value, out var id) ? id : null;

    private sealed class Scope(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}

public sealed class CompanyScopeException(string message) : InvalidOperationException(message);

// Copies the signed-in person's company claim into the ambient context for the rest of the request.
public sealed class CompanyContextMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        CompanyContext.Set(CompanyContext.FromPrincipal(context.User));
        return next(context);
    }
}
