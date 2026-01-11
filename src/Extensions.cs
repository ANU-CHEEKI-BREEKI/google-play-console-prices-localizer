using System.Text;
using Google.Apis.AndroidPublisher.v3;
using Google.Apis.AndroidPublisher.v3.Data;

namespace ANU.APIs.GoogleDeveloperAPI.IAPManaging
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

        public static IEnumerable<OneTimeProduct> Filter(this IEnumerable<OneTimeProduct> products, string filterIAP)
            => products.Where(p => string.IsNullOrEmpty(filterIAP) || p.ProductId == filterIAP);

        public static void PrintIapList(this IEnumerable<OneTimeProduct> products, bool printLocalPrices, string? defaultRegion = null)
        {
            var stringPairs = new List<StringPairs>();

            foreach (var product in products)
            {
                var option = product.PurchaseOptions.First(po => po.BuyOption.LegacyCompatible == true);
                var usPrice = option.RegionalPricingAndAvailabilityConfigs.First(price => price.RegionCode == (defaultRegion ?? "US"));

                stringPairs.Add(new StringPairs { A = product.ProductId, B = usPrice.Price.FormattedPrice() });

                if (!printLocalPrices)
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

        public static decimal ToDecimalPrice(this Money money)
        {
            if (money == null)
                return 0;

            double fractionalPart = (money.Nanos ?? 0) / 1_000_000_000.0;
            double total = (money.Units ?? 0) + fractionalPart;

            return (decimal)total;
        }

        public static async Task SendBatchedWithRetryAsync(this IList<OneTimeProduct> products, AndroidPublisherService service, string package, int maxRetries = 5)
        {
            // Update all products using BatchUpdate
            var updateRequests = products.Select(product => new UpdateOneTimeProductRequest
            {
                OneTimeProduct = product,
                UpdateMask = "purchaseOptions",
                RegionsVersion = product.RegionsVersion
            }).ToList();

            // we have TIMEOUT EXCEPTIONS
            // so lets update one IAP per request

            var count = updateRequests.Count();
            var q = 0;
            foreach (var updateRequest in updateRequests)
            {
                q++;
                Console.WriteLine($"Sending BatchUpdate {q}/{count} for {updateRequest.OneTimeProduct.ProductId}...");

                var batchUpdateRequest = new BatchUpdateOneTimeProductsRequest
                {
                    Requests = [updateRequest]
                };

                // also leta add retry logic
                // if a request fails (timeout or glitch), we try 3 times before giving up
                var currentRetry = 0;
                var success = false;

                while (currentRetry < maxRetries && !success)
                {
                    try
                    {
                        var batchRequest = service!.Monetization.Onetimeproducts.BatchUpdate(batchUpdateRequest, package);
                        await batchRequest.ExecuteAsync();
                        success = true; // It worked! Exit the retry loop
                    }
                    catch (Exception ex)
                    {
                        currentRetry++;
                        Console.WriteLine($"  [Attempt {currentRetry}/{maxRetries}] Failed: {ex.Message}");

                        if (currentRetry >= maxRetries)
                        {
                            Console.WriteLine($"  >>> SKIPPING {updateRequest.OneTimeProduct.ProductId} after {maxRetries} failed attempts.");
                        }
                        else
                        {
                            Console.WriteLine("  Waiting 5 seconds before retrying...");
                            await Task.Delay(TimeSpan.FromSeconds(5));
                        }
                    }
                }
            }
        }

        public static async Task SendWithRetryAsync(this IList<OneTimeProduct> products, AndroidPublisherService service, string package, int maxRetries = 5)
        {
            // batch requests works slow
            // and we any way updating each product one by one
            // MAYBE will be faster to use Patch call instead

            var count = products.Count;
            var q = 0;
            foreach (var product in products)
            {
                q++;
                Console.WriteLine($"Sending Patch {q}/{count} for {product.ProductId}...");

                var currentRetry = 0;
                var success = false;

                while (currentRetry < maxRetries && !success)
                {
                    try
                    {
                        var patchRequest = service!.Monetization.Onetimeproducts.Patch(product, package, product.ProductId);
                        patchRequest.RegionsVersionVersion = product.RegionsVersion.Version;
                        patchRequest.UpdateMask = "purchaseOptions";
                        await patchRequest.ExecuteAsync();
                        success = true; // It worked! Exit the retry loop
                    }
                    catch (Exception ex)
                    {
                        currentRetry++;
                        Console.WriteLine($"  [Attempt {currentRetry}/{maxRetries}] Failed: {ex.Message}");

                        if (currentRetry >= maxRetries)
                        {
                            Console.WriteLine($"  >>> SKIPPING {product.ProductId} after {maxRetries} failed attempts.");
                        }
                        else
                        {
                            Console.WriteLine("  Waiting 5 seconds before retrying...");
                            await Task.Delay(TimeSpan.FromSeconds(5));
                        }
                    }
                }
            }
        }
    }
}

