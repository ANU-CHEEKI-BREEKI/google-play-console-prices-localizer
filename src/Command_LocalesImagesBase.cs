using System.Security.Cryptography;

namespace ANU.APIs.GoogleDeveloperAPI.IAPManaging
{
    /// <summary>
    /// The shared half of the store images pair: the image types Google knows, the folder layout
    /// '&lt;dir&gt;/&lt;language&gt;/&lt;type&gt;/NN.png', and the manifest csv that lets the import
    /// tell an untouched export from a localized one. Screenshots, the icon, the feature graphic and
    /// the tv banner all live per language in the store listing, reachable only through an edit.
    /// </summary>
    public abstract class Command_LocalesImagesBase : Command_LocalesEditBase
    {
        /// <summary>
        /// the table the export writes next to the folders: which file came from which remote image,
        /// and the hash of the file as it landed on disk. The import compares the hashes to know
        /// which folders were localized and which are still exactly what the export wrote
        /// </summary>
        protected const string ManifestFileName = "images.csv";

        /// <summary>
        /// every image type of the store listing, in api spelling - the same strings are the folder
        /// names, so a folder maps to its api call by name alone
        /// </summary>
        protected static readonly string[] AllTypes =
        [
            "phoneScreenshots",
            "sevenInchScreenshots",
            "tenInchScreenshots",
            "tvScreenshots",
            "wearScreenshots",
            "icon",
            "featureGraphic",
            "tvBanner",
        ];

        /// <summary>google's caps: up to 8 screenshots per type, exactly one of everything else</summary>
        protected static int MaxCount(string type)
            => type.EndsWith("Screenshots", StringComparison.OrdinalIgnoreCase) ? 8 : 1;

        protected string ImagesDir => Config.StoreImagesDirPath;

        /// <summary>
        /// the --types option as canonical api names, every type when not given, null after a name
        /// nobody knows - already reported, the caller only has to stop
        /// </summary>
        protected List<string>? ResolveTypes()
        {
            var raw = Args.TryGetOption("--types", "");
            if (string.IsNullOrWhiteSpace(raw))
                return [.. AllTypes];

            var picked = new List<string>();

            foreach (var name in raw.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var canonical = AllTypes.FirstOrDefault(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase));
                if (canonical is null)
                {
                    Console.WriteLine($"[ERROR] '{name}' is not an image type. The types: {string.Join(", ", AllTypes)}");
                    return null;
                }

                picked.Add(canonical);
            }

            return picked;
        }

        /// <summary>
        /// an image type as the enum one of the image requests wants it. The generated library
        /// repeats an identical enum on every request type, so the name is parsed instead of being
        /// mapped by hand four times
        /// </summary>
        protected static TEnum TypeEnum<TEnum>(string type) where TEnum : struct, Enum
            => Enum.Parse<TEnum>(type, true);

        /// <summary>
        /// the languages the store listing has, straight off the edit. Images can only exist for a
        /// language whose listing exists, so this is the ground truth for both halves
        /// </summary>
        protected async Task<List<string>> ListingLanguages(string editId)
            => [.. ((await Service!.Edits.Listings.List(Package, editId).ExecuteAsync()).Listings ?? [])
                .Select(l => l.Language)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => l!)
                .OrderBy(l => l, StringComparer.Ordinal)];

        /// <summary>the --locales option applied to a language list, everything when it is not given</summary>
        protected List<string> FilterLanguages(List<string> languages)
        {
            if (Config.Locales is not { Count: > 0 } wanted)
                return languages;

            var picked = languages
                .Where(l => wanted.Any(w => string.Equals(w, l, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            foreach (var miss in wanted.Where(w => !languages.Any(l => string.Equals(l, w, StringComparison.OrdinalIgnoreCase))))
                Console.WriteLine($"Warning: the store listing has no language '{miss}', skipped.");

            return picked;
        }

        protected static string Sha256Of(byte[] bytes)
            => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        protected static string Sha256OfFile(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        /// <summary>'.png' or '.jpg' from what the bytes actually are, whatever the url promised</summary>
        protected static string ExtensionOf(byte[] bytes)
        {
            if (bytes is [0xFF, 0xD8, ..])
                return ".jpg";

            if (bytes is [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..])
                return ".webp";

            // png, and the fallback: Google serves store images as png or jpeg
            return ".png";
        }

        /// <summary>the mime type Google accepts for a local file, or null for a file it never takes</summary>
        protected static string? ContentTypeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            _ => null,
        };

        /// <summary>one manifest line: where a remote image landed on disk, and as what</summary>
        protected record ManifestRow(string Language, string Type, int Position, string File, string ImageId, string FileSha256);

        protected string ManifestPath => Path.Combine(ImagesDir, ManifestFileName);

        protected async Task SaveManifest(List<ManifestRow> rows)
        {
            List<string> headers = ["Language", "ImageType", "Position", "File", "ImageId", "FileSha256"];

            var table = rows
                .OrderBy(r => r.Language, StringComparer.Ordinal)
                .ThenBy(r => r.Type, StringComparer.Ordinal)
                .ThenBy(r => r.Position)
                .Select(r => new List<string> { r.Language, r.Type, r.Position.ToString(), r.File, r.ImageId, r.FileSha256 })
                .ToList();

            await CommandLinesUtils.SaveCsv(ManifestPath, headers, table);
        }

        /// <summary>the manifest of the last export, empty when there was none - that only costs the 'unchanged' shortcut</summary>
        protected async Task<List<ManifestRow>> LoadManifest(bool verbose)
        {
            if (!File.Exists(ManifestPath))
                return [];

            var table = await CommandLinesUtils.LoadCsvTable(ManifestPath, ManifestPath, verbose);

            var rows = new List<ManifestRow>();

            foreach (var row in table.Rows)
            {
                row.TryGetValue("Language", out var language);
                row.TryGetValue("ImageType", out var type);
                row.TryGetValue("Position", out var position);
                row.TryGetValue("File", out var file);
                row.TryGetValue("ImageId", out var id);
                row.TryGetValue("FileSha256", out var sha);

                if (string.IsNullOrWhiteSpace(language) || string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(file))
                    continue;

                rows.Add(new ManifestRow(
                    language,
                    type,
                    int.TryParse(position, out var parsed) ? parsed : 0,
                    file,
                    id ?? "",
                    (sha ?? "").ToLowerInvariant()
                ));
            }

            return rows;
        }
    }
}
