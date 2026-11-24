using System.Text.Json;
using ShipRight.Shared.Store;
using ShipRight.Shared.Tenancy;

namespace ShipRight.Modules.Tailscale;

public sealed record TailnetConnection
{
    public string Id { get; init; } = string.Empty;
    public Guid CompanyId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Tailnet { get; init; } = "-";
    public string AuthKey { get; init; } = string.Empty;
    public string OAuthClientId { get; init; } = string.Empty;
    public string OAuthClientSecret { get; init; } = string.Empty;
    public string Tags { get; init; } = "tag:shipright";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ModifiedAt { get; init; } = DateTimeOffset.UtcNow;
}

public interface ITailnetStore
{
    Task<List<TailnetConnection>> GetAllAsync();
    Task<TailnetConnection?> GetByIdAsync(string id);
    Task SaveAsync(TailnetConnection connection);
    Task DeleteAsync(string id);
}

/// <summary>Tenant-scoped tailnet settings. Secrets are encrypted before they touch disk and never leave the server.</summary>
public sealed class JsonTailnetStore : ITailnetStore
{
    private static readonly Guid DesktopCompany = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private readonly string _dataDir;
    private readonly string _file;
    private readonly ICompanyContext? _company;
    private readonly object _gate = new();

    public JsonTailnetStore(string dataDir, ICompanyContext? company = null)
    {
        _dataDir = dataDir;
        _file = Path.Combine(dataDir, "tailnets.json");
        _company = company;
        Directory.CreateDirectory(dataDir);
    }

    public Task<List<TailnetConnection>> GetAllAsync()
    {
        lock (_gate)
        {
            var company = Filter();
            return Task.FromResult(Read()
                .Where(c => company is null || c.CompanyId == company)
                .Select(Reveal)
                .ToList());
        }
    }

    public Task<TailnetConnection?> GetByIdAsync(string id)
    {
        lock (_gate)
        {
            var company = Filter();
            var found = Read().FirstOrDefault(c => c.Id == id && (company is null || c.CompanyId == company));
            return Task.FromResult(found is null ? null : Reveal(found));
        }
    }

    public Task SaveAsync(TailnetConnection connection)
    {
        lock (_gate)
        {
            var all = Read();
            var existing = all.FirstOrDefault(c => c.Id == connection.Id);
            var company = _company?.CompanyId ?? existing?.CompanyId ?? DesktopCompany;
            if (existing is not null && existing.CompanyId != company && _company is { IsSystem: false })
                throw new CompanyScopeException("That Tailscale network belongs to another company.");

            var saved = connection with
            {
                Id = string.IsNullOrWhiteSpace(connection.Id) ? Guid.NewGuid().ToString("N") : connection.Id,
                CompanyId = company,
                AuthKey = string.IsNullOrEmpty(connection.AuthKey) ? existing?.AuthKey ?? "" : Encrypt(connection.AuthKey),
                OAuthClientSecret = string.IsNullOrEmpty(connection.OAuthClientSecret)
                    ? existing?.OAuthClientSecret ?? ""
                    : Encrypt(connection.OAuthClientSecret),
                CreatedAt = existing?.CreatedAt ?? connection.CreatedAt,
                ModifiedAt = DateTimeOffset.UtcNow,
            };

            all.RemoveAll(c => c.Id == saved.Id);
            all.Add(saved);
            Write(all);
        }
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string id)
    {
        lock (_gate)
        {
            var all = Read();
            var company = Filter();
            var removed = all.RemoveAll(c => c.Id == id && (company is null || c.CompanyId == company));
            if (removed > 0) Write(all);
        }
        return Task.CompletedTask;
    }

    private Guid? Filter() =>
        _company is null ? null : _company.CompanyId ?? (_company.IsSystem ? null : throw new CompanyScopeException("No company is associated with this call."));

    private List<TailnetConnection> Read()
    {
        if (!File.Exists(_file)) return [];
        var json = File.ReadAllText(_file);
        return JsonSerializer.Deserialize<List<TailnetConnection>>(json) ?? [];
    }

    private void Write(List<TailnetConnection> all)
    {
        Directory.CreateDirectory(_dataDir);
        var temp = _file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, _file, overwrite: true);
    }

    private TailnetConnection Reveal(TailnetConnection connection) => connection with
    {
        AuthKey = Decrypt(connection.AuthKey),
        OAuthClientSecret = Decrypt(connection.OAuthClientSecret),
    };

    private string Encrypt(string value) => string.IsNullOrEmpty(value) ? "" : SecureStore.Encrypt(value, _dataDir);
    private string Decrypt(string value) => string.IsNullOrEmpty(value) ? "" : SecureStore.Decrypt(value, _dataDir);
}
