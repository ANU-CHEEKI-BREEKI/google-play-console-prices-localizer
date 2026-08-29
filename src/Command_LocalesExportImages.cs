using System.Text.RegularExpressions;
using Google.Apis.AndroidPublisher.v3;
using Google.Apis.AndroidPublisher.v3.Data;

namespace ANU.APIs.GoogleDeveloperAPI.IAPManaging
{
    /// <summary>
    /// Downloads every store listing image - screenshots, icon, feature graphic, tv banner - into
    /// one folder per language, so they can be localized offline and imported back.
    ///
    /// Read only towards Google: the edit it opens to reach the listings is thrown away. Towards the
    /// disk it is not - the exported folders are the mirror of what is online, so the numbered files
    /// of a previous export are overwritten and stale ones removed. Localize, then import, then
    /// export again; not the other way around.
    ///
    /// Google hands out preview urls, not the uploaded files. Asked with '=s0' the image service
    /// serves the full resolution, pixel identical - but re-encoded, so the bytes and hashes differ
    /// from the original upload. That is exactly why the manifest records the hash of the file as it
    /// landed on disk: it is the only hash the import can compare a local file against.
    /// </summary>
    public class Command_LocalesExportImages : Command_LocalesImagesBase
    {
        /// <summary>an app with many languages easily reaches several hundred images</summary>
        const int MaxParallel = 6;

        /// <summary>the file names this export owns: '01.png', '07.jpg'. Everything else is somebody's work</summary>
        static readonly Regex OwnFiles = new(@"^\d\d\.[A-Za-z]+$", RegexOptions.Compiled);

        /// <summary>one remote image resolved to a destination, so the download loop needs no api calls</summary>
        record PlannedDownload(string Language, string Type, int Position, string ImageId, string Url);

        public override async Task ExecuteAsync()
        {
            try
            {
                var verbose = Args.HasFlag("-v");

                if (string.IsNullOrWhiteSpace(Package))
                {
                    Console.WriteLine("no package name. specify it in config.json or with --package");
                    return;
                }

                if (string.IsNullOrWhiteSpace(ImagesDir) || File.Exists(ImagesDir))
                {
                    Console.WriteLine($"[ERROR] '{ImagesDir}' is not a directory to write the images into.");
                    Console.WriteLine("        set 'StoreImagesDirPath' in your config.json, or pass --images-dir <path>");
                    return;
                }

                var types = ResolveTypes();
                if (types is null)
                    return;

                Console.WriteLine("reading the store listing...");

                var planned = new List<PlannedDownload>();
                List<string> languages;

                AppEdit? edit = null;
                try
                {
                    edit = await Service!.Edits.Insert(null, Package).ExecuteAsync();

                    languages = FilterLanguages(await ListingLanguages(edit.Id));

                    if (languages.Count == 0)
                    {
                        Console.WriteLine("the store listing has no languages, so it has no images either.");
                        return;
                    }

                    Console.WriteLine($"{languages.Count} language(s), asking for {types.Count} image type(s) each...");

                    planned = await Scan(edit.Id, languages, types);
                }
                finally
                {
                    // the urls outlive the edit, only the listing calls needed it
                    await DiscardEdit(edit, verbose);
                }

                Console.WriteLine($"{planned.Count} image(s) online.");

                CleanOwnFiles(languages, types);

                if (planned.Count == 0)
                {
                    Console.WriteLine("nothing to download. Create '<language>/<type>' folders with your images and run 'locales import images'.");
                    return;
                }

                var manifest = await DownloadAll(planned, verbose);

                await SaveManifest(manifest);

                Console.WriteLine();
                Console.WriteLine($"written:  {Path.GetFullPath(ImagesDir)}");
                Console.WriteLine($"manifest: {Path.GetFullPath(ManifestPath)}");
                Console.WriteLine($"{manifest.Count} of {planned.Count} image(s) in {languages.Count} language(s)");

                PrintCoverage(manifest, languages, types);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

        /// <summary>every image of every language and type, a listing call per pair</summary>
        async Task<List<PlannedDownload>> Scan(string editId, List<string> languages, List<string> types)
        {
            using var throttle = new SemaphoreSlim(MaxParallel);
            var planned = new System.Collections.Concurrent.ConcurrentBag<PlannedDownload>();

            var tasks = languages.SelectMany(language => types.Select(async type =>
            {
                await throttle.WaitAsync();
                try
                {
                    var response = await Service!.Edits.Images
                        .List(Package, editId, language, TypeEnum<EditsResource.ImagesResource.ListRequest.ImageTypeEnum>(type))
                        .ExecuteAsync();

                    var images = response.Images ?? [];

                    for (int i = 0; i < images.Count; i++)
                    {
                        if (string.IsNullOrWhiteSpace(images[i].Url))
                            continue;

                        planned.Add(new PlannedDownload(language, type, i + 1, images[i].Id ?? "", images[i].Url!));
                    }
                }
                finally
                {
                    throttle.Release();
                }
            }));

            await Task.WhenAll(tasks);

            return [.. planned.OrderBy(p => p.Language, StringComparer.Ordinal).ThenBy(p => p.Type, StringComparer.Ordinal).ThenBy(p => p.Position)];
        }

        /// <summary>
        /// removes the numbered files of a previous export from the folders about to be written, so
        /// the folders end up the exact mirror of what is online - five images now where eight were
        /// before must not leave three stale ones. Files named any other way are left alone.
        /// </summary>
        void CleanOwnFiles(List<string> languages, List<string> types)
        {
            foreach (var language in languages)
            {
                foreach (var type in types)
                {
                    var folder = Path.Combine(ImagesDir, language, type);
                    if (!Directory.Exists(folder))
                        continue;

                    foreach (var file in Directory.EnumerateFiles(folder))
                    {
                        if (OwnFiles.IsMatch(Path.GetFileName(file)))
                            File.Delete(file);
                    }
                }
            }
        }

        async Task<List<ManifestRow>> DownloadAll(List<PlannedDownload> planned, bool verbose)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            using var throttle = new SemaphoreSlim(MaxParallel);

            var manifest = new System.Collections.Concurrent.ConcurrentBag<ManifestRow>();
            var failed = 0;

            var tasks = planned.Select(async item =>
            {
                await throttle.WaitAsync();
                try
                {
                    // the api url serves a preview size; '=s0' asks the image service for the original resolution
                    var url = item.Url.Contains('=') ? item.Url : item.Url + "=s0";

                    var bytes = await http.GetByteArrayAsync(url);

                    var file = Path.Combine(item.Language, item.Type, $"{item.Position:00}{ExtensionOf(bytes)}");
                    var path = Path.Combine(ImagesDir, file);

                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await File.WriteAllBytesAsync(path, bytes);

                    manifest.Add(new ManifestRow(item.Language, item.Type, item.Position, file, item.ImageId, Sha256Of(bytes)));

                    if (verbose)
                        Console.WriteLine($"   {item.Language,-10} {item.Type,-22} #{item.Position:00} {bytes.Length / 1024} KB");
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    Console.WriteLine($"Warning: could not download {item.Language}/{item.Type} #{item.Position:00}: {ex.Message}");
                }
                finally
                {
                    throttle.Release();
                }
            });

            await Task.WhenAll(tasks);

            if (failed > 0)
                Console.WriteLine($"{failed} download(s) failed, they are not in the manifest. Re-run to try again.");

            return [.. manifest];
        }

        /// <summary>
        /// which language has how many images of which type, because a language with no localized
        /// screenshots is exactly the thing worth seeing before translating begins
        /// </summary>
        static void PrintCoverage(List<ManifestRow> manifest, List<string> languages, List<string> types)
        {
            var used = types.Where(t => manifest.Any(m => m.Type == t)).ToList();
            if (used.Count == 0)
                return;

            Console.WriteLine();
            Console.WriteLine($"filled in ({string.Join(", ", used)}):");

            foreach (var language in languages)
            {
                var counts = used.Select(t => manifest.Count(m => m.Language == language && m.Type == t));
                var line = string.Join("  ", counts.Select(c => $"{c,2}"));
                var total = manifest.Count(m => m.Language == language);
                var note = total == 0 ? "  <- no images of its own" : "";
                Console.WriteLine($"        {language,-10} {line}{note}");
            }
        }

        public override string Name => "locales export images";

        public override string Description
            => "Downloads the store listing images - screenshots, icon, feature graphic - into one folder per language, ready to be localized and imported back.";

        public override void PrintHelp()
        {
            Console.WriteLine("locales export images [--images-dir <path>] [--types <type[,type...]>] [--locales <code[,code...]>] [-v]");
            Console.WriteLine();
            Console.WriteLine();

            Console.WriteLine("description:");
            CommandLinesUtils.PrintDescription(Description);
            CommandLinesUtils.PrintDescription("Layout: '<dir>/<language>/<type>/01.png', one folder per store language, one subfolder per image type, files numbered in the order the store shows them. The types are the api names: " + string.Join(", ", AllTypes) + ".");
            CommandLinesUtils.PrintDescription($"A '{ManifestFileName}' is written next to the folders. It records where every remote image landed and the hash of the file on disk - that is how 'locales import images' can tell a folder you localized from one that is still exactly what the export wrote, and skip the latter.");
            CommandLinesUtils.PrintDescription("Read only towards Google: the edit it opens to reach the listings is discarded. The folders on disk are rewritten though - numbered files of a previous export are overwritten and stale ones removed, so localize and import before exporting again. Files named any other way are never touched.");
            CommandLinesUtils.PrintDescription("Google serves the images re-encoded at full resolution: the pixels are what is online, the exact original bytes are not. To localize a language, replace the files in its folder - same numbering, png or jpeg - or create the '<language>/<type>' folder if the language has no images yet.");

            Console.WriteLine();
            Console.WriteLine("options:");

            CommandLinesUtils.PrintOption(
                "--images-dir <path>",
                "Specifies the directory to write the folders into. If not specified, used path from global config json ('StoreImagesDirPath'), which defaults to './store-images' next to it."
            );
            CommandLinesUtils.PrintOption(
                "--types <type[,type...]>",
                "Only these image types, comma separated, e.g. 'phoneScreenshots,featureGraphic'. Default is all of them."
            );
            CommandLinesUtils.PrintOption(
                "--locales <code[,code...]>",
                "Only these store languages, comma separated. Default is every language the store listing has."
            );
            CommandLinesUtils.PrintOption(
                "-v",
                "Include additional verbose output"
            );

            CommandLinesUtils.PrintCommonOptions();
        }
    }
}
