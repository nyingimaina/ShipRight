namespace ShipRight.Modules.Filesystem;

/// <summary>Validation for a folder a person creates on a remote server from the folder picker.</summary>
public static class RemoteFolderName
{
    private const int MaxLength = 255;

    /// <summary>Returns a plain-language problem, or null when the name is usable.</summary>
    public static string? Validate(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return "Type a name for the new folder.";
        if (trimmed is "." or "..") return "A folder can't be called \".\" or \"..\".";
        if (trimmed.Contains('/')) return "A folder name can't contain \"/\". Create one folder at a time.";
        if (trimmed.Contains('\0')) return "The folder name has an invalid character.";
        if (trimmed.Length > MaxLength) return $"A folder name can be at most {MaxLength} characters.";
        return null;
    }

    public static string Join(string parent, string name) =>
        parent.TrimEnd('/') + "/" + name.Trim();
}
