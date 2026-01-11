using Google.Apis.AndroidPublisher.v3;
using Google.Apis.Auth.OAuth2;
using ANU.APIs.GoogleDeveloperAPI.IAPManaging;
using static Google.Apis.Services.BaseClientService;

var commands = new CommandsCollection()
{
    new Command_List(),
    new Command_Restore(),
    new Command_LocalizePrices(),
};

if (commands.TryPrintHelp(args))
    return;

var command = commands.FirstOrDefault(c => c.IsMatches(args));
if (command is null)
{
    Console.WriteLine("no command fount for passed parameters");
    return;
}

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

var resolvedPathGetter = new CommandLinesUtils.ResolvedPathGetter();
var configPath = args.TryGetOption("--config", "../config.json");

var verbose = args.HasFlag("-v");

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
config.DefaultPricesFilePath = Path.Combine(configDirectory, config.DefaultPricesFilePath);

var credentials = await GoogleWebAuthorizationBroker.AuthorizeAsync(
    (await GoogleClientSecrets.FromFileAsync(config.CredentialsFilePath)).Secrets,
    [AndroidPublisherService.Scope.Androidpublisher],
    "user",
    canceller.Token
);

using var service = new AndroidPublisherService(new Initializer()
{
    HttpClientInitializer = credentials,
    ApplicationName = "IAP managing"
});

// set larger timeout
service.HttpClient.Timeout = TimeSpan.FromMinutes(5);

config.PackageName = args.TryGetOption("--package", config.PackageName);
config.CredentialsFilePath = args.TryGetOption("--credentials", config.CredentialsFilePath);
config.DefaultPricesFilePath = args.TryGetOption("--prices", config.DefaultPricesFilePath);

config.LocalizedPricesTemplateFilePath = args.TryGetOption("--localized-template", config.LocalizedPricesTemplateFilePath);
config.RoundPricesForFilePath = args.TryGetOption("--round-prices", config.RoundPricesForFilePath);

// patch config with explicit command line options
config.DefaultRegion = args.TryGetOption("--region", config.DefaultRegion);
config.DefaultCurrency = args.TryGetOption("--currency", config.DefaultCurrency);
config.Iap = args.TryGetOption("--iap", config.Iap);

Console.WriteLine();

command.Initialize(service, config, args);
await command.ExecuteAsync();

Console.WriteLine();