using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Database;

namespace ShipRight.Tests.Modules.Database;

/// <summary>
/// Kestrel refuses a request body over 30 MB by default. The decrypt tool accepts backups up to 4 GB, so the route must
/// raise that transport limit before the multipart reader starts; otherwise a real 88 MB backup never reaches the code
/// that could open it.
/// </summary>
[TestClass]
public class BackupToolRouterTests
{
    private sealed class MaxBody : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly { get; set; }
        public long? MaxRequestBodySize { get; set; }
    }

    private static DefaultHttpContext Context(MaxBody feature)
    {
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);
        return context;
    }

    [TestMethod]
    public void DecryptUpload_AllowsTheLargestBackupItAdvertises()
    {
        var feature = new MaxBody { MaxRequestBodySize = 30_000_000 };
        var context = Context(feature);

        BackupToolRouter.RaiseUploadLimit(context.Request);

        Assert.IsTrue(feature.MaxRequestBodySize >= 4L * 1024 * 1024 * 1024,
            "the route's own 4 GB limit must be reachable through Kestrel");
    }

    [TestMethod]
    public void DecryptUpload_LeavesAHostFixedLimitAlone()
    {
        var feature = new MaxBody { IsReadOnly = true, MaxRequestBodySize = 30_000_000 };
        var context = Context(feature);

        BackupToolRouter.RaiseUploadLimit(context.Request);

        Assert.AreEqual(30_000_000, feature.MaxRequestBodySize);
    }

    [TestMethod]
    public void DecryptUpload_DoesNotFailWhenTheHostOffersNoLimitFeature()
    {
        var context = new DefaultHttpContext();

        BackupToolRouter.RaiseUploadLimit(context.Request);
    }

    [TestMethod]
    public void UploadedName_IsReducedToASafeFileName()
    {
        Assert.AreEqual("backup.sql.zst", BackupToolRouter.SafeFileName(@"C:\Users\me\backup.sql.zst"));
        Assert.AreEqual("backup.sql.zst", BackupToolRouter.SafeFileName("/home/me/backup.sql.zst"));
        Assert.AreEqual("dailies_2026-11-27_02-00-04_schedule.sql.zst", BackupToolRouter.SafeFileName("dailies_2026-11-27_02-00-04_schedule.sql.zst"));
        Assert.AreEqual(string.Empty, BackupToolRouter.SafeFileName(".."));
        Assert.AreEqual(string.Empty, BackupToolRouter.SafeFileName(null));
    }
}
