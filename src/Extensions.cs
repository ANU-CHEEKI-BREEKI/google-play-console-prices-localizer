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
    }
}

