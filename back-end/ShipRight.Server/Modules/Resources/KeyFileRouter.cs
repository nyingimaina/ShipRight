using ShipRight.Modules.Resources.Models;
using ShipRight.Modules.Resources.Stores;

namespace ShipRight.Modules.Resources;

/// <summary>Metadata-only view of a key file: the .pem content is never part of this (R10).</summary>
public record KeyFileMetadataDto(Guid Id, string Name, IReadOnlyList<string> Tags, DateTime CreatedAt, DateTime ModifiedAt);

public record KeyFilePageDto(IReadOnlyList<KeyFileMetadataDto> Items, int Total, int Page, int PageSize);

public record CreateKeyFileRequest(string? Name, string? Content, List<string>? Tags);

public record UpdateKeyFileRequest(string? Name, List<string>? Tags, string? Content);

public static class KeyFileRouter
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;
    // A page past the end is simply empty; the cap keeps Skip = (page - 1) * pageSize well inside
    // int32 (and a non-negative SQL OFFSET) for any page a caller asks for (R9).
    public const int MaxPage = 1_000_000;
    public const int MaxNameLength = 200;
    public const int MaxContentBytes = 64 * 1024;

    public static void MapKeyFileRoutes(this WebApplication app)
    {
        app.MapGet("/api/resources/key-files", async (
            IKeyFileResourceStore store, int page = 1, int pageSize = DefaultPageSize) =>
        {
            var (safePage, safeSize) = Normalize(page, pageSize);
            var (items, total) = await store.GetPageAsync(safePage, safeSize);
            return Results.Ok(new KeyFilePageDto(
                items.Select(ToDto).ToList(), total, safePage, safeSize));
        });

        app.MapGet("/api/resources/key-files/{id:guid}", async (Guid id, IKeyFileResourceStore store) =>
        {
            var resource = await store.GetByIdAsync(id);
            return resource is null ? Results.NotFound() : Results.Ok(ToDto(resource));
        });

        app.MapPost("/api/resources/key-files", async (
            CreateKeyFileRequest request, IKeyFileResourceStore store) =>
        {
            var errors = ValidateName(request.Name);
            foreach (var (field, messages) in ValidateContent(request.Content))
                errors[field] = messages;
            if (errors.Count > 0) return Results.ValidationProblem(errors);

            var now = DateTime.UtcNow;
            var saved = new KeyFileResource
            {
                Id = Guid.NewGuid(),
                Name = request.Name!.Trim(),
                Content = request.Content!,
                Tags = CleanTags(request.Tags),
                CreatedAt = now,
                ModifiedAt = now,
            };
            await store.SaveAsync(saved);
            return Results.Created($"/api/resources/key-files/{saved.Id}", ToDto(saved));
        });

        app.MapPut("/api/resources/key-files/{id:guid}", async (
            Guid id, UpdateKeyFileRequest request, IKeyFileResourceStore store) =>
        {
            var existing = await store.GetByIdAsync(id);
            if (existing is null) return Results.NotFound();

            var errors = ValidateName(request.Name);
            if (request.Content is not null)
                foreach (var (field, messages) in ValidateContent(request.Content))
                    errors[field] = messages;
            if (errors.Count > 0) return Results.ValidationProblem(errors);

            var updated = existing with
            {
                Name = request.Name!.Trim(),
                Tags = request.Tags is null ? existing.Tags : CleanTags(request.Tags),
                Content = string.IsNullOrEmpty(request.Content) ? existing.Content : request.Content!,
                ModifiedAt = DateTime.UtcNow,
            };
            await store.SaveAsync(updated);
            return Results.Ok(ToDto(updated));
        });

        app.MapDelete("/api/resources/key-files/{id:guid}", async (
            Guid id, IKeyFileResourceStore store) =>
        {
            var existing = await store.GetByIdAsync(id);
            if (existing is null) return Results.NotFound();

            await store.DeleteAsync(id);
            return Results.Ok(new { message = "Key file deleted." });
        });
    }

    internal static (int Page, int PageSize) Normalize(int page, int pageSize)
    {
        if (page < 1) page = 1;
        if (page > MaxPage) page = MaxPage;
        if (pageSize < 1) pageSize = DefaultPageSize;
        if (pageSize > MaxPageSize) pageSize = MaxPageSize;
        return (page, pageSize);
    }

    private static Dictionary<string, string[]> ValidateName(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) return new() { ["name"] = ["Name is required."] };
        if (trimmed.Length > MaxNameLength)
            return new() { ["name"] = [$"Name must be {MaxNameLength} characters or fewer."] };
        return new();
    }

    private static Dictionary<string, string[]> ValidateContent(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return new() { ["content"] = ["Choose a .pem key file."] };
        if (global::System.Text.Encoding.UTF8.GetByteCount(content) > MaxContentBytes)
            return new() { ["content"] = ["The key file must be 64 KB or smaller."] };
        if (!content.Contains("-----BEGIN", StringComparison.Ordinal)
            || !content.Contains("PRIVATE KEY-----", StringComparison.Ordinal))
            return new() { ["content"] = ["That file does not look like a private key (.pem). Choose the key file Amazon gave you."] };
        return new();
    }

    private static List<string> CleanTags(List<string>? tags) =>
        tags is null
            ? []
            : tags.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static KeyFileMetadataDto ToDto(KeyFileResource r) =>
        new(r.Id, r.Name, r.Tags, r.CreatedAt, r.ModifiedAt);
}
