using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Desktop.Services;

namespace ShipRight.Tests;

[TestClass]
public class NotificationBridgeTests
{
    [TestMethod]
    public void ProcessMessage_ValidNotification_CallsShowAsync()
    {
        var calls = new List<(string Title, string Message, string? Type)>();
        var bridge = new NotificationBridge(new FakeNotification((t, m, n) => calls.Add((t, m, n))));

        bridge.ProcessMessage("""{"type":"notification","title":"Build Complete","message":"Success","notificationType":"build_completed"}""");

        Assert.AreEqual(1, calls.Count);
        Assert.AreEqual("Build Complete", calls[0].Title);
        Assert.AreEqual("Success", calls[0].Message);
        Assert.AreEqual("build_completed", calls[0].Type);
    }

    [TestMethod]
    public void ProcessMessage_EmptyBody_Skips()
    {
        var calls = new List<(string Title, string Message, string? Type)>();
        var bridge = new NotificationBridge(new FakeNotification((t, m, n) => calls.Add((t, m, n))));

        bridge.ProcessMessage("");

        Assert.AreEqual(0, calls.Count);
    }

    [TestMethod]
    public void ProcessMessage_WhitespaceBody_Skips()
    {
        var calls = new List<(string Title, string Message, string? Type)>();
        var bridge = new NotificationBridge(new FakeNotification((t, m, n) => calls.Add((t, m, n))));

        bridge.ProcessMessage("   ");

        Assert.AreEqual(0, calls.Count);
    }

    [TestMethod]
    public void ProcessMessage_MalformedJson_Skips()
    {
        var calls = new List<(string Title, string Message, string? Type)>();
        var bridge = new NotificationBridge(new FakeNotification((t, m, n) => calls.Add((t, m, n))));

        bridge.ProcessMessage("not json");

        Assert.AreEqual(0, calls.Count);
    }

    [TestMethod]
    public void ProcessMessage_WrongType_Skips()
    {
        var calls = new List<(string Title, string Message, string? Type)>();
        var bridge = new NotificationBridge(new FakeNotification((t, m, n) => calls.Add((t, m, n))));

        bridge.ProcessMessage("""{"type":"other","title":"Hello"}""");

        Assert.AreEqual(0, calls.Count);
    }

    [TestMethod]
    public void ProcessMessage_MissingTitle_Skips()
    {
        var calls = new List<(string Title, string Message, string? Type)>();
        var bridge = new NotificationBridge(new FakeNotification((t, m, n) => calls.Add((t, m, n))));

        bridge.ProcessMessage("""{"type":"notification","message":"no title"}""");

        Assert.AreEqual(0, calls.Count);
    }

    [TestMethod]
    public void ProcessMessage_EmptyTitle_Skips()
    {
        var calls = new List<(string Title, string Message, string? Type)>();
        var bridge = new NotificationBridge(new FakeNotification((t, m, n) => calls.Add((t, m, n))));

        bridge.ProcessMessage("""{"type":"notification","title":"","message":"empty title"}""");

        Assert.AreEqual(0, calls.Count);
    }

    [TestMethod]
    public void ProcessMessage_MissingMessage_StillPasses()
    {
        var calls = new List<(string Title, string Message, string? Type)>();
        var bridge = new NotificationBridge(new FakeNotification((t, m, n) => calls.Add((t, m, n))));

        bridge.ProcessMessage("""{"type":"notification","title":"No Message","notificationType":"build_completed"}""");

        Assert.AreEqual(1, calls.Count);
        Assert.AreEqual("No Message", calls[0].Title);
        Assert.AreEqual("", calls[0].Message);
    }

    [TestMethod]
    public void ProcessMessage_NullNotificationType_StillPasses()
    {
        var calls = new List<(string Title, string Message, string? Type)>();
        var bridge = new NotificationBridge(new FakeNotification((t, m, n) => calls.Add((t, m, n))));

        bridge.ProcessMessage("""{"type":"notification","title":"Generic"}""");

        Assert.AreEqual(1, calls.Count);
        Assert.AreEqual("Generic", calls[0].Title);
        Assert.IsNull(calls[0].Type);
    }

    private sealed class FakeNotification : IPlatformNotification
    {
        private readonly Action<string, string, string?> _onShow;
        public FakeNotification(Action<string, string, string?> onShow) => _onShow = onShow;
        public Task ShowAsync(string title, string message, string? notificationType = null)
        {
            _onShow(title, message, notificationType);
            return Task.CompletedTask;
        }
    }
}