using Rocket.Libraries.DatabaseIntegrator;

namespace ShipRight.Shared.Tenancy;

// What the company-scoped stores ask before touching a row. Reads, updates and deletes are filtered by the
// signed-in person's company inside the SQL; inserts are stamped with it. Background work inside a system scope
// reads across companies, and stamps inserts with the company that owns the project the row belongs to.
public sealed class CompanyScope(ICompanyContext context, IDatabaseHelper<Guid> db)
{
    private const string CompanyOfProject = "SELECT CompanyId FROM Project WHERE Id = @ProjectId LIMIT 1";

    // True when there is a company or system work. A public call (the health check) has neither, so a
    // store's Count reports 0 for it instead of failing or counting other companies' rows.
    public bool IsScoped => context.CompanyId is not null || context.IsSystem;

    // The company to filter by, or null when running as system work. Throws when there is neither, so a store
    // can never quietly return another company's rows.
    public Guid? Filter() => context.CompanyId ?? (context.IsSystem ? null : context.RequireCompanyId());

    // The company to write on a new row.
    public async Task<Guid> InsertCompanyAsync(Guid? projectId = null)
    {
        if (context.CompanyId is { } id) return id;
        if (context.IsSystem && projectId is { } project)
        {
            var found = (await db.GetManyAsync<CompanyRow>(CompanyOfProject, new() { ["ProjectId"] = project })).FirstOrDefault();
            if (found is not null) return found.CompanyId;
        }
        throw new CompanyScopeException("No company is associated with this write.");
    }

    private sealed class CompanyRow
    {
        public Guid CompanyId { get; set; }
    }
}
