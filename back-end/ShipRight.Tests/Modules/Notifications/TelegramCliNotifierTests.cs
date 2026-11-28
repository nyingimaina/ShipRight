using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Notifications;

namespace ShipRight.Tests.Modules.Notifications;

[TestClass]
public class TelegramCliNotifierTests
{
    [TestMethod]
    public void ParseError_ReadsTheJsonKindAndMessage()
    {
        var (kind, message) = TelegramCliNotifier.ParseError(
            """{"ok":false,"error":{"kind":"ChatNotFound","message":"That chat was not found."}}""", "");

        Assert.AreEqual("ChatNotFound", kind);
        Assert.AreEqual("That chat was not found.", message);
    }

    [TestMethod]
    public void ParseError_FallsBackToStderr()
    {
        var (kind, message) = TelegramCliNotifier.ParseError("not json", "the CLI is missing");

        Assert.AreEqual("Other", kind);
        Assert.AreEqual("the CLI is missing", message);
    }

    [TestMethod]
    public void ParseError_FallsBackWhenTheJsonHasNoMessage()
    {
        var (kind, message) = TelegramCliNotifier.ParseError(
            """{"ok":false,"error":{"kind":"Other"}}""", "stderr text");

        Assert.AreEqual("Other", kind);
        Assert.AreEqual("stderr text", message);
    }
}
