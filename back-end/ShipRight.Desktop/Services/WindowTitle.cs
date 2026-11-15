namespace ShipRight.Desktop.Services;

/// <summary>The main window's title: the product name and the version running (build metadata after '+' is left out).</summary>
public static class WindowTitle
{
    public static string For(string? version)
    {
        var v = (version ?? "").Split('+')[0].Trim();
        return v.Length == 0 ? "ShipRight" : $"ShipRight {v}";
    }
}
