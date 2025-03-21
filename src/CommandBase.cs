using Google.Apis.AndroidPublisher.v3;

namespace gps_iap_managing
{
    public abstract class CommandBase
    {
        public AndroidPublisherService? Service { get; set; }
        public Config Config { get; private set; } = null!;
        public string[] Args { get; set; } = null!;

        public string Package => Config.PackageName;

        public void Initialize(AndroidPublisherService service, Config config, string[] args)
        {
            Args = args;
            Service = service;
            Config = config;
        }

        public abstract bool IsMatches(string[] args);
        public abstract Task ExecuteAsync();
        public abstract void PrintHelp();
    }
}

