using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Shared.ProcessRunner;

namespace ShipRight.Tests.Shared.ProcessRunner;

[TestClass]
public class GitAutomationTests
{
    [DataTestMethod]
    [DataRow("git", new[] { "clone", "https://host/repo" }, true)]
    [DataRow("git", new[] { "fetch", "origin" }, true)]
    [DataRow("git", new[] { "pull", "origin", "master" }, true)]
    [DataRow("git", new[] { "push", "origin", "master" }, true)]
    [DataRow("git", new[] { "ls-remote", "origin" }, true)]
    [DataRow("git", new[] { "submodule", "update", "--init" }, true)]
    [DataRow("git", new[] { "-C", "/mnt/d/repo", "pull", "origin", "master" }, true)]
    [DataRow("git", new[] { "-c", "credential.helper=", "pull" }, true)]
    [DataRow("git", new[] { "--no-pager", "fetch" }, true)]
    public void RequiresNetwork_NetworkCapableGitCommand_ReturnsTrue(
        string executable, string[] args, bool expected)
    {
        Assert.AreEqual(expected, GitAutomation.RequiresNetwork(executable, args));
    }

    [DataTestMethod]
    [DataRow("git", new[] { "status", "--porcelain" })]
    [DataRow("git", new[] { "-C", "/repo", "rev-parse", "HEAD" })]
    [DataRow("git", new[] { "checkout", "master" })]
    [DataRow("git", new[] { "commit", "-m", "chore: bump" })]
    [DataRow("git", new[] { "add", "." })]
    [DataRow("git", new[] { "fsck", "--no-progress" })]
    [DataRow("git", new[] { "tag" })]
    [DataRow("git", new[] { "merge", "--abort" })]
    [DataRow("docker", new[] { "pull" })]
    [DataRow("wsl", new[] { "git", "pull" })]
    public void RequiresNetwork_LocalOrNonGitCommand_ReturnsFalse(string executable, string[] args)
    {
        Assert.IsFalse(GitAutomation.RequiresNetwork(executable, args));
    }

    [DataTestMethod]
    [DataRow("Git", new[] { "pull" })]
    [DataRow("git.exe", new[] { "pull" })]
    [DataRow("/usr/bin/git", new[] { "pull" })]
    [DataRow("git", new string[0])]
    public void RequiresNetwork_UnexpectedExecutableShape_ReturnsFalse(string executable, string[] args)
    {
        Assert.IsFalse(GitAutomation.RequiresNetwork(executable, args));
    }

    [TestMethod]
    public void NonInteractiveEnvironment_DisablesTerminalAndDialogPrompts()
    {
        var env = GitAutomation.NonInteractiveEnvironment;

        Assert.AreEqual("0", env["GIT_TERMINAL_PROMPT"]);
        Assert.AreEqual("Never", env["GCM_INTERACTIVE"]);
    }

    [TestMethod]
    public void MergeEnvironment_NoCallerOverride_ReturnsNonInteractiveEnvironment()
    {
        var merged = GitAutomation.MergeEnvironment(null);

        Assert.AreEqual("0", merged["GIT_TERMINAL_PROMPT"]);
        Assert.AreEqual("Never", merged["GCM_INTERACTIVE"]);
    }

    [TestMethod]
    public void MergeEnvironment_CallerOverrideWins_AndCallerDictionaryIsNotMutated()
    {
        var caller = new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "1", ["CUSTOM"] = "value" };

        var merged = GitAutomation.MergeEnvironment(caller);

        Assert.AreEqual("1", merged["GIT_TERMINAL_PROMPT"]);
        Assert.AreEqual("value", merged["CUSTOM"]);
        Assert.AreEqual("1", caller["GIT_TERMINAL_PROMPT"]);
        Assert.IsFalse(caller.ContainsKey("GCM_INTERACTIVE"));
    }

    [TestMethod]
    public void ResolveTimeout_NoCallerTimeout_ReturnsDefaultNetworkTimeout()
    {
        Assert.AreEqual(GitAutomation.DefaultNetworkTimeout, GitAutomation.ResolveTimeout(null));
    }

    [TestMethod]
    public void ResolveTimeout_CallerTimeoutProvided_IsRespected()
    {
        var caller = TimeSpan.FromSeconds(42);

        Assert.AreEqual(caller, GitAutomation.ResolveTimeout(caller));
    }

    [TestMethod]
    public void Apply_NetworkGitCommand_InjectsEnvironmentAndDefaultTimeout()
    {
        var applied = GitAutomation.Apply("git", ["pull", "origin", "master"], null, null);

        Assert.AreEqual("0", applied.Env!["GIT_TERMINAL_PROMPT"]);
        Assert.AreEqual(GitAutomation.DefaultNetworkTimeout, applied.Timeout);
    }

    [TestMethod]
    public void Apply_LocalGitCommand_LeavesCallerValuesUntouched()
    {
        var callerEnv = new Dictionary<string, string> { ["CUSTOM"] = "value" };
        var callerTimeout = TimeSpan.FromSeconds(7);

        var applied = GitAutomation.Apply("git", ["status", "--porcelain"], callerEnv, callerTimeout);

        Assert.AreSame(callerEnv, applied.Env);
        Assert.AreEqual(callerTimeout, applied.Timeout);
    }

    [TestMethod]
    public void Apply_NonGitCommand_LeavesCallerValuesUntouched()
    {
        var applied = GitAutomation.Apply("docker", ["pull"], null, null);

        Assert.IsNull(applied.Env);
        Assert.IsNull(applied.Timeout);
    }
}
