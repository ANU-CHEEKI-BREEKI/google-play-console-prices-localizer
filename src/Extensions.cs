using System.Diagnostics;
using System.Net;
using System.Text;
using Google;
using Google.Apis.AndroidPublisher.v3;
using Google.Apis.AndroidPublisher.v3.Data;
using GamesConfig = Google.Apis.GamesConfiguration.v1configuration;

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

            if (stringPairs.Count == 0)
            {
                Console.WriteLine("   (no products)");
                return;
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

        public static async Task<List<GamesConfig.Data.AchievementConfiguration>> ListAllAsync(
            this GamesConfig.AchievementConfigurationsResource resource,
            string gamesProjectId,
            int pageSize = 200
        )
        {
            var all = new List<GamesConfig.Data.AchievementConfiguration>();
            string? pageToken = null;

            do
            {
                var request = resource.List(gamesProjectId);
                request.MaxResults = pageSize;
                request.PageToken = pageToken;

                var response = await request.ExecuteAsync();

                if (response.Items is not null)
                    all.AddRange(response.Items);

                pageToken = response.NextPageToken;
            }
            while (!string.IsNullOrEmpty(pageToken));

            return all;
        }

        public static async Task<List<GamesConfig.Data.LeaderboardConfiguration>> ListAllAsync(
            this GamesConfig.LeaderboardConfigurationsResource resource,
            string gamesProjectId,
            int pageSize = 200
        )
        {
            var all = new List<GamesConfig.Data.LeaderboardConfiguration>();
            string? pageToken = null;

            do
            {
                var request = resource.List(gamesProjectId);
                request.MaxResults = pageSize;
                request.PageToken = pageToken;

                var response = await request.ExecuteAsync();

                if (response.Items is not null)
                    all.AddRange(response.Items);

                pageToken = response.NextPageToken;
            }
            while (!string.IsNullOrEmpty(pageToken));

            return all;
        }

        /// <summary>
        /// value of the bundle in one locale, empty when that locale is not translated.
        /// Locales are matched case insensitively, the console and the api disagree on the casing
        /// of things like pt-BR often enough to matter
        /// </summary>
        public static string ValueFor(this GamesConfig.Data.LocalizedStringBundle? bundle, string locale)
            => (bundle?.Translations ?? [])
                .FirstOrDefault(t => string.Equals(t.Locale, locale, StringComparison.OrdinalIgnoreCase))
                ?.Value ?? "";

        /// <summary>every locale the bundle carries a translation for</summary>
        public static IEnumerable<string> Locales(this GamesConfig.Data.LocalizedStringBundle? bundle)
            => (bundle?.Translations ?? [])
                .Select(t => t.Locale)
                .Where(l => !string.IsNullOrWhiteSpace(l))!;

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
        /// a private copy of an amount. A converted price is shared by every product that has the
        /// same base price, and a region's percentage is applied by writing into the Money, so what
        /// a caller edits must never be the object the conversion cache holds
        /// </summary>
        public static Money Copy(this Money? money)
            => new Money
            {
                CurrencyCode = money?.CurrencyCode,
                Units = money?.Units,
                Nanos = money?.Nanos,
            };

        /// <summary>
        /// Google's exchange rates for a set of base prices.
        /// ConvertRegionPrices answers for an amount, not for a product: a catalog of thirty
        /// products priced at five distinct amounts needs five requests, not thirty. The distinct
        /// prices go out a few at a time, and every product then reads its rates out of memory.
        /// </summary>
        public static async Task<Dictionary<decimal, ConvertRegionPricesResponse>> ConvertRegionPricesAsync(
            this AndroidPublisherService service,
            string package,
            string currency,
            IEnumerable<decimal> prices,
            bool verbose,
            int parallel = 8
        )
        {
            var distinct = prices.Distinct().ToList();
            var rates = new Dictionary<decimal, ConvertRegionPricesResponse>();

            if (distinct.Count == 0)
                return rates;

            Console.WriteLine($"   -> Asking Google for the exchange rates of {distinct.Count} distinct price(s), {Math.Min(parallel, distinct.Count)} at a time...");

            var gate = new SemaphoreSlim(parallel);

            var tasks = distinct.Select(async price =>
            {
                await gate.WaitAsync();
                try
                {
                    var units = (long)Math.Floor(price);
                    var nanos = (int)((price - units) * 1_000_000_000);

                    ConvertRegionPricesResponse? response = null;

                    await ExecuteWithRetryAsync(async () =>
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));

                        var request = new ConvertRegionPricesRequest
                        {
                            Price = new Money
                            {
                                CurrencyCode = currency,
                                Units = units,
                                Nanos = nanos,
                            }
                        };

                        response = await service.Monetization
                            .ConvertRegionPrices(request, package)
                            .ExecuteAsync(timeout.Token);
                    }, $"the exchange rates of {price} {currency}");

                    if (verbose)
                        Console.WriteLine($"      {price} {currency}: {response?.ConvertedRegionPrices?.Count ?? 0} region(s)");

                    return (Price: price, Response: response);
                }
                finally
                {
                    gate.Release();
                }
            }).ToList();

            foreach (var (price, response) in await Task.WhenAll(tasks))
            {
                if (response is not null)
                    rates[price] = response;
            }

            return rates;
        }

        /// <summary>
        /// the converted price of one region, as a copy. Google answers per region for the regions
        /// it knows and falls back to a usd/eur pair for the rest, and the region keeps whichever
        /// of the two its current price is already in
        /// </summary>
        public static Money PriceFor(this ConvertRegionPricesResponse converted, OneTimeProductPurchaseOptionRegionalPricingAndAvailabilityConfig oldConfig)
        {
            if (converted.ConvertedRegionPrices is not null
                && converted.ConvertedRegionPrices.TryGetValue(oldConfig.RegionCode, out var regionPrice)
                && regionPrice?.Price is not null)
                return regionPrice.Price.Copy();

            var other = converted.ConvertedOtherRegionsPrice;

            return oldConfig.Price?.CurrencyCode == other?.UsdPrice?.CurrencyCode
                ? other?.UsdPrice.Copy() ?? new Money()
                : other?.EurPrice.Copy() ?? new Money();
        }

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
        /// Activates purchase options. The batch endpoint only takes options of a single product
        /// per request ("All nested requests must match the parent request product ID"), so this
        /// is one request per product, several at a time, like the price updates.
        /// </summary>
        public static async Task<bool> ActivateAsync(this AndroidPublisherService service, string package, IList<(string ProductId, string PurchaseOptionId)> options, int parallel = 8)
        {
            if (options.Count == 0)
                return true;

            var byProduct = options.GroupBy(o => o.ProductId).ToList();

            Console.WriteLine($"   -> Activating {byProduct.Count} product(s), {Math.Min(parallel, byProduct.Count)} at a time...");

            var watch = Stopwatch.StartNew();
            var gate = new SemaphoreSlim(parallel);
            var done = 0;

            var tasks = byProduct.Select(async group =>
            {
                await gate.WaitAsync();
                try
                {
                    var body = new BatchUpdatePurchaseOptionStatesRequest
                    {
                        Requests = group.Select(o => new UpdatePurchaseOptionStateRequest
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

                    var ok = await ExecuteWithRetryAsync(async () =>
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                        await service.Monetization.Onetimeproducts.PurchaseOptions
                            .BatchUpdateStates(body, package, group.Key)
                            .ExecuteAsync(timeout.Token);
                    }, group.Key);

                    var n = Interlocked.Increment(ref done);
                    Console.WriteLine($"      {(ok ? "activated" : "FAILED   ")} {group.Key}  ({n}/{byProduct.Count}, {watch.Elapsed.TotalSeconds:0}s)");
                    return ok;
                }
                finally
                {
                    gate.Release();
                }
            }).ToList();

            var heartbeat = Heartbeat(watch, () => $"{done}/{byProduct.Count} done", tasks);
            var results = await Task.WhenAll(tasks);
            await heartbeat;

            return results.All(r => r);
        }

        /// <summary>
        /// Sends price updates, one request per product, several at a time.
        /// Measured on this API: Google needs about two minutes per product no matter how it is
        /// sent, and products in one batch request are processed one after another, so a batch
        /// of two does not answer within five minutes. Separate requests in parallel are the only
        /// way the wall clock does not grow with the number of products.
        /// LATENCY_TOLERANT by default: the change can take up to 24 hours to reach devices,
        /// pass sensitive=true when that matters.
        /// </summary>
        public static async Task<SendReport> SendWithRetryAsync(this IList<OneTimeProduct> products, AndroidPublisherService service, string package, bool sensitive = false, int parallel = 8)
        {
            if (products.Count == 0)
                return new SendReport(0, [], TimeSpan.Zero, parallel);

            Console.WriteLine($"   -> Sending {products.Count} product(s), {Math.Min(parallel, products.Count)} at a time ({(sensitive ? "latency sensitive" : "latency tolerant")})...");
            Console.WriteLine("      Google takes about two minutes per product, this is normal.");

            var watch = Stopwatch.StartNew();
            var gate = new SemaphoreSlim(parallel);
            var done = 0;

            var tasks = products.Select(async product =>
            {
                await gate.WaitAsync();
                try
                {
                    var ok = await ExecuteWithRetryAsync(async () =>
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));

                        var patch = service.Monetization.Onetimeproducts.Patch(product, package, product.ProductId);
                        patch.RegionsVersionVersion = product.RegionsVersion.Version;
                        patch.UpdateMask = "purchaseOptions";
                        if (!sensitive)
                            patch.LatencyTolerance = MonetizationResource.OnetimeproductsResource.PatchRequest.LatencyToleranceEnum.PRODUCTUPDATELATENCYTOLERANCELATENCYTOLERANT;

                        await patch.ExecuteAsync(timeout.Token);
                    }, product.ProductId);

                    var n = Interlocked.Increment(ref done);
                    Console.WriteLine($"      {(ok ? "updated" : "FAILED ")} {product.ProductId}  ({n}/{products.Count}, {watch.Elapsed.TotalSeconds:0}s)");
                    return ok ? null : product.ProductId;
                }
                finally
                {
                    gate.Release();
                }
            }).ToList();

            var heartbeat = Heartbeat(watch, () => $"{done}/{products.Count} done", tasks);
            var results = await Task.WhenAll(tasks);
            await heartbeat;

            return new SendReport(products.Count, [.. results.Where(id => id is not null)!], watch.Elapsed, Math.Min(parallel, products.Count));
        }

        /// <summary>
        /// what one send run did, so the command can print a summary instead of leaving the
        /// reader to count the lines that scrolled by
        /// </summary>
        public record SendReport(int Sent, List<string> Failed, TimeSpan Elapsed, int Parallel)
        {
            public int Updated => Sent - Failed.Count;
        }

        /// <summary>a duration a human reads at a glance: "12m 40s", not "760,3s"</summary>
        public static string Human(this TimeSpan time)
            => time.TotalMinutes >= 1
                ? $"{(int)time.TotalMinutes}m {time.Seconds:00}s"
                : $"{time.TotalSeconds:0.0}s";

        private static async Task Heartbeat<T>(Stopwatch watch, Func<string> status, List<Task<T>> tasks)
        {
            while (!tasks.All(t => t.IsCompleted))
            {
                await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(15)), Task.WhenAll(tasks));
                if (!tasks.All(t => t.IsCompleted))
                    Console.WriteLine($"      still waiting... {watch.Elapsed.TotalSeconds:0}s, {status()}");
            }
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

