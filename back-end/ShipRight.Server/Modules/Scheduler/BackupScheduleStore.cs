using System.Text.Json;
using ShipRight.Shared.Tenancy;

namespace ShipRight.Modules.Scheduler;

/// <summary>A backup schedule the user created, persisted so a restart does not forget it.</summary>
public sealed record BackupScheduleDefinition
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public Guid CompanyId { get; init; }
    public string ProjectId { get; init; } = string.Empty;
    public string ProjectName { get; init; } = string.Empty;
    public string DatabaseName { get; init; } = string.Empty;
    public string Cron { get; init; } = string.Empty;
    public string TimeZone { get; init; } = "UTC";
    public string MissedRunPolicy { get; init; } = "RunOnce";
    public string OverlapPolicy { get; init; } = "Skip";
    public string? MaxDuration { get; init; }
    /// <summary>The id Tempo assigned at runtime; changes on every restart and is refreshed at registration.</summary>
    public Guid? RuntimeScheduleId { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public interface IBackupScheduleStore
{
    Task<List<BackupScheduleDefinition>> GetAllAsync();
    Task<BackupScheduleDefinition?> GetByRuntimeIdAsync(Guid runtimeScheduleId);
    Task SaveAsync(BackupScheduleDefinition definition);
    Task DeleteByRuntimeIdAsync(Guid runtimeScheduleId);
    Task DeleteAsync(string id);
}

/// <summary>Company-scoped JSON store, matching how the other small ShipRight settings are kept.</summary>
public sealed class JsonBackupScheduleStore : IBackupScheduleStore
{
    private static readonly Guid DesktopCompany = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _file;
    private readonly ICompanyContext? _company;
    private readonly object _gate = new();

    public JsonBackupScheduleStore(string dataDir, ICompanyContext? company = null)
    {
        Directory.CreateDirectory(dataDir);
        _file = Path.Combine(dataDir, "backup-schedules.json");
        _company = company;
    }

    public Task<List<BackupScheduleDefinition>> GetAllAsync()
    {
        lock (_gate)
        {
            var company = Filter();
            return Task.FromResult(Read()
                .Where(s => company is null || s.CompanyId == company)
                .OrderBy(s => s.CreatedAt)
                .ToList());
        }
    }

    public Task<BackupScheduleDefinition?> GetByRuntimeIdAsync(Guid runtimeScheduleId)
    {
        lock (_gate)
        {
            var company = Filter();
            return Task.FromResult(Read().FirstOrDefault(s =>
                s.RuntimeScheduleId == runtimeScheduleId && (company is null || s.CompanyId == company)));
        }
    }

    public Task SaveAsync(BackupScheduleDefinition definition)
    {
        lock (_gate)
        {
            var all = Read();
            var existing = all.FirstOrDefault(s => s.Id == definition.Id);
            var company = _company?.CompanyId ?? existing?.CompanyId ?? DesktopCompany;
            if (existing is not null && existing.CompanyId != company && _company is { IsSystem: false })
                throw new CompanyScopeException("That backup schedule belongs to another company.");

            var saved = definition with
            {
                CompanyId = company,
                CreatedAt = existing?.CreatedAt ?? definition.CreatedAt,
            };

            all.RemoveAll(s => s.Id == saved.Id);
            all.Add(saved);
            Write(all);
        }
        return Task.CompletedTask;
    }

    public Task DeleteByRuntimeIdAsync(Guid runtimeScheduleId)
    {
        lock (_gate)
        {
            var all = Read();
            var company = Filter();
            var removed = all.RemoveAll(s =>
                s.RuntimeScheduleId == runtimeScheduleId && (company is null || s.CompanyId == company));
            if (removed > 0) Write(all);
        }
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string id)
    {
        lock (_gate)
        {
            var all = Read();
            var company = Filter();
            var removed = all.RemoveAll(s => s.Id == id && (company is null || s.CompanyId == company));
            if (removed > 0) Write(all);
        }
        return Task.CompletedTask;
    }

    private Guid? Filter() =>
        _company is null ? null : _company.CompanyId ?? (_company.IsSystem ? null : throw new CompanyScopeException("No company is associated with this call."));

    private List<BackupScheduleDefinition> Read()
    {
        if (!File.Exists(_file)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<BackupScheduleDefinition>>(File.ReadAllText(_file), Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void Write(List<BackupScheduleDefinition> all)
    {
        var temp = _file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(all, Json));
        File.Move(temp, _file, overwrite: true);
    }
}
