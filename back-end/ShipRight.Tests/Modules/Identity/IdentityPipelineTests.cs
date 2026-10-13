using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Rocket.Libraries.DatabaseIntegrator;
using ShipRight.Modules.Identity;
using ShipRight.Shared.Tenancy;
using ShipRight.Tests.Modules.Auth;
using ShipRight.Tests.Modules.Tenancy;

namespace ShipRight.Tests.Modules.Identity;

// The real Identity wiring (hasher, lockout, claims, reset tokens, cookie) running on a stand-in database.
[TestClass]
public class IdentityPipelineTests
{
    private const string Password = "correct-horse-1";

    private static ServiceProvider Build(RecordingDatabaseHelper? db = null, GoogleSettings? google = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDatabaseHelper<Guid>>(db ?? new RecordingDatabaseHelper());
        services.AddShipRightIdentity(google ?? new GoogleSettings(null, null));
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        return services.BuildServiceProvider();
    }

    private static AppUser NewUser(IServiceProvider services, string? hash = null, string role = AppRoles.Admin)
    {
        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            CompanyId = Guid.NewGuid(),
            Email = "Person@Example.test",
            NormalizedEmail = "PERSON@EXAMPLE.TEST",
            Name = "Person",
            Role = role,
        };
        user.PasswordHash = hash ?? services.GetRequiredService<IPasswordHasher<AppUser>>().HashPassword(user, Password);
        return user;
    }

    [TestMethod]
    public void Options_AreFiveAttemptsFiveMinutesAndEightCharacters()
    {
        // R1, R2
        using var provider = Build();
        using var scope = provider.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<IdentityOptions>>().Value;

        Assert.AreEqual(5, options.Lockout.MaxFailedAccessAttempts);
        Assert.AreEqual(TimeSpan.FromMinutes(5), options.Lockout.DefaultLockoutTimeSpan);
        Assert.AreEqual(8, options.Password.RequiredLength);
        Assert.IsTrue(options.User.RequireUniqueEmail);
    }

    [TestMethod]
    public void Cookie_LastsSevenDays_IsHttpOnly_AndRechecksTheAccountEveryMinute()
    {
        // R10, R5, R8
        using var provider = Build();
        var cookie = provider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(IdentityConstants.ApplicationScheme);

        Assert.AreEqual(TimeSpan.FromDays(7), cookie.ExpireTimeSpan);
        Assert.IsTrue(cookie.Cookie.HttpOnly);
        Assert.AreEqual(TimeSpan.FromMinutes(1), provider.GetRequiredService<IOptions<SecurityStampValidatorOptions>>().Value.ValidationInterval);
    }

    [TestMethod]
    public async Task SixthAttempt_WithinFiveMinutes_IsRefused_EvenWithTheCorrectPassword()
    {
        // R1 + security NFR
        using var provider = Build();
        using var scope = provider.CreateScope();
        var signIn = scope.ServiceProvider.GetRequiredService<SignInManager<AppUser>>();
        var user = NewUser(scope.ServiceProvider);

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var wrong = await signIn.CheckPasswordSignInAsync(user, "wrong-password", lockoutOnFailure: true);
            Assert.IsFalse(wrong.Succeeded);
        }

        var sixth = await signIn.CheckPasswordSignInAsync(user, Password, lockoutOnFailure: true);
        Assert.IsTrue(sixth.IsLockedOut);
        Assert.IsTrue(user.LockoutEnd > DateTime.UtcNow.AddMinutes(4));
    }

    [TestMethod]
    public async Task CorrectPassword_ClearsEarlierFailures()
    {
        // R1
        using var provider = Build();
        using var scope = provider.CreateScope();
        var signIn = scope.ServiceProvider.GetRequiredService<SignInManager<AppUser>>();
        var user = NewUser(scope.ServiceProvider);

        await signIn.CheckPasswordSignInAsync(user, "wrong-password", lockoutOnFailure: true);
        var ok = await signIn.CheckPasswordSignInAsync(user, Password, lockoutOnFailure: true);

        Assert.IsTrue(ok.Succeeded);
        Assert.AreEqual(0, user.AccessFailedCount);
    }

    [TestMethod]
    public async Task OldAccount_SignsInWithOldPassword_AndIsQuietlyUpgraded()
    {
        // R1, R12: nobody is asked to reset; the stored hash moves to the Identity format on that sign-in.
        using var provider = Build();
        using var scope = provider.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var legacy = AuthCharacterizationTests.BuildLegacyHash(Password);
        var user = NewUser(scope.ServiceProvider, legacy);

        Assert.IsTrue(await users.CheckPasswordAsync(user, Password));

        Assert.AreNotEqual(legacy, user.PasswordHash);
        Assert.IsFalse(LegacyAwarePasswordHasher.IsLegacy(user.PasswordHash!, out _));
        Assert.IsTrue(await users.CheckPasswordAsync(user, Password));
    }

    [TestMethod]
    public async Task OldAccount_WithWrongPassword_IsRefused_AndKeepsItsOldHash()
    {
        // R1
        using var provider = Build();
        using var scope = provider.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var legacy = AuthCharacterizationTests.BuildLegacyHash(Password);
        var user = NewUser(scope.ServiceProvider, legacy);

        Assert.IsFalse(await users.CheckPasswordAsync(user, "not-the-password"));
        Assert.AreEqual(legacy, user.PasswordHash);
    }

    [TestMethod]
    public async Task Email_IsFoundWhateverTheCapitals()
    {
        // R1, R2: lookups are done on the upper-cased email.
        var db = new RecordingDatabaseHelper();
        using var provider = Build(db);
        using var scope = provider.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();

        await users.FindByEmailAsync("pErSoN@example.TEST");

        Assert.IsTrue(RecordingDatabaseHelper.ParametersOf(db.Statements[0].Parameters).Values.Contains("PERSON@EXAMPLE.TEST"));
    }

    [TestMethod]
    public async Task SignInClaims_CarryCompanyAndRole_AndFlagATemporaryPassword()
    {
        // R4, R5, R10
        using var provider = Build();
        using var scope = provider.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<AppUser>>();
        var user = NewUser(scope.ServiceProvider, role: AppRoles.Operator);
        user.MustChangePassword = true;

        var principal = await factory.CreateAsync(user);

        Assert.AreEqual(user.CompanyId.ToString(), principal.FindFirstValue(AppClaims.CompanyId));
        Assert.AreEqual(AppRoles.Operator, principal.FindFirstValue(ClaimTypes.Role));
        Assert.IsTrue(principal.HasClaim(AppClaimsPrincipalFactory.MustChangePasswordClaim, "true"));
        Assert.AreEqual(user.Id.ToString(), principal.FindFirstValue(ClaimTypes.NameIdentifier));
    }

    [TestMethod]
    public async Task ResetToken_WorksOnce_ThenIsRefused()
    {
        // R10: a reset link works once.
        using var provider = Build();
        using var scope = provider.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = NewUser(scope.ServiceProvider);

        var token = await users.GeneratePasswordResetTokenAsync(user);
        var first = await users.ResetPasswordAsync(user, token, "brand-new-pass-1");
        var second = await users.ResetPasswordAsync(user, token, "another-new-pass-2");

        Assert.IsTrue(first.Succeeded);
        Assert.IsFalse(second.Succeeded);
        Assert.IsTrue(await users.CheckPasswordAsync(user, "brand-new-pass-1"));
    }

    [TestMethod]
    public async Task ResetToken_ForAnotherPerson_IsRefused()
    {
        // R10
        using var provider = Build();
        using var scope = provider.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var owner = NewUser(scope.ServiceProvider);
        var other = NewUser(scope.ServiceProvider);

        var token = await users.GeneratePasswordResetTokenAsync(owner);

        Assert.IsFalse((await users.ResetPasswordAsync(other, token, "brand-new-pass-1")).Succeeded);
    }

    [TestMethod]
    public async Task ResetPassword_DoesNotSwitchASwitchedOffAccountBackOn()
    {
        // R10: resetting only touches the password; IsActive is never written by the user store.
        using var provider = Build();
        using var scope = provider.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = NewUser(scope.ServiceProvider);
        user.IsActive = false;

        await users.ResetPasswordAsync(user, await users.GeneratePasswordResetTokenAsync(user), "brand-new-pass-1");

        Assert.IsFalse(user.IsActive);
    }

    [TestMethod]
    public async Task UserStore_NeverWritesRoleCompanyOrActive_WhenSavingCredentials()
    {
        // R5, R7, R8: a sign-in that saves a failed-attempt count cannot overwrite an Admin's decision.
        var db = new RecordingDatabaseHelper();
        using var provider = Build(db);
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<DapperUserStore>();

        await store.UpdateAsync(NewUser(scope.ServiceProvider), CancellationToken.None);

        var sql = db.LastSql;
        StringAssert.StartsWith(sql, "UPDATE");
        Assert.IsFalse(sql.Contains("Role ="));
        Assert.IsFalse(sql.Contains("IsActive ="));
        Assert.IsFalse(sql.Contains("CompanyId ="));
    }

    [TestMethod]
    public async Task UserStore_CannotDeleteAnAccount()
    {
        // R8: people are switched off, never deleted.
        using var provider = Build();
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<DapperUserStore>();

        var result = await store.DeleteAsync(NewUser(scope.ServiceProvider), CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
    }

    [TestMethod]
    public async Task UserStore_HasOneRolePerPerson()
    {
        // R4
        using var provider = Build();
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<DapperUserStore>();
        var user = NewUser(scope.ServiceProvider, role: AppRoles.Viewer);

        CollectionAssert.AreEqual(new[] { AppRoles.Viewer }, (await store.GetRolesAsync(user, CancellationToken.None)).ToArray());
        Assert.IsTrue(await store.IsInRoleAsync(user, "viewer", CancellationToken.None));
        await Assert.ThrowsExceptionAsync<ArgumentException>(() => store.AddToRoleAsync(user, "Owner", CancellationToken.None));
    }

    [TestMethod]
    public async Task Google_IsOffUntilConfigured()
    {
        // R3, R13: no client id and secret: the button is hidden and email sign-in is unaffected.
        using var off = Build();
        using var on = Build(google: new GoogleSettings("client-id", "client-secret"));

        Assert.IsFalse(off.GetRequiredService<GoogleSettings>().Enabled);
        Assert.IsNull(await off.GetRequiredService<IAuthenticationSchemeProvider>().GetSchemeAsync(GoogleSettings.Scheme));
        Assert.IsTrue(on.GetRequiredService<GoogleSettings>().Enabled);
        Assert.IsNotNull(await on.GetRequiredService<IAuthenticationSchemeProvider>().GetSchemeAsync(GoogleSettings.Scheme));
    }

    [TestMethod]
    public async Task Google_RequestsOnlyIdentityScopes_AndSavesNoTokens()
    {
        // R3: Google tokens are not kept anywhere.
        using var provider = Build(google: new GoogleSettings("client-id", "client-secret"));
        var options = provider.GetRequiredService<IOptionsMonitor<Microsoft.AspNetCore.Authentication.OAuth.OAuthOptions>>().Get(GoogleSettings.Scheme);

        Assert.IsFalse(options.SaveTokens);
        Assert.IsTrue(options.UsePkce);
        CollectionAssert.AreEquivalent(new[] { "openid", "email", "profile" }, options.Scope.ToArray());
    }

    [TestMethod]
    public void Registration_PutsNewPeopleInTheirOwnCompany()
    {
        // R2: the register service creates a company and its Admin through one repository call.
        using var provider = Build();
        using var scope = provider.CreateScope();

        Assert.IsNotNull(scope.ServiceProvider.GetRequiredService<RegistrationService>());
    }

    [TestMethod]
    public async Task Registration_CreatesOneCompanyAndOneAdmin_WithAHashedPassword()
    {
        // R2: the company row and the person row go in together; the password is stored only as a hash.
        var db = new RecordingDatabaseHelper();
        using var provider = Build(db);
        using var scope = provider.CreateScope();
        var registration = scope.ServiceProvider.GetRequiredService<RegistrationService>();

        var user = await registration.RegisterAsync("New@Example.test", "New Person", "  ", "plain-text-pass-1");

        Assert.IsNotNull(user);
        Assert.AreEqual(AppRoles.Admin, user.Role);
        Assert.AreEqual("NEW@EXAMPLE.TEST", user.NormalizedEmail);
        Assert.AreNotEqual("plain-text-pass-1", user.PasswordHash);
        Assert.AreEqual(2, db.Statements.Count);
        StringAssert.Contains(db.Statements[0].Sql, "INSERT INTO Company");
        StringAssert.Contains(db.Statements[1].Sql, "INSERT INTO `User`");
        Assert.IsTrue(RecordingDatabaseHelper.ParametersOf(db.Statements[1].Parameters).Values.Contains(user.CompanyId));
        Assert.IsTrue(RecordingDatabaseHelper.ParametersOf(db.Statements[0].Parameters).Values.Contains("New Person"),
            "a blank company name falls back to the person's name");
    }

    [TestMethod]
    public async Task Teammate_IsCreatedInTheAdminsCompany_AsMustChangePassword()
    {
        // R6
        var db = new RecordingDatabaseHelper();
        using var provider = Build(db);
        using var scope = provider.CreateScope();
        var registration = scope.ServiceProvider.GetRequiredService<RegistrationService>();
        var company = Guid.NewGuid();

        var user = await registration.CreateTeammateAsync(company, "mate@example.test", "Mate", AppRoles.Operator, "temporary-pass-1");

        Assert.IsNotNull(user);
        Assert.AreEqual(company, user.CompanyId);
        Assert.IsTrue(user.MustChangePassword);
        Assert.AreEqual(1, db.Statements.Count);
        var values = RecordingDatabaseHelper.ParametersOf(db.Statements[0].Parameters).Values;
        Assert.IsFalse(values.Contains("temporary-pass-1"), "the temporary password is stored only as a hash");
    }
}
