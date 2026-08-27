using Google.Apis.AndroidPublisher.v3;
using Google.Apis.GamesConfiguration.v1configuration;
using Newtonsoft.Json.Linq;
using Google.Apis.Playdeveloperreporting.v1beta1;

namespace ANU.APIs.GoogleDeveloperAPI.IAPManaging
{
    public abstract class CommandBase
    {
        public AndroidPublisherService? Service { get; set; }
        public PlaydeveloperreportingService? ReportingService { get; set; }
        public GamesConfigurationService? GamesService { get; set; }
        public Config Config { get; private set; } = null!;
        public string[] Args { get; set; } = null!;

        public string Package => Config.PackageName;

        /// <summary>Play Games Services project id, the games configuration API's equivalent of the package name</summary>
        public string GamesProjectId => Config.GamesProjectId;

        /// <summary>
        /// product ids from the --iap option, empty means every product.
        /// A run time filter, deliberately not part of the config: nobody wants a
        /// config that permanently narrows the app down to one product
        /// </summary>
        public string IapFilter => Args.TryGetOption("--iap", "");

        /// <summary>
        /// how many products go to Google at once. Google needs about two minutes per product no
        /// matter how the update is sent, so this is what decides the wall clock of a price run
        /// </summary>
        protected int Parallelism(int fallback = 8)
            => int.TryParse(Args.TryGetOption("--parallel", ""), out var parsed)
                ? Math.Clamp(parsed, 1, 16)
                : fallback;

        public abstract string Name { get; }
        public abstract string Description { get; }

        /// <summary>
        /// Google APIs the command talks to. Only requested services are created,
        /// so a command never asks the user to grant scopes it does not need.
        /// </summary>
        /// <summary>
        /// whether the command needs an app config at all. Offline commands like 'config'
        /// run before any config is located and before any sign-in.
        /// </summary>
        public virtual bool NeedsConfig => true;

        public virtual bool NeedsAndroidPublisher => true;
        public virtual bool NeedsPlayDeveloperReporting => false;
        public virtual bool NeedsGamesConfiguration => false;

        public virtual string[] RequiredScopes
        {
            get
            {
                var scopes = new List<string>();
                // the games configuration API is authorized by the very same scope as the publisher one,
                // so a command that needs both still asks the user for a single grant
                if (NeedsAndroidPublisher || NeedsGamesConfiguration)
                    scopes.Add(AndroidPublisherService.Scope.Androidpublisher);
                if (NeedsPlayDeveloperReporting)
                    scopes.Add(PlaydeveloperreportingService.Scope.Playdeveloperreporting);
                return [.. scopes];
            }
        }

        /// <summary>
        /// Key of the cached OAuth token. Commands with different scope sets use different keys,
        /// otherwise they would invalidate each other's tokens on every run.
        /// </summary>
        public virtual string AuthUserKey => "user";

        public void Initialize(
            AndroidPublisherService? service,
            PlaydeveloperreportingService? reportingService,
            GamesConfigurationService? gamesService,
            Config config,
            string[] args
        )
        {
            Args = args;
            Service = service;
            ReportingService = reportingService;
            GamesService = gamesService;
            Config = config;
        }

        /// <summary>
        /// The columns an export produces, in order. By default only languages that actually exist -
        /// the source locales plus <paramref name="found"/>, everything already translated - because
        /// a column for a language the app does not have yet is only wanted when new languages are
        /// being added, and that is what --all-locales is for.
        ///
        /// Order: the source locales first, in exactly the order they are configured, because a
        /// translation service reads the leading columns as its context and the order decides which
        /// one is the primary source. Then the rest of what exists, in the order the locales json
        /// names it. A language the json never mentions still gets its column, sorted, at the end.
        ///
        /// --all-locales adds a column for every entry of the locales json even when nothing is
        /// translated into it yet. That is how a language is added at all: Play Games Services hides
        /// a language until something is translated into it, and a product listing exists only where
        /// somebody wrote one, so an empty language is invisible unless the file names it.
        ///
        /// --locales names exact columns for one run and always gets them, empty or not - an
        /// explicitly asked for column is never filtered away.
        /// </summary>
        protected async Task<List<LocaleColumn>> ResolveLocales(IEnumerable<string> found, bool verbose)
        {
            var translated = found
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(l => l, StringComparer.Ordinal)
                .Select(l => new LocaleColumn(l, l))
                .ToList();

            // without a configured order the single default language leads
            var leading = (Config.SourceLocales is { Count: > 0 }
                    ? Config.SourceLocales
                    : [Config.DefaultLanguageCode])
                .Select(l => new LocaleColumn(l, l));

            var explicitList = Config.Locales is { Count: > 0 };
            var configured = explicitList
                ? Config.Locales.Select(l => new LocaleColumn(l, l)).ToList()
                : await LoadLocalesFile(verbose);

            var includeEmpty = explicitList || Args.HasFlag("--all-locales");

            var locales = new List<LocaleColumn>();

            // the first mention decides the order, the locales json decides the column name:
            // indonesian is 'id' to the api whether it was found translated or not, and the csv
            // must still say 'id-ID' for it
            void Add(LocaleColumn locale)
            {
                if (string.IsNullOrWhiteSpace(locale.Locale))
                    return;

                if (locales.Any(l => string.Equals(l.Locale, locale.Locale, StringComparison.OrdinalIgnoreCase)))
                    return;

                var alias = configured.FirstOrDefault(c => string.Equals(c.Locale, locale.Locale, StringComparison.OrdinalIgnoreCase));
                locales.Add(alias ?? locale);
            }

            foreach (var locale in leading)
                Add(locale);

            foreach (var locale in configured)
            {
                if (includeEmpty || translated.Any(t => string.Equals(t.Locale, locale.Locale, StringComparison.OrdinalIgnoreCase)))
                    Add(locale);
            }

            foreach (var locale in translated)
                Add(locale);

            return locales;
        }

        /// <summary>
        /// The locales json: a plain array of locale codes, in the order the columns should come out.
        ///
        /// An entry is normally just the code, which is both what Google wants and what the csv column
        /// is called. When those two have to differ, the entry is a one property object instead -
        /// { "id": "id-ID" } keeps sending "id" to the api while the csv says "id-ID", because a
        /// translation service reads a column called "id" as an identifier rather than indonesian.
        ///
        /// A root level object works too, for a file that is all aliases. A missing file is not an
        /// error, it just means nothing beyond what is already translated.
        /// </summary>
        protected async Task<List<LocaleColumn>> LoadLocalesFile(bool verbose)
        {
            var path = Config.LocalesFilePath;

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                if (verbose)
                    Console.WriteLine($"no locales file at {Path.GetFullPath(path ?? "")}, exporting only what is already translated");
                return [];
            }

            var parsed = JToken.Parse(await File.ReadAllTextAsync(path));

            var locales = parsed switch
            {
                JArray array => [.. array.SelectMany(ReadEntry)],
                JObject map => ReadProperties(map),
                _ => new List<LocaleColumn>(),
            };

            if (verbose)
                Console.WriteLine($"loaded {locales.Count} locale(s) from {Path.GetFullPath(path)}");

            return locales;

            static IEnumerable<LocaleColumn> ReadEntry(JToken entry) => entry switch
            {
                JObject alias => ReadProperties(alias),
                _ => Column(entry.ToString()),
            };

            static List<LocaleColumn> ReadProperties(JObject map)
                => [.. map.Properties().SelectMany(p => Column(p.Name, p.Value.ToString()))];

            static IEnumerable<LocaleColumn> Column(string locale, string? column = null)
            {
                if (string.IsNullOrWhiteSpace(locale))
                    yield break;

                yield return new LocaleColumn(locale, string.IsNullOrWhiteSpace(column) ? locale : column);
            }
        }

        public abstract Task ExecuteAsync();
        public abstract void PrintHelp();
    }
}
