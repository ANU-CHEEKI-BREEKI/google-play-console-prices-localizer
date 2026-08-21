using Google.Apis.AndroidPublisher.v3;
using Google.Apis.Playdeveloperreporting.v1beta1;

namespace ANU.APIs.GoogleDeveloperAPI.IAPManaging
{
    public abstract class CommandBase
    {
        public AndroidPublisherService? Service { get; set; }
        public PlaydeveloperreportingService? ReportingService { get; set; }
        public Config Config { get; private set; } = null!;
        public string[] Args { get; set; } = null!;

        public string Package => Config.PackageName;

        /// <summary>
        /// product ids from the --iap option, empty means every product.
        /// A run time filter, deliberately not part of the config: nobody wants a
        /// config that permanently narrows the app down to one product
        /// </summary>
        public string IapFilter => Args.TryGetOption("--iap", "");

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

        public virtual string[] RequiredScopes
        {
            get
            {
                var scopes = new List<string>();
                if (NeedsAndroidPublisher)
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

        public void Initialize(AndroidPublisherService? service, PlaydeveloperreportingService? reportingService, Config config, string[] args)
        {
            Args = args;
            Service = service;
            ReportingService = reportingService;
            Config = config;
        }

        public abstract Task ExecuteAsync();
        public abstract void PrintHelp();
    }
}
