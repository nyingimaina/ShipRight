using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Identity;
using ShipRight.Modules.Resources.Models;
using ShipRight.Modules.Resources.Stores;
using ShipRight.Tests.Modules.Identity;
using ShipRight.Tests.Modules.Tenancy;

namespace ShipRight.Tests.Modules.Resources;

// The key-files endpoints run through the real cloud app over HTTP (R4-R10): sign-in, roles, paging,
// validation and the write-only .pem content. SQL itself is checked through the recorded statements.
[TestClass]
public class KeyFileRouterTests
{
    private const string Secret = "-----BEGIN RSA PRIVATE KEY-----\nMII-SUPER-SECRET-BODY\n-----END RSA PRIVATE KEY-----\n";
    private const string OldSecret = "-----BEGIN RSA PRIVATE KEY-----\nOLD-BODY\n-----END RSA PRIVATE KEY-----\n";
    private const string NewSecret = "-----BEGIN OPENSSH PRIVATE KEY-----\nNEW-BODY\n-----END OPENSSH PRIVATE KEY-----\n";

    private static InMemoryCloudFactory _factory = null!;

    [ClassInitialize]
    public static void Start(TestContext _) => _factory = new InMemoryCloudFactory();

    [ClassCleanup]
    public static void Stop() => _factory.Dispose();

    private static string NewEmail(string prefix = "keyuser") => $"{prefix}-{Guid.NewGuid():N}@example.test";

    private static async Task<(ApiClient Client, AppUser User)> SignedIn(string role = AppRoles.Admin)
    {
        var user = _factory.Seed(NewEmail(role.ToLowerInvariant()), role);
        var client = _factory.NewClient();
        var login = await client.Login(user.Email);
        Assert.AreEqual(HttpStatusCode.OK, login.Status, login.Body);
        return (client, user);
    }

    private static void ClearReads()
    {
        _factory.Db.ReturnOneBlankRow = false;
        _factory.Db.CustomizeBlankRow = null;
    }

    private static void ReturnOneKeyFile(string dataJson)
    {
        _factory.Db.ReturnOneBlankRow = true;
        _factory.Db.CustomizeBlankRow = row =>
            row.GetType().GetProperty("Data")!.SetValue(row, dataJson);
    }

    // ── R8: sign-in and company scope ────────────────────────────────────────

    [TestMethod]
    public async Task WithoutSignIn_EveryKeyFileEndpoint_IsRejected()
    {
        // R8, API-001
        var client = _factory.NewClient();

        Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.Get("/api/resources/key-files")).Status);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.Get($"/api/resources/key-files/{Guid.NewGuid()}")).Status);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.Post("/api/resources/key-files", new { name = "k", content = Secret })).Status);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.Put($"/api/resources/key-files/{Guid.NewGuid()}", new { name = "k" })).Status);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.Delete($"/api/resources/key-files/{Guid.NewGuid()}")).Status);
    }

    [TestMethod]
    public async Task AnotherCompanysKeyFile_IsIndistinguishableFromOneThatDoesNotExist()
    {
        // R8, API-002: the query is filtered by the caller's company, so an unknown id is simply not found.
        var (client, _) = await SignedIn();
        ClearReads();

        var result = await client.Get($"/api/resources/key-files/{Guid.NewGuid()}");

        Assert.AreEqual(HttpStatusCode.NotFound, result.Status);
    }

    [TestMethod]
    public async Task ListQuery_FiltersByTheSignedInPersonsCompany_AndIsPagedInSql()
    {
        // R8, R9, PERF-001: one company-filtered, limited page query.
        var (client, user) = await SignedIn();
        _factory.Db.Statements.Clear();

        var list = await client.Get("/api/resources/key-files");

        Assert.AreEqual(HttpStatusCode.OK, list.Status, list.Body);
        var page = _factory.Db.Statements.First(s => s.Sql.Contains("LIMIT"));
        StringAssert.Contains(page.Sql, "CompanyId");
        var parameters = RecordingDatabaseHelper.ParametersOf(page.Parameters);
        Assert.AreEqual(user.CompanyId, parameters["CompanyId"]);
        Assert.IsTrue(page.Sql.Contains("LIMIT @Take OFFSET @Skip"), page.Sql);
    }

    // ── R9: paging ────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task List_DefaultsToTwentyPerPage_AndReportsTheTotal()
    {
        // R9
        var (client, _) = await SignedIn();
        _factory.Db.Statements.Clear();

        var list = await client.Get("/api/resources/key-files");

        Assert.AreEqual("1", list.Text("page"));
        Assert.AreEqual("20", list.Text("pageSize"));
        Assert.AreEqual(0, list.Json.GetProperty("total").GetInt32());
        Assert.AreEqual(0, list.Json.GetProperty("items").GetArrayLength());
        var parameters = RecordingDatabaseHelper.ParametersOf(_factory.Db.Statements.First(s => s.Sql.Contains("LIMIT")).Parameters);
        Assert.AreEqual(20, parameters["Take"]);
        Assert.AreEqual(0, parameters["Skip"]);
    }

    [TestMethod]
    public async Task List_CapsThePageSizeAtTheServerMaximum()
    {
        // R9
        var (client, _) = await SignedIn();
        _factory.Db.Statements.Clear();

        var list = await client.Get("/api/resources/key-files?page=3&pageSize=100000");

        Assert.AreEqual("3", list.Text("page"));
        Assert.AreEqual("100", list.Text("pageSize"));
        var parameters = RecordingDatabaseHelper.ParametersOf(_factory.Db.Statements.First(s => s.Sql.Contains("LIMIT")).Parameters);
        Assert.AreEqual(100, parameters["Take"]);
        Assert.AreEqual(200, parameters["Skip"]);
    }

    [TestMethod]
    public async Task List_PageSizeZeroOrNegativePage_FallBackToTheDefaults_NeverLoadEverything()
    {
        // R9
        var (client, _) = await SignedIn();
        _factory.Db.Statements.Clear();

        var zero = await client.Get("/api/resources/key-files?page=0&pageSize=0");

        Assert.AreEqual("1", zero.Text("page"));
        Assert.AreEqual("20", zero.Text("pageSize"));
        var parameters = RecordingDatabaseHelper.ParametersOf(_factory.Db.Statements.First(s => s.Sql.Contains("LIMIT")).Parameters);
        Assert.AreEqual(20, parameters["Take"]);
    }

    [TestMethod]
    public async Task List_PagePastTheEnd_ReturnsAnEmptyPage_NotAnError()
    {
        // R9
        var (client, _) = await SignedIn();

        var list = await client.Get("/api/resources/key-files?page=999&pageSize=20");

        Assert.AreEqual(HttpStatusCode.OK, list.Status, list.Body);
        Assert.AreEqual(0, list.Json.GetProperty("items").GetArrayLength());
    }

    [TestMethod]
    public async Task List_AnAbsurdlyLargePage_IsBounded_AndNeverUsesANegativeOffset()
    {
        // R9, SEC-1: page has no upper limit from the client, so the server must bound it; Skip must stay
        // a non-negative int (a past-the-end page is an empty page, never a 500 from a negative SQL OFFSET).
        var (client, _) = await SignedIn();
        _factory.Db.Statements.Clear();

        var list = await client.Get("/api/resources/key-files?page=2000000000&pageSize=100");

        Assert.AreEqual(HttpStatusCode.OK, list.Status, list.Body);
        Assert.AreEqual(0, list.Json.GetProperty("items").GetArrayLength());
        var parameters = RecordingDatabaseHelper.ParametersOf(_factory.Db.Statements.First(s => s.Sql.Contains("LIMIT")).Parameters);
        Assert.IsTrue((int)parameters["Skip"]! >= 0, $"Skip was {parameters["Skip"]}");
    }

    // ── R5: create ────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Create_WithNameAndKeyFile_Succeeds_AndNeverReturnsTheContent()
    {
        // R5, R10
        var (client, _) = await SignedIn();

        var created = await client.Post("/api/resources/key-files", new { name = "ec2-prod", content = Secret, tags = new[] { "aws" } });

        Assert.AreEqual(HttpStatusCode.Created, created.Status, created.Body);
        Assert.AreEqual("ec2-prod", created.Text("name"));
        Assert.AreEqual("aws", created.Json.GetProperty("tags")[0].GetString());
        Assert.IsFalse(created.Body.Contains("SUPER-SECRET-BODY"), created.Body);
        Assert.IsFalse(created.Body.Contains("-----BEGIN"), created.Body);
    }

    [TestMethod]
    public async Task Create_SameNameTwice_IsAllowed_NamesAreNotUnique()
    {
        // R5
        var (client, _) = await SignedIn();

        var first = await client.Post("/api/resources/key-files", new { name = "shared", content = Secret });
        var second = await client.Post("/api/resources/key-files", new { name = "shared", content = Secret });

        Assert.AreEqual(HttpStatusCode.Created, first.Status, first.Body);
        Assert.AreEqual(HttpStatusCode.Created, second.Status, second.Body);
    }

    [DataTestMethod]
    [DataRow(null, Secret, "name")]
    [DataRow("   ", Secret, "name")]
    [DataRow("ok", null, "content")]
    [DataRow("ok", "this is not a private key", "content")]
    public async Task Create_InvalidFields_ComeBackAsProblemDetailsBesideTheField(string? name, string? content, string field)
    {
        // R5, API-003
        var (client, _) = await SignedIn();

        var result = await client.Post("/api/resources/key-files", new { name, content });

        Assert.AreEqual(HttpStatusCode.BadRequest, result.Status, result.Body);
        Assert.AreEqual("application/problem+json", result.ContentType);
        Assert.IsTrue(result.HasFieldError(field), result.Body);
    }

    [TestMethod]
    public async Task Create_NameLongerThanTwoHundredCharacters_IsRejected()
    {
        // R5
        var (client, _) = await SignedIn();

        var result = await client.Post("/api/resources/key-files", new { name = new string('a', 201), content = Secret });

        Assert.AreEqual(HttpStatusCode.BadRequest, result.Status);
        Assert.IsTrue(result.HasFieldError("name"), result.Body);
    }

    [TestMethod]
    public async Task Create_FileLargerThanSixtyFourKb_IsRejected()
    {
        // R5
        var (client, _) = await SignedIn();
        var huge = "-----BEGIN RSA PRIVATE KEY-----\n" + new string('a', 70000) + "\n-----END RSA PRIVATE KEY-----\n";

        var result = await client.Post("/api/resources/key-files", new { name = "big", content = huge });

        Assert.AreEqual(HttpStatusCode.BadRequest, result.Status);
        Assert.IsTrue(result.HasFieldError("content"), result.Body);
    }

    // ── R10: the content is write-only ────────────────────────────────────────

    [TestMethod]
    public async Task Detail_NeverReturnsTheStoredContent()
    {
        // R10
        var (client, _) = await SignedIn();
        ReturnOneKeyFile(
            "{\"id\":\"" + Guid.NewGuid() + "\",\"name\":\"k\",\"content\":\"" + Secret.Replace("\n", "\\n") + "\"," +
            "\"tags\":[],\"createdAt\":\"2020-01-01T00:00:00Z\",\"modifiedAt\":\"2020-01-01T00:00:00Z\"}");
        try
        {
            var detail = await client.Get($"/api/resources/key-files/{Guid.NewGuid()}");

            Assert.AreEqual(HttpStatusCode.OK, detail.Status, detail.Body);
            Assert.AreEqual("k", detail.Text("name"));
            Assert.IsFalse(detail.Body.Contains("SUPER-SECRET-BODY"), detail.Body);
        }
        finally { ClearReads(); }
    }

    [TestMethod]
    public async Task AValidationFailure_DoesNotEchoTheContentBack()
    {
        // R10
        var (client, _) = await SignedIn();

        var result = await client.Post("/api/resources/key-files", new { name = "", content = Secret });

        Assert.AreEqual(HttpStatusCode.BadRequest, result.Status);
        Assert.IsFalse(result.Body.Contains("SUPER-SECRET-BODY"), result.Body);
    }

    // ── R6: edit ──────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Update_NameAndTagsOnly_KeepsTheStoredContent()
    {
        // R6: editing must not blank out the .pem when only the name or tags changed.
        var (client, _) = await SignedIn();
        ReturnOneKeyFile(
            "{\"id\":\"" + Guid.NewGuid() + "\",\"name\":\"old\",\"content\":\"" + OldSecret.Replace("\n", "\\n") + "\"," +
            "\"tags\":[],\"createdAt\":\"2020-01-01T00:00:00Z\",\"modifiedAt\":\"2020-01-01T00:00:00Z\"}");
        try
        {
            _factory.Db.Statements.Clear();
            var result = await client.Put($"/api/resources/key-files/{Guid.NewGuid()}", new { name = "renamed", tags = new[] { "prod" } });

            Assert.AreEqual(HttpStatusCode.OK, result.Status, result.Body);
            Assert.AreEqual("renamed", result.Text("name"));
            Assert.IsFalse(result.Body.Contains("OLD-BODY"), result.Body);
            var update = _factory.Db.Statements.Last(s => s.Sql.StartsWith("UPDATE KeyFileResource"));
            var data = System.Text.Json.JsonDocument.Parse((string)RecordingDatabaseHelper.ParametersOf(update.Parameters)["Data"]!).RootElement;
            Assert.AreEqual("renamed", data.GetProperty("name").GetString());
            StringAssert.Contains(data.GetProperty("content").GetString(), "OLD-BODY", "the stored content must be kept");
        }
        finally { ClearReads(); }
    }

    [TestMethod]
    public async Task Update_WithANewFile_ReplacesTheContent()
    {
        // R6
        var (client, _) = await SignedIn();
        ReturnOneKeyFile(
            "{\"id\":\"" + Guid.NewGuid() + "\",\"name\":\"old\",\"content\":\"" + OldSecret.Replace("\n", "\\n") + "\"," +
            "\"tags\":[],\"createdAt\":\"2020-01-01T00:00:00Z\",\"modifiedAt\":\"2020-01-01T00:00:00Z\"}");
        try
        {
            _factory.Db.Statements.Clear();
            var result = await client.Put($"/api/resources/key-files/{Guid.NewGuid()}", new { name = "old", content = NewSecret });

            Assert.AreEqual(HttpStatusCode.OK, result.Status, result.Body);
            Assert.IsFalse(result.Body.Contains("NEW-BODY"), result.Body);
            var update = _factory.Db.Statements.Last(s => s.Sql.StartsWith("UPDATE KeyFileResource"));
            var data = System.Text.Json.JsonDocument.Parse((string)RecordingDatabaseHelper.ParametersOf(update.Parameters)["Data"]!).RootElement;
            StringAssert.Contains(data.GetProperty("content").GetString(), "NEW-BODY");
        }
        finally { ClearReads(); }
    }

    [TestMethod]
    public async Task Update_ReplacingTheContent_DoesNotTouchAnyServer()
    {
        // R6 edge case (QA-5): a server already authorized with the old content keeps working; replacing the
        // .pem writes only the KeyFileResource row.
        var (client, _) = await SignedIn();
        ReturnOneKeyFile(
            "{\"id\":\"" + Guid.NewGuid() + "\",\"name\":\"old\",\"content\":\"" + OldSecret.Replace("\n", "\\n") + "\"," +
            "\"tags\":[],\"createdAt\":\"2020-01-01T00:00:00Z\",\"modifiedAt\":\"2020-01-01T00:00:00Z\"}");
        try
        {
            _factory.Db.Statements.Clear();
            var result = await client.Put($"/api/resources/key-files/{Guid.NewGuid()}", new { name = "old", content = NewSecret });

            Assert.AreEqual(HttpStatusCode.OK, result.Status, result.Body);
            Assert.IsFalse(
                _factory.Db.Statements.Any(s => s.Sql.Contains("Server")),
                string.Join("\n", _factory.Db.Statements.Select(s => s.Sql)));
        }
        finally { ClearReads(); }
    }

    // ── R7: delete ────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Delete_SoftDeletesOnlyThatKeyFile_ForTheCallersCompany()
    {
        // R7
        var (client, user) = await SignedIn();
        ReturnOneKeyFile("{\"id\":\"" + Guid.NewGuid() + "\",\"name\":\"k\",\"content\":\"\",\"tags\":[]}");
        try
        {
            _factory.Db.Statements.Clear();
            var result = await client.Delete($"/api/resources/key-files/{Guid.NewGuid()}");

            Assert.AreEqual(HttpStatusCode.OK, result.Status, result.Body);
            var delete = _factory.Db.Statements.Last(s => s.Sql.StartsWith("UPDATE KeyFileResource"));
            StringAssert.Contains(delete.Sql, "Deleted = 1");
            StringAssert.Contains(delete.Sql, "CompanyId");
            Assert.AreEqual(user.CompanyId, RecordingDatabaseHelper.ParametersOf(delete.Parameters)["CompanyId"]);
        }
        finally { ClearReads(); }
    }

    [TestMethod]
    public async Task Delete_DoesNotTouchTheServerThatWasAuthorizedWithIt()
    {
        // R7 edge case (QA-6): deleting a key file must not delete or modify any server record; a server
        // already authorized with it keeps working.
        var (client, _) = await SignedIn();
        ReturnOneKeyFile("{\"id\":\"" + Guid.NewGuid() + "\",\"name\":\"k\",\"content\":\"\",\"tags\":[]}");
        try
        {
            _factory.Db.Statements.Clear();
            var result = await client.Delete($"/api/resources/key-files/{Guid.NewGuid()}");

            Assert.AreEqual(HttpStatusCode.OK, result.Status, result.Body);
            Assert.IsFalse(
                _factory.Db.Statements.Any(s => s.Sql.Contains("Server")),
                string.Join("\n", _factory.Db.Statements.Select(s => s.Sql)));
        }
        finally { ClearReads(); }
    }
}

// Desktop key-file store: the .pem is encrypted at rest (R10), paging is served from the store (R9),
// and a failed write never leaves a half-created key file behind (R5, REV-1).
[TestClass]
public class SqliteKeyFileResourceStoreTests : IDisposable
{
    private const string OldPem = "-----BEGIN RSA PRIVATE KEY-----\nOLD-BODY\n-----END RSA PRIVATE KEY-----\n";
    private const string NewPem = "-----BEGIN OPENSSH PRIVATE KEY-----\nNEW-BODY\n-----END OPENSSH PRIVATE KEY-----\n";

    private readonly string _tmpDir;

    public SqliteKeyFileResourceStoreTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"sr_key_file_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* temp folder, best effort */ }
    }

    private static KeyFileResource KeyFile(string name, string content) => new()
    {
        Name = name,
        Content = content,
        CreatedAt = DateTime.UtcNow,
        ModifiedAt = DateTime.UtcNow,
    };

    [TestMethod]
    public async Task Save_WhenTheWriteFails_LeavesNothingHalfCreated()
    {
        // REV-1, R5 pitfall: a failed save must not leave a key file visible in the list or on disk.
        var store = new SqliteKeyFileResourceStore(_tmpDir);
        using (var conn = new SqliteConnection($"Data Source={Path.Combine(_tmpDir, "key_files.db")}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DROP TABLE key_file_resources";
            cmd.ExecuteNonQuery();
        }

        await Assert.ThrowsExceptionAsync<SqliteException>(() => store.SaveAsync(KeyFile("k", NewPem)));

        var (items, total) = await store.GetPageAsync(1, 20);
        Assert.AreEqual(0, total, "a failed save must not leave a key file in the list");
        Assert.AreEqual(0, items.Count);
        Assert.AreEqual(0, (await new SqliteKeyFileResourceStore(_tmpDir).GetPageAsync(1, 20)).Total,
            "a failed save must not leave a key file on disk");
    }

    [TestMethod]
    public async Task TwoEdits_LaterSaveWins_AndEveryReaderSeesTheNewerRecord()
    {
        // R6 edge case (QA-4): if two people edit the same key file, the later save wins and both see it.
        var store = new SqliteKeyFileResourceStore(_tmpDir);
        var original = KeyFile("first", OldPem);
        await store.SaveAsync(original);
        await store.SaveAsync(original with { Name = "second", Content = NewPem, ModifiedAt = DateTime.UtcNow.AddSeconds(1) });

        var (items, total) = await store.GetPageAsync(1, 20);
        Assert.AreEqual(1, total);
        Assert.AreEqual("second", items[0].Name);

        var anotherReader = new SqliteKeyFileResourceStore(_tmpDir);
        var seen = await anotherReader.GetByIdAsync(original.Id);
        Assert.IsNotNull(seen);
        Assert.AreEqual("second", seen.Name);
        StringAssert.Contains(seen.Content, "NEW-BODY");
    }

    [TestMethod]
    public async Task Content_IsEncryptedAtRest_AndDecryptedOnRead()
    {
        // R10 acceptance (QA-7): the .pem is protected at rest exactly like the app's other secrets.
        var store = new SqliteKeyFileResourceStore(_tmpDir);
        var resource = KeyFile("k", NewPem);
        await store.SaveAsync(resource);

        using (var conn = new SqliteConnection($"Data Source={Path.Combine(_tmpDir, "key_files.db")}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT data FROM key_file_resources LIMIT 1";
            var raw = (string)cmd.ExecuteScalar()!;
            Assert.IsFalse(raw.Contains("NEW-BODY"), "the .pem must be encrypted at rest, not stored as plaintext");
        }

        var reopened = new SqliteKeyFileResourceStore(_tmpDir);
        StringAssert.Contains((await reopened.GetByIdAsync(resource.Id))!.Content, "NEW-BODY");
    }

    [TestMethod]
    public async Task GetPage_WithAThousandKeyFiles_ReturnsOnlyTheRequestedPage()
    {
        // PERF-001, R9 (QA-8): at 1,000 key files the first page is still 20 rows with the total.
        var store = new SqliteKeyFileResourceStore(_tmpDir);
        for (var i = 0; i < 1000; i++)
            await store.SaveAsync(KeyFile($"key-{i:D4}", NewPem));

        var (items, total) = await store.GetPageAsync(1, 20);

        Assert.AreEqual(1000, total);
        Assert.AreEqual(20, items.Count);
    }
}

// TEST-002 (QA-9): the key-file endpoints through HTTP against a real MariaDB, so the migration, the
// table shape and the store's SQL run for real. Needs SHIPRIGHT_TEST_DB_CONNECTION; without it every
// test here is inconclusive (see MariaDbTestDatabase).
[TestClass]
public class KeyFileRouterMariaDbTests
{
    private static readonly System.Text.Json.JsonSerializerOptions Camel = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
    };

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

    private static MariaDbTestDatabase Db => _database!;

    private static async Task<(ApiClient Client, Guid CompanyId)> RegisteredAdmin()
    {
        var client = _factory!.NewClient();
        var email = $"keyfile-{Guid.NewGuid():N}@example.test";
        var result = await client.Register(email);
        Assert.AreEqual(HttpStatusCode.OK, result.Status, result.Body);
        return (client, Guid.Parse(result.Text("user", "companyId")!));
    }

    private static void SeedKeyFile(Guid companyId, string name, string content, DateTime created)
    {
        var id = Guid.NewGuid();
        var data = System.Text.Json.JsonSerializer.Serialize(new
        {
            Id = id,
            Name = name,
            Content = content,
            Tags = Array.Empty<string>(),
            CreatedAt = created,
            ModifiedAt = created,
        }, Camel);
        Db.Execute(
            "INSERT INTO KeyFileResource (Id, CompanyId, Name, Data, Created, Modified, Deleted) " +
            "VALUES (@id, @company, @name, @data, @created, @modified, 0)",
            true,
            ("id", id.ToString("D")),
            ("company", companyId.ToString("D")),
            ("name", name),
            ("data", data),
            ("created", created),
            ("modified", created));
    }

    [TestMethod]
    public async Task List_AgainstRealSql_IsCompanyScoped_Paged_AndReportsTheTotal()
    {
        // R8, R9, TEST-002: company filter, LIMIT/OFFSET and COUNT run against the real KeyFileResource table.
        var (client, companyId) = await RegisteredAdmin();
        var (_, otherCompany) = await RegisteredAdmin();
        var created = DateTime.UtcNow;
        for (var i = 0; i < 25; i++)
            SeedKeyFile(companyId, $"own-{i:D2}", "-----BEGIN PRIVATE KEY-----\nx\n-----END PRIVATE KEY-----\n", created.AddSeconds(i));
        for (var i = 0; i < 5; i++)
            SeedKeyFile(otherCompany, $"theirs-{i:D2}", "-----BEGIN PRIVATE KEY-----\ny\n-----END PRIVATE KEY-----\n", created.AddSeconds(i));

        var list = await client.Get("/api/resources/key-files");

        Assert.AreEqual(HttpStatusCode.OK, list.Status, list.Body);
        Assert.AreEqual(20, list.Json.GetProperty("items").GetArrayLength());
        Assert.AreEqual("20", list.Text("pageSize"));
        Assert.AreEqual(25, list.Json.GetProperty("total").GetInt32());
        Assert.IsFalse(list.Body.Contains("PRIVATE KEY"), list.Body);
    }

    [TestMethod]
    public async Task CreateDetailAndDelete_RoundTripThroughRealSql()
    {
        // R5, R6, R7, TEST-002: the insert/select/soft-delete SQL works against the real table.
        var (client, _) = await RegisteredAdmin();

        var created = await client.Post("/api/resources/key-files", new
        {
            name = "ec2-prod",
            content = "-----BEGIN OPENSSH PRIVATE KEY-----\nREAL-SQL-BODY\n-----END OPENSSH PRIVATE KEY-----\n",
        });
        Assert.AreEqual(HttpStatusCode.Created, created.Status, created.Body);
        var id = created.Text("id")!;
        Assert.IsFalse(created.Body.Contains("REAL-SQL-BODY"), created.Body);

        var detail = await client.Get($"/api/resources/key-files/{id}");
        Assert.AreEqual(HttpStatusCode.OK, detail.Status, detail.Body);
        Assert.AreEqual("ec2-prod", detail.Text("name"));

        var deleted = await client.Delete($"/api/resources/key-files/{id}");
        Assert.AreEqual(HttpStatusCode.OK, deleted.Status, deleted.Body);
        Assert.AreEqual(HttpStatusCode.NotFound, (await client.Get($"/api/resources/key-files/{id}")).Status);

        // The content was actually stored (and is still never selected back into a metadata read).
        var stored = Db.Scalar<string>("SELECT Data FROM KeyFileResource WHERE Id = @id", ("id", id));
        StringAssert.Contains(stored, "REAL-SQL-BODY");
    }
}
