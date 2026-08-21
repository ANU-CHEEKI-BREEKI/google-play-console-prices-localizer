public class Config
{
    public string PackageName { get; set; } = "";
    public string CredentialsFilePath { get; set; } = "";
    public string DefaultPricesFilePath { get; set; } = "";
    
    public string LocalizedPricesTemplateFilePath { get; set; } = "";
    public string RoundPricesForFilePath { get; set; } = "";

    /// <summary>
    /// csv with the product definitions the 'export-iaps' and 'create-iaps' commands read and write.
    /// the default must not be empty: Program.cs combines it with the config directory, and
    /// Path.Combine(directory, "") gives back the directory itself, which is not a file to write to
    /// </summary>
    public string ProductDefinitionsFilePath { get; set; } = "./product-definitions.csv";

    /// <summary>
    /// csv with the achievement translations the 'export-achievements' command writes.
    /// like ProductDefinitionsFilePath the default must not be empty, Program.cs combines it
    /// with the config directory
    /// </summary>
    public string AchievementDefinitionsFilePath { get; set; } = "./achievement-definitions.csv";

    /// <summary>language of the store listing the 'export-iaps' and 'create-iaps' commands read and write</summary>
    public string DefaultLanguageCode { get; set; } = "en-US";

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

public class ProductConfigs : Dictionary<string, decimal> { }
public class LocalizedPricesPercentagesConfigs : Dictionary<string, decimal> { }
