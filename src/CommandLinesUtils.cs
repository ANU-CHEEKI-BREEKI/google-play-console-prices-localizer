using Newtonsoft.Json;

public static class CommandLinesUtils
{
    public static async Task<T?> LoadJson<T>(string[] args, bool logToConsole, string arg, string defaultPath)
    {
        var pathToPricesTemplate = defaultPath;
        var pathIndex = args.Select((a, i) => new { a, i }).Where(a => a.a.StartsWith(arg)).FirstOrDefault()?.i ?? -1;
        if (pathIndex >= 0)
            pathToPricesTemplate = args[pathIndex].Substring(arg.Length);

        var json = await File.ReadAllTextAsync(pathToPricesTemplate);
        if (logToConsole)
            Console.WriteLine($"loaded json: {json}");

        var pricesTemplate = JsonConvert.DeserializeObject<T>(json);
        return pricesTemplate;
    }
}

