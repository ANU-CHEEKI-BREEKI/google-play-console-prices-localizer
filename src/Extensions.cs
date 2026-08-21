using System.Net;
using System.Text;
using Google;
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

        /// <summary>
        /// Lists every one-time product, following the page tokens.
        /// The API returns only 50 products per page by default, and a create that does not see
        /// an existing product would silently overwrite its prices.
        /// </summary>
        public static async Task<List<OneTimeProduct>> ListAllAsync(this MonetizationResource.OnetimeproductsResource resource, string package, int pageSize = 1000)
        {
            var all = new List<OneTimeProduct>();
            string? pageToken = null;

            do
            {
                var request = resource.List(package);
                request.PageSize = pageSize;
                request.PageToken = pageToken;

                var response = await request.ExecuteAsync();

                if (response.OneTimeProducts is not null)
                    all.AddRange(response.OneTimeProducts);

                pageToken = response.NextPageToken;
            }
            while (!string.IsNullOrEmpty(pageToken));

            return all;
        }

        /// <summary>
        /// exact decimal price, unlike ToDecimalPrice it never routes through a double
        /// </summary>
        public static decimal ToExactDecimalPrice(this Money money)
            => money is null ? 0m : (money.Units ?? 0) + (money.Nanos ?? 0) / 1_000_000_000m;

        /// <summary>
        /// same 5 retries / 5s backoff / wording as SendWithRetryAsync, but reports the outcome
        /// </summary>
        public static async Task<bool> ExecuteWithRetryAsync(Func<Task> action, string label, int maxRetries = 5)
        {
            var currentRetry = 0;

            while (currentRetry < maxRetries)
            {
                try
                {
                    await action();
                    return true;
                }
                catch (Exception ex)
                {
                    currentRetry++;
                    Console.WriteLine($"  [Attempt {currentRetry}/{maxRetries}] Failed: {ex.Message}");

                    // a rejected request is rejected just as hard the fifth time,
                    // retrying it only burns 25 seconds before saying the same thing
                    if (IsPermanent(ex))
                    {
                        Console.WriteLine($"  >>> SKIPPING {label}, Google Play rejected the request.");
                        return false;
                    }

                    if (currentRetry >= maxRetries)
                    {
                        Console.WriteLine($"  >>> SKIPPING {label} after {maxRetries} failed attempts.");
                        return false;
                    }

                    Console.WriteLine("  Waiting 5 seconds before retrying...");
                    await Task.Delay(TimeSpan.FromSeconds(5));
                }
            }

            return false;
        }

        /// <summary>
        /// a client error that will never succeed on its own: a malformed body, a forbidden value,
        /// a missing permission. 'too many requests' is the one that does go away on its own
        /// </summary>
        private static bool IsPermanent(Exception ex)
            => ex is GoogleApiException api
               && api.HttpStatusCode >= HttpStatusCode.BadRequest
               && api.HttpStatusCode < HttpStatusCode.InternalServerError
               && api.HttpStatusCode != HttpStatusCode.TooManyRequests;

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

