using System.Net.Sockets;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Renci.SshNet.Common;
using ShipRight.Modules.ConnectionTest;

namespace ShipRight.Tests.Modules.ConnectionTest;

/// <summary>Turning "could not connect" and "login refused" into a plain sentence and a next step.</summary>
[TestClass]
public class ReachAndFailureTests
{
    // ── looks like a tailnet address (wording only; nothing depends on Tailscale) ──

    [DataTestMethod]
    [DataRow("box.tail1a2b3.ts.net", true)]
    [DataRow("100.101.5.6", true)]
    [DataRow("100.64.0.1", true)]
    [DataRow("100.127.255.254", true)]
    [DataRow("100.63.0.1", false)]
    [DataRow("100.128.0.1", false)]
    [DataRow("example.com", false)]
    [DataRow("10.0.0.5", false)]
    [DataRow("", false)]
    public void LooksLikeTailnet(string host, bool expected) =>
        Assert.AreEqual(expected, ReachAdvisor.LooksLikeTailnet(host));

    // ── reachability ─────────────────────────────────────────────────────────

    [TestMethod]
    public void Reached_Passes()
    {
        var i = ReachAdvisor.Evaluate("h", TcpProbeResult.Success);
        Assert.AreEqual("reach", i.Id);
        Assert.AreEqual(CheckStatus.Pass, i.Status);
    }

    [TestMethod]
    public void NameNotFound_OnATailnetName_MentionsTailscale_WithoutDependingOnIt()
    {
        var i = ReachAdvisor.Evaluate("box.tail1a2b3.ts.net", TcpProbeResult.Failed(TcpFailure.NameNotFound));
        Assert.AreEqual(CheckStatus.Fail, i.Status);
        StringAssert.Contains(i.Message, "Tailscale");
        StringAssert.Contains(i.Fix!, "tailscale status");
    }

    [TestMethod]
    public void NameNotFound_OnAnOrdinaryName_TalksAboutTheAddress()
    {
        var i = ReachAdvisor.Evaluate("example.invalid", TcpProbeResult.Failed(TcpFailure.NameNotFound));
        StringAssert.Contains(i.Message, "address");
        Assert.IsFalse(i.Message.Contains("Tailscale"));
    }

    [TestMethod]
    public void Refused_SaysTheComputerAnswersButNothingListensOnPort22()
    {
        var i = ReachAdvisor.Evaluate("1.2.3.4", TcpProbeResult.Failed(TcpFailure.Refused));
        StringAssert.Contains(i.Message, "22");
        Assert.AreEqual(CheckStatus.Fail, i.Status);
    }

    [TestMethod]
    public void TimedOut_OnATailnetAddress_SuggestsCheckingTailscaleIsConnected()
    {
        var i = ReachAdvisor.Evaluate("100.101.5.6", TcpProbeResult.Failed(TcpFailure.TimedOut));
        StringAssert.Contains(i.Fix!, "Tailscale");
    }

    [TestMethod]
    public void TimedOut_OnAnOrdinaryAddress_SuggestsTheFirewallAndThatTheServerIsOn()
    {
        var i = ReachAdvisor.Evaluate("203.0.113.9", TcpProbeResult.Failed(TcpFailure.TimedOut));
        StringAssert.Contains(i.Fix!, "firewall");
    }

    [TestMethod]
    public void EveryFailureKind_HasAMessageAndAFix()
    {
        foreach (var kind in Enum.GetValues<TcpFailure>().Where(k => k != TcpFailure.None))
        {
            var i = ReachAdvisor.Evaluate("h", TcpProbeResult.Failed(kind));
            Assert.AreEqual(CheckStatus.Fail, i.Status, kind.ToString());
            Assert.IsFalse(string.IsNullOrWhiteSpace(i.Message), kind.ToString());
            Assert.IsFalse(string.IsNullOrWhiteSpace(i.Fix), kind.ToString());
        }
    }

    [DataTestMethod]
    [DataRow(SocketError.HostNotFound, TcpFailure.NameNotFound)]
    [DataRow(SocketError.TryAgain, TcpFailure.NameNotFound)]
    [DataRow(SocketError.ConnectionRefused, TcpFailure.Refused)]
    [DataRow(SocketError.TimedOut, TcpFailure.TimedOut)]
    [DataRow(SocketError.HostUnreachable, TcpFailure.Unreachable)]
    [DataRow(SocketError.NetworkUnreachable, TcpFailure.Unreachable)]
    [DataRow(SocketError.AccessDenied, TcpFailure.Other)]
    public void SocketErrors_AreMappedToKinds(SocketError error, TcpFailure expected) =>
        Assert.AreEqual(expected, TcpProbe.Kind(new SocketException((int)error)));

    // ── login and other connection failures ──────────────────────────────────

    [TestMethod]
    public void RefusedKey_ExplainsAuthorizedKeysAndTheUsername()
    {
        var i = SshFailureClassifier.Classify(new SshAuthenticationException("Permission denied (publickey)."), "h", "ubuntu");
        Assert.AreEqual("login", i.Id);
        Assert.AreEqual(CheckStatus.Fail, i.Status);
        StringAssert.Contains(i.Message, "key");
        StringAssert.Contains(i.Message, "ubuntu");
        StringAssert.Contains(i.Fix!, "authorized_keys");
    }

    [TestMethod]
    public void MissingKeyFile_SaysTheKeyCannotBeFound()
    {
        var i = SshFailureClassifier.Classify(new FileNotFoundException("nope", "/k/x.pem"), "h", "u");
        Assert.AreEqual("login", i.Id);
        StringAssert.Contains(i.Message, "key file");
    }

    [TestMethod]
    public void PassphraseProtectedKey_IsExplained()
    {
        var i = SshFailureClassifier.Classify(new SshPassPhraseNullOrEmptyException("pass"), "h", "u");
        StringAssert.Contains(i.Message, "passphrase");
    }

    [TestMethod]
    public void HandshakeAndTimeoutFailures_AreReportedAsReachProblems()
    {
        foreach (Exception e in new Exception[]
                 {
                     new SshConnectionException("Connection refused"),
                     new SshOperationTimeoutException("timed out"),
                     new SocketException((int)SocketError.TimedOut),
                     new TimeoutException(),
                 })
        {
            var i = SshFailureClassifier.Classify(e, "h", "u");
            Assert.AreEqual("reach", i.Id, e.GetType().Name);
            Assert.AreEqual(CheckStatus.Fail, i.Status);
        }
    }

    [TestMethod]
    public void AnythingElse_ShowsTheRealMessage_AsALoginProblem()
    {
        var i = SshFailureClassifier.Classify(new InvalidOperationException("boom"), "h", "u");
        Assert.AreEqual("login", i.Id);
        StringAssert.Contains(i.Message, "boom");
    }

    [TestMethod]
    public void NoMessageNamesTheKeyFilePath()
    {
        var i = SshFailureClassifier.Classify(new FileNotFoundException("Could not find file", "/secret/place/key.pem"), "h", "u");
        Assert.IsFalse((i.Message + i.Fix).Contains("/secret/place"));
    }
}
