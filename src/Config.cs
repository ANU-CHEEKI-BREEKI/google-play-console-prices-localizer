public class Config
{
    public string PackageName { get; set; } = "";
    public string CredentialsFilePath { get; set; } = "";
    public string DefaultPricesFilePath { get; set; } = "";
    
    public string LocalizedPricesTemplateFilePath { get; set; } = "";
    public string RoundPricesForFilePath { get; set; } = "";
    
    public string DefaultRegion { get; set; } = "US";
    public string DefaultCurrency { get; set; } = "USD";
    public string Iap { get; set; } = "";
}

public class ProductConfigs : Dictionary<string, decimal> { }
public class LocalizedPricesPercentagesConfigs : Dictionary<string, decimal> { }
