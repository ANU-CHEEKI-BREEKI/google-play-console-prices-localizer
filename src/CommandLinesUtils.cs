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

    /// <summary>
    /// a parsed csv: the header row as it was written, and the data rows keyed by header name
    /// </summary>
    public class CsvTable
    {
        /// <summary>original, non lowercased header names, so things like locale codes keep their casing</summary>
        public List<string> Headers { get; set; } = new();

        /// <summary>every row is a map of "header name" -> "cell value", the lookup is case insensitive</summary>
        public List<Dictionary<string, string>> Rows { get; set; } = new();
    }

    /// <summary>
    /// Loads a csv file as a list of rows, where each row is a map of "header name" -> "cell value".
    ///
    /// Tolerates the way spreadsheet apps (macos Numbers, Excel, Google Sheets) export tables:
    /// - a separator is auto detected, both ';' and ',' are supported
    /// - leading title/blank lines before the header row are skipped
    /// - quoted cells are unwrapped, including embedded separators, newlines and doubled quotes ("")
    /// </summary>
    public static async Task<List<Dictionary<string, string>>> LoadCsv(string path, string fallbackPath, bool logToConsole)
        => (await LoadCsvTable(path, fallbackPath, logToConsole)).Rows;

    /// <summary>
    /// same as <see cref="LoadCsv"/>, but also gives back the header row with its original casing
    /// </summary>
    public static async Task<CsvTable> LoadCsvTable(string path, string fallbackPath, bool logToConsole)
    {
        var resolvedPath = path;

        if (!File.Exists(resolvedPath))
            resolvedPath = fallbackPath;

        var csv = await File.ReadAllTextAsync(resolvedPath);
        if (logToConsole)
            Console.WriteLine($"loaded csv: {resolvedPath}");

        var separator = DetectCsvSeparator(csv);
        if (logToConsole)
            Console.WriteLine($"detected csv separator: '{separator}'");

        var rows = ParseCsv(csv, separator);

        // the header is the first row with more than one cell.
        // it skips exported title lines like "product-definitions" and blank lines
        var headerIndex = rows.FindIndex(r => r.Count > 1);
        if (headerIndex < 0)
            return new();

        var header = rows[headerIndex].Select(h => h.Trim()).ToList();

        var table = new CsvTable { Headers = header };
        for (int i = headerIndex + 1; i < rows.Count; i++)
        {
            var cells = rows[i];

            // skip empty lines
            if (cells.All(string.IsNullOrWhiteSpace))
                continue;

            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int c = 0; c < header.Count; c++)
                row[header[c]] = c < cells.Count ? cells[c].Trim() : "";

            table.Rows.Add(row);
        }

        return table;
    }

    /// <summary>
    /// Writes a csv that spreadsheet apps and the translation tooling both read back as is.
    /// every cell is quoted, so separators and newlines inside descriptions survive the round trip
    /// </summary>
    public static async Task SaveCsv(string path, List<string> headers, List<List<string>> rows, char separator = ',')
    {
        var builder = new System.Text.StringBuilder();

        builder.AppendLine(string.Join(separator, headers.Select(Quote)));

        foreach (var row in rows)
            builder.AppendLine(string.Join(separator, row.Select(Quote)));

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        await File.WriteAllTextAsync(path, builder.ToString());

        static string Quote(string cell) => $"\"{(cell ?? "").Replace("\"", "\"\"")}\"";
    }

    /// <summary>
    /// picks the separator that splits the header row into the most cells.
    /// looking only at the header keeps commas inside descriptions from voting
    /// </summary>
    private static char DetectCsvSeparator(string csv)
    {
        var candidates = new[] { ';', ',', '\t' };

        foreach (var line in csv.Split('\n'))
        {
            var best = candidates
                .Select(s => new { Separator = s, Count = ParseCsvLine(line, s).Count })
                .OrderByDescending(x => x.Count)
                .First();

            // this is the header row, use whatever splits it best
            if (best.Count > 1)
                return best.Separator;
        }

        return ';';
    }

    private static List<List<string>> ParseCsv(string csv, char separator)
    {
        var rows = new List<List<string>>();
        var cells = new List<string>();
        var cell = new System.Text.StringBuilder();
        var inQuotes = false;

        for (int i = 0; i < csv.Length; i++)
        {
            var ch = csv[i];

            if (inQuotes)
            {
                if (ch == '"')
                {
                    // "" inside a quoted cell is an escaped quote
                    if (i + 1 < csv.Length && csv[i + 1] == '"')
                    {
                        cell.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    cell.Append(ch);
                }
                continue;
            }

            if (ch == '"')
            {
                inQuotes = true;
            }
            else if (ch == separator)
            {
                cells.Add(cell.ToString());
                cell.Clear();
            }
            else if (ch == '\n' || ch == '\r')
            {
                // swallow the \n of a \r\n pair
                if (ch == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n')
                    i++;

                cells.Add(cell.ToString());
                cell.Clear();
                rows.Add(cells);
                cells = new List<string>();
            }
            else
            {
                cell.Append(ch);
            }
        }

        if (cell.Length > 0 || cells.Count > 0)
        {
            cells.Add(cell.ToString());
            rows.Add(cells);
        }

        return rows;
    }

    private static List<string> ParseCsvLine(string line, char separator)
        => ParseCsv(line, separator).FirstOrDefault() ?? new();

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

