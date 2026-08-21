using Newtonsoft.Json;

namespace ANU.APIs.GoogleDeveloperAPI.IAPManaging
{
    /// <summary>
    /// Named pointers to app config files, kept in the user's home directory the way gcloud or gh
    /// keep theirs. The repo is public and the app configs live outside it, so this is what lets
    /// a command run without spelling the config path out every time.
    /// </summary>
    public class Profiles
    {
        public static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "gps-iap", "profiles.json"
        );

        /// <summary>name of the profile used when neither --config nor --profile is given</summary>
        public string? Current { get; set; }

        /// <summary>profile name -> absolute path of its config.json</summary>
        public Dictionary<string, string> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public static Profiles Load()
        {
            if (!File.Exists(FilePath))
                return new();

            var loaded = JsonConvert.DeserializeObject<Profiles>(File.ReadAllText(FilePath)) ?? new();

            // the deserializer hands back a case sensitive dictionary, names should not be
            loaded.Entries = new Dictionary<string, string>(loaded.Entries, StringComparer.OrdinalIgnoreCase);
            return loaded;
        }

        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonConvert.SerializeObject(this, Formatting.Indented));
        }

        /// <summary>
        /// the config path a run should use, in order of preference:
        /// an explicit --config, an explicit --profile, the current profile, the legacy ../config.json
        /// </summary>
        public static string ResolveConfigPath(string[] args, out string source)
        {
            var explicitPath = args.TryGetOption("--config", "");
            if (!string.IsNullOrEmpty(explicitPath))
            {
                source = "--config";
                return explicitPath;
            }

            var profiles = Load();
            var name = args.TryGetOption("--profile", "");

            if (!string.IsNullOrEmpty(name))
            {
                if (!profiles.Entries.TryGetValue(name, out var path))
                    throw new InvalidOperationException($"no profile named '{name}'. run 'config list' to see the known ones, or 'config add {name} <path-to-config.json>' to create it.");

                source = $"--profile {name}";
                return path;
            }

            if (!string.IsNullOrEmpty(profiles.Current) && profiles.Entries.TryGetValue(profiles.Current, out var currentPath))
            {
                source = $"profile '{profiles.Current}'";
                return currentPath;
            }

            source = "default";
            return "../config.json";
        }
    }
}
