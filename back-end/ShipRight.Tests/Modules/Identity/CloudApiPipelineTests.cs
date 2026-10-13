using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Identity;
using ShipRight.Tests.Modules.Auth;
using ShipRight.Tests.Modules.Tenancy;

namespace ShipRight.Tests.Modules.Identity;

// The cloud app run in memory through HTTP: cookies, 401 / 403 answers, public endpoints, roles and the company
// taken from the sign-in. Runs everywhere; the SQL-level checks are in the MariaDB suite (CloudApiIdentityTests).
[TestClass]
public class CloudApiPipelineTests
{
    private const string Password = "long-enough-1";

    private static InMemoryCloudFactory _factory = null!;

    [ClassInitialize]
    public static void Start(TestContext _) => _factory = new InMemoryCloudFactory();

    [ClassCleanup]
    public static void Stop() => _factory.Dispose();

    private static string NewEmail(string prefix = "user") => $"{prefix}-{Guid.NewGuid():N}@example.test";

    private static async Task<ApiClient> SignedIn(AppUser user, string password = Password)
    {
        var client = _factory.NewClient();
        var login = await client.Login(user.Email, password);
        Assert.AreEqual(HttpStatusCode.OK, login.Status, login.Body);
        return client;
    }

    // ── R5: sign-in required everywhere except the public endpoints ──────────

    [DataTestMethod]
    [DataRow("GET", "/api/projects")]
    [DataRow("GET", "/api/projects/abc")]
    [DataRow("POST", "/api/projects")]
    [DataRow("PUT", "/api/projects/abc")]
    [DataRow("DELETE", "/api/projects/abc")]
    [DataRow("GET", "/api/builds")]
    [DataRow("POST", "/api/builds/start")]
    [DataRow("POST", "/api/builds/abc/deploy")]
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
    [DataRow("POST", "/api/auth/logout")]
    [DataRow("POST", "/api/auth/change-password")]
    [DataRow("POST", "/api/system/shutdown")]
    public async Task WithoutSignIn_EveryEndpoint_AnswersSignInRequired(string method, string url)
    {
        // R5, API-001
        var result = await _factory.NewClient().Send(new HttpMethod(method), url, method == "GET" ? null : new { });

        Assert.AreEqual(HttpStatusCode.Unauthorized, result.Status, $"{method} {url}");
        Assert.AreEqual("Sign in required.", result.Text("message"));
        Assert.AreEqual("True", result.Text("isError"));
    }

    [TestMethod]
    public async Task PublicEndpoints_NeedNoSignIn()
    {
        // R5: the health check, the provider list, and the sign-in, registration and reset calls themselves.
        var client = _factory.NewClient();

        Assert.AreEqual(HttpStatusCode.OK, (await client.Get("/api/health")).Status);
        Assert.AreEqual(HttpStatusCode.OK, (await client.Get("/api/auth/providers")).Status);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await client.Post("/api/auth/login", new { email = "", password = "" })).Status);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await client.Post("/api/auth/register", new { })).Status);
        Assert.AreEqual(HttpStatusCode.OK, (await client.Post("/api/auth/forgot-password", new { email = "nobody@example.test" })).Status);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await client.Post("/api/auth/reset-password", new { token = "x", newPassword = "long-enough-1" })).Status);
    }

    [TestMethod]
    public async Task HealthCheck_WorksWithoutSignIn_AndCountsNothingOfAnyCompany()
    {
        // BOOT-001, R4
        var health = await _factory.NewClient().Get("/api/health");

        Assert.AreEqual(HttpStatusCode.OK, health.Status, health.Body);
        Assert.AreEqual("0", health.Text("projectCount"));
        Assert.AreEqual("0", health.Text("buildCount"));
        Assert.AreEqual("Cloud", health.Text("mode"));
    }

    [TestMethod]
    public async Task OldTokenOrCookieFromBeforeTheSwitch_GetsAPlainSignInRequired()
    {
        // R12
        var client = _factory.NewClient();
        client.Http.DefaultRequestHeaders.Add("Authorization", "Bearer eyJhbGciOiJIUzI1NiJ9.e30.old-token");
        client.Http.DefaultRequestHeaders.Add("Cookie", $"{IdentityRegistration.CookieName}=garbage");

        var result = await client.Get("/api/projects");

        Assert.AreEqual(HttpStatusCode.Unauthorized, result.Status);
        Assert.AreEqual("Sign in required.", result.Text("message"));
        Assert.AreEqual("application/json", result.ContentType);
    }

    [TestMethod]
    public async Task OldAuthEndpoints_NoLongerIssueTokens()
    {
        // R12
        var client = _factory.NewClient();

        foreach (var url in new[] { "/api/auth/refresh", "/api/auth/revoke-all", "/api/auth/signup" })
        {
            var result = await client.Post(url);
            Assert.AreNotEqual(HttpStatusCode.OK, result.Status, url);
            Assert.IsFalse(result.Body.Contains("accessToken") || result.Body.Contains("refreshToken"), url);
        }
    }

    // ── R2 register ──────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Register_SignsThePersonInWithACookie_AsAdminOfTheirOwnCompany_AndReturnsNoToken()
    {
        // R2, R10, R12
        var client = _factory.NewClient();
        var email = NewEmail();

        var register = await client.Register(email);

        Assert.AreEqual(HttpStatusCode.OK, register.Status, register.Body);
        Assert.AreEqual("Admin", register.Text("user", "role"));
        Assert.IsFalse(register.Body.Contains("token", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(register.Body.Contains("passwordHash", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(register.Body.Contains(Password));
        var me = await client.Get("/api/auth/me");
        Assert.AreEqual(HttpStatusCode.OK, me.Status);
        Assert.AreEqual(email, me.Text("user", "email"));
        Assert.AreEqual(register.Text("user", "companyId"), me.Text("user", "companyId"));
    }

    [TestMethod]
    public async Task Register_StoresOnlyAHash()
    {
        // R1, security NFR
        var email = NewEmail("hash");
        await _factory.NewClient().Register(email, "my-very-secret-pw");

        var stored = _factory.Users.Peek(email)!.PasswordHash;
        Assert.IsNotNull(stored);
        Assert.IsFalse(stored.Contains("my-very-secret-pw"));
        Assert.IsFalse(LegacyAwarePasswordHasher.IsLegacy(stored, out _));
    }

    [TestMethod]
    public async Task Register_WithATakenEmail_AnyCapitalisation_IsRefusedBesideTheEmailField()
    {
        // R2
        var email = NewEmail();
        await _factory.NewClient().Register(email);
        var companies = _factory.Users.CompanyCount;

        var again = await _factory.NewClient().Register(email.ToUpperInvariant());

        Assert.AreEqual(HttpStatusCode.BadRequest, again.Status);
        Assert.AreEqual("application/problem+json", again.ContentType);
        Assert.IsTrue(again.HasFieldError("email"));
        Assert.AreEqual(companies, _factory.Users.CompanyCount, "a refused registration leaves no company behind");
    }

    [TestMethod]
    public async Task Register_PressedManyTimesAtOnce_CreatesOneAccount()
    {
        // R2
        var email = NewEmail("race");

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => _factory.NewClient().Register(email)));

        Assert.AreEqual(1, results.Count(r => r.Status == HttpStatusCode.OK));
        Assert.AreEqual(5, results.Count(r => r.Status == HttpStatusCode.BadRequest));
    }

    [DataTestMethod]
    [DataRow("", "long-enough-1", "Name", "email")]
    [DataRow("   ", "long-enough-1", "Name", "email")]
    [DataRow("not-an-email", "long-enough-1", "Name", "email")]
    [DataRow("ok@example.test", "short", "Name", "password")]
    [DataRow("ok@example.test", "        ", "Name", "password")]
    [DataRow("ok@example.test", "long-enough-1", "   ", "name")]
    public async Task Register_InvalidFields_ComeBackAsProblemDetailsWithFieldErrors(string email, string password, string name, string field)
    {
        // R2, API-003
        var result = await _factory.NewClient().Post("/api/auth/register", new { email, password, name, companyName = "Co" });

        Assert.AreEqual(HttpStatusCode.BadRequest, result.Status);
        Assert.AreEqual("application/problem+json", result.ContentType);
        Assert.IsTrue(result.HasFieldError(field), result.Body);
    }

    // ── R1 sign-in ───────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Login_WrongPasswordAndUnknownEmail_LookTheSame()
    {
        // R1
        var user = _factory.Seed(NewEmail());
        var client = _factory.NewClient();

        var wrong = await client.Login(user.Email, "not-the-password");
        var unknown = await client.Login(NewEmail("nobody"), "whatever-123");

        Assert.AreEqual(HttpStatusCode.Unauthorized, wrong.Status);
        Assert.AreEqual(HttpStatusCode.Unauthorized, unknown.Status);
        Assert.AreEqual(wrong.Body, unknown.Body);
        Assert.IsFalse(wrong.Body.Contains(user.Email));
    }

    [TestMethod]
    public async Task Login_AnyCapitalisationOfTheEmail_Works()
    {
        // R1
        var user = _factory.Seed(NewEmail());

        Assert.AreEqual(HttpStatusCode.OK, (await _factory.NewClient().Login(user.Email.ToUpperInvariant())).Status);
    }

    [TestMethod]
    public async Task Login_FiveWrongPasswords_LockTheAccount_SoTheSixthTryFailsEvenWithTheRightPassword()
    {
        // R1 + security NFR
        var user = _factory.Seed(NewEmail());
        var client = _factory.NewClient();

        for (var i = 0; i < 5; i++) await client.Login(user.Email, "wrong-password-" + i);
        var sixth = await client.Login(user.Email, Password);

        Assert.AreEqual(HttpStatusCode.TooManyRequests, sixth.Status);
        StringAssert.Contains(sixth.Text("message")!, "Try again");
        Assert.IsFalse(sixth.Body.Contains("lockout", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(_factory.Users.Peek(user.Id)!.LockoutEnd > DateTime.UtcNow.AddMinutes(4));
    }

    [TestMethod]
    public async Task OldAccount_SignsInWithTheOldPassword_AndItsHashIsQuietlyUpgraded()
    {
        // R1, R12
        var legacy = AuthCharacterizationTests.BuildLegacyHash("old-password-1");
        var user = _factory.Seed(NewEmail("legacy"), passwordHash: legacy);

        var login = await _factory.NewClient().Login(user.Email.ToUpperInvariant(), "old-password-1");

        Assert.AreEqual(HttpStatusCode.OK, login.Status, login.Body);
        Assert.AreEqual("Admin", login.Text("user", "role"));
        Assert.AreEqual(user.CompanyId.ToString(), login.Text("user", "companyId"));
        var stored = _factory.Users.Peek(user.Id)!.PasswordHash!;
        Assert.AreNotEqual(legacy, stored);
        Assert.IsFalse(LegacyAwarePasswordHasher.IsLegacy(stored, out _));
        Assert.AreEqual(HttpStatusCode.OK, (await _factory.NewClient().Login(user.Email, "old-password-1")).Status);
    }

    [TestMethod]
    public async Task Logout_EndsAccessAtOnce()
    {
        // R10
        var client = await SignedIn(_factory.Seed(NewEmail()));
        Assert.AreEqual(HttpStatusCode.OK, (await client.Get("/api/projects")).Status);

        await client.Post("/api/auth/logout");

        Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.Get("/api/projects")).Status);
    }

    [TestMethod]
    public async Task ErrorResponses_NeverContainSecrets()
    {
        // security NFR
        var user = _factory.Seed(NewEmail());
        var client = _factory.NewClient();

        var bodies = new[]
        {
            (await client.Login(user.Email, "super-secret-wrong-pw")).Body,
            (await client.Register(user.Email, "super-secret-dup-pw")).Body,
            (await client.Post("/api/auth/reset-password", new { token = "t", newPassword = "super-secret-reset-pw" })).Body,
        };

        foreach (var body in bodies) Assert.IsFalse(body.Contains("super-secret"), body);
    }

    // ── R5 roles ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Viewer_CanRead_ButEveryChangeIsRefusedWithAClearMessage()
    {
        // R5
        var viewer = await SignedIn(_factory.Seed(NewEmail("viewer"), AppRoles.Viewer));

        Assert.AreEqual(HttpStatusCode.OK, (await viewer.Get("/api/projects")).Status);
        Assert.AreEqual(HttpStatusCode.OK, (await viewer.Get("/api/builds")).Status);
        Assert.AreEqual(HttpStatusCode.OK, (await viewer.Get("/api/servers")).Status);
        var start = await viewer.Post("/api/builds/start", new { projectId = "x" });
        Assert.AreEqual(HttpStatusCode.Forbidden, start.Status);
        Assert.AreEqual("You do not have permission to do that.", start.Text("message"));
        foreach (var (method, url) in new[] { ("POST", "/api/builds/abc/deploy"), ("POST", "/api/builds/abc/cancel"), ("POST", "/api/projects"),
                     ("PUT", "/api/projects/abc"), ("DELETE", "/api/projects/abc"), ("POST", "/api/servers"), ("POST", "/api/team") })
            Assert.AreEqual(HttpStatusCode.Forbidden, (await viewer.Send(new HttpMethod(method), url, new { })).Status, $"{method} {url}");
    }

    [TestMethod]
    public async Task Operator_CanBuildAndDeploy_ButNotChangeSettingsOrServers()
    {
        // R5
        var op = await SignedIn(_factory.Seed(NewEmail("operator"), AppRoles.Operator));

        Assert.AreEqual(HttpStatusCode.OK, (await op.Get("/api/projects")).Status);
        Assert.AreNotEqual(HttpStatusCode.Forbidden, (await op.Post("/api/builds/start", new { projectId = "x" })).Status);
        Assert.AreNotEqual(HttpStatusCode.Forbidden, (await op.Post("/api/builds/abc/deploy", new { })).Status);
        foreach (var (method, url) in new[] { ("POST", "/api/servers"), ("PUT", "/api/servers/abc"), ("DELETE", "/api/servers/abc"),
                     ("POST", "/api/projects"), ("DELETE", "/api/projects/abc"), ("POST", "/api/resources/scripts"), ("GET", "/api/team") })
            Assert.AreEqual(HttpStatusCode.Forbidden, (await op.Send(new HttpMethod(method), url, method == "GET" ? null : new { })).Status, $"{method} {url}");
    }

    [TestMethod]
    public async Task Admin_IsNeverRefused()
    {
        // R5
        var admin = await SignedIn(_factory.Seed(NewEmail("admin")));

        foreach (var (method, url) in new[] { ("GET", "/api/team"), ("POST", "/api/servers"), ("POST", "/api/projects"),
                     ("POST", "/api/builds/start"), ("POST", "/api/resources/scripts"), ("GET", "/api/resources/credentials") })
            Assert.AreNotEqual(HttpStatusCode.Forbidden, (await admin.Send(new HttpMethod(method), url, method == "GET" ? null : new { })).Status, $"{method} {url}");
    }

    // ── R4: the company comes from the sign-in ───────────────────────────────

    [TestMethod]
    public async Task Queries_AreFilteredByTheSignedInPersonsCompany_NotByWhatTheRequestSays()
    {
        // R4: a company id sent in a header or the query string is ignored.
        var mine = _factory.Seed(NewEmail("mine"));
        var otherCompany = Guid.NewGuid();
        var client = await SignedIn(mine);
        client.Http.DefaultRequestHeaders.Add("X-Company-Id", otherCompany.ToString());
        _factory.Db.Statements.Clear();

        await client.Get($"/api/projects?companyId={otherCompany}");
        await client.Get($"/api/projects/{Guid.NewGuid()}?companyId={otherCompany}");

        var sent = _factory.Db.Statements.Where(s => s.Sql.Contains("Project")).ToList();
        Assert.IsTrue(sent.Count >= 2);
        foreach (var statement in sent)
        {
            StringAssert.Contains(statement.Sql, "CompanyId");
            var values = RecordingDatabaseHelper.ParametersOf(statement.Parameters).Values;
            Assert.IsTrue(values.Contains(mine.CompanyId), statement.Sql);
            Assert.IsFalse(values.Contains(otherCompany), statement.Sql);
        }
    }

    [TestMethod]
    public async Task TwoCompanies_EachQueryOnlyWithTheirOwnCompanyId()
    {
        // R4
        var a = _factory.Seed(NewEmail("a"));
        var b = _factory.Seed(NewEmail("b"));
        var clientA = await SignedIn(a);
        var clientB = await SignedIn(b);
        _factory.Db.Statements.Clear();

        await clientA.Get("/api/builds");
        var fromA = _factory.Db.Statements.Where(s => s.Sql.Contains("Build")).ToList();
        _factory.Db.Statements.Clear();
        await clientB.Get("/api/builds");
        var fromB = _factory.Db.Statements.Where(s => s.Sql.Contains("Build")).ToList();

        Assert.IsTrue(fromA.Count > 0 && fromB.Count > 0);
        foreach (var s in fromA) Assert.IsTrue(RecordingDatabaseHelper.ParametersOf(s.Parameters).Values.Contains(a.CompanyId));
        foreach (var s in fromB) Assert.IsTrue(RecordingDatabaseHelper.ParametersOf(s.Parameters).Values.Contains(b.CompanyId));
        Assert.IsFalse(fromA.Concat(fromB).Any(s => RecordingDatabaseHelper.ParametersOf(s.Parameters).Values.Contains(a.CompanyId)
                                                      && RecordingDatabaseHelper.ParametersOf(s.Parameters).Values.Contains(b.CompanyId)));
    }

    // ── R6 R10 temporary passwords and teammates ─────────────────────────────

    [TestMethod]
    public async Task APersonWithATemporaryPassword_CanDoNothingElseUntilTheyChooseANewOne()
    {
        // R10, R6
        var user = _factory.Seed(NewEmail("temp"), AppRoles.Operator, "temp-pass-123", mustChange: true);
        var client = _factory.NewClient();

        var login = await client.Login(user.Email, "temp-pass-123");
        Assert.AreEqual("True", login.Text("user", "mustChangePassword"));
        Assert.AreEqual(HttpStatusCode.Forbidden, (await client.Get("/api/projects")).Status);
        Assert.AreEqual(HttpStatusCode.OK, (await client.Get("/api/auth/me")).Status);

        var wrong = await client.Post("/api/auth/change-password", new { currentPassword = "nope-nope-1", newPassword = Password });
        Assert.AreEqual(HttpStatusCode.BadRequest, wrong.Status);
        Assert.IsTrue(wrong.HasFieldError("currentPassword"));

        var changed = await client.Post("/api/auth/change-password", new { currentPassword = "temp-pass-123", newPassword = Password });
        Assert.AreEqual(HttpStatusCode.OK, changed.Status, changed.Body);
        Assert.AreEqual("False", changed.Text("user", "mustChangePassword"));
        Assert.AreEqual(HttpStatusCode.OK, (await client.Get("/api/projects")).Status);
        Assert.AreEqual(HttpStatusCode.OK, (await _factory.NewClient().Login(user.Email, Password)).Status);
    }

    [TestMethod]
    public async Task ChangePassword_ShortNewPassword_IsReportedBesideTheField()
    {
        // R10
        var client = await SignedIn(_factory.Seed(NewEmail()));

        var result = await client.Post("/api/auth/change-password", new { currentPassword = Password, newPassword = "short" });

        Assert.AreEqual(HttpStatusCode.BadRequest, result.Status);
        Assert.IsTrue(result.HasFieldError("newPassword"));
    }

    [TestMethod]
    public async Task AdminCreatesATeammate_InTheirOwnCompany_WhoMustChangeThePasswordAtFirstSignIn()
    {
        // R6
        var admin = _factory.Seed(NewEmail("boss"));
        var client = await SignedIn(admin);
        var email = NewEmail("mate");

        var created = await client.Post("/api/team", new { email, name = "Priya", temporaryPassword = "temp-pass-123", role = "Operator", companyId = Guid.NewGuid() });

        Assert.AreEqual(HttpStatusCode.Created, created.Status, created.Body);
        Assert.IsFalse(created.Body.Contains("temp-pass-123"));
        var mate = _factory.Users.Peek(email)!;
        Assert.AreEqual(admin.CompanyId, mate.CompanyId, "the company comes from the Admin, not the request");
        Assert.AreEqual(AppRoles.Operator, mate.Role);
        Assert.IsTrue(mate.MustChangePassword);
        Assert.AreNotEqual("temp-pass-123", mate.PasswordHash);
        var login = await _factory.NewClient().Login(email, "temp-pass-123");
        Assert.AreEqual("True", login.Text("user", "mustChangePassword"));
    }

    [TestMethod]
    public async Task Teammate_WithATakenEmail_IsRefusedBesideTheEmailField_AndCreatesNothing()
    {
        // R6
        var other = _factory.Seed(NewEmail("taken"), companyId: Guid.NewGuid());
        var admin = await SignedIn(_factory.Seed(NewEmail("boss")));

        var result = await admin.Post("/api/team", new { email = other.Email.ToUpperInvariant(), name = "Dup", temporaryPassword = "temp-pass-123", role = "Viewer" });

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
        // R6, API-003
        var admin = await SignedIn(_factory.Seed(NewEmail("boss")));

        var result = await admin.Post("/api/team", new { email, name, temporaryPassword = temp, role });

        Assert.AreEqual(HttpStatusCode.BadRequest, result.Status);
        Assert.AreEqual("application/problem+json", result.ContentType);
        Assert.IsTrue(result.HasFieldError(field), result.Body);
    }

    [TestMethod]
    public async Task OperatorsAndViewers_CannotUseTheTeamEndpoints()
    {
        // R6
        foreach (var role in new[] { AppRoles.Operator, AppRoles.Viewer })
        {
            var client = await SignedIn(_factory.Seed(NewEmail(role.ToLowerInvariant()), role));

            Assert.AreEqual(HttpStatusCode.Forbidden, (await client.Get("/api/team")).Status, role);
            Assert.AreEqual(HttpStatusCode.Forbidden, (await client.Post("/api/team", new { })).Status, role);
            Assert.AreEqual(HttpStatusCode.Forbidden, (await client.Put($"/api/team/{Guid.NewGuid()}/role", new { role = "Admin" })).Status, role);
            Assert.AreEqual(HttpStatusCode.Forbidden, (await client.Post($"/api/team/{Guid.NewGuid()}/deactivate")).Status, role);
        }
    }

    [TestMethod]
    public async Task TeamList_RunsAsAPagedQueryForTheAdminsCompany()
    {
        // R9, R4, API-005
        var admin = _factory.Seed(NewEmail("boss"));
        var client = await SignedIn(admin);
        _factory.Db.Statements.Clear();

        var list = await client.Get("/api/team?page=2&pageSize=100000");

        Assert.AreEqual(HttpStatusCode.OK, list.Status, list.Body);
        Assert.AreEqual("50", list.Text("pageSize"), "the page size has a server-side maximum");
        Assert.AreEqual("2", list.Text("page"));
        var page = _factory.Db.Statements.First(s => s.Sql.Contains("LIMIT"));
        var parameters = RecordingDatabaseHelper.ParametersOf(page.Parameters);
        Assert.AreEqual(admin.CompanyId, parameters["CompanyId"]);
        Assert.AreEqual(50, parameters["Take"]);
        Assert.AreEqual(50, parameters["Skip"]);
    }

    // ── R8 switched off ──────────────────────────────────────────────────────

    [TestMethod]
    public async Task ASwitchedOffPerson_CannotSignIn_AndIsToldWhy()
    {
        // R8
        var user = _factory.Seed(NewEmail("off"), AppRoles.Operator, active: false);

        var login = await _factory.NewClient().Login(user.Email);

        Assert.AreEqual(HttpStatusCode.Forbidden, login.Status);
        StringAssert.Contains(login.Text("message")!, "switched off");
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await _factory.NewClient().Get("/api/projects")).Status);
    }

    [TestMethod]
    public async Task APersonSwitchedOffWhileSignedIn_LosesAccessOnTheNextRequest()
    {
        // R8, R5
        var user = _factory.Seed(NewEmail("live"), AppRoles.Operator);
        var client = await SignedIn(user);
        Assert.AreEqual(HttpStatusCode.OK, (await client.Get("/api/projects")).Status);

        _factory.Users.SwitchOff(user.Id);

        Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.Get("/api/projects")).Status);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await _factory.NewClient().Login(user.Email)).Status);
    }

    [TestMethod]
    public async Task ARoleChangedWhileSignedIn_AppliesOnTheNextRequest()
    {
        // R5, R7: the cookie is re-issued from the stored account on each check.
        var user = _factory.Seed(NewEmail("promote"), AppRoles.Viewer);
        var client = await SignedIn(user);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await client.Post("/api/projects", new { name = "p" })).Status);

        var changed = _factory.Users.Peek(user.Id)!;
        changed.Role = AppRoles.Admin;
        _factory.Users.Put(changed);

        Assert.AreNotEqual(HttpStatusCode.Forbidden, (await client.Post("/api/projects", new { name = "p" })).Status);
    }

    // ── R10 reset ────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task ForgotPassword_AnswersTheSameForKnownAndUnknownEmails_AndSendsOnlyToTheKnownOne()
    {
        // R10
        var user = _factory.Seed(NewEmail("forgot"));
        var client = _factory.NewClient();

        var known = await client.Post("/api/auth/forgot-password", new { email = user.Email });
        var unknown = await client.Post("/api/auth/forgot-password", new { email = NewEmail("ghost") });

        Assert.AreEqual(known.Body, unknown.Body);
        Assert.AreEqual(1, _factory.Email.Sent.Count(s => s.Recipient == user.Email));
        Assert.IsFalse(known.Body.Contains(_factory.Email.Sent.Last(s => s.Recipient == user.Email).Token));
    }

    [TestMethod]
    public async Task ResetLink_WorksOnce_AndTheOldPasswordStopsWorking()
    {
        // R10
        var user = _factory.Seed(NewEmail("reset"));
        var client = _factory.NewClient();
        await client.Post("/api/auth/forgot-password", new { email = user.Email });
        var token = _factory.Email.Sent.Last(s => s.Recipient == user.Email).Token;

        var first = await client.Post("/api/auth/reset-password", new { token, newPassword = "brand-new-pass-1" });
        var second = await client.Post("/api/auth/reset-password", new { token, newPassword = "another-new-pass-2" });

        Assert.AreEqual(HttpStatusCode.OK, first.Status, first.Body);
        Assert.AreEqual(HttpStatusCode.BadRequest, second.Status);
        StringAssert.Contains(second.Text("message")!, "invalid or has expired");
        Assert.AreEqual(HttpStatusCode.OK, (await _factory.NewClient().Login(user.Email, "brand-new-pass-1")).Status);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await _factory.NewClient().Login(user.Email, Password)).Status);
    }

    [TestMethod]
    public async Task ResetLink_ForASwitchedOffAccount_DoesNotWork_AndDoesNotSwitchItOn()
    {
        // R10
        var user = _factory.Seed(NewEmail("reset-off"), AppRoles.Viewer);
        var client = _factory.NewClient();
        await client.Post("/api/auth/forgot-password", new { email = user.Email });
        var token = _factory.Email.Sent.Last(s => s.Recipient == user.Email).Token;
        _factory.Users.SwitchOff(user.Id);

        var reset = await client.Post("/api/auth/reset-password", new { token, newPassword = "brand-new-pass-1" });
        var emailsBefore = _factory.Email.Sent.Count;
        await client.Post("/api/auth/forgot-password", new { email = user.Email });

        Assert.AreEqual(HttpStatusCode.BadRequest, reset.Status);
        Assert.IsFalse(_factory.Users.Peek(user.Id)!.IsActive);
        Assert.AreEqual(emailsBefore, _factory.Email.Sent.Count, "a switched-off person gets no reset email");
    }

    [TestMethod]
    public async Task ResetLink_Garbage_IsRefusedPlainly()
    {
        // R10
        var result = await _factory.NewClient().Post("/api/auth/reset-password", new { token = "nonsense", newPassword = "brand-new-pass-1" });

        Assert.AreEqual(HttpStatusCode.BadRequest, result.Status);
        Assert.AreEqual("application/json", result.ContentType);
    }

    [TestMethod]
    public async Task ResetRequests_AreRateLimited()
    {
        // R10: nobody can flood an inbox.
        var user = _factory.Seed(NewEmail("flood"));
        var client = _factory.NewClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 8; i++) statuses.Add((await client.Post("/api/auth/forgot-password", new { email = user.Email })).Status);

        CollectionAssert.Contains(statuses, HttpStatusCode.TooManyRequests);
        Assert.IsTrue(_factory.Email.Sent.Count(s => s.Recipient == user.Email) <= 5);
    }

    // ── R3 Google ────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Google_WhenNotConfigured_IsOff_AndEmailSignInStillWorks()
    {
        // R3, R13
        var client = _factory.NewClient();

        Assert.AreEqual("False", (await client.Get("/api/auth/providers")).Text("google"));
        Assert.AreEqual(HttpStatusCode.NotFound, (await client.Get("/api/auth/google/start")).Status);
        var back = await client.Http.GetAsync("/api/auth/google/complete");
        Assert.AreEqual(HttpStatusCode.Redirect, back.StatusCode);
        StringAssert.Contains(back.Headers.Location!.OriginalString, "google_failed");
        Assert.AreEqual(HttpStatusCode.OK, (await client.Register(NewEmail("nogoogle"))).Status);
    }

    // ── SEC-10 / API-004: project reads never hand secrets to non-Admins ─────

    private const string ProjectWithSecretsJson =
        "{\"id\":\"11111111-1111-1111-1111-111111111111\",\"name\":\"Demo\"," +
        "\"services\":[{\"name\":\"api\",\"dockerImageName\":\"ghcr.io/org/api\",\"dockerUsername\":\"api-user\",\"dockerPassword\":\"d0cker-secret-pass\"}]," +
        "\"database\":{\"containerName\":\"db\",\"databaseName\":\"app\",\"rootPassword\":\"r00t-secret-pass\"}}";

    private static void MakeProjectReadsReturnASecret() =>
        _factory.Db.CustomizeBlankRow = row =>
            row.GetType().GetProperty("Data")!.SetValue(row, ProjectWithSecretsJson);

    private void ClearProjectReads()
    {
        _factory.Db.ReturnOneBlankRow = false;
        _factory.Db.CustomizeBlankRow = null;
    }

    [TestMethod]
    public async Task ProjectReads_GiveEveryRoleTheList_ButOnlyAnAdminTheStoredPasswords()
    {
        // SEC-10, API-004, R5: a Viewer or Operator can still list and open projects, but the
        // Docker registry password and the database root password never reach them.
        // Start the host first: start-up reads build history before the project rows below exist.
        Assert.AreEqual(HttpStatusCode.OK, (await _factory.NewClient().Get("/api/health")).Status);
        _factory.Db.ReturnOneBlankRow = true;
        MakeProjectReadsReturnASecret();
        try
        {
            foreach (var role in new[] { AppRoles.Viewer, AppRoles.Operator })
            {
                var client = await SignedIn(_factory.Seed(NewEmail(role.ToLowerInvariant()), role));

                var list = await client.Get("/api/projects");
                Assert.AreEqual(HttpStatusCode.OK, list.Status, list.Body);
                Assert.IsTrue(list.Body.Contains("ghcr.io/org/api"), $"{role} can still see the project");
                Assert.IsFalse(list.Body.Contains("d0cker-secret-pass"), list.Body);
                Assert.IsFalse(list.Body.Contains("r00t-secret-pass"), list.Body);

                var one = await client.Get("/api/projects/11111111-1111-1111-1111-111111111111");
                Assert.AreEqual(HttpStatusCode.OK, one.Status, one.Body);
                Assert.IsFalse(one.Body.Contains("d0cker-secret-pass"), one.Body);
                Assert.IsFalse(one.Body.Contains("r00t-secret-pass"), one.Body);
            }

            var admin = await SignedIn(_factory.Seed(NewEmail("admin"), AppRoles.Admin));
            var adminList = await admin.Get("/api/projects");
            Assert.AreEqual(HttpStatusCode.OK, adminList.Status, adminList.Body);
            Assert.IsTrue(adminList.Body.Contains("d0cker-secret-pass"), adminList.Body);
            Assert.IsTrue(adminList.Body.Contains("r00t-secret-pass"), adminList.Body);
        }
        finally
        {
            ClearProjectReads();
        }
    }
}
