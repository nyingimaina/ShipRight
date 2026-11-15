using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Desktop.Services;

namespace ShipRight.Tests;

[TestClass]
public class WindowTitleTests
{
    [TestMethod]
    public void Title_shows_the_product_and_its_version()
    {
        Assert.AreEqual("ShipRight 4.10.8", WindowTitle.For("4.10.8"));
    }

    [TestMethod]
    public void Title_drops_build_metadata_after_a_plus()
    {
        Assert.AreEqual("ShipRight 4.10.8", WindowTitle.For("4.10.8+abc123"));
    }

    [TestMethod]
    public void Title_is_just_the_product_when_the_version_is_unknown()
    {
        Assert.AreEqual("ShipRight", WindowTitle.For(null));
        Assert.AreEqual("ShipRight", WindowTitle.For("  "));
    }

    [TestMethod]
    public void Window_has_no_menu_bar()
    {
        var axaml = File.ReadAllText(FindUp("back-end/ShipRight.Desktop/MainWindow.axaml"));
        Assert.IsFalse(axaml.Contains("<Menu"), "the menu bar must stay removed");
    }

    private static string FindUp(string rel)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, rel);
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException(rel);
    }
}
