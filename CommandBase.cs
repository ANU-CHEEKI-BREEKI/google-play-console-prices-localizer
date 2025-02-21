using Google.Apis.AndroidPublisher.v3;

namespace gps_iap_managing
{
    public abstract class CommandBase
    {
        public AndroidPublisherService? Service { get; set; }
        public string? Package { get; private set; }
        public string[] Args { get; set; } = null!;

        public void Initialize(AndroidPublisherService service, string package, string[] args)
        {
            this.Args = args;
            this.Service = service;
            this.Package = package;
        }

        public abstract bool IsMatches(string[] args);
        public abstract Task ExecuteAsync();
        public abstract void PrintHelp();
    }
}

