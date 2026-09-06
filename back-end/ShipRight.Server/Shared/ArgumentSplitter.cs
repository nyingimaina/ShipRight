using System.Text;

namespace ShipRight.Shared;

public static class ArgumentSplitter
{
    public static string[] Split(string? args)
    {
        if (string.IsNullOrWhiteSpace(args))
            return [];

        var result = new List<string>();
        var current = new StringBuilder();
        bool inQuote = false;

        for (int i = 0; i < args.Length; i++)
        {
            var c = args[i];

            if (c == '"')
            {
                if (inQuote && i + 1 < args.Length && args[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuote = !inQuote;
                }
            }
            else if (c == ' ' && !inQuote)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
            result.Add(current.ToString());

        return [.. result];
    }
}
