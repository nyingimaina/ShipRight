using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MySql.Data.MySqlClient;

namespace ShipRight.Tests.Modules.Identity;

// A throw-away MariaDB database for tests that go through HTTP (TEST-002).
// Point SHIPRIGHT_TEST_DB_CONNECTION at a server (no Database=), for example the compose database:
//   Server=localhost;Port=3306;User Id=root;Password=<root password>;
// Each test class gets its own database, built from schema.sql, and drops it afterwards. When the setting is
// absent the tests report "inconclusive" instead of failing, so a machine without MariaDB can still run the suite.
public sealed class MariaDbTestDatabase : IDisposable
{
    public const string SettingName = "SHIPRIGHT_TEST_DB_CONNECTION";

    private readonly string _serverConnection;

    public string Name { get; } = "shipright_test_" + Guid.NewGuid().ToString("N")[..10];
    public string ConnectionString => $"{_serverConnection.TrimEnd(';')};Database={Name};Charset=utf8;ConvertZeroDateTime=True;";

    private MariaDbTestDatabase(string serverConnection) => _serverConnection = serverConnection;

    // Creates the database from schema.sql, or marks the calling test inconclusive when no server is configured.
    public static MariaDbTestDatabase CreateOrSkip()
    {
        var server = Environment.GetEnvironmentVariable(SettingName);
        if (string.IsNullOrWhiteSpace(server))
            Assert.Inconclusive($"Set {SettingName} to a MariaDB server connection string to run the HTTP tests.");

        var database = new MariaDbTestDatabase(server!);
        try
        {
            database.Execute($"CREATE DATABASE `{database.Name}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci", useDatabase: false);
            database.RunScript(File.ReadAllText(SchemaPath()));
        }
        catch
        {
            database.Dispose();
            throw;
        }
        return database;
    }

    // Same, but with the shape the database had before the Identity switch, so the upgrade can be tested.
    public static MariaDbTestDatabase CreateLegacyOrSkip()
    {
        var server = Environment.GetEnvironmentVariable(SettingName);
        if (string.IsNullOrWhiteSpace(server))
            Assert.Inconclusive($"Set {SettingName} to a MariaDB server connection string to run the HTTP tests.");

        var database = new MariaDbTestDatabase(server!);
        database.Execute($"CREATE DATABASE `{database.Name}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci", useDatabase: false);
        database.RunScript(LegacySchema);
        return database;
    }

    public static string SchemaPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "back-end", "ShipRight.Server", "Database", "schema.sql")))
            dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new DirectoryNotFoundException("repo root"), "back-end", "ShipRight.Server", "Database", "schema.sql");
    }

    public void RunScript(string script)
    {
        // schema.sql names its own database; tests use their own.
        var body = Regex.Replace(script, @"(?im)^\s*(CREATE DATABASE|USE)\b.*$", "");
        using var connection = Open();
        new MySqlScript(connection, body).Execute();
    }

    public int Execute(string sql, bool useDatabase = true, params (string Name, object Value)[] parameters)
    {
        using var connection = useDatabase ? Open() : OpenServer();
        using var command = new MySqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command.ExecuteNonQuery();
    }

    public T? Scalar<T>(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = Open();
        using var command = new MySqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        var result = command.ExecuteScalar();
        return result is null or DBNull ? default : (T)Convert.ChangeType(result, typeof(T));
    }

    private MySqlConnection Open()
    {
        var connection = new MySqlConnection(ConnectionString);
        connection.Open();
        return connection;
    }

    private MySqlConnection OpenServer()
    {
        var connection = new MySqlConnection(_serverConnection);
        connection.Open();
        return connection;
    }

    public void Dispose()
    {
        try { Execute($"DROP DATABASE IF EXISTS `{Name}`", useDatabase: false); }
        catch { /* best effort: a leftover test database is harmless */ }
        MySqlConnection.ClearAllPools();
    }

    // The tables as they were before this change (Company, User with IsAdmin/TokenVersion, the two token tables, Project).
    private const string LegacySchema = """
        CREATE TABLE Company (
            Id CHAR(36) NOT NULL PRIMARY KEY, Name VARCHAR(255) NOT NULL, Slug VARCHAR(255) NOT NULL,
            Created DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
            Modified DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
            Deleted BOOLEAN NOT NULL DEFAULT FALSE, UNIQUE KEY uq_company_slug (Slug)) ENGINE=InnoDB;
        CREATE TABLE User (
            Id CHAR(36) NOT NULL PRIMARY KEY, CompanyId CHAR(36) NOT NULL, Email VARCHAR(255) NOT NULL,
            PasswordHash VARCHAR(255) NOT NULL, Name VARCHAR(255) NOT NULL DEFAULT '',
            IsAdmin BOOLEAN NOT NULL DEFAULT FALSE, TokenVersion INT NOT NULL DEFAULT 1,
            Created DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
            Modified DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
            Deleted BOOLEAN NOT NULL DEFAULT FALSE, UNIQUE KEY uq_user_email (Email), KEY idx_user_company (CompanyId),
            CONSTRAINT fk_user_company FOREIGN KEY (CompanyId) REFERENCES Company(Id)) ENGINE=InnoDB;
        CREATE TABLE RefreshToken (
            Id CHAR(36) NOT NULL PRIMARY KEY, UserId CHAR(36) NOT NULL, TokenHash VARCHAR(64) NOT NULL,
            ExpiresAt DATETIME NOT NULL, Created DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
            Modified DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
            Deleted BOOLEAN NOT NULL DEFAULT FALSE, RevokedAt DATETIME NULL,
            CONSTRAINT fk_refresh_token_user FOREIGN KEY (UserId) REFERENCES User(Id)) ENGINE=InnoDB;
        CREATE TABLE ResetToken (
            Id CHAR(36) NOT NULL PRIMARY KEY, UserId CHAR(36) NOT NULL, TokenHash VARCHAR(64) NOT NULL,
            ExpiresAt DATETIME NOT NULL, Created DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
            Modified DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
            Deleted BOOLEAN NOT NULL DEFAULT FALSE, UsedAt DATETIME NULL,
            CONSTRAINT fk_reset_token_user FOREIGN KEY (UserId) REFERENCES User(Id)) ENGINE=InnoDB;
        CREATE TABLE Project (
            Id CHAR(36) NOT NULL PRIMARY KEY, CompanyId CHAR(36) NOT NULL, Name VARCHAR(255) NOT NULL,
            Data LONGTEXT NOT NULL, Created DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
            Modified DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
            Deleted BOOLEAN NOT NULL DEFAULT FALSE, KEY idx_project_company (CompanyId),
            CONSTRAINT fk_project_company FOREIGN KEY (CompanyId) REFERENCES Company(Id)) ENGINE=InnoDB;
        """;
}
