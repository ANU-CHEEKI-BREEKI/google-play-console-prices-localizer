using Google.Apis.AndroidPublisher.v3;
using Google.Apis.AndroidPublisher.v3.Data;
using Google.Apis.Auth.OAuth2;
using gps_iap_managing;
using static Google.Apis.Services.BaseClientService;

var commands = new CommandsCollection()
{
    new Command_Restore(),
    new Command_LocalizePrices(),
    new Command_List(),
};

if (commands.TryPrintHelp(args))
    return;

var credentialPath = args[0];
var package = args[1];

using var canceller = new CancellationTokenSource(TimeSpan.FromSeconds(10));
var credentials = await GoogleWebAuthorizationBroker.AuthorizeAsync(
    (await GoogleClientSecrets.FromFileAsync(credentialPath)).Secrets,
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

command.Initialize(service, package, args);
await command.ExecuteAsync();

Console.WriteLine("done.");