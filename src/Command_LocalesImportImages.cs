using Google.Apis.AndroidPublisher.v3;
using Google.Apis.AndroidPublisher.v3.Data;

namespace ANU.APIs.GoogleDeveloperAPI.IAPManaging
{
    /// <summary>
    /// Uploads the localized store images back, the other half of 'locales export images'.
    ///
    /// A language + type folder that has files replaces its online set completely - delete all,
    /// upload in file order - because the store shows screenshots in upload order and there is no
    /// other way to put five new ones in the right places. Folders that do not exist locally are
    /// never touched, and folders that are still byte for byte what the export wrote (the manifest
    /// knows) are skipped, so only what was actually localized is sent.
    ///
    /// Everything happens inside one edit, committed at the end. On a dry run and on any failure
    /// the edit is discarded, and Google forgets the deletions and uploads with it - half an import
    /// can never reach players.
    /// </summary>
    public class Command_LocalesImportImages : Command_LocalesImagesBase
    {
        static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg"];

        /// <summary>all local files of one language + type, in the order they should end up on the store</summary>
        class Group
        {
            public required string Language { get; init; }
            public required string Type { get; init; }
            public required List<string> Files { get; init; }

            public List<string> LocalShas { get; } = [];
            public IList<Image> Remote { get; set; } = [];
            public bool Unchanged { get; set; }
        }

        public override async Task ExecuteAsync()
        {
            try
            {
                var verbose = Args.HasFlag("-v");
                var dryRun = Args.HasFlag("-n") || Args.HasFlag("--dry-run");
                var keep = Args.HasFlag("--keep");

                if (string.IsNullOrWhiteSpace(Package))
                {
                    Console.WriteLine("no package name. specify it in config.json or with --package");
                    return;
                }

                if (!Directory.Exists(ImagesDir))
                {
                    Console.WriteLine($"[ERROR] no directory at {Path.GetFullPath(ImagesDir ?? "")}");
                    Console.WriteLine("        run 'locales export images' first, or pass --images-dir <path>");
                    return;
                }

                var types = ResolveTypes();
                if (types is null)
                    return;

                var groups = CollectGroups(types, keep);
                if (groups.Count == 0)
                {
                    Console.WriteLine($"no images found in {Path.GetFullPath(ImagesDir)}.");
                    Console.WriteLine("expected '<language>/<type>/<name>.png', e.g. 'uk/phoneScreenshots/01.png'.");
                    return;
                }

                Console.WriteLine($"{groups.Sum(g => g.Files.Count)} local image(s) in {groups.Count} set(s) across {groups.Select(g => g.Language).Distinct(StringComparer.OrdinalIgnoreCase).Count()} language(s).");

                var manifest = await LoadManifest(verbose);

                AppEdit? edit = null;
                var committed = false;
                try
                {
                    edit = await Service!.Edits.Insert(null, Package).ExecuteAsync();

                    groups = DropUnknownLanguages(groups, await ListingLanguages(edit.Id));
                    if (groups.Count == 0)
                        return;

                    await ReadRemote(groups, edit.Id);

                    MarkUnchanged(groups, manifest, verbose);

                    var changed = groups.Where(g => !g.Unchanged).ToList();

                    PrintPlan(groups, keep);

                    if (changed.Count == 0)
                    {
                        Console.WriteLine();
                        Console.WriteLine("everything is still what the export wrote or matches what is online. Nothing to send.");
                        return;
                    }

                    if (dryRun)
                    {
                        Console.WriteLine();
                        Console.WriteLine("dry run, nothing was sent.");
                        return;
                    }

                    Console.WriteLine();

                    var (uploaded, deleted) = await Apply(changed, edit.Id, keep, verbose);

                    Console.WriteLine();
                    Console.WriteLine("   -> committing the edit...");

                    await CommitEdit(edit.Id);
                    committed = true;

                    Console.WriteLine();
                    Console.WriteLine($"uploaded {uploaded} image(s) in {changed.Count} set(s), replaced {deleted}. Nothing else was part of the edit.");
                    PrintCommitted();
                    Console.WriteLine("run 'locales export images' again to refresh the folders and the manifest.");
                }
                catch (Google.GoogleApiException ex)
                {
                    Console.WriteLine();
                    Console.WriteLine("[ERROR] Google rejected the edit, nothing was published:");
                    Console.WriteLine($"        {ex.Message.Trim()}");

                    PrintCommitHint(ex);
                }
                finally
                {
                    // a committed edit is gone on its own, only an abandoned one needs discarding -
                    // and discarding takes every deletion and upload of a failed run with it
                    if (!committed)
                        await DiscardEdit(edit, verbose);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

        /// <summary>
        /// walks '&lt;dir&gt;/&lt;language&gt;/&lt;type&gt;' and collects the files of every set, in
        /// name order - the export numbers them '01', '02', so the name order is the store order
        /// </summary>
        List<Group> CollectGroups(List<string> types, bool keep)
        {
            var groups = new List<Group>();

            foreach (var languageDir in Directory.EnumerateDirectories(ImagesDir).OrderBy(d => d, StringComparer.Ordinal))
            {
                var language = Path.GetFileName(languageDir);

                if (Config.Locales is { Count: > 0 } wanted
                    && !wanted.Any(w => string.Equals(w, language, StringComparison.OrdinalIgnoreCase)))
                    continue;

                foreach (var typeDir in Directory.EnumerateDirectories(languageDir).OrderBy(d => d, StringComparer.Ordinal))
                {
                    var name = Path.GetFileName(typeDir);
                    var type = AllTypes.FirstOrDefault(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase));

                    if (type is null)
                    {
                        Console.WriteLine($"Warning: '{language}/{name}' is not an image type folder, skipped. The types: {string.Join(", ", AllTypes)}");
                        continue;
                    }

                    if (!types.Contains(type))
                        continue;

                    var files = Directory.EnumerateFiles(typeDir)
                        .Where(f => ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                        .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    if (files.Count == 0)
                        continue;

                    // google's cap is checked before anything is read from the api: with --keep the
                    // remote count is not known yet, so that half is re-checked in the plan
                    if (!keep && files.Count > MaxCount(type))
                    {
                        Console.WriteLine($"Warning: {language}/{type} has {files.Count} file(s), Google takes at most {MaxCount(type)}. Skipped.");
                        continue;
                    }

                    groups.Add(new Group { Language = language, Type = type, Files = files });
                }
            }

            return groups;
        }

        /// <summary>
        /// images can only exist for a language whose store listing exists, so a folder for any
        /// other language is dropped with the way to fix it
        /// </summary>
        static List<Group> DropUnknownLanguages(List<Group> groups, List<string> listingLanguages)
        {
            var known = new List<Group>();

            foreach (var group in groups)
            {
                var listed = listingLanguages.FirstOrDefault(l => string.Equals(l, group.Language, StringComparison.OrdinalIgnoreCase));

                if (listed is null)
                {
                    Console.WriteLine($"Warning: the store listing has no language '{group.Language}', its folder is skipped.");
                    Console.WriteLine("         add the language first: 'locales import listing' with its texts filled in.");
                    continue;
                }

                known.Add(group);
            }

            if (known.Count == 0)
                Console.WriteLine("nothing left to upload.");

            return known;
        }

        async Task ReadRemote(List<Group> groups, string editId)
        {
            foreach (var group in groups)
            {
                var response = await Service!.Edits.Images
                    .List(Package, editId, group.Language, TypeEnum<EditsResource.ImagesResource.ListRequest.ImageTypeEnum>(group.Type))
                    .ExecuteAsync();

                group.Remote = response.Images ?? [];

                foreach (var file in group.Files)
                    group.LocalShas.Add(Sha256OfFile(file));
            }
        }

        /// <summary>
        /// A set is unchanged when the local files are byte for byte what the last export wrote for
        /// exactly the images that are still online - same files, same hashes, same remote ids in
        /// the same order. Also when the local hashes equal the remote ones, which catches a set
        /// this command itself uploaded earlier. Everything else is a change, because Google
        /// re-encodes what it serves and a byte comparison against the download can say no more.
        /// </summary>
        void MarkUnchanged(List<Group> groups, List<ManifestRow> manifest, bool verbose)
        {
            foreach (var group in groups)
            {
                var remoteShas = group.Remote.Select(r => (r.Sha256 ?? "").ToLowerInvariant()).ToList();

                if (group.Files.Count == group.Remote.Count && remoteShas.SequenceEqual(group.LocalShas))
                {
                    group.Unchanged = true;
                    continue;
                }

                var rows = manifest
                    .Where(m => string.Equals(m.Language, group.Language, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(m.Type, group.Type, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(m => m.Position)
                    .ToList();

                if (rows.Count != group.Files.Count || rows.Count != group.Remote.Count)
                    continue;

                var untouched = rows
                    .Zip(group.Files, group.LocalShas.AsEnumerable())
                    .All(z => PathsEqual(z.First.File, Path.GetRelativePath(ImagesDir, z.Second))
                        && string.Equals(z.First.FileSha256, z.Third, StringComparison.OrdinalIgnoreCase));

                var sameRemote = rows
                    .Zip(group.Remote)
                    .All(z => !string.IsNullOrWhiteSpace(z.First.ImageId)
                        && string.Equals(z.First.ImageId, z.Second.Id, StringComparison.Ordinal));

                group.Unchanged = untouched && sameRemote;

                if (verbose && !group.Unchanged)
                    Console.WriteLine($"   {group.Language}/{group.Type}: differs from the export, will be replaced");
            }
        }

        static bool PathsEqual(string a, string b)
            => string.Equals(a.Replace('\\', '/'), b.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

        void PrintPlan(List<Group> groups, bool keep)
        {
            Console.WriteLine();
            Console.WriteLine(keep ? "plan (append):" : "plan (replace):");

            foreach (var group in groups)
            {
                var action = group.Unchanged
                    ? "unchanged, skipped"
                    : keep || group.Remote.Count == 0
                        ? $"{group.Files.Count} uploaded, {group.Remote.Count} kept"
                        : $"{group.Files.Count} uploaded, {group.Remote.Count} deleted";

                Console.WriteLine($"        {group.Language,-10} {group.Type,-22} {action}");
            }
        }

        async Task<(int uploaded, int deleted)> Apply(List<Group> changed, string editId, bool keep, bool verbose)
        {
            var uploaded = 0;
            var deleted = 0;

            foreach (var group in changed)
            {
                if (keep && group.Remote.Count + group.Files.Count > MaxCount(group.Type))
                {
                    Console.WriteLine($"Warning: {group.Language}/{group.Type} would hold {group.Remote.Count + group.Files.Count} image(s) with --keep, Google takes at most {MaxCount(group.Type)}. Skipped.");
                    continue;
                }

                // deleting first keeps the order clean: the uploads then are the whole set, in file
                // order. Until the commit this only lives in the edit, players still see the old set
                if (!keep && group.Remote.Count > 0)
                {
                    await Service!.Edits.Images
                        .Deleteall(Package, editId, group.Language, TypeEnum<EditsResource.ImagesResource.DeleteallRequest.ImageTypeEnum>(group.Type))
                        .ExecuteAsync();

                    deleted += group.Remote.Count;
                }

                // sequential on purpose, the store shows the set in upload order
                foreach (var file in group.Files)
                {
                    var contentType = ContentTypeOf(file)
                        ?? throw new InvalidOperationException($"'{file}' is not a png or jpeg.");

                    using var stream = File.OpenRead(file);

                    var upload = Service!.Edits.Images.Upload(
                        Package,
                        editId,
                        group.Language,
                        TypeEnum<EditsResource.ImagesResource.UploadMediaUpload.ImageTypeEnum>(group.Type),
                        stream,
                        contentType
                    );

                    // a media upload reports its failure instead of throwing it
                    var progress = await upload.UploadAsync();
                    if (progress.Exception is not null)
                        throw new InvalidOperationException($"failed to upload {group.Language}/{group.Type}/{Path.GetFileName(file)}: {progress.Exception.Message}", progress.Exception);

                    uploaded++;

                    if (verbose)
                        Console.WriteLine($"   {group.Language,-10} {group.Type,-22} {Path.GetFileName(file)} uploaded");
                }

                Console.WriteLine($"   -> {group.Language,-10} {group.Type,-22} {group.Files.Count} uploaded");
            }

            return (uploaded, deleted);
        }

        public override string Name => "locales import images";

        public override string Description
            => "Uploads the localized store images back, replacing each language + type set that has local files. Committed as one edit with nothing else in it.";

        public override void PrintHelp()
        {
            Console.WriteLine("locales import images [--images-dir <path>] [--types <type[,type...]>] [--locales <code[,code...]>] [--keep] [--review] [-n|--dry-run] [-v]");
            Console.WriteLine();
            Console.WriteLine();

            Console.WriteLine("description:");
            CommandLinesUtils.PrintDescription(Description);
            CommandLinesUtils.PrintDescription("Reads the layout 'locales export images' writes: '<dir>/<language>/<type>/<name>.png', files in name order - keep the numbering to keep the order on the store. png and jpeg only.");
            CommandLinesUtils.PrintDescription($"A set that has local files replaces its online counterpart completely: delete all, upload in order. A set with no local folder is never touched, and a set that is still byte for byte what the export wrote (the '{ManifestFileName}' knows) is skipped - so only what was actually localized is sent, and re-running an unchanged folder does nothing.");
            CommandLinesUtils.PrintDescription("A folder for a language the store listing does not have is skipped with a warning - the language is created by 'locales import listing', text first, images second.");
            CommandLinesUtils.PrintDescription("Everything goes as one edit, committed at the end: on -n/--dry-run and on any failure the edit is discarded, and the deletions vanish with it - players never see half an import. The commit is a draft by default - the changes wait in the Play Console (Publishing overview) until a human sends them for review. Only --review sends them right away.");
            CommandLinesUtils.PrintDescription($"Google's caps are checked before sending: at most 8 screenshots per type, one icon, one feature graphic, one tv banner. Wrong dimensions Google reports itself, per image, and the whole edit is then discarded here.");

            Console.WriteLine();
            Console.WriteLine("options:");

            CommandLinesUtils.PrintOption(
                "--images-dir <path>",
                "Specifies the directory to read the folders from. If not specified, used path from global config json ('StoreImagesDirPath'), which defaults to './store-images' next to it."
            );
            CommandLinesUtils.PrintOption(
                "--types <type[,type...]>",
                "Only these image types, comma separated, e.g. 'phoneScreenshots,featureGraphic'. Default is every type folder found."
            );
            CommandLinesUtils.PrintOption(
                "--locales <code[,code...]>",
                "Only these languages, comma separated. Default is every language folder found."
            );
            CommandLinesUtils.PrintOption(
                "--keep",
                "Add the local images after the existing ones instead of replacing them. Nothing is deleted."
            );
            CommandLinesUtils.PrintOption(
                "--review",
                "Send the changes straight to Google review instead of leaving them as a Play Console draft. After approval they go live on their own."
            );
            CommandLinesUtils.PrintOption(
                "-n|--dry-run",
                "Show what would be deleted and uploaded, and send nothing."
            );
            CommandLinesUtils.PrintOption(
                "-v",
                "Include additional verbose output"
            );

            CommandLinesUtils.PrintCommonOptions();
        }
    }
}
