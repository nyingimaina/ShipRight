using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Shared;

namespace ShipRight.Tests.Shared;

[TestClass]
public class ArgumentSplitterTests
{
    [TestMethod]
    public void Split_Null_ReturnsEmpty()
    {
        var result = ArgumentSplitter.Split(null);
        Assert.AreEqual(0, result.Length);
    }

    [TestMethod]
    public void Split_Empty_ReturnsEmpty()
    {
        var result = ArgumentSplitter.Split("");
        Assert.AreEqual(0, result.Length);
    }

    [TestMethod]
    public void Split_Whitespace_ReturnsEmpty()
    {
        var result = ArgumentSplitter.Split("   ");
        Assert.AreEqual(0, result.Length);
    }

    [TestMethod]
    public void Split_SingleArg_ReturnsOne()
    {
        var result = ArgumentSplitter.Split("--no-verify");
        Assert.AreEqual(1, result.Length);
        Assert.AreEqual("--no-verify", result[0]);
    }

    [TestMethod]
    public void Split_MultipleArgs_ReturnsAll()
    {
        var result = ArgumentSplitter.Split("--no-verify --force");
        Assert.AreEqual(2, result.Length);
        Assert.AreEqual("--no-verify", result[0]);
        Assert.AreEqual("--force", result[1]);
    }

    [TestMethod]
    public void Split_QuotedValue_PreservesSpaces()
    {
        var result = ArgumentSplitter.Split("--push-option=\"skip ci\"");
        Assert.AreEqual(1, result.Length);
        Assert.AreEqual("--push-option=skip ci", result[0]);
    }

    [TestMethod]
    public void Split_MultipleQuotedValues()
    {
        var result = ArgumentSplitter.Split("--opt=\"a b\" --other=\"c d\"");
        Assert.AreEqual(2, result.Length);
        Assert.AreEqual("--opt=a b", result[0]);
        Assert.AreEqual("--other=c d", result[1]);
    }

    [TestMethod]
    public void Split_EscapedQuoteInsideQuotes_ProducesLiteralQuote()
    {
        var result = ArgumentSplitter.Split("--opt=\"a\"\"b\"");
        Assert.AreEqual(1, result.Length);
        Assert.AreEqual("--opt=a\"b", result[0]);
    }

    [TestMethod]
    public void Split_MixedQuotedAndUnquoted()
    {
        var result = ArgumentSplitter.Split("--no-verify --push-option=\"skip ci\" --force");
        Assert.AreEqual(3, result.Length);
        Assert.AreEqual("--no-verify", result[0]);
        Assert.AreEqual("--push-option=skip ci", result[1]);
        Assert.AreEqual("--force", result[2]);
    }

    [TestMethod]
    public void Split_TrailingSpaces_IgnoresThem()
    {
        var result = ArgumentSplitter.Split("  --no-verify  --force  ");
        Assert.AreEqual(2, result.Length);
        Assert.AreEqual("--no-verify", result[0]);
        Assert.AreEqual("--force", result[1]);
    }

    [TestMethod]
    public void Split_UnclosedQuote_TreatsRestAsQuoted()
    {
        var result = ArgumentSplitter.Split("--opt=\"a b c");
        Assert.AreEqual(1, result.Length);
        Assert.AreEqual("--opt=a b c", result[0]);
    }
}
