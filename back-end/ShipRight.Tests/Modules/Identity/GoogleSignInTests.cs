using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Identity;

namespace ShipRight.Tests.Modules.Identity;

// R3: what happens after Google has said who the person is.
[TestClass]
public class GoogleSignInTests
{
    private static InMemoryCloudFactory _factory = null!;

    [ClassInitialize]
    public static void Start(TestContext context)
    {
        _factory = new InMemoryCloudFactory();
        _ = _factory.NewClient(); // starts the app so its services exist
    }

    [ClassCleanup]
    public static void Stop() => _factory.Dispose();

    private static string NewEmail() => $"g-{Guid.NewGuid():N}@example.test";

    private static async Task<AppUser?> Resolve(string email, string? name, string subject)
    {
        using var scope = _factory.Services.CreateScope();
        return await GoogleSignIn.ResolveUserAsync(
            scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>(),
            scope.ServiceProvider.GetRequiredService<RegistrationService>(), email, name, subject);
    }

    private static ClaimsPrincipal Principal(string? subject, string? email, string? verified, string? name = "Pat Person")
    {
        var claims = new List<Claim>();
        if (subject is not null) claims.Add(new Claim(ClaimTypes.NameIdentifier, subject));
        if (email is not null) claims.Add(new Claim(ClaimTypes.Email, email));
        if (verified is not null) claims.Add(new Claim("email_verified", verified));
        if (name is not null) claims.Add(new Claim(ClaimTypes.Name, name));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "External"));
    }

    [TestMethod]
    public async Task FirstTimePerson_BecomesAdminOfANewCompanyNamedAfterThem()
    {
        // R3
        var email = NewEmail();
        var companies = _factory.Users.CompanyCount;

        var user = await Resolve(email, "Pat Person", "google-sub-1-" + email);

        Assert.IsNotNull(user);
        Assert.AreEqual(AppRoles.Admin, user.Role);
        Assert.AreEqual(companies + 1, _factory.Users.CompanyCount);
        Assert.AreEqual("Pat Person", _factory.Users.CompanyNames.Last());
        Assert.IsNull(user.PasswordHash, "no password is stored for a Google-only person");
        Assert.IsTrue(user.IsActive);
    }

    [TestMethod]
    public async Task SigningInTwice_FindsTheSameAccount_AndCreatesNoSecondCompany()
    {
        // R3
        var email = NewEmail();
        var subject = "google-sub-2-" + email;
        var first = await Resolve(email, "Pat", subject);
        var companies = _factory.Users.CompanyCount;

        var second = await Resolve(email, "Pat", subject);

        Assert.AreEqual(first!.Id, second!.Id);
        Assert.AreEqual(companies, _factory.Users.CompanyCount);
    }

    [TestMethod]
    public async Task ADoubleSignInAtOnce_CreatesOnlyOneCompany()
    {
        // R3
        var email = NewEmail();
        var subject = "google-sub-3-" + email;
        var companies = _factory.Users.CompanyCount;

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Resolve(email, "Race", subject)));

        Assert.IsTrue(results.All(u => u is not null));
        Assert.AreEqual(1, results.Select(u => u!.Id).Distinct().Count());
        Assert.AreEqual(companies + 1, _factory.Users.CompanyCount);
    }

    [TestMethod]
    public async Task APersonWithAnExistingAccount_IsSignedInToIt_WithoutANewCompany()
    {
        // R3
        var existing = _factory.Seed(NewEmail(), AppRoles.Operator);
        var companies = _factory.Users.CompanyCount;

        var user = await Resolve(existing.Email.ToUpperInvariant(), "Someone", "google-sub-4-" + existing.Id);

        Assert.AreEqual(existing.Id, user!.Id);
        Assert.AreEqual(existing.CompanyId, user.CompanyId);
        Assert.AreEqual(AppRoles.Operator, user.Role, "their role is not changed by signing in with Google");
        Assert.AreEqual(companies, _factory.Users.CompanyCount);
    }

    [TestMethod]
    public async Task ASwitchedOffPerson_IsStillFlaggedOff_SoTheSignInIsRefused()
    {
        // R8: the endpoint refuses anyone who is not active.
        var off = _factory.Seed(NewEmail(), AppRoles.Viewer, active: false);

        var user = await Resolve(off.Email, "Off", "google-sub-5-" + off.Id);

        Assert.AreEqual(off.Id, user!.Id);
        Assert.IsFalse(user.IsActive);
    }

    [TestMethod]
    public async Task ASwitchedOffPerson_AtTheGoogleCallback_IsRefused_AndNoSessionIsCreated()
    {
        // R8, R3: the callback's own guard must refuse a switched-off person; without it they would be signed in.
        using var factory = new InMemoryCloudFactory(google: true);
        var off = factory.Seed(NewEmail(), AppRoles.Viewer, active: false);
        var client = factory.NewClient();

        // Google has verified the email, so the identity itself is good — only the switch-off may stop the sign-in.
        client.Http.DefaultRequestHeaders.Add(InMemoryCloudFactory.TestExternalIdentityHeader,
            $"google-sub-{off.Id}|{off.Email}|Off Person|true");
        var planted = await client.Http.GetAsync("/api/auth/providers");
        Assert.AreEqual(HttpStatusCode.NoContent, planted.StatusCode, "the external (Google) cookie was planted");
        client.Http.DefaultRequestHeaders.Remove(InMemoryCloudFactory.TestExternalIdentityHeader);

        var complete = await client.Http.GetAsync("/api/auth/google/complete");

        Assert.AreEqual(HttpStatusCode.Redirect, complete.StatusCode, await complete.Content.ReadAsStringAsync());
        StringAssert.Contains(complete.Headers.Location!.OriginalString, "switched_off");
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.Get("/api/projects")).Status, "no session was created");
    }

    [TestMethod]
    public void AnEmailGoogleHasNotVerified_IsRefused()
    {
        // R3
        Assert.IsNull(GoogleSignIn.ReadVerifiedIdentity(Principal("sub", "a@example.test", "false")));
        Assert.IsNull(GoogleSignIn.ReadVerifiedIdentity(Principal("sub", "a@example.test", null)));
    }

    [TestMethod]
    public void MissingSubjectOrEmail_IsRefused()
    {
        // R3
        Assert.IsNull(GoogleSignIn.ReadVerifiedIdentity(Principal(null, "a@example.test", "true")));
        Assert.IsNull(GoogleSignIn.ReadVerifiedIdentity(Principal("sub", null, "true")));
        Assert.IsNull(GoogleSignIn.ReadVerifiedIdentity(Principal("sub", "  ", "true")));
        Assert.IsNull(GoogleSignIn.ReadVerifiedIdentity(null));
    }

    [TestMethod]
    public void AVerifiedEmail_IsAccepted_WithItsStableId()
    {
        // R3
        var identity = GoogleSignIn.ReadVerifiedIdentity(Principal("sub-123", " a@example.test ", "True", "Pat"));

        Assert.IsNotNull(identity);
        Assert.AreEqual("sub-123", identity.Subject);
        Assert.AreEqual("a@example.test", identity.Email);
        Assert.AreEqual("Pat", identity.Name);
    }

    [TestMethod]
    public async Task APersonWithNoDisplayName_GetsACompanyNamedAfterTheirEmail()
    {
        // R3
        var email = NewEmail();

        await Resolve(email, null, "google-sub-6-" + email);

        Assert.AreEqual(email, _factory.Users.CompanyNames.Last());
    }
}
