public class Config
{
    public string PackageName { get; set; } = "";
    public string CredentialsFilePath { get; set; } = "";
    public string DefaultPricesFilePath { get; set; } = "";
    public decimal SetDefaultPricesPercentage { get; set; } = 1m;
}

public class ProductConfig
{
    public double DefaultPrice { get; set; }
}
