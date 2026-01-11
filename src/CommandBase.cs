using Google.Apis.AndroidPublisher.v3;

namespace ANU.APIs.GoogleDeveloperAPI.IAPManaging
{
    public abstract class CommandBase
    {
        public AndroidPublisherService? Service { get; set; }
        public Config Config { get; private set; } = null!;
        public string[] Args { get; set; } = null!;

        public string Package => Config.PackageName;

        public abstract string Name { get; }
        public abstract string Description { get; }

        public void Initialize(AndroidPublisherService service, Config config, string[] args)
        {
            Args = args;
            Service = service;
            Config = config;
        }

        public abstract Task ExecuteAsync();
        public abstract void PrintHelp();
    }
}

