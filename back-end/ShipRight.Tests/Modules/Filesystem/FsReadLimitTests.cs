using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Filesystem;

namespace ShipRight.Tests.Modules.Filesystem;

[TestClass]
public class FsReadLimitTests
{
    private static JsonElement Body(object o) => JsonSerializer.SerializeToElement(o);

    [TestMethod]
    public void A_file_over_the_preview_limit_is_reported_with_its_size_so_the_screen_can_ask_what_to_do()
    {
        var b = Body(FsRouter.TooLargeToPreview(13L * 1024 * 1024));
        Assert.IsTrue(b.GetProperty("isError").GetBoolean());
        Assert.IsTrue(b.GetProperty("tooLarge").GetBoolean());
        Assert.AreEqual(13L * 1024 * 1024, b.GetProperty("sizeBytes").GetInt64());
        Assert.AreEqual(512L * 1024, b.GetProperty("limitBytes").GetInt64());
    }

    [TestMethod]
    public void The_plain_words_message_is_unchanged_for_anything_that_only_reads_it()
    {
        var m = Body(FsRouter.TooLargeToPreview(1_000_000)).GetProperty("message").GetString()!;
        StringAssert.Contains(m, "File too large to preview");
        StringAssert.Contains(m, "1,000,000 bytes");
        StringAssert.Contains(m, "512 KB");
    }
}
