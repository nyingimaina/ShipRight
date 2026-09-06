using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Resources;

namespace ShipRight.Tests.Modules.Resources;

[TestClass]
public class AwsCredentialsParserTests
{
    [TestMethod]
    public void EmptyInput_ReturnsNoProfiles()
    {
        var result = AwsCredentialsParser.Parse(null, null);
        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void DefaultAndNamedProfiles_FromCredentialsFile()
    {
        var credentials = """
            [default]
            aws_access_key_id = AKIA123
            aws_secret_access_key = secret1

            [prod]
            aws_access_key_id = AKIA456
            aws_secret_access_key = secret2
            """;

        var result = AwsCredentialsParser.Parse(credentials, null);

        Assert.AreEqual(2, result.Count);
        Assert.AreEqual("default", result[0].Name);
        Assert.AreEqual("prod", result[1].Name);
        Assert.IsTrue(result.All(p => p.HasKeys));
    }

    [TestMethod]
    public void ConfigProfilePrefix_IsNormalized()
    {
        var config = """
            [profile shipright-prod]
            region = eu-west-1
            """;

        var result = AwsCredentialsParser.Parse(null, config);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("shipright-prod", result[0].Name);
        Assert.AreEqual("eu-west-1", result[0].Region);
        Assert.IsFalse(result[0].HasKeys);
    }

    [TestMethod]
    public void CredentialsAndConfig_AreMergedIntoOneProfile()
    {
        var credentials = """
            [prod]
            aws_access_key_id = AKIA123
            aws_secret_access_key = secret1
            """;
        var config = """
            [profile prod]
            region = us-east-1
            """;

        var result = AwsCredentialsParser.Parse(credentials, config);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("prod", result[0].Name);
        Assert.AreEqual("us-east-1", result[0].Region);
        Assert.IsTrue(result[0].HasKeys);
    }

    [TestMethod]
    public void MissingSecretKey_ReportsNoKeys()
    {
        var credentials = """
            [half]
            aws_access_key_id = AKIA123
            """;

        var result = AwsCredentialsParser.Parse(credentials, null);

        Assert.AreEqual(1, result.Count);
        Assert.IsFalse(result[0].HasKeys);
    }

    [TestMethod]
    public void SsoProfile_IsFlaggedUnsupported()
    {
        var config = """
            [profile sso-user]
            sso_start_url = https://my-sso.awsapps.com/start
            sso_account_id = 123456789012
            sso_role_name = ReadOnly
            sso_region = us-east-1
            """;

        var result = AwsCredentialsParser.Parse(null, config);

        Assert.AreEqual(1, result.Count);
        Assert.IsNotNull(result[0].UnsupportedReason);
        StringAssert.Contains(result[0].UnsupportedReason!, "SSO");
    }

    [TestMethod]
    public void CredentialProcess_IsFlaggedUnsupported()
    {
        var config = """
            [profile assume]
            credential_process = my-assumerole-program
            """;

        var result = AwsCredentialsParser.Parse(null, config);

        Assert.AreEqual(1, result.Count);
        Assert.IsNotNull(result[0].UnsupportedReason);
        StringAssert.Contains(result[0].UnsupportedReason!, "credential_process");
    }

    [TestMethod]
    public void CommentsTabsAndBlankLines_AreIgnored()
    {
        var credentials = """
            # top comment
            ; another comment
            [prod]	
            	aws_access_key_id = AKIA123
            	aws_secret_access_key = secret1
            """;

        var result = AwsCredentialsParser.Parse(credentials, null);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("prod", result[0].Name);
        Assert.IsTrue(result[0].HasKeys);
    }

    [TestMethod]
    public void SecretValues_NeverAppearInResults()
    {
        var credentials = """
            [prod]
            aws_access_key_id = AKIASUPERSECRETACCESSKEYID
            aws_secret_access_key = zzz-super-secret-value-42
            """;

        var result = AwsCredentialsParser.Parse(credentials, null);
        var serialized = string.Join("|", result.Select(p => string.Join(",", new[] { p.Name, p.Region, p.UnsupportedReason })));

        Assert.IsFalse(serialized.Contains("AKIASUPERSECRET"));
        Assert.IsFalse(serialized.Contains("super-secret-value-42"));
    }

    [TestMethod]
    public void EmptySection_BeforeAnyKey_IsSkipped()
    {
        var credentials = "[empty]\n\n[real]\naws_access_key_id = X\naws_secret_access_key = Y\n";

        var result = AwsCredentialsParser.Parse(credentials, null);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("real", result[0].Name);
    }
}