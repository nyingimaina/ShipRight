using ShipRight.Modules.Resources.Models;

namespace ShipRight.Modules.Resources.Stores;

/// <summary>
/// Company-scoped storage for .pem key files (R8). Listing is paged in the store, never by loading
/// every row (R9); the content is only ever written, never read back to a caller.
/// </summary>
public interface IKeyFileResourceStore
{
    Task<(List<KeyFileResource> Items, int Total)> GetPageAsync(int page, int pageSize);

    Task<KeyFileResource?> GetByIdAsync(Guid id);

    Task SaveAsync(KeyFileResource resource);

    Task DeleteAsync(Guid id);
}
