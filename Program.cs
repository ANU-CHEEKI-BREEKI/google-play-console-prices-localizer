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

var resolvedPathGetter = new CommandLinesUtils.ResolvedPathGetter();
var config = await CommandLinesUtils.LoadJson<Config>(args, false, "--config", "../config.json", resolvedPathGetter);
if (config is null)
    throw new ArgumentNullException("config");

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

command.Initialize(service, config, args);
await command.ExecuteAsync();

Console.WriteLine("done.");