using Jattac.Libraries.QBuilder;
using Rocket.Libraries.DatabaseIntegrator;
using Serilog;
using ShipRight.Database;
using ShipRight.Modules.Auth.Models;

namespace ShipRight.Modules.Auth.Services;

public interface ISetupService
{
    Task EnsureAdminUserAsync(string adminEmail, string adminPassword);
}

public class SetupService : ISetupService
{
    private readonly IDatabaseHelper<Guid> _db;
    private readonly IPasswordHelper _passwordHelper;
    private readonly ITokenService _tokenService;

    public SetupService(
        IDatabaseHelper<Guid> db,
        IPasswordHelper passwordHelper,
        ITokenService tokenService)
    {
        _db = db;
        _passwordHelper = passwordHelper;
        _tokenService = tokenService;
    }

    public async Task EnsureAdminUserAsync(string adminEmail, string adminPassword)
    {
        using var qBuilder = AppQBuilderExtensions.NewQBuilder();
        var built = qBuilder
            .UseSelector()
            .Select<User>("*")
            .Then()
            .UseTableBoundFilter<User>()
            .WhereEqualTo(u => u.Email, adminEmail)
            .Then()
            .BuildWithParameters();

        var existing = await _db.GetManyAsync<User>(built.ParameterizedSql, built.Parameters);

        if (existing.Count > 0)
        {
            Log.Information("Admin user {Email} already exists, skipping setup", adminEmail);
            return;
        }

        var companyId = Guid.NewGuid();
        var company = new Company
        {
            Id = companyId,
            Name = "Default",
            Slug = "default",
            Created = DateTime.UtcNow,
            Modified = DateTime.UtcNow,
        };

        using var companyInsert = AppQBuilderExtensions.NewQBuilder();
        var companyBuilt = companyInsert
            .UseTableBoundInsert<Company>()
            .FromObject(company)
            .BuildWithParameters();
        await _db.ExecuteAsync(companyBuilt.ParameterizedSql, companyBuilt.Parameters);

        var admin = new User
        {
            Id = Guid.NewGuid(),
            CompanyId = companyId,
            Email = adminEmail,
            PasswordHash = _passwordHelper.HashPassword(adminPassword),
            Name = "Admin",
            IsAdmin = true,
            TokenVersion = 1,
            Created = DateTime.UtcNow,
            Modified = DateTime.UtcNow,
        };

        using var userInsert = AppQBuilderExtensions.NewQBuilder();
        var userBuilt = userInsert
            .UseTableBoundInsert<User>()
            .FromObject(admin)
            .BuildWithParameters();
        await _db.ExecuteAsync(userBuilt.ParameterizedSql, userBuilt.Parameters);

        Log.Information("Created admin user {Email} with company {CompanyId}", adminEmail, companyId);
    }
}
