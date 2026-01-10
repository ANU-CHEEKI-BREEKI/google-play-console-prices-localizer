using Newtonsoft.Json;

public static class CommandLinesUtils
{
    public class ResolvedPathGetter
    {
        public string ResolvedPath { get; set; } = "";
    }

    public static async Task<T?> LoadJson<T>(string[] args, bool logToConsole, string arg, string defaultPath, ResolvedPathGetter? resolvedPathGetter = null)
    {
        var resolvedPath = defaultPath;

        var pathIndex = args.Select((a, i) => new { a, i }).Where(a => a.a.StartsWith(arg)).FirstOrDefault()?.i ?? -1;
        if (pathIndex >= 0)
            resolvedPath = args[pathIndex + 1];

        if (resolvedPathGetter is not null)
            resolvedPathGetter.ResolvedPath = resolvedPath;

        var json = await File.ReadAllTextAsync(resolvedPath);
        if (logToConsole)
            Console.WriteLine($"loaded json: {json}");

        var pricesTemplate = JsonConvert.DeserializeObject<T>(json);

        return pricesTemplate;
    }
}

