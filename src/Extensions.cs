using System.Diagnostics;
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

        /// <summary>
        /// keeps only the products named in the --iap option, a comma separated list of ids.
        /// empty means everything
        /// </summary>
        public static IEnumerable<OneTimeProduct> Filter(this IEnumerable<OneTimeProduct> products, string filterIAP)
        {
            var ids = ParseIapFilter(filterIAP);
            return ids.Count == 0 ? products : products.Where(p => ids.Contains(p.ProductId));
        }

        public static HashSet<string> ParseIapFilter(string filterIAP)
            => new(
                (filterIAP ?? "").Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.Ordinal
            );

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

        /// <summary>
        /// the SDK ships no constants for these enums, these are the REST values
        /// </summary>
        public const string PurchaseOptionActive = "ACTIVE";
        public const string LatencyTolerant = "PRODUCT_UPDATE_LATENCY_TOLERANCE_LATENCY_TOLERANT";

        /// <summary>
        /// the single purchase option this tool manages on a product, or null when there is none
        /// </summary>
        public static OneTimeProductPurchaseOption? LegacyOption(this OneTimeProduct product)
            => product.PurchaseOptions?.FirstOrDefault(po => po.BuyOption?.LegacyCompatible == true);

        /// <summary>
        /// Activates purchase options, up to 100 per request, which is the API limit.
        /// Returns false if any batch failed after the retries.
        /// </summary>
        public static async Task<bool> ActivateAsync(this AndroidPublisherService service, string package, IList<(string ProductId, string PurchaseOptionId)> options)
        {
            var allOk = true;
            var batches = options.Chunk(100).ToList();

            for (var i = 0; i < batches.Count; i++)
            {
                var batch = batches[i];
                var label = batches.Count == 1 ? $"{batch.Length} purchase option(s)" : $"batch {i + 1}/{batches.Count} ({batch.Length} purchase option(s))";

                Console.WriteLine($"   -> Activating {label}...");

                var body = new BatchUpdatePurchaseOptionStatesRequest
                {
                    Requests = batch.Select(o => new UpdatePurchaseOptionStateRequest
                    {
                        ActivatePurchaseOptionRequest = new ActivatePurchaseOptionRequest
                        {
                            PackageName = package,
                            ProductId = o.ProductId,
                            PurchaseOptionId = o.PurchaseOptionId,
                            LatencyTolerance = LatencyTolerant,
                        }
                    }).ToList()
                };

                // the product id in the path is required but every request carries its own,
                // so the first one of the batch is as good as any
                var ok = await ExecuteWithRetryAsync(
                    () => service.Monetization.Onetimeproducts.PurchaseOptions
                        .BatchUpdateStates(body, package, batch[0].ProductId)
                        .ExecuteAsync(),
                    label
                );

                allOk &= ok;
            }

            return allOk;
        }

        /// <summary>
        /// Sends price updates the way Google recommends for bulk changes: every product in one
        /// batchUpdate, LATENCY_TOLERANT. Measured on this API: a single latency-sensitive patch
        /// with the full region list does not answer within five minutes, the tolerant batch with
        /// the same payload answers in about two. The price is that the change can take up to
        /// 24 hours to reach devices; pass sensitive=true to use the slow path when that matters.
        /// </summary>
        public static async Task<bool> SendWithRetryAsync(this IList<OneTimeProduct> products, AndroidPublisherService service, string package, bool sensitive = false)
        {
            if (products.Count == 0)
                return true;

            var requests = products.Select(product => new UpdateOneTimeProductRequest
            {
                OneTimeProduct = product,
                UpdateMask = "purchaseOptions",
                RegionsVersion = product.RegionsVersion,
                LatencyTolerance = sensitive ? null : LatencyTolerant,
            }).ToList();

            var allOk = true;
            var batches = requests.Chunk(100).ToList();

            for (var i = 0; i < batches.Count; i++)
            {
                var batch = batches[i];
                var label = batches.Count == 1 ? $"{batch.Length} product(s)" : $"batch {i + 1}/{batches.Count} ({batch.Length} product(s))";

                Console.WriteLine($"   -> Sending {label} in one request ({(sensitive ? "latency sensitive" : "latency tolerant")})...");
                if (!sensitive)
                    Console.WriteLine("      Google takes about two minutes to write a full region list, this is normal.");

                var body = new BatchUpdateOneTimeProductsRequest { Requests = batch };

                var ok = await ExecuteWithRetryAsync(
                    () => Timed("the update request", async () =>
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                        await service.Monetization.Onetimeproducts.BatchUpdate(body, package).ExecuteAsync(timeout.Token);
                    }),
                    label
                );

                allOk &= ok;
            }

            return allOk;
        }

        /// <summary>
        /// a silent console is indistinguishable from a hang, this says how long we have been waiting
        /// </summary>
        public static async Task<T> Timed<T>(string label, Func<Task<T>> action)
        {
            var watch = Stopwatch.StartNew();
            using var finished = new CancellationTokenSource();

            var heartbeat = Task.Run(async () =>
            {
                while (!finished.IsCancellationRequested)
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(10), finished.Token); }
                    catch (OperationCanceledException) { return; }

                    Console.WriteLine($"      still waiting for {label}... {watch.Elapsed.TotalSeconds:0}s");
                }
            });

            try
            {
                var result = await action();
                Console.WriteLine($"      {label} took {watch.Elapsed.TotalSeconds:0.0}s");
                return result;
            }
            finally
            {
                finished.Cancel();
                await heartbeat;
            }
        }

        public static Task Timed(string label, Func<Task> action)
            => Timed(label, async () => { await action(); return true; });

    }
}

