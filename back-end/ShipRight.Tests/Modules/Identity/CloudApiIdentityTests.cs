using System.Diagnostics;
using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Identity;
using ShipRight.Tests.Modules.Auth;

namespace ShipRight.Tests.Modules.Identity;

// TEST-002: sign-in, registration, roles, company isolation and the team screen's API, all through HTTP against a
// real MariaDB. Needs SHIPRIGHT_TEST_DB_CONNECTION (see MariaDbTestDatabase); without it every test is inconclusive.
[TestClass]
public class CloudApiIdentityTests
{
    private const string Password = "long-enough-1";

    private static MariaDbTestDatabase? _database;
    private static CloudApiFactory? _factory;

    [ClassInitialize]
    public static void StartApp(TestContext _)
    {
        _database = MariaDbTestDatabase.CreateOrSkip();
        _factory = new CloudApiFactory(_database);
    }

    [ClassCleanup]
    public static void StopApp()
    {
        _factory?.Dispose();
        _database?.Dispose();
    }

    private static CloudApiFactory Factory => _factory!;
    private static MariaDbTestDatabase Db => _database!;

    private static string NewEmail(string prefix = "user") => $"{prefix}-{Guid.NewGuid():N}@example.test";

    private static async Task<(ApiClient Client, string Email, Guid CompanyId)> RegisteredAdmin(string prefix = "admin")
    {
        var client = Factory.NewClient();
        var email = NewEmail(prefix);
        var result = await client.Register(email);
        Assert.AreEqual(HttpStatusCode.OK, result.Status, result.Body);
        return (client, email, Guid.Parse(result.Text("user", "companyId")!));
    }

    private static async Task<(ApiClient Client, string Email, Guid Id)> TeammateIn(ApiClient admin, string role)
    {
        var email = NewEmail(role.ToLowerInvariant());
        var created = await admin.Post("/api/team", new { email, name = $"{role} Person", temporaryPassword = "temp-pass-123", role });
        Assert.AreEqual(HttpStatusCode.Created, created.Status, created.Body);

        var teammate = Factory.NewClient();
        Assert.AreEqual(HttpStatusCode.OK, (await teammate.Login(email, "temp-pass-123")).Status);
        // A temporary password must be replaced before anything else works (R10).
        var changed = await teammate.Post("/api/auth/change-password", new { currentPassword = "temp-pass-123", newPassword = Password });
        Assert.AreEqual(HttpStatusCode.OK, changed.Status, changed.Body);
        return (teammate, email, Guid.Parse(created.Text("id")!));
    }

    private static Guid SeedProject(Guid companyId, string name)
    {
        var id = Guid.NewGuid();
        Db.Execute("INSERT INTO Project (Id, CompanyId, Name, Data) VALUES (@id, @company, @name, '{}')",
            true, ("id", id.ToString()), ("company", companyId.ToString()), ("name", name));
        return id;
    }

    // ── R2 register ──────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Register_CreatesOneCompanyAndOneAdmin_AndSignsThePersonIn()
    {
        // R2, R4
        var before = Db.Scalar<long>("SELECT COUNT(*) FROM Company");
        var (client, email, companyId) = await RegisteredAdmin();

        Assert.AreEqual(before + 1, Db.Scalar<long>("SELECT COUNT(*) FROM Company"));
        Assert.AreEqual(1L, Db.Scalar<long>("SELECT COUNT(*) FROM `User` WHERE CompanyId = @c AND Role = 'Admin'", ("c", companyId.ToString())));
        var me = await client.Get("/api/auth/me");
        Assert.AreEqual(HttpStatusCode.OK, me.Status);
        Assert.AreEqual("Admin", me.Text("user", "role"));
        Assert.AreEqual(email, me.Text("user", "email"));
    }

    [TestMethod]
    public async Task Register_NeverReturnsOrStoresThePasswordInThePlain()
    {
        // R1, security NFR
        var client = Factory.NewClient();
        var email = NewEmail("hash");
        var result = await client.Register(email, "my-very-secret-pw");

        Assert.IsFalse(result.Body.Contains("my-very-secret-pw"));
        Assert.IsFalse(result.Body.Contains("passwordHash", StringComparison.OrdinalIgnoreCase));
        var stored = Db.Scalar<string>("SELECT PasswordHash FROM `User` WHERE NormalizedEmail = @e", ("e", email.ToUpperInvariant()));
        Assert.IsNotNull(stored);
        Assert.AreNotEqual("my-very-secret-pw", stored);
    }

    [TestMethod]
    public async Task Register_WithATakenEmail_AnyCapitalisation_IsRefusedBesideTheEmailField_AndLeavesNoCompany()
    {
        // R2: and a failed registration leaves no company without a person.
        var (_, email, _) = await RegisteredAdmin();
        var companies = Db.Scalar<long>("SELECT COUNT(*) FROM Company");

        var again = await Factory.NewClient().Register(email.ToUpperInvariant());

        Assert.AreEqual(HttpStatusCode.BadRequest, again.Status);
        Assert.AreEqual("application/problem+json", again.ContentType);
        Assert.IsTrue(again.HasFieldError("email"));
        Assert.AreEqual(companies, Db.Scalar<long>("SELECT COUNT(*) FROM Company"));
        Assert.AreEqual(0L, Db.Scalar<long>("SELECT COUNT(*) FROM Company c LEFT JOIN `User` u ON u.CompanyId = c.Id WHERE u.Id IS NULL"));
    }

    [TestMethod]
    public async Task Register_PressedTwiceAtOnce_CreatesOnlyOneCompanyAndAccount()
    {
        // R2: double click, and two people racing for the same email.
        var email = NewEmail("race");
        var companies = Db.Scalar<long>("SELECT COUNT(*) FROM Company");

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Factory.NewClient().Register(email)));

        Assert.AreEqual(1, results.Count(r => r.Status == HttpStatusCode.OK), string.Join(" | ", results.Select(r => r.Body)));
        Assert.AreEqual(1L, Db.Scalar<long>("SELECT COUNT(*) FROM `User` WHERE NormalizedEmail = @e", ("e", email.ToUpperInvariant())));
        Assert.AreEqual(companies + 1, Db.Scalar<long>("SELECT COUNT(*) FROM Company"));
    }

    [DataTestMethod]
    [DataRow("", "long-enough-1", "Name", "email")]
    [DataRow("   ", "long-enough-1", "Name", "email")]
    [DataRow("not-an-email", "long-enough-1", "Name", "email")]
    [DataRow("ok@example.test", "short", "Name", "password")]
    [DataRow("ok@example.test", "long-enough-1", "   ", "name")]
    public async Task Register_InvalidFields_AreReportedBesideTheField(string email, string password, string name, string field)
    {
        // R2: RFC 7807 with field errors.
        var result = await Factory.NewClient().Post("/api/auth/register", new { email, password, name, companyName = "Co" });

        Assert.AreEqual(HttpStatusCode.BadRequest, result.Status);
        Assert.AreEqual("application/problem+json", result.ContentType);
        Assert.IsTrue(result.HasFieldError(field), result.Body);
    }

    // ── R1 sign-in ───────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Login_WrongPasswordAndUnknownEmail_GiveTheSameGenericMessage()
    {
        // R1
        var (_, email, _) = await RegisteredAdmin();
        var client = Factory.NewClient();

        var wrong = await client.Login(email, "not-the-password");
        var unknown = await client.Login(NewEmail("nobody"), "whatever-123");

        Assert.AreEqual(HttpStatusCode.Unauthorized, wrong.Status);
        Assert.AreEqual(HttpStatusCode.Unauthorized, unknown.Status);
        Assert.AreEqual(wrong.Text("message"), unknown.Text("message"));
        Assert.IsFalse(wrong.Body.Contains(email));
    }

    [TestMethod]
    public async Task Login_AnyCapitalisationOfTheEmail_Works()
    {
        // R1
        var (_, email, _) = await RegisteredAdmin();

        var result = await Factory.NewClient().Login(email.ToUpperInvariant());

        Assert.AreEqual(HttpStatusCode.OK, result.Status, result.Body);
    }

    [TestMethod]
    public async Task Login_FiveWrongPasswords_LockTheAccount_EvenForTheCorrectPasswordAfterwards()
    {
        // R1 + security NFR
        var (_, email, _) = await RegisteredAdmin();
        var client = Factory.NewClient();

        for (var i = 0; i < 5; i++) await client.Login(email, "wrong-password-" + i);
        var sixth = await client.Login(email, Password);

        Assert.AreEqual(HttpStatusCode.TooManyRequests, sixth.Status);
        StringAssert.Contains(sixth.Text("message")!, "Try again");
        Assert.IsFalse(sixth.Body.Contains("lockout", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task OldAccount_SignsInWithTheOldEmailAndPassword_AndItsHashIsUpgraded()
    {
        // R1, R12: an account made before the switch, with its old-format hash, keeps working.
        var companyId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var email = NewEmail("legacy");
        var legacyHash = AuthCharacterizationTests.BuildLegacyHash("old-password-1");
        Db.Execute("INSERT INTO Company (Id, Name, Slug) VALUES (@c, 'Legacy Co', @slug)", true, ("c", companyId.ToString()), ("slug", "legacy-" + companyId));
        Db.Execute("INSERT INTO `User` (Id, CompanyId, Email, NormalizedEmail, PasswordHash, Name, Role, SecurityStamp) VALUES (@u, @c, @e, @n, @h, 'Legacy', 'Admin', 'stamp')",
            true, ("u", userId.ToString()), ("c", companyId.ToString()), ("e", email), ("n", email.ToUpperInvariant()), ("h", legacyHash));
        SeedProject(companyId, "legacy project");

        var client = Factory.NewClient();
        var login = await client.Login(email.ToUpperInvariant(), "old-password-1");

        Assert.AreEqual(HttpStatusCode.OK, login.Status, login.Body);
        Assert.AreNotEqual(legacyHash, Db.Scalar<string>("SELECT PasswordHash FROM `User` WHERE Id = @u", ("u", userId.ToString())));
        var projects = await client.Get("/api/projects");
        Assert.AreEqual(1, projects.Json.GetArrayLength(), "the same company's data is still there");
        Assert.AreEqual(HttpStatusCode.OK, (await Factory.NewClient().Login(email, "old-password-1")).Status);
    }

    [TestMethod]
    public async Task Logout_EndsAccessAtOnce()
    {
        // R10
        var (client, _, _) = await RegisteredAdmin();
        Assert.AreEqual(HttpStatusCode.OK, (await client.Get("/api/projects")).Status);

        await client.Post("/api/auth/logout");

        Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.Get("/api/projects")).Status);
    }

    // ── R5 sign-in required, roles ───────────────────────────────────────────

    [DataTestMethod]
    [DataRow("GET", "/api/projects")]
    [DataRow("GET", "/api/projects/abc")]
    [DataRow("POST", "/api/projects")]
    [DataRow("GET", "/api/builds")]
    [DataRow("POST", "/api/builds/start")]
    [DataRow("GET", "/api/servers")]
    [DataRow("POST", "/api/servers")]
    [DataRow("GET", "/api/resources/scripts")]
    [DataRow("GET", "/api/resources/credentials")]
    [DataRow("GET", "/api/team")]
    [DataRow("POST", "/api/team")]
    [DataRow("GET", "/api/scheduler/history")]
    [DataRow("GET", "/api/watch-branch/history")]
    [DataRow("GET", "/api/fs/list")]
    [DataRow("GET", "/api/auth/me")]
    [DataRow("POST", "/api/auth/change-password")]
    [DataRow("POST", "/api/system/shutdown")]
    public async Task EndpointsWithoutSignIn_AnswerSignInRequired(string method, string url)
    {
        // R5, API-001
        var result = await Factory.NewClient().Send(new HttpMethod(method), url, method == "GET" ? null : new { });

        Assert.AreEqual(HttpStatusCode.Unauthorized, result.Status, $"{method} {url}");
        Assert.AreEqual("Sign in required.", result.Text("message"));
    }

    [TestMethod]
    public async Task PublicEndpoints_NeedNoSignIn()
    {
        // R5: health, provider list, and the sign-in / registration / reset calls themselves.
        var client = Factory.NewClient();

        Assert.AreEqual(HttpStatusCode.OK, (await client.Get("/api/health")).Status);
        Assert.AreEqual(HttpStatusCode.OK, (await client.Get("/api/auth/providers")).Status);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await client.Post("/api/auth/login", new { email = "", password = "" })).Status);
        Assert.AreEqual(HttpStatusCode.OK, (await client.Post("/api/auth/forgot-password", new { email = "nobody@example.test" })).Status);
        Assert.AreEqual(HttpStatusCode.OK, (await client.Get("/login")).Status, "the web app's own pages load");
    }

    [TestMethod]
    public async Task OperatorCanReadAndOperate_ButNotChangeSettingsOrServers()
    {
        // R5
        var (admin, _, _) = await RegisteredAdmin();
        var (op, _, _) = await TeammateIn(admin, "Operator");

        Assert.AreEqual(HttpStatusCode.OK, (await op.Get("/api/projects")).Status);
        Assert.AreEqual(HttpStatusCode.OK, (await op.Get("/api/servers")).Status);
        Assert.AreNotEqual(HttpStatusCode.Forbidden, (await op.Post("/api/builds/start", new { projectId = "none" })).Status);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await op.Post("/api/servers", new { name = "s" })).Status);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await op.Put("/api/servers/abc", new { name = "s" })).Status);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await op.Delete("/api/servers/abc")).Status);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await op.Post("/api/projects", new { name = "p" })).Status);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await op.Post("/api/resources/scripts", new { name = "x" })).Status);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await op.Get("/api/team")).Status);
    }

    [TestMethod]
    public async Task ViewerCanOnlyRead_AndSeesNoChangeAllowed()
    {
        // R5
        var (admin, _, _) = await RegisteredAdmin();
        var (viewer, _, _) = await TeammateIn(admin, "Viewer");

        Assert.AreEqual(HttpStatusCode.OK, (await viewer.Get("/api/projects")).Status);
        Assert.AreEqual(HttpStatusCode.OK, (await viewer.Get("/api/builds")).Status);
        var start = await viewer.Post("/api/builds/start", new { projectId = "none" });
        Assert.AreEqual(HttpStatusCode.Forbidden, start.Status);
        Assert.AreEqual("You do not have permission to do that.", start.Text("message"));
        Assert.AreEqual(HttpStatusCode.Forbidden, (await viewer.Post("/api/builds/abc/deploy", new { })).Status);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await viewer.Post("/api/projects", new { name = "p" })).Status);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await viewer.Post("/api/team", new { })).Status);
    }

    [TestMethod]
    public async Task AdminDoesEverything()
    {
        // R5
        var (admin, _, _) = await RegisteredAdmin();

        Assert.AreEqual(HttpStatusCode.OK, (await admin.Get("/api/servers")).Status);
        Assert.AreEqual(HttpStatusCode.OK, (await admin.Get("/api/team")).Status);
        Assert.AreNotEqual(HttpStatusCode.Forbidden, (await admin.Post("/api/builds/start", new { projectId = "none" })).Status);
        Assert.AreNotEqual(HttpStatusCode.Forbidden, (await admin.Post("/api/servers", new { name = "s" })).Status);
    }

    [TestMethod]
    public async Task ARoleChangeAppliesOnThePersonsNextRequest()
    {
        // R5, R7
        var (admin, _, _) = await RegisteredAdmin();
        var (viewer, _, viewerId) = await TeammateIn(admin, "Viewer");
        Assert.AreEqual(HttpStatusCode.Forbidden, (await viewer.Post("/api/projects", new { name = "p" })).Status);

        var changed = await admin.Put($"/api/team/{viewerId}/role", new { role = "Admin" });

        Assert.AreEqual(HttpStatusCode.OK, changed.Status, changed.Body);
        Assert.AreNotEqual(HttpStatusCode.Forbidden, (await viewer.Post("/api/projects", new { name = "p" })).Status);
    }

    // ── R4 company isolation ─────────────────────────────────────────────────

    [TestMethod]
    public async Task AnotherCompanysProject_LooksExactlyLikeOneThatDoesNotExist()
    {
        // R4
        var (a, _, companyA) = await RegisteredAdmin("a");
        var (b, _, _) = await RegisteredAdmin("b");
        var projectId = SeedProject(companyA, "secret project");

        var own = await a.Get($"/api/projects/{projectId}");
        var other = await b.Get($"/api/projects/{projectId}");
        var missing = await b.Get($"/api/projects/{Guid.NewGuid()}");

        Assert.AreEqual(HttpStatusCode.OK, own.Status);
        Assert.AreEqual(HttpStatusCode.NotFound, other.Status);
        Assert.AreEqual(missing.Status, other.Status);
        Assert.AreEqual(missing.ContentType, other.ContentType);
        Assert.IsFalse(other.Body.Contains("secret project"));
    }

    [TestMethod]
    public async Task Lists_OnlyEverShowTheCallersOwnCompany()
    {
        // R4
        var (a, _, companyA) = await RegisteredAdmin("a");
        var (b, _, companyB) = await RegisteredAdmin("b");
        SeedProject(companyA, "alpha project");
        SeedProject(companyB, "beta project");

        var listA = (await a.Get("/api/projects")).Body;
        var listB = (await b.Get("/api/projects")).Body;

        StringAssert.Contains(listA, "alpha project");
        Assert.IsFalse(listA.Contains("beta project"));
        StringAssert.Contains(listB, "beta project");
        Assert.IsFalse(listB.Contains("alpha project"));
    }

    [TestMethod]
    public async Task ChangingOrDeletingAnotherCompanysProject_ChangesNothing()
    {
        // R4
        var (a, _, companyA) = await RegisteredAdmin("a");
        var (b, _, _) = await RegisteredAdmin("b");
        var projectId = SeedProject(companyA, "keep me");

        Assert.AreEqual(HttpStatusCode.NotFound, (await b.Delete($"/api/projects/{projectId}")).Status);
        Assert.AreEqual(HttpStatusCode.NotFound, (await b.Put($"/api/projects/{projectId}", new { name = "hijacked" })).Status);

        Assert.AreEqual(0L, Db.Scalar<long>("SELECT Deleted FROM Project WHERE Id = @p", ("p", projectId.ToString())));
        Assert.AreEqual("keep me", Db.Scalar<string>("SELECT Name FROM Project WHERE Id = @p", ("p", projectId.ToString())));
        Assert.AreEqual(HttpStatusCode.OK, (await a.Get($"/api/projects/{projectId}")).Status);
    }

    [TestMethod]
    public async Task ACompanyIdInAHeaderOrQuery_IsIgnored()
    {
        // R4: the company comes from the account, never from the request.
        var (a, _, companyA) = await RegisteredAdmin("a");
        var (b, _, _) = await RegisteredAdmin("b");
        var projectId = SeedProject(companyA, "alpha only");

        b.Http.DefaultRequestHeaders.Add("X-Company-Id", companyA.ToString());
        var viaHeader = await b.Get($"/api/projects/{projectId}?companyId={companyA}");

        Assert.AreEqual(HttpStatusCode.NotFound, viaHeader.Status);
        Assert.IsFalse((await b.Get($"/api/projects?companyId={companyA}")).Body.Contains("alpha only"));
    }

    [TestMethod]
    public async Task AnotherCompanysTeamMember_IsNotFound_ForEveryTeamAction()
    {
        // R4, R7, R8
        var (a, _, _) = await RegisteredAdmin("a");
        var (b, _, _) = await RegisteredAdmin("b");
        var (_, _, mateId) = await TeammateIn(b, "Operator");

        Assert.AreEqual(HttpStatusCode.NotFound, (await a.Put($"/api/team/{mateId}/role", new { role = "Viewer" })).Status);
        Assert.AreEqual(HttpStatusCode.NotFound, (await a.Post($"/api/team/{mateId}/deactivate")).Status);
        Assert.AreEqual(HttpStatusCode.NotFound, (await a.Post($"/api/team/{mateId}/activate")).Status);
        Assert.IsFalse((await a.Get("/api/team")).Body.Contains(mateId.ToString()));
        Assert.AreEqual("Operator", Db.Scalar<string>("SELECT Role FROM `User` WHERE Id = @u", ("u", mateId.ToString())));
    }

    // ── R6 R7 R8 R9 team ─────────────────────────────────────────────────────

    [TestMethod]
    public async Task Teammate_IsCreatedInTheAdminsCompany_AndNeverEchoesThePassword()
    {
        // R6
        var (admin, _, companyId) = await RegisteredAdmin();
        var email = NewEmail("mate");

        var created = await admin.Post("/api/team", new { email, name = "Priya", temporaryPassword = "temp-pass-123", role = "Operator", companyId = Guid.NewGuid() });

        Assert.AreEqual(HttpStatusCode.Created, created.Status, created.Body);
        Assert.IsFalse(created.Body.Contains("temp-pass-123"));
        Assert.IsFalse(created.Body.Contains("passwordHash", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(companyId.ToString(), Db.Scalar<string>("SELECT CompanyId FROM `User` WHERE NormalizedEmail = @e", ("e", email.ToUpperInvariant())),
            "a company id in the body is ignored");
        var list = await admin.Get("/api/team");
        StringAssert.Contains(list.Body, email);
        Assert.IsFalse(list.Body.Contains("temp-pass-123"));
    }

    [TestMethod]
    public async Task Teammate_MustChooseANewPasswordFirst_ThenWorksNormally()
    {
        // R6, R10
        var (admin, _, _) = await RegisteredAdmin();
        var email = NewEmail("temp");
        await admin.Post("/api/team", new { email, name = "Temp", temporaryPassword = "temp-pass-123", role = "Operator" });
        var mate = Factory.NewClient();

        var login = await mate.Login(email, "temp-pass-123");
        Assert.AreEqual("True", login.Text("user", "mustChangePassword"));
        Assert.AreEqual(HttpStatusCode.Forbidden, (await mate.Get("/api/projects")).Status, "nothing else works yet");
        Assert.AreEqual(HttpStatusCode.OK, (await mate.Get("/api/auth/me")).Status);

        var wrong = await mate.Post("/api/auth/change-password", new { currentPassword = "wrong-pass-123", newPassword = Password });
        Assert.AreEqual(HttpStatusCode.BadRequest, wrong.Status);
        Assert.IsTrue(wrong.HasFieldError("currentPassword"));

        var changed = await mate.Post("/api/auth/change-password", new { currentPassword = "temp-pass-123", newPassword = Password });
        Assert.AreEqual(HttpStatusCode.OK, changed.Status, changed.Body);
        Assert.AreEqual(HttpStatusCode.OK, (await mate.Get("/api/projects")).Status);
        Assert.AreEqual(HttpStatusCode.OK, (await Factory.NewClient().Login(email, Password)).Status);
    }

    [TestMethod]
    public async Task Teammate_WithATakenEmail_IsRefusedBesideTheEmailField_EvenInAnotherCompany()
    {
        // R6
        var (admin, _, _) = await RegisteredAdmin();
        var (_, otherEmail, _) = await RegisteredAdmin("other");

        var result = await admin.Post("/api/team", new { email = otherEmail.ToUpperInvariant(), name = "Dup", temporaryPassword = "temp-pass-123", role = "Viewer" });

        Assert.AreEqual(HttpStatusCode.BadRequest, result.Status);
        Assert.IsTrue(result.HasFieldError("email"));
    }

    [DataTestMethod]
    [DataRow("bad", "Name", "temp-pass-123", "Viewer", "email")]
    [DataRow("ok@example.test", "Name", "short", "Viewer", "temporaryPassword")]
    [DataRow("ok@example.test", "Name", "temp-pass-123", "", "role")]
    [DataRow("ok@example.test", "Name", "temp-pass-123", "Owner", "role")]
    public async Task Teammate_InvalidFields_AreReportedBesideTheField(string email, string name, string temp, string role, string field)
    {
        // R6
        var (admin, _, _) = await RegisteredAdmin();

        var result = await admin.Post("/api/team", new { email, name, temporaryPassword = temp, role });

        Assert.AreEqual(HttpStatusCode.BadRequest, result.Status);
        Assert.IsTrue(result.HasFieldError(field), result.Body);
    }

    [TestMethod]
    public async Task Teammate_SavedTwiceAtOnce_IsCreatedOnce()
    {
        // R6
        var (admin, _, companyId) = await RegisteredAdmin();
        var email = NewEmail("twice");

        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ =>
            admin.Post("/api/team", new { email, name = "Twice", temporaryPassword = "temp-pass-123", role = "Viewer" })));

        Assert.AreEqual(1, results.Count(r => r.Status == HttpStatusCode.Created));
        Assert.AreEqual(2L, Db.Scalar<long>("SELECT COUNT(*) FROM `User` WHERE CompanyId = @c", ("c", companyId.ToString())));
    }

    [TestMethod]
    public async Task LastActiveAdmin_CannotBeDemotedOrSwitchedOff()
    {
        // R7, R8
        var (admin, _, _) = await RegisteredAdmin();
        var me = (await admin.Get("/api/auth/me")).Text("user", "id");

        var demote = await admin.Put($"/api/team/{me}/role", new { role = "Viewer" });
        var off = await admin.Post($"/api/team/{me}/deactivate");

        Assert.AreEqual(HttpStatusCode.Conflict, demote.Status);
        StringAssert.Contains(demote.Text("message")!, "at least one active Admin");
        Assert.AreEqual(HttpStatusCode.Conflict, off.Status);
        Assert.AreEqual("Admin", Db.Scalar<string>("SELECT Role FROM `User` WHERE Id = @u", ("u", me!)));
        Assert.AreEqual(1L, Db.Scalar<long>("SELECT IsActive FROM `User` WHERE Id = @u", ("u", me!)));
    }

    [TestMethod]
    public async Task OnceAnotherAdminExists_TheFirstCanStepDown()
    {
        // R7
        var (admin, _, _) = await RegisteredAdmin();
        var me = (await admin.Get("/api/auth/me")).Text("user", "id");
        var (_, _, mateId) = await TeammateIn(admin, "Operator");

        Assert.AreEqual(HttpStatusCode.OK, (await admin.Put($"/api/team/{mateId}/role", new { role = "Admin" })).Status);
        Assert.AreEqual(HttpStatusCode.OK, (await admin.Put($"/api/team/{me}/role", new { role = "Viewer" })).Status);
    }

    [TestMethod]
    public async Task TwoAdminsStepDownAtOnce_LeaveTheCompanyWithAnAdmin()
    {
        // R7: the guard is inside one UPDATE, so both cannot win.
        var (admin, _, companyId) = await RegisteredAdmin();
        var (mate, _, mateId) = await TeammateIn(admin, "Operator");
        var me = (await admin.Get("/api/auth/me")).Text("user", "id");
        await admin.Put($"/api/team/{mateId}/role", new { role = "Admin" });

        var results = await Task.WhenAll(
            admin.Put($"/api/team/{me}/role", new { role = "Viewer" }),
            mate.Put($"/api/team/{mateId}/role", new { role = "Viewer" }));

        Assert.IsTrue(results.Any(r => r.Status == HttpStatusCode.Conflict), string.Join(" | ", results.Select(r => r.Body)));
        Assert.IsTrue(Db.Scalar<long>("SELECT COUNT(*) FROM `User` WHERE CompanyId = @c AND Role = 'Admin' AND IsActive = 1", ("c", companyId.ToString())) >= 1);
    }

    [TestMethod]
    public async Task RoleChange_WhenSomeoneElseChangedItFirst_IsReportedNotSilentlyOverwritten()
    {
        // R7
        var (admin, _, _) = await RegisteredAdmin();
        var (_, _, mateId) = await TeammateIn(admin, "Operator");
        await admin.Put($"/api/team/{mateId}/role", new { role = "Viewer" });

        var stale = await admin.Put($"/api/team/{mateId}/role", new { role = "Admin", expectedRole = "Operator" });

        Assert.AreEqual(HttpStatusCode.Conflict, stale.Status);
        Assert.AreEqual("Viewer", Db.Scalar<string>("SELECT Role FROM `User` WHERE Id = @u", ("u", mateId.ToString())));
    }

    [TestMethod]
    public async Task SwitchingOff_StopsSignInAndAnOpenSession_KeepsEverything_AndCanBeUndone()
    {
        // R8
        var (admin, _, companyId) = await RegisteredAdmin();
        var (mate, mateEmail, mateId) = await TeammateIn(admin, "Operator");
        Assert.AreEqual(HttpStatusCode.OK, (await mate.Get("/api/projects")).Status);

        var off = await admin.Post($"/api/team/{mateId}/deactivate");
        Assert.AreEqual(HttpStatusCode.OK, off.Status, off.Body);

        Assert.AreEqual(HttpStatusCode.Unauthorized, (await mate.Get("/api/projects")).Status, "an open session loses access");
        var login = await Factory.NewClient().Login(mateEmail);
        Assert.AreEqual(HttpStatusCode.Forbidden, login.Status);
        StringAssert.Contains(login.Text("message")!, "switched off");
        Assert.AreEqual(1L, Db.Scalar<long>("SELECT COUNT(*) FROM `User` WHERE Id = @u", ("u", mateId.ToString())), "nothing is deleted");
        Assert.AreEqual((await admin.Post("/api/team", new { email = mateEmail, name = "again", temporaryPassword = "temp-pass-123", role = "Viewer" })).Status,
            HttpStatusCode.BadRequest, "the email stays taken");

        Assert.AreEqual(HttpStatusCode.OK, (await admin.Post($"/api/team/{mateId}/activate")).Status);
        Assert.AreEqual(HttpStatusCode.OK, (await Factory.NewClient().Login(mateEmail)).Status, "the old password works again");
        Assert.IsTrue(Db.Scalar<long>("SELECT COUNT(*) FROM `User` WHERE CompanyId = @c", ("c", companyId.ToString())) >= 2);
    }

    [TestMethod]
    public async Task SwitchingOffOrOn_Twice_DoesNothingAndShowsNoError()
    {
        // R8
        var (admin, _, _) = await RegisteredAdmin();
        var (_, _, mateId) = await TeammateIn(admin, "Viewer");

        Assert.AreEqual(HttpStatusCode.OK, (await admin.Post($"/api/team/{mateId}/activate")).Status);
        Assert.AreEqual(HttpStatusCode.OK, (await admin.Post($"/api/team/{mateId}/deactivate")).Status);
        Assert.AreEqual(HttpStatusCode.OK, (await admin.Post($"/api/team/{mateId}/deactivate")).Status);
    }

    [TestMethod]
    public async Task BuildStartedByASwitchedOffTeammate_StillShowsTheirName()
    {
        // R8: switching off keeps everything — including the name on the builds the person started.
        var (admin, _, companyId) = await RegisteredAdmin();
        var projectId = SeedProject(companyId, "Initiated");
        var (mate, _, mateId) = await TeammateIn(admin, "Operator");

        var started = await mate.Post("/api/builds/start",
            new { projectId = projectId.ToString(), serviceVersions = Array.Empty<object>() });
        Assert.AreEqual(HttpStatusCode.Accepted, started.Status, started.Body);
        var buildId = started.Text("buildId")!;

        var before = await mate.Get($"/api/builds/{buildId}");
        Assert.AreEqual(HttpStatusCode.OK, before.Status, before.Body);
        Assert.AreEqual("Operator Person", before.Text("initiator"), "who started the build is recorded");

        Assert.AreEqual(HttpStatusCode.OK, (await admin.Post($"/api/team/{mateId}/deactivate")).Status, "the teammate is switched off");

        var after = await admin.Get($"/api/builds/{buildId}");
        Assert.AreEqual("Operator Person", after.Text("initiator"), "the build still shows the switched-off person's name");
        var history = await admin.Get($"/api/builds?projectId={projectId}");
        StringAssert.Contains(history.Body, "Operator Person", "the build history still shows the name");
    }

    [TestMethod]
    public async Task TeamList_IsPaged_OrderedByName_WithATotal_AndAServerSideMaximum()
    {
        // R9, API-005
        var (admin, _, _) = await RegisteredAdmin();
        foreach (var name in new[] { "Zed", "Amy", "Bob" })
            await admin.Post("/api/team", new { email = NewEmail(name), name, temporaryPassword = "temp-pass-123", role = "Viewer" });

        var first = await admin.Get("/api/team?page=1&pageSize=2");
        var second = await admin.Get("/api/team?page=2&pageSize=2");
        var huge = await admin.Get("/api/team?pageSize=100000");

        Assert.AreEqual("4", first.Text("total"));
        Assert.AreEqual(2, first.Json.GetProperty("items").GetArrayLength());
        Assert.AreEqual(2, second.Json.GetProperty("items").GetArrayLength());
        var names = first.Json.GetProperty("items").EnumerateArray().Concat(second.Json.GetProperty("items").EnumerateArray())
            .Select(i => i.GetProperty("name").GetString()!).ToList();
        CollectionAssert.AreEqual(names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(), names);
        Assert.AreEqual("50", huge.Text("pageSize"));
        Assert.AreEqual("1", (await admin.Get("/api/team")).Text("page"));
        Assert.AreEqual("20", (await admin.Get("/api/team")).Text("pageSize"));
    }

    [TestMethod]
    public async Task CompanyWithOnlyItsAdmin_ListsThatPerson()
    {
        // R9
        var (admin, email, _) = await RegisteredAdmin();

        var list = await admin.Get("/api/team");

        Assert.AreEqual("1", list.Text("total"));
        StringAssert.Contains(list.Body, email);
    }

    [TestMethod]
    public async Task TeamList_With50People_AndSignIn_AreEachUnderOneSecond()
    {
        // performance NFR
        var (admin, email, companyId) = await RegisteredAdmin();
        for (var i = 0; i < 49; i++)
            Db.Execute("INSERT INTO `User` (Id, CompanyId, Email, NormalizedEmail, PasswordHash, Name, Role, SecurityStamp) VALUES (@u, @c, @e, @n, NULL, @name, 'Viewer', 'stamp')",
                true, ("u", Guid.NewGuid().ToString()), ("c", companyId.ToString()), ("e", $"p{i}-{Guid.NewGuid():N}@example.test"),
                ("n", $"P{i}-{Guid.NewGuid():N}@EXAMPLE.TEST"), ("name", $"Person {i:00}"));

        var watch = Stopwatch.StartNew();
        var list = await admin.Get("/api/team?pageSize=50");
        var listTime = watch.Elapsed;
        watch.Restart();
        var login = await Factory.NewClient().Login(email);
        var loginTime = watch.Elapsed;

        Assert.AreEqual("50", list.Text("total"));
        Assert.AreEqual(HttpStatusCode.OK, login.Status);
        Assert.IsTrue(listTime < TimeSpan.FromSeconds(1), listTime.ToString());
        Assert.IsTrue(loginTime < TimeSpan.FromSeconds(1), loginTime.ToString());
    }

    // ── R10 reset ────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task ForgotPassword_SaysTheSameThing_WhetherOrNotTheEmailExists_AndNeverShowsTheLink()
    {
        // R10
        var (_, email, _) = await RegisteredAdmin();
        var client = Factory.NewClient();

        var known = await client.Post("/api/auth/forgot-password", new { email });
        var unknown = await client.Post("/api/auth/forgot-password", new { email = NewEmail("ghost") });

        Assert.AreEqual(HttpStatusCode.OK, known.Status);
        Assert.AreEqual(known.Body, unknown.Body);
        Assert.IsTrue(Factory.Email.Sent.Any(s => s.Recipient == email));
        var token = Factory.Email.Sent.Last(s => s.Recipient == email).Token;
        Assert.IsFalse(known.Body.Contains(token));
    }

    [TestMethod]
    public async Task ResetLink_WorksOnce_ThenSaysItIsUsedOrExpired()
    {
        // R10
        var (_, email, _) = await RegisteredAdmin();
        var client = Factory.NewClient();
        await client.Post("/api/auth/forgot-password", new { email });
        var token = Factory.Email.Sent.Last(s => s.Recipient == email).Token;

        var first = await client.Post("/api/auth/reset-password", new { token, newPassword = "brand-new-pass-1" });
        var second = await client.Post("/api/auth/reset-password", new { token, newPassword = "another-new-pass-2" });

        Assert.AreEqual(HttpStatusCode.OK, first.Status, first.Body);
        Assert.AreEqual(HttpStatusCode.BadRequest, second.Status);
        StringAssert.Contains(second.Text("message")!, "invalid or has expired");
        Assert.AreEqual(HttpStatusCode.OK, (await Factory.NewClient().Login(email, "brand-new-pass-1")).Status);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await Factory.NewClient().Login(email, Password)).Status);
    }

    [TestMethod]
    public async Task ResetLink_WithAGarbageToken_IsRefusedPlainly()
    {
        // R10
        var result = await Factory.NewClient().Post("/api/auth/reset-password", new { token = "nonsense", newPassword = "brand-new-pass-1" });

        Assert.AreEqual(HttpStatusCode.BadRequest, result.Status);
        Assert.AreEqual("application/json", result.ContentType);
    }

    [TestMethod]
    public async Task ResettingForASwitchedOffAccount_NeitherWorksNorSwitchesItOn()
    {
        // R10
        var (admin, _, _) = await RegisteredAdmin();
        var (_, mateEmail, mateId) = await TeammateIn(admin, "Viewer");
        var client = Factory.NewClient();
        await client.Post("/api/auth/forgot-password", new { email = mateEmail });
        var token = Factory.Email.Sent.Last(s => s.Recipient == mateEmail).Token;
        await admin.Post($"/api/team/{mateId}/deactivate");

        var reset = await client.Post("/api/auth/reset-password", new { token, newPassword = "brand-new-pass-1" });

        Assert.AreEqual(HttpStatusCode.BadRequest, reset.Status);
        Assert.AreEqual(0L, Db.Scalar<long>("SELECT IsActive FROM `User` WHERE Id = @u", ("u", mateId.ToString())));
        var before = Factory.Email.Sent.Count;
        await client.Post("/api/auth/forgot-password", new { email = mateEmail });
        Assert.AreEqual(before, Factory.Email.Sent.Count, "a switched-off person gets no reset email");
    }

    [TestMethod]
    public async Task ResetRequests_AreRateLimited()
    {
        // R10: nobody can flood an inbox.
        var (_, email, _) = await RegisteredAdmin();
        var client = Factory.NewClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 8; i++) statuses.Add((await client.Post("/api/auth/forgot-password", new { email })).Status);

        CollectionAssert.Contains(statuses, HttpStatusCode.TooManyRequests);
        Assert.IsTrue(Factory.Email.Sent.Count(s => s.Recipient == email) <= 5);
    }

    // ── R3 Google ────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Google_WhenNotConfigured_IsOffAndEmailSignInStillWorks()
    {
        // R3, R13
        var client = Factory.NewClient();

        var providers = await client.Get("/api/auth/providers");
        var start = await client.Get("/api/auth/google/start");

        Assert.AreEqual("False", providers.Text("google"));
        Assert.AreEqual(HttpStatusCode.NotFound, start.Status);
        Assert.AreEqual(HttpStatusCode.OK, (await client.Register(NewEmail("nogoogle"))).Status);
    }

    // ── R11 R12 ──────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task OldCookieOrTokenFromBeforeTheSwitch_GetsAPlainSignInRequired_NotAnErrorPage()
    {
        // R12
        var client = Factory.NewClient();
        client.Http.DefaultRequestHeaders.Add("Authorization", "Bearer eyJhbGciOiJIUzI1NiJ9.e30.old-token");
        client.Http.DefaultRequestHeaders.Add("Cookie", $"{IdentityRegistration.CookieName}=garbage");

        var result = await client.Get("/api/projects");

        Assert.AreEqual(HttpStatusCode.Unauthorized, result.Status);
        Assert.AreEqual("Sign in required.", result.Text("message"));
    }

    [TestMethod]
    public async Task OldAuthEndpoints_AreGone()
    {
        // R12: no refresh tokens, no revoke-all.
        var client = Factory.NewClient();

        foreach (var url in new[] { "/api/auth/refresh", "/api/auth/revoke-all", "/api/auth/signup" })
        {
            var result = await client.Post(url);
            Assert.AreNotEqual(HttpStatusCode.OK, result.Status, url);
            Assert.IsFalse(result.Body.Contains("accessToken") || result.Body.Contains("refreshToken"), url);
        }
    }

    [TestMethod]
    public async Task ErrorResponses_NeverContainSecrets()
    {
        // security NFR: passwords never come back in an error.
        var (_, email, _) = await RegisteredAdmin();
        var client = Factory.NewClient();

        var bodies = new[]
        {
            (await client.Login(email, "super-secret-wrong-pw")).Body,
            (await client.Register(email, "super-secret-dup-pw")).Body,
            (await client.Post("/api/auth/reset-password", new { token = "t", newPassword = "super-secret-reset-pw" })).Body,
        };

        foreach (var body in bodies) Assert.IsFalse(body.Contains("super-secret"), body);
    }
}
