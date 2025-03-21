using Google.Apis.AndroidPublisher.v3;
using Google.Apis.Auth.OAuth2;
using gps_iap_managing;
using static Google.Apis.Services.BaseClientService;

var commands = new CommandsCollection()
{
    new Command_List(),
    new Command_Restore(),
    new Command_LocalizePrices(),
    new Command_RestoreReversed(),
    new Command_LocalizePricesReversed(),
};

if (commands.TryPrintHelp(args))
    return;

var config = await CommandLinesUtils.LoadJson<Config>(args, false, "--config", "../config.json");
if (config is null)
    throw new ArgumentNullException("config");

using var canceller = new CancellationTokenSource(TimeSpan.FromSeconds(30));

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

var command = commands.FirstOrDefault(c => c.IsMatches(args));
if (command is null)
{
    Console.WriteLine("no command fount for passed parameters");
    return;
}

command.Initialize(service, config, args);
await command.ExecuteAsync();

Console.WriteLine("done.");