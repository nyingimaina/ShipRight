using MyApp.Api.Shared.Http;
using MyApp.Api.Shared.Storage;

namespace MyApp.Api.Features.Customers;

/// <summary>What the API returns for a customer. Stored records never leave the repository (API-004).</summary>
public sealed record CustomerDto(long Id, string Name, string Email, DateTime CreatedAt);

/// <summary>What is saved in customers.json.</summary>
public sealed class StoredCustomer
{
    public long Id { get; set; }
    public long OwnerId { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// All storage for customers, in a file (fast-MVP version). Every operation is scoped by owner, so one user can never read or change
/// another's records even by guessing ids (API-002). Lists are paged (API-005). The methods are the same ones the database version
/// has, so the HTTP code does not change when the database is added later.
/// </summary>
internal sealed class CustomerRepository(FileStorage files, TimeProvider time)
{
    private readonly FileTable<StoredCustomer> _customers = files.Table<StoredCustomer>("customers");

    public Task<PagedResult<CustomerDto>> ListAsync(long ownerId, PageRequest page, string? search, CancellationToken ct)
    {
        var prefix = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        return Task.FromResult(_customers.Read(all =>
        {
            var matches = all
                .Where(c => c.OwnerId == ownerId && (prefix is null || StartsWith(c.Name, prefix) || StartsWith(c.Email, prefix)))
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Id)
                .ToList();
            var items = matches.Skip(page.Offset).Take(page.PageSize).Select(ToDto).ToList();
            return new PagedResult<CustomerDto>(items, matches.Count, page.Page, page.PageSize);
        }));
    }

    public Task<CustomerDto?> GetAsync(long ownerId, long id, CancellationToken ct) =>
        Task.FromResult(_customers.Read(all => all.Where(c => c.Id == id && c.OwnerId == ownerId).Select(ToDto).FirstOrDefault()));

    /// <summary>Null when this owner already has a customer with that email (checked and saved together).</summary>
    public Task<CustomerDto?> CreateAsync(long ownerId, string name, string email, CancellationToken ct) =>
        Task.FromResult(_customers.Write(table =>
        {
            if (table.Items.Any(c => c.OwnerId == ownerId && Same(c.Email, email))) return null;
            var customer = new StoredCustomer { Id = table.TakeId(), OwnerId = ownerId, Name = name, Email = email, CreatedAt = time.GetUtcNow().UtcDateTime };
            table.Items.Add(customer);
            return ToDto(customer);
        }));

    /// <summary>Outcome of an update: the new row, "not found" (also for someone else's record), or a duplicate email.</summary>
    public Task<(CustomerDto? Customer, bool Duplicate)> UpdateAsync(long ownerId, long id, string name, string email, CancellationToken ct) =>
        Task.FromResult(_customers.Write(table =>
        {
            if (table.Items.FirstOrDefault(c => c.Id == id && c.OwnerId == ownerId) is not { } customer) return ((CustomerDto?)null, false);
            if (table.Items.Any(c => c.OwnerId == ownerId && c.Id != id && Same(c.Email, email))) return (null, true);
            customer.Name = name;
            customer.Email = email;
            return (ToDto(customer), false);
        }));

    public Task<bool> DeleteAsync(long ownerId, long id, CancellationToken ct) =>
        Task.FromResult(_customers.Write(table => table.Items.RemoveAll(c => c.Id == id && c.OwnerId == ownerId) > 0));

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool StartsWith(string text, string prefix) => text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    private static CustomerDto ToDto(StoredCustomer c) => new(c.Id, c.Name, c.Email, c.CreatedAt);
}
