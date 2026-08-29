using Google.Apis.AndroidPublisher.v3;
using Google.Apis.Auth.OAuth2;
using Google.Apis.GamesConfiguration.v1configuration;
using Google.Apis.Playdeveloperreporting.v1beta1;
using ANU.APIs.GoogleDeveloperAPI.IAPManaging;
using static Google.Apis.Services.BaseClientService;

var commands = new CommandsCollection()
{
    new Command_List(),
    new Command_ExportIaps(),
    new Command_CreateIaps(),
    new Command_Activate(),
    new Command_Restore(),
    new Command_LocalizePrices(),
    new Command_Vitals(),
    new Command_Locales(),
    new Command_Config(),
};

if (commands.TryPrintHelp(args))
    return;

var command = commands.FirstOrDefault(c => Array.IndexOf(args, c.Name) == 0);
if (command is null)
{
    Console.WriteLine("no command fount for passed parameters");
    return;
}

// the command sees its args before anything else: a command with subcommands routes both its help
// and the set of google services it needs off them, and both are decided before Initialize runs
command.Args = args;

if (args.HasFlag("-h")
    || args.HasFlag("--help"))
{
    Console.WriteLine();
    Console.WriteLine();
    command.PrintHelp();
    Console.WriteLine();
    Console.WriteLine();
    return;
}

var verbose = args.HasFlag("-v");

if (!command.NeedsConfig)
{
    command.Initialize(null, null, null, new Config(), args);
    await command.ExecuteAsync();
    return;
}

var resolvedPathGetter = new CommandLinesUtils.ResolvedPathGetter();

string configPath;
try
{
    configPath = Profiles.ResolveConfigPath(args, out var configSource);
    if (verbose)
        Console.WriteLine($"config: {configPath} ({configSource})");
}
catch (InvalidOperationException ex)
{
    Console.WriteLine($"[ERROR] {ex.Message}");
    return;
}

if (!File.Exists(configPath) && !File.Exists(Path.Combine(configPath, "config.json")))
{
    Console.WriteLine($"[ERROR] config not found: {Path.GetFullPath(configPath)}");
    Console.WriteLine("        pass --config <path>, or register a profile once: config add <name> <path-to-config.json>");
    return;
}

var config = await CommandLinesUtils.LoadJson<Config>(
    configPath,
    Path.Combine(configPath, "config.json"),
    verbose,
    resolvedPathGetter
);

if (config is null)
    config = new();

using var canceller = new CancellationTokenSource(TimeSpan.FromSeconds(30));


// patch paths to be relative to config file
var absoluteConfigPath = Path.GetFullPath(resolvedPathGetter.ResolvedPath);
var configDirectory = Path.GetDirectoryName(absoluteConfigPath);

config.CredentialsFilePath = Path.Combine(configDirectory, config.CredentialsFilePath);
config.LocalizedPricesTemplateFilePath = Path.Combine(configDirectory, config.LocalizedPricesTemplateFilePath);
config.RoundPricesForFilePath = Path.Combine(configDirectory, config.RoundPricesForFilePath);
config.ProductDefinitionsFilePath = Path.Combine(configDirectory, config.ProductDefinitionsFilePath);
config.AchievementTranslationsFilePath = Path.Combine(configDirectory, config.AchievementTranslationsFilePath);
config.LocalesFilePath = Path.Combine(configDirectory, config.LocalesFilePath);
config.IapTranslationsFilePath = Path.Combine(configDirectory, config.IapTranslationsFilePath);
config.ReleaseNotesFilePath = Path.Combine(configDirectory, config.ReleaseNotesFilePath);
config.ListingTranslationsFilePath = Path.Combine(configDirectory, config.ListingTranslationsFilePath);
config.StoreImagesDirPath = Path.Combine(configDirectory, config.StoreImagesDirPath);
config.VitalsOutputPath = Path.Combine(configDirectory, config.VitalsOutputPath);


// patch config with explicit command line options
config.PackageName = args.TryGetOption("--package", config.PackageName);
config.CredentialsFilePath = args.TryGetOption("--credentials", config.CredentialsFilePath);

config.LocalizedPricesTemplateFilePath = args.TryGetOption("--localized-template", config.LocalizedPricesTemplateFilePath);
config.RoundPricesForFilePath = args.TryGetOption("--round-prices", config.RoundPricesForFilePath);

config.DefaultRegion = args.TryGetOption("--region", config.DefaultRegion);
config.DefaultCurrency = args.TryGetOption("--currency", config.DefaultCurrency);
config.ProductDefinitionsFilePath = args.TryGetOption("--products", config.ProductDefinitionsFilePath);
config.AchievementTranslationsFilePath = args.TryGetOption("--csv", config.AchievementTranslationsFilePath);
config.LocalesFilePath = args.TryGetOption("--locales-file", config.LocalesFilePath);
config.IapTranslationsFilePath = args.TryGetOption("--csv", config.IapTranslationsFilePath);
config.ReleaseNotesFilePath = args.TryGetOption("--csv", config.ReleaseNotesFilePath);
config.ListingTranslationsFilePath = args.TryGetOption("--csv", config.ListingTranslationsFilePath);
config.StoreImagesDirPath = args.TryGetOption("--images-dir", config.StoreImagesDirPath);
config.DefaultLanguageCode = args.TryGetOption("--language", config.DefaultLanguageCode);

var sourceLocales = args.TryGetOption("--source-locales", "");
if (!string.IsNullOrWhiteSpace(sourceLocales))
    config.SourceLocales = [.. sourceLocales.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

var locales = args.TryGetOption("--locales", "");
if (!string.IsNullOrWhiteSpace(locales))
    config.Locales = [.. locales.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

config.GamesProjectId = args.TryGetOption("--games-project", config.GamesProjectId);
config.VitalsOutputPath = args.TryGetOption("--out", config.VitalsOutputPath);


var credentials = await GoogleWebAuthorizationBroker.AuthorizeAsync(
    (await GoogleClientSecrets.FromFileAsync(config.CredentialsFilePath)).Secrets,
    command.RequiredScopes,
    command.AuthUserKey,
    canceller.Token
);

var initializer = new Initializer()
{
    HttpClientInitializer = credentials,
    ApplicationName = "IAP managing"
};

using var service = command.NeedsAndroidPublisher
    ? new AndroidPublisherService(initializer)
    : null;

using var reportingService = command.NeedsPlayDeveloperReporting
    ? new PlaydeveloperreportingService(initializer)
    : null;

using var gamesService = command.NeedsGamesConfiguration
    ? new GamesConfigurationService(initializer)
    : null;

// set larger timeout: a price write takes Google about two minutes per product,
// the per-request cancellation tokens in the commands are the real limit
if (service is not null)
    service.HttpClient.Timeout = TimeSpan.FromMinutes(15);
if (reportingService is not null)
    reportingService.HttpClient.Timeout = TimeSpan.FromMinutes(5);

Console.WriteLine();

command.Initialize(service, reportingService, gamesService, config, args);
await command.ExecuteAsync();

Console.WriteLine();
Console.WriteLine("done.");