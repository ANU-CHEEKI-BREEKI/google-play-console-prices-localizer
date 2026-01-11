using Newtonsoft.Json;

public static class CommandLinesUtils
{
    public class ResolvedPathGetter
    {
        public string ResolvedPath { get; set; } = "";
    }

    public static async Task<T?> LoadJson<T>(string path, string fallbackPath, bool logToConsole, ResolvedPathGetter? resolvedPathGetter = null)
    {
        var resolvedPath = path;

        if (!File.Exists(resolvedPath))
            resolvedPath = fallbackPath;

        var json = await File.ReadAllTextAsync(resolvedPath);
        if (logToConsole)
            Console.WriteLine($"loaded json: {json}");

        var pricesTemplate = JsonConvert.DeserializeObject<T>(json);

        if (resolvedPathGetter is not null)
            resolvedPathGetter.ResolvedPath = resolvedPath;

        return pricesTemplate;
    }

    public static async Task<T?> LoadJson<T>(this string[] args, bool logToConsole, string arg, string defaultPath, ResolvedPathGetter? resolvedPathGetter = null)
    {
        var resolvedPath = defaultPath;

        var pathIndex = args.Select((a, i) => new { a, i }).Where(a => a.a.StartsWith(arg)).FirstOrDefault()?.i ?? -1;
        if (pathIndex >= 0)
            resolvedPath = args[pathIndex + 1];

        if (resolvedPathGetter is not null)
            resolvedPathGetter.ResolvedPath = resolvedPath;

        return await LoadJson<T>(resolvedPath, defaultPath, logToConsole, resolvedPathGetter);
    }

    public static bool HasFlag(this string[] args, string flag)
        => args.Contains(flag);

    public static string TryGetOption(this string[] args, string arg, string defaultValue)
    {
        var pathIndex = args
            .Select((a, i) => new { a, i })
            .Where(a => a.a.StartsWith(arg))
            .FirstOrDefault()?.i ?? -1;

        if (pathIndex < 0 || pathIndex + 1 >= args.Length)
            return defaultValue;

        return args[pathIndex + 1];
    }

    public static void PrintDescription(string description, int indent = 8)
    {
        const int totalWidth = 80;
        int textWidth = totalWidth - indent;
        string padding = new string(' ', indent);

        var words = description.Split(' ');
        string currentLine = "";

        foreach (var word in words)
        {
            if ((currentLine + word).Length > textWidth)
            {
                Console.WriteLine(padding + currentLine.TrimEnd());
                currentLine = "";
            }
            currentLine += word + " ";
        }
        Console.WriteLine(padding + currentLine.TrimEnd());
    }

    public static void PrintOption(string option, string description, int firstColumnWidth = 30)
    {
        const int totalWidth = 100; // Total terminal width
        int descriptionWidth = totalWidth - firstColumnWidth;

        // Split description into lines that fit the remaining width
        var words = description.Split(' ');
        var lines = new List<string>();
        var currentLine = "";

        foreach (var word in words)
        {
            if ((currentLine + word).Length > descriptionWidth)
            {
                lines.Add(currentLine.TrimEnd());
                currentLine = "";
            }
            currentLine += word + " ";
        }
        lines.Add(currentLine.TrimEnd());

        // Print the first line: [Option][Padding][First part of description]
        Console.Write($"  {option.PadRight(firstColumnWidth - 2)} ");
        Console.WriteLine(lines[0]);

        // Print remaining lines: [Padding][Remaining description]
        for (int i = 1; i < lines.Count; i++)
        {
            Console.WriteLine(new string(' ', firstColumnWidth) + lines[i]);
        }
    }
}

