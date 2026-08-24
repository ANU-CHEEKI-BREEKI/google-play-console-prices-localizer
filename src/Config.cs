public class Config
{
    public string PackageName { get; set; } = "";
    public string CredentialsFilePath { get; set; } = "";

    public string LocalizedPricesTemplateFilePath { get; set; } = "";
    public string RoundPricesForFilePath { get; set; } = "";

    /// <summary>
    /// csv with the product definitions the 'export-iaps' and 'create-iaps' commands read and write.
    /// the default must not be empty: Program.cs combines it with the config directory, and
    /// Path.Combine(directory, "") gives back the directory itself, which is not a file to write to
    /// </summary>
    public string ProductDefinitionsFilePath { get; set; } = "./product-definitions.csv";

    /// <summary>
    /// csv with the achievement translations the 'locales export achievements' command writes.
    /// like ProductDefinitionsFilePath the default must not be empty, Program.cs combines it
    /// with the config directory
    /// </summary>
    public string AchievementTranslationsFilePath { get; set; } = "./achievement-translations.csv";

    /// <summary>
    /// csv with the one-time product title and description translations the
    /// 'locales export iaps' command writes. Like the other paths, must not be empty
    /// </summary>
    public string IapTranslationsFilePath { get; set; } = "./iap-translations.csv";

    /// <summary>language of the store listing the 'export-iaps' and 'create-iaps' commands read and write</summary>
    public string DefaultLanguageCode { get; set; } = "en-US";

    /// <summary>
    /// locales that lead the exported columns, in exactly this order, and that are always exported
    /// even when nothing is translated into them yet.
    /// A translation service reads the leading columns as the context it translates from, and one
    /// source is rarely enough - "en-US", "uk", "ru" means english is the source, ukrainian is the
    /// second opinion and russian is filled by copying from the ukrainian one.
    /// Everything else is appended after these, sorted. Empty falls back to DefaultLanguageCode alone
    /// </summary>
    public List<string> SourceLocales { get; set; } = [];

    /// <summary>
    /// json file with every locale the exports produce a column for, a plain array of codes in the
    /// order you want the columns.
    /// It has to be maintained by hand: Play Games Services hides a language until something is
    /// actually translated into it, and its api has no resource for the language list at all, so a
    /// language added in the console and still empty cannot be discovered. A missing file is fine,
    /// it just means only the locales already carrying a translation are exported
    /// </summary>
    public string LocalesFilePath { get; set; } = "./locales.json";

    /// <summary>
    /// the --locales option, a one run override of whatever LocalesFilePath holds.
    /// deliberately not read from config.json: the list belongs in its own file
    /// </summary>
    [Newtonsoft.Json.JsonIgnore]
    public List<string> Locales { get; set; } = [];

    /// <summary>
    /// numeric id of the Play Games Services project, the one the console shows next to the game name
    /// as "Project ID". Not the package name: one games project can be shared by several apps,
    /// and the games configuration API addresses games by this id only
    /// </summary>
    public string GamesProjectId { get; set; } = "";
    
    public string DefaultRegion { get; set; } = "US";
    public string DefaultCurrency { get; set; } = "USD";

    public string VitalsOutputPath { get; set; } = "./vitals-export";
}

/// <summary>
/// one exported language: the locale code Google wants, and the column name the csv carries it under.
/// They are usually the same, and have to be separable when they are not - Play Games Services calls
/// indonesian "id", which a translation service reads as an identifier column rather than a language,
/// so the csv says "id-ID" while the api still gets "id"
/// </summary>
public record LocaleColumn(string Locale, string Column)
{
    public override string ToString() => Locale == Column ? Locale : $"{Locale} as {Column}";
}

public class ProductConfigs : Dictionary<string, decimal> { }
public class LocalizedPricesPercentagesConfigs : Dictionary<string, decimal> { }
