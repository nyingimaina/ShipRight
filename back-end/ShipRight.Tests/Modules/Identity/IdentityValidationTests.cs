using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Identity;

namespace ShipRight.Tests.Modules.Identity;

[TestClass]
public class IdentityValidationTests
{
    private static RegisterRequest ValidRegister() => new("person@example.test", "long-enough-1", "Person", "Acme");

    [TestMethod]
    public void Register_WithValidFields_HasNoErrors()
    {
        // R2
        Assert.AreEqual(0, IdentityValidation.Register(ValidRegister()).Count);
    }

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("not-an-email")]
    [DataRow("two@@example.test")]
    [DataRow("nodot@example")]
    public void Register_BadEmail_IsReportedBesideTheEmailField(string? email)
    {
        // R2: empty or whitespace-only counts as missing.
        var errors = IdentityValidation.Register(ValidRegister() with { Email = email });

        Assert.IsTrue(errors.ContainsKey("email"));
    }

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("        ")]
    [DataRow("short")]
    [DataRow("1234567")]
    public void Register_ShortOrBlankPassword_IsReportedBesideThePasswordField(string? password)
    {
        // R2: at least 8 characters, and eight spaces do not count.
        Assert.IsTrue(IdentityValidation.Register(ValidRegister() with { Password = password }).ContainsKey("password"));
    }

    [TestMethod]
    public void Register_EightCharacterPassword_IsAccepted()
    {
        // R2
        Assert.IsFalse(IdentityValidation.Register(ValidRegister() with { Password = "12345678" }).ContainsKey("password"));
    }

    [TestMethod]
    public void Register_NamesOver200Characters_AreRefused_BothFields()
    {
        // R2
        var errors = IdentityValidation.Register(ValidRegister() with { Name = new string('n', 201), CompanyName = new string('c', 201) });

        Assert.IsTrue(errors.ContainsKey("name"));
        Assert.IsTrue(errors.ContainsKey("companyName"));
        Assert.AreEqual(0, IdentityValidation.Register(ValidRegister() with { Name = new string('n', 200), CompanyName = null }).Count);
    }

    [TestMethod]
    public void Register_BlankName_IsMissing_ButCompanyNameMayBeLeftOut()
    {
        // R2
        var errors = IdentityValidation.Register(ValidRegister() with { Name = "  ", CompanyName = "  " });

        Assert.IsTrue(errors.ContainsKey("name"));
        Assert.IsFalse(errors.ContainsKey("companyName"));
    }

    [TestMethod]
    public void Teammate_NeedsEmailNameTemporaryPasswordAndAKnownRole()
    {
        // R6
        var errors = IdentityValidation.Teammate(new CreateTeammateRequest("bad", "", "short", null));

        Assert.IsTrue(errors.ContainsKey("email"));
        Assert.IsTrue(errors.ContainsKey("name"));
        Assert.IsTrue(errors.ContainsKey("temporaryPassword"));
        Assert.IsTrue(errors.ContainsKey("role"));
    }

    [TestMethod]
    public void Teammate_UnknownRole_IsRefused_KnownRolesAccepted()
    {
        // R6, R4: nothing beyond Admin, Operator and Viewer.
        Assert.IsTrue(IdentityValidation.Teammate(new("a@example.test", "A", "long-enough-1", "Owner")).ContainsKey("role"));
        foreach (var role in AppRoles.All)
            Assert.AreEqual(0, IdentityValidation.Teammate(new("a@example.test", "A", "long-enough-1", role)).Count);
    }

    [TestMethod]
    public void RoleChange_NeedsAKnownRole()
    {
        // R7
        Assert.IsTrue(IdentityValidation.Role(new ChangeRoleRequest(null, null)).ContainsKey("role"));
        Assert.IsTrue(IdentityValidation.Role(new ChangeRoleRequest("god", null)).ContainsKey("role"));
        Assert.AreEqual(0, IdentityValidation.Role(new ChangeRoleRequest("Viewer", "Admin")).Count);
    }
}
