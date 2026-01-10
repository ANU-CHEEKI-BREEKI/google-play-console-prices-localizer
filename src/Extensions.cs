using System.Text;
using Google.Apis.AndroidPublisher.v3.Data;

namespace gps_iap_managing
{
    public static class Extensions
    {
        public static void PrintIapList(this IList<InAppProduct> products, bool printPrices)
        {
            foreach (var item in products)
            {
                Console.WriteLine($"{item.Sku} defaultPrice: {item.DefaultPrice.FormattedPrice()}");
                if (!printPrices)
                    continue;

                var sb = new StringBuilder();

                foreach (var price in item.Prices)
                {
                    sb.Append($"{price.Key}: {price.Value.FormattedPrice()}");
                    sb.Append(", ");
                }

                Console.Write("    ");
                Console.WriteLine(sb);
            }
        }

        public static string FormattedPrice(this Price price)
            => $"{decimal.Parse(price.PriceMicros) / 1_000_000} {price.Currency}";

        public static void PrintIapList(this IList<OneTimeProduct> products, bool printPrices)
        {
            var stringPairs = new List<StringPairs>();

            foreach (var product in products)
            {
                var option = product.PurchaseOptions.First(po => po.BuyOption.LegacyCompatible == true);
                var usPrice = option.RegionalPricingAndAvailabilityConfigs.First(price => price.RegionCode == "US");

                stringPairs.Add(new StringPairs { A = product.ProductId, B = usPrice.Price.FormattedPrice() });

                if (!printPrices)
                    continue;

                foreach (var config in option.RegionalPricingAndAvailabilityConfigs)
                    stringPairs.Add(new StringPairs { A = $"    {config.RegionCode}", B = config.Price.FormattedPrice() });
            }

            var aMaxLength = stringPairs.Max(p => p.A.Length) + 4;
            var bMaxLength = stringPairs.Max(p => p.B.Length) + 4;

            foreach (var item in stringPairs)
                Console.WriteLine($"{item.A.PadRight(aMaxLength, '.')}{item.B.PadLeft(bMaxLength, '.')}");
        }

        private class StringPairs
        {
            public string A;
            public string B;
        }

        public static string FormattedPrice(this Money money)
        {
            if (money == null) return "0.00";

            double fractionalPart = (money.Nanos ?? 0) / 1_000_000_000.0;
            double total = (money.Units ?? 0) + fractionalPart;

            return $"{total:0.00} {money.CurrencyCode}";
        }
    }
}

