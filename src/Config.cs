public class Config
{
    public string PackageName { get; set; } = "";
    public string CredentialsFilePath { get; set; } = "";
    public string DefaultPricesFilePath { get; set; } = "";
    public string DefaultCurrencyRegion { get; set; } = "US";
}

public class ProductConfigs : Dictionary<string, decimal> { }
