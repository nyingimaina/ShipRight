using MyApp.Api.Shared.Config;

namespace MyApp.Api.Tests;

/// <summary>The .env file: how settings such as AUTH_MODE reach the app when it is run from a terminal. No database needed.</summary>
public sealed class DotEnvTests
{
    [Fact]
    public void DotEnv_ReadsValuesAndIgnoresCommentsAndBlanks()
    {
        var values = DotEnv.Parse("# a comment\n\nDB_PASSWORD=secret one\nEMPTY=\nQUOTED=\"in quotes\"\n  SPACED = x \n");

        Assert.Equal("secret one", values["DB_PASSWORD"]);
        Assert.Equal("in quotes", values["QUOTED"]);
        Assert.Equal("x", values["SPACED"]);
        Assert.False(values.ContainsKey("EMPTY"), "an empty value counts as not set");
        Assert.Equal(3, values.Count);
    }

    [Fact]
    public void DotEnv_LoadsFromAParentFolder_WithoutOverridingWhatIsAlreadySet()
    {
        var root = Directory.CreateTempSubdirectory("devteam-dotenv-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, ".env"), "DEVTEAM_DOTENV_A=from-file\nDEVTEAM_DOTENV_B=from-file\n");
            var nested = Directory.CreateDirectory(Path.Combine(root, "api", "src")).FullName;
            Environment.SetEnvironmentVariable("DEVTEAM_DOTENV_A", null);
            Environment.SetEnvironmentVariable("DEVTEAM_DOTENV_B", "already-set");

            var found = DotEnv.Load(nested);

            Assert.True(found);
            Assert.Equal("from-file", Environment.GetEnvironmentVariable("DEVTEAM_DOTENV_A"));
            Assert.Equal("already-set", Environment.GetEnvironmentVariable("DEVTEAM_DOTENV_B"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEVTEAM_DOTENV_A", null);
            Environment.SetEnvironmentVariable("DEVTEAM_DOTENV_B", null);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void DotEnv_WithNoFile_ReportsNotFound()
    {
        var root = Directory.CreateTempSubdirectory("devteam-dotenv-").FullName;
        try { Assert.False(DotEnv.Load(root, searchUp: false)); }
        finally { Directory.Delete(root, true); }
    }
}
