namespace ANU.APIs.GoogleDeveloperAPI.IAPManaging
{
    /// <summary>
    /// Manages the named profiles in ~/.config/gps-iap/profiles.json.
    /// Runs entirely offline: no config is loaded and no Google sign-in happens.
    /// </summary>
    public class Command_Config : CommandBase
    {
        public override bool NeedsConfig => false;
        public override bool NeedsAndroidPublisher => false;

        public override Task ExecuteAsync()
        {
            var sub = Args.Length > 1 ? Args[1] : "list";
            var profiles = Profiles.Load();

            switch (sub)
            {
                case "list":
                    List(profiles);
                    break;

                case "add":
                    Add(profiles, Arg(2), Arg(3));
                    break;

                case "use":
                    Use(profiles, Arg(2));
                    break;

                case "remove":
                    Remove(profiles, Arg(2));
                    break;

                case "path":
                    Console.WriteLine(Profiles.FilePath);
                    break;

                default:
                    Console.WriteLine($"unknown subcommand '{sub}'. see 'config --help'.");
                    break;
            }

            return Task.CompletedTask;
        }

        private string Arg(int index)
            => Args.Length > index && !Args[index].StartsWith('-') ? Args[index] : "";

        private static void List(Profiles profiles)
        {
            if (profiles.Entries.Count == 0)
            {
                Console.WriteLine("no profiles yet.");
                Console.WriteLine("add one with: config add <name> <path-to-config.json>");
                return;
            }

            var width = profiles.Entries.Keys.Max(k => k.Length) + 2;

            foreach (var (name, path) in profiles.Entries.OrderBy(e => e.Key))
            {
                var marker = string.Equals(name, profiles.Current, StringComparison.OrdinalIgnoreCase) ? "*" : " ";
                var missing = File.Exists(path) ? "" : "   (file not found)";
                Console.WriteLine($" {marker} {name.PadRight(width)}{path}{missing}");
            }

            Console.WriteLine();

            if (string.IsNullOrEmpty(profiles.Current))
                Console.WriteLine("no current profile. pick one with: config use <name>");
            else
                Console.WriteLine($"current: {profiles.Current}");
        }

        private static void Add(Profiles profiles, string name, string path)
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(path))
            {
                Console.WriteLine("usage: config add <name> <path-to-config.json>");
                return;
            }

            var full = Path.GetFullPath(path);

            // a folder is fine too, as long as it holds a config.json, same as --config
            if (Directory.Exists(full))
                full = Path.Combine(full, "config.json");

            if (!File.Exists(full))
            {
                Console.WriteLine($"[ERROR] config not found: {full}");
                return;
            }

            var replaced = profiles.Entries.ContainsKey(name);
            profiles.Entries[name] = full;

            // the first profile becomes current on its own, that is the whole point of adding one
            if (string.IsNullOrEmpty(profiles.Current))
                profiles.Current = name;

            profiles.Save();

            Console.WriteLine($"{(replaced ? "updated" : "added")} profile '{name}' -> {full}");
            if (string.Equals(profiles.Current, name, StringComparison.OrdinalIgnoreCase))
                Console.WriteLine($"'{name}' is the current profile.");
        }

        private static void Use(Profiles profiles, string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                Console.WriteLine("usage: config use <name>");
                return;
            }

            if (!profiles.Entries.ContainsKey(name))
            {
                Console.WriteLine($"[ERROR] no profile named '{name}'. run 'config list' to see the known ones.");
                return;
            }

            profiles.Current = name;
            profiles.Save();

            Console.WriteLine($"current profile: {name}");
        }

        private static void Remove(Profiles profiles, string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                Console.WriteLine("usage: config remove <name>");
                return;
            }

            if (!profiles.Entries.Remove(name))
            {
                Console.WriteLine($"[ERROR] no profile named '{name}'.");
                return;
            }

            if (string.Equals(profiles.Current, name, StringComparison.OrdinalIgnoreCase))
                profiles.Current = null;

            profiles.Save();

            Console.WriteLine($"removed profile '{name}'.");
            if (profiles.Current is null && profiles.Entries.Count > 0)
                Console.WriteLine("no current profile now. pick one with: config use <name>");
        }

        public override string Name => "config";
        public override string Description => "Manages named profiles, so a command can run without --config. A profile is just a name for a config.json path, kept in your home directory.";

        public override void PrintHelp()
        {
            Console.WriteLine("config list");
            Console.WriteLine("config add <name> <path-to-config.json>");
            Console.WriteLine("config use <name>");
            Console.WriteLine("config remove <name>");
            Console.WriteLine("config path");
            Console.WriteLine();
            Console.WriteLine();

            Console.WriteLine("description:");
            CommandLinesUtils.PrintDescription(Description);
            CommandLinesUtils.PrintDescription($"Profiles are stored in '{Profiles.FilePath}'. The repo never sees them, so app configs can stay outside of it.");
            CommandLinesUtils.PrintDescription("When a command runs, the config is picked in this order: --config <path>, --profile <name>, the current profile, and finally '../config.json'.");
            CommandLinesUtils.PrintDescription("This command does not sign in to Google.");

            Console.WriteLine();
            Console.WriteLine("subcommands:");

            CommandLinesUtils.PrintOption("list", "Show every profile. The current one is marked with '*'. This is the default when no subcommand is given.");
            CommandLinesUtils.PrintOption("add <name> <path>", "Register a config.json under a name, or point an existing name at a new path. A folder that contains config.json works too. The first profile added becomes current.");
            CommandLinesUtils.PrintOption("use <name>", "Make a profile the current one.");
            CommandLinesUtils.PrintOption("remove <name>", "Forget a profile. The config file itself is not touched.");
            CommandLinesUtils.PrintOption("path", "Print where the profiles file lives.");

            Console.WriteLine();
            Console.WriteLine("examples:");
            Console.WriteLine();
            CommandLinesUtils.PrintDescription("config add titan-souls ../apps-configs/titan-souls", 4);
            CommandLinesUtils.PrintDescription("config add island-raid ../apps-configs/island-raid", 4);
            CommandLinesUtils.PrintDescription("config use island-raid", 4);
            CommandLinesUtils.PrintDescription("list -l                          # runs for island-raid", 4);
            CommandLinesUtils.PrintDescription("list -l --profile titan-souls    # one-off, current profile stays", 4);
        }
    }
}
