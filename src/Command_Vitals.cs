using System.Globalization;
using System.Text;
using Google.Apis.Playdeveloperreporting.v1beta1;
using Google.Apis.Playdeveloperreporting.v1beta1.Data;
using Newtonsoft.Json;

namespace ANU.APIs.GoogleDeveloperAPI.IAPManaging
{
    /// <summary>
    /// Exports Android vitals through the Google Play Developer Reporting API into one
    /// self-contained report. The Play Console shows the same numbers, but spread over dozens
    /// of screens - here everything lands in a single file you can hand to an LLM.
    /// </summary>
    public class Command_Vitals : CommandBase
    {
        const string LosAngeles = "America/Los_Angeles";
        const int MaxIssuesPageSize = 1000;

        public override string Name => "vitals";

        public override string Description
            => "Exports Android vitals (crash/ANR rates, slow start & rendering, wakeups, wakelocks, LMKs, top crash clusters with stack traces and anomalies) into a single markdown/json report, ready to be analyzed by an LLM.";

        // this command talks to a completely different API than the IAP ones
        public override bool NeedsAndroidPublisher => false;
        public override bool NeedsPlayDeveloperReporting => true;
        public override string AuthUserKey => "user-reporting";

        bool verbose;
        string period = "DAILY";

        public override async Task ExecuteAsync()
        {
            verbose = Args.HasFlag("-v");

            if (string.IsNullOrWhiteSpace(Package))
            {
                Console.WriteLine("no package name. specify it in config.json or with --package");
                return;
            }

            period = Args.TryGetOption("--period", "DAILY").ToUpperInvariant();
            if (period != "DAILY" && period != "HOURLY")
            {
                Console.WriteLine($"unknown --period '{period}'. supported: DAILY, HOURLY");
                return;
            }

            var sets = ResolveSets(Args.TryGetOption("--sets", "crash,anr,errors"));
            if (sets is null)
                return;

            var breakdowns = ResolveBreakdowns(Args.TryGetOption("--by", "versionCode,apiLevel,deviceModel,countryCode"));

            var cohort = Args.TryGetOption("--cohort", "OS_PUBLIC").ToUpperInvariant();
            var filter = Args.TryGetOption("--filter", "");
            var days = ParseInt(Args.TryGetOption("--days", "28"), 28);
            var top = ParseInt(Args.TryGetOption("--top", "15"), 15);
            var issuesCount = ParseInt(Args.TryGetOption("--issues", "20"), 20);
            var samples = ParseInt(Args.TryGetOption("--samples", "1"), 1);
            var maxPages = ParseInt(Args.TryGetOption("--max-pages", "5"), 5);
            var maxTraceLines = ParseInt(Args.TryGetOption("--max-trace-lines", "150"), 150);
            var format = Args.TryGetOption("--format", "md").ToLowerInvariant();
            var withAnomalies = !Args.HasFlag("--no-anomalies");

            var timeZone = period == "HOURLY" ? "UTC" : LosAngeles;

            var report = new VitalsReport
            {
                Package = Package,
                AggregationPeriod = period,
                TimeZone = timeZone,
                UserCohort = cohort,
                Filter = string.IsNullOrWhiteSpace(filter) ? null : filter,
                GeneratedAtUtc = DateTime.UtcNow,
            };

            try
            {
                var options = await ReportingService!.Apps.FetchReleaseFilterOptions($"apps/{Package}").ExecuteAsync();

                foreach (var track in options.Tracks ?? [])
                {
                    foreach (var release in track.ServingReleases ?? [])
                    {
                        report.Releases.Add(new VitalsRelease
                        {
                            Track = track.DisplayName ?? track.Type ?? "",
                            DisplayName = release.DisplayName ?? "",
                            VersionCodes = [.. (release.VersionCodes ?? []).Select(c => c.ToString())],
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                report.Errors.Add($"releases: {Describe(ex)}");
            }

            var releaseNames = report.Releases
                .SelectMany(r => r.VersionCodes.Select(code => (code, r.DisplayName)))
                .GroupBy(x => x.code)
                .ToDictionary(g => g.Key, g => g.First().DisplayName);

            // freshness first: it tells us how far the data actually goes,
            // so we never ask for a tail of empty rows
            foreach (var set in sets)
            {
                try
                {
                    var freshness = await set.GetFreshnessAsync(ReportingService!, set.ResourceName(Package));
                    foreach (var item in freshness?.Freshnesses ?? [])
                    {
                        report.Freshness.Add(new VitalsFreshness
                        {
                            MetricSet = set.Key,
                            AggregationPeriod = item.AggregationPeriod ?? "",
                            LatestEndTime = FormatDateTime(item.LatestEndTime, item.AggregationPeriod ?? ""),
                        });
                    }
                }
                catch (Exception ex)
                {
                    report.Errors.Add($"{set.Key}: could not read freshness - {Describe(ex)}");
                }
            }

            var (startDate, endDate) = ResolveWindow(days, report);
            var start = ToGoogle(startDate);
            var end = ToGoogle(endDate);

            report.From = FormatDateTime(start, period);
            report.To = FormatDateTime(end, period);

            Console.WriteLine($"exporting vitals for {Package}");
            Console.WriteLine($"window: {report.From} .. {report.To} ({period}, {timeZone}), cohort: {cohort}");
            Console.WriteLine();

            foreach (var set in sets)
            {
                var section = new VitalsSection { Key = set.Key, Title = set.Title };
                report.Sections.Add(section);

                if (period == "HOURLY" && !set.SupportsHourly)
                {
                    section.Note = "metric set supports DAILY aggregation only - skipped";
                    Console.WriteLine($"  {set.Key}: DAILY only, skipped");
                    continue;
                }

                // freshness differs per metric set - errorCount is usually a day ahead of the rate
                // sets, and asking a rate set for that extra day is a hard 400, not an empty tail
                var setEndDate = ClampToFreshness(endDate, set.Key, report);
                if (setEndDate <= startDate)
                {
                    section.Note = $"no data available yet in the requested window (freshest is {FormatDateTime(ToGoogle(setEndDate), period)})";
                    Console.WriteLine($"  {set.Key}: {section.Note}");
                    continue;
                }

                var timeline = new GooglePlayDeveloperReportingV1beta1TimelineSpec
                {
                    AggregationPeriod = period,
                    StartTime = start,
                    EndTime = ToGoogle(setEndDate),
                };

                section.From = report.From;
                section.To = FormatDateTime(timeline.EndTime, period);

                Console.WriteLine(section.To == report.To
                    ? $"  {set.Key}: timeline..."
                    : $"  {set.Key}: timeline... (trimmed to {section.To}, that is all the data there is)");

                var metrics = set.MetricsFor(period);
                section.Metrics = [.. metrics];

                try
                {
                    var rows = await QueryAllAsync(set, new VitalsQuery
                    {
                        Metrics = [.. metrics],
                        Dimensions = [.. set.RequiredDimensions],
                        Filter = string.IsNullOrWhiteSpace(filter) ? null : filter,
                        UserCohort = cohort,
                        Timeline = timeline,
                    }, maxPages);

                    section.Timeline = BuildTimeline(rows, metrics, set.RequiredDimensions);
                    section.Totals = Aggregate(rows, metrics, set);
                }
                catch (Exception ex)
                {
                    section.Note = $"failed: {Describe(ex)}";
                    Console.WriteLine($"  {set.Key}: {section.Note}");
                    continue;
                }

                foreach (var dimension in breakdowns)
                {
                    if (!set.SupportedDimensions.Contains(dimension))
                        continue;

                    if (verbose)
                        Console.WriteLine($"  {set.Key}: by {dimension}...");

                    try
                    {
                        var dimensions = new List<string>(set.RequiredDimensions);
                        if (!dimensions.Contains(dimension))
                            dimensions.Add(dimension);

                        var rows = await QueryAllAsync(set, new VitalsQuery
                        {
                            Metrics = [.. metrics],
                            Dimensions = dimensions,
                            Filter = string.IsNullOrWhiteSpace(filter) ? null : filter,
                            UserCohort = cohort,
                            Timeline = timeline,
                        }, maxPages);

                        section.Breakdowns.Add(BuildBreakdown(set, dimension, rows, top, releaseNames));
                    }
                    catch (Exception ex)
                    {
                        report.Errors.Add($"{set.Key} by {dimension}: {Describe(ex)}");
                    }
                }
            }

            if (issuesCount > 0)
            {
                Console.WriteLine("  top error issues...");
                try
                {
                    report.Issues = await LoadIssuesAsync(start, end, filter, issuesCount, samples, maxTraceLines);
                }
                catch (Exception ex)
                {
                    report.Errors.Add($"error issues: {Describe(ex)}");
                    Console.WriteLine($"  error issues: {Describe(ex)}");
                }
            }

            if (withAnomalies)
            {
                Console.WriteLine("  anomalies...");
                try
                {
                    report.Anomalies = await LoadAnomaliesAsync();
                }
                catch (Exception ex)
                {
                    report.Errors.Add($"anomalies: {Describe(ex)}");
                    Console.WriteLine($"  anomalies: {Describe(ex)}");
                }
            }

            await WriteAsync(report, format);
        }

        // ---------------------------------------------------------------- querying

        async Task<List<GooglePlayDeveloperReportingV1beta1MetricsRow>> QueryAllAsync(VitalsMetricSet set, VitalsQuery query, int maxPages)
        {
            var all = new List<GooglePlayDeveloperReportingV1beta1MetricsRow>();
            var pages = 0;

            do
            {
                var page = await set.QueryAsync(ReportingService!, set.ResourceName(Package), query);
                all.AddRange(page.Rows);
                query.PageToken = page.NextPageToken;
                pages++;

                if (pages >= maxPages && !string.IsNullOrEmpty(query.PageToken))
                {
                    if (verbose)
                        Console.WriteLine($"    truncated after {maxPages} pages ({all.Count} rows), raise --max-pages to get the rest");
                    break;
                }
            }
            while (!string.IsNullOrEmpty(query.PageToken));

            return all;
        }

        async Task<List<VitalsIssue>> LoadIssuesAsync(GoogleTypeDateTime start, GoogleTypeDateTime end, string filter, int count, int samples, int maxTraceLines)
        {
            // issue search always works on hour-aligned UTC intervals,
            // regardless of the aggregation period used for the metric timelines
            var utcStart = ToUtcHour(start, period);
            var utcEnd = ToUtcHour(end, period);

            var request = ReportingService!.Vitals.Errors.Issues.Search($"apps/{Package}");
            request.IntervalStartTimeYear = utcStart.Year;
            request.IntervalStartTimeMonth = utcStart.Month;
            request.IntervalStartTimeDay = utcStart.Day;
            request.IntervalStartTimeHours = utcStart.Hour;
            request.IntervalStartTimeTimeZoneId = "UTC";
            request.IntervalEndTimeYear = utcEnd.Year;
            request.IntervalEndTimeMonth = utcEnd.Month;
            request.IntervalEndTimeDay = utcEnd.Day;
            request.IntervalEndTimeHours = utcEnd.Hour;
            request.IntervalEndTimeTimeZoneId = "UTC";
            request.OrderBy = "errorReportCount desc";
            request.PageSize = Math.Min(count, MaxIssuesPageSize);
            request.SampleErrorReportLimit = samples > 0 ? 1 : 0;

            if (!string.IsNullOrWhiteSpace(filter))
                request.Filter = filter;

            var response = await request.ExecuteAsync();

            var issues = new List<VitalsIssue>();

            foreach (var issue in (response.ErrorIssues ?? []).Take(count))
            {
                var id = LastSegment(issue.Name);

                var model = new VitalsIssue
                {
                    Id = id,
                    Type = issue.Type ?? "",
                    Cause = issue.Cause ?? "",
                    Location = issue.Location ?? "",
                    ErrorReportCount = issue.ErrorReportCount?.ToString() ?? "0",
                    DistinctUsers = issue.DistinctUsers?.ToString() ?? "0",
                    DistinctUsersPercent = issue.DistinctUsersPercent?.Value,
                    FirstVersionCode = issue.FirstAppVersion?.VersionCode?.ToString(),
                    LastVersionCode = issue.LastAppVersion?.VersionCode?.ToString(),
                    FirstApiLevel = issue.FirstOsVersion?.ApiLevel?.ToString(),
                    LastApiLevel = issue.LastOsVersion?.ApiLevel?.ToString(),
                    LastErrorReportTime = issue.LastErrorReportTimeDateTimeOffset?.ToString("u"),
                    ConsoleUri = issue.IssueUri,
                };

                foreach (var annotation in issue.Annotations ?? [])
                {
                    model.Annotations.Add(new VitalsAnnotation
                    {
                        Category = annotation.Category ?? "",
                        Title = annotation.Title ?? "",
                        Body = annotation.Body ?? "",
                    });
                }

                issues.Add(model);
            }

            if (samples > 0)
                await LoadStackTracesAsync(issues, utcStart, utcEnd, samples, maxTraceLines);

            return issues;
        }

        async Task LoadStackTracesAsync(List<VitalsIssue> issues, DateTime utcStart, DateTime utcEnd, int samples, int maxTraceLines)
        {
            foreach (var issue in issues)
            {
                if (string.IsNullOrEmpty(issue.Id))
                    continue;

                try
                {
                    var request = ReportingService!.Vitals.Errors.Reports.Search($"apps/{Package}");
                    request.IntervalStartTimeYear = utcStart.Year;
                    request.IntervalStartTimeMonth = utcStart.Month;
                    request.IntervalStartTimeDay = utcStart.Day;
                    request.IntervalStartTimeHours = utcStart.Hour;
                    request.IntervalStartTimeTimeZoneId = "UTC";
                    request.IntervalEndTimeYear = utcEnd.Year;
                    request.IntervalEndTimeMonth = utcEnd.Month;
                    request.IntervalEndTimeDay = utcEnd.Day;
                    request.IntervalEndTimeHours = utcEnd.Hour;
                    request.IntervalEndTimeTimeZoneId = "UTC";
                    request.Filter = $"errorIssueId = {QuoteFilterValue(issue.Id)}";
                    request.PageSize = samples;

                    var response = await request.ExecuteAsync();

                    foreach (var report in (response.ErrorReports ?? []).Take(samples))
                    {
                        var (text, originalLines) = TrimTrace(report.ReportText ?? "", maxTraceLines);

                        issue.SampleReports.Add(new VitalsReportSample
                        {
                            VersionCode = report.AppVersion?.VersionCode?.ToString(),
                            ApiLevel = report.OsVersion?.ApiLevel?.ToString(),
                            DeviceModel = DescribeDevice(report.DeviceModel),
                            EventTime = report.EventTimeDateTimeOffset?.ToString("u"),
                            Text = text,
                            TrimmedFromLines = originalLines,
                        });
                    }
                }
                catch (Exception ex)
                {
                    issue.SampleReportsNote = $"could not load stack trace: {Describe(ex)}";
                }
            }
        }

        /// <summary>
        /// An ANR report is a dump of every thread in the process - thousands of lines, of which only
        /// the head (the blocked main thread) is usually worth reading. Crash traces are short and pass through.
        /// </summary>
        static (string text, int? originalLines) TrimTrace(string text, int maxLines)
        {
            if (maxLines <= 0)
                return (text, null);

            var lines = text.Split('\n');
            if (lines.Length <= maxLines)
                return (text, null);

            return (string.Join('\n', lines.Take(maxLines)), lines.Length);
        }

        async Task<List<VitalsAnomaly>> LoadAnomaliesAsync()
        {
            var request = ReportingService!.Anomalies.List($"apps/{Package}");
            var response = await request.ExecuteAsync();

            var anomalies = new List<VitalsAnomaly>();

            foreach (var anomaly in response.Anomalies ?? [])
            {
                anomalies.Add(new VitalsAnomaly
                {
                    MetricSet = LastSegment(anomaly.MetricSet),
                    Metric = anomaly.Metric?.Metric ?? "",
                    Value = anomaly.Metric?.DecimalValue?.Value,
                    From = FormatDateTime(anomaly.TimelineSpec?.StartTime, anomaly.TimelineSpec?.AggregationPeriod ?? "DAILY"),
                    To = FormatDateTime(anomaly.TimelineSpec?.EndTime, anomaly.TimelineSpec?.AggregationPeriod ?? "DAILY"),
                    Dimensions = string.Join(", ", (anomaly.Dimensions ?? []).Select(d => $"{d.Dimension}={DimensionValue(d)}")),
                });
            }

            return anomalies;
        }

        // ---------------------------------------------------------------- shaping

        static List<VitalsTimelinePoint> BuildTimeline(
            List<GooglePlayDeveloperReportingV1beta1MetricsRow> rows,
            string[] metrics,
            string[] requiredDimensions)
        {
            var points = new List<VitalsTimelinePoint>();

            // required dimensions (reportType, startType) split a single day into several rows -
            // keep them as a separate series instead of silently collapsing them
            foreach (var group in rows.GroupBy(r => new
            {
                Time = FormatDateTime(r.StartTime, r.AggregationPeriod ?? "DAILY"),
                Series = string.Join("/", (r.Dimensions ?? [])
                    .Where(d => requiredDimensions.Contains(d.Dimension))
                    .Select(DimensionValue)),
            }))
            {
                var point = new VitalsTimelinePoint
                {
                    Time = group.Key.Time,
                    Series = group.Key.Series,
                };

                foreach (var metric in metrics)
                {
                    var value = group.Sum(r => MetricValue(r, metric) ?? 0);
                    point.Values[metric] = value;
                }

                points.Add(point);
            }

            return [.. points.OrderBy(p => p.Time, StringComparer.Ordinal).ThenBy(p => p.Series, StringComparer.Ordinal)];
        }

        static Dictionary<string, decimal> Aggregate(
            List<GooglePlayDeveloperReportingV1beta1MetricsRow> rows,
            string[] metrics,
            VitalsMetricSet set)
        {
            var totals = new Dictionary<string, decimal>();

            var userDays = rows.Sum(r => MetricValue(r, "distinctUsers") ?? 0);
            totals["userDays"] = userDays;
            totals["peakDailyUsers"] = rows
                .GroupBy(r => FormatDateTime(r.StartTime, r.AggregationPeriod ?? "DAILY"))
                .Select(g => g.Sum(r => MetricValue(r, "distinctUsers") ?? 0))
                .DefaultIfEmpty(0)
                .Max();

            foreach (var metric in metrics)
            {
                if (metric == "distinctUsers")
                    continue;

                if (IsRate(set, metric))
                {
                    // rates are per-period percentages - averaging them plainly would
                    // give a low-traffic day the same weight as a launch day
                    if (userDays > 0)
                    {
                        var weighted = rows.Sum(r => (MetricValue(r, metric) ?? 0) * (MetricValue(r, "distinctUsers") ?? 0));
                        totals[metric] = weighted / userDays;
                    }
                }
                else
                {
                    totals[metric] = rows.Sum(r => MetricValue(r, metric) ?? 0);
                }
            }

            return totals;
        }

        static VitalsBreakdown BuildBreakdown(
            VitalsMetricSet set,
            string dimension,
            List<GooglePlayDeveloperReportingV1beta1MetricsRow> rows,
            int top,
            Dictionary<string, string> releaseNames)
        {
            var breakdown = new VitalsBreakdown
            {
                Dimension = dimension,
                Metric = set.PrimaryMetric,
                IsRate = IsRate(set, set.PrimaryMetric),
            };

            var slices = new List<VitalsSlice>();

            foreach (var group in rows.GroupBy(r => DimensionKey(r, dimension)))
            {
                var users = group.Sum(r => MetricValue(r, "distinctUsers") ?? 0);
                var weighted = group.Sum(r => (MetricValue(r, set.PrimaryMetric) ?? 0) * (MetricValue(r, "distinctUsers") ?? 0));
                var count = group.Sum(r => MetricValue(r, set.PrimaryMetric) ?? 0);

                slices.Add(new VitalsSlice
                {
                    // release display names already tend to embed the code ("103 (1.4.5)")
                    Value = dimension == "versionCode" && releaseNames.TryGetValue(group.Key, out var release)
                        ? (release.Contains(group.Key, StringComparison.Ordinal) ? release : $"{group.Key} ({release})")
                        : group.Key,
                    UserDays = users,
                    Rate = users > 0 ? weighted / users : 0,
                    // for rates this is an estimate of how many user-days were actually hit,
                    // which is what makes a slice worth looking at
                    Impact = breakdown.IsRate ? weighted : count,
                });
            }

            var totalUserDays = slices.Sum(s => s.UserDays);
            foreach (var slice in slices)
                slice.UserShare = totalUserDays > 0 ? slice.UserDays / totalUserDays : 0;

            breakdown.TotalSlices = slices.Count;
            breakdown.Slices = [.. slices.OrderByDescending(s => s.Impact).ThenByDescending(s => s.UserDays).Take(top)];

            return breakdown;
        }

        static bool IsRate(VitalsMetricSet set, string metric)
            => set.PrimaryIsRate || metric.Contains("Rate", StringComparison.Ordinal);

        static decimal? MetricValue(GooglePlayDeveloperReportingV1beta1MetricsRow row, string metric)
        {
            var value = (row.Metrics ?? []).FirstOrDefault(m => m.Metric == metric)?.DecimalValue?.Value;
            if (value is null)
                return null;

            return decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
        }

        static string DimensionKey(GooglePlayDeveloperReportingV1beta1MetricsRow row, string dimension)
        {
            var value = (row.Dimensions ?? []).FirstOrDefault(d => d.Dimension == dimension);
            return value is null ? "(unknown)" : DimensionValue(value);
        }

        static string? DescribeDevice(GooglePlayDeveloperReportingV1beta1DeviceModelSummary? device)
        {
            if (device is null)
                return null;

            var id = device.DeviceId is null ? null : $"{device.DeviceId.BuildBrand}/{device.DeviceId.BuildDevice}";

            if (string.IsNullOrEmpty(device.MarketingName))
                return id;

            return string.IsNullOrEmpty(id) ? device.MarketingName : $"{device.MarketingName} ({id})";
        }

        static string DimensionValue(GooglePlayDeveloperReportingV1beta1DimensionValue value)
        {
            var raw = value.StringValue ?? value.Int64Value?.ToString() ?? "";
            return string.IsNullOrEmpty(value.ValueLabel) || value.ValueLabel == raw
                ? raw
                : $"{value.ValueLabel} ({raw})";
        }

        // ---------------------------------------------------------------- time

        /// <summary>
        /// Trims the window end to what the given metric set actually has. The API rejects
        /// anything past its own freshness with a 400 instead of just returning fewer rows.
        /// </summary>
        DateTime ClampToFreshness(DateTime end, string setKey, VitalsReport report)
        {
            var raw = report.Freshness
                .FirstOrDefault(f => f.MetricSet == setKey && f.AggregationPeriod == period)?.LatestEndTime;

            if (string.IsNullOrEmpty(raw) || ParseLoose(raw) is not DateTime freshest)
                return end;

            return freshest < end ? freshest : end;
        }

        (DateTime start, DateTime end) ResolveWindow(int days, VitalsReport report)
        {
            var explicitFrom = Args.TryGetOption("--from", "");
            var explicitTo = Args.TryGetOption("--to", "");

            var zone = period == "HOURLY"
                ? TimeZoneInfo.Utc
                : FindTimeZone(LosAngeles);

            var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime;

            // the freshest end the API admits to having - asking beyond it just returns nothing
            var freshest = report.Freshness
                .Where(f => f.AggregationPeriod == period && !string.IsNullOrEmpty(f.LatestEndTime))
                .Select(f => ParseLoose(f.LatestEndTime))
                .Where(d => d is not null)
                .Select(d => d!.Value)
                .DefaultIfEmpty(DateTime.MinValue)
                .Max();

            DateTime end;
            if (!string.IsNullOrWhiteSpace(explicitTo) && ParseLoose(explicitTo) is DateTime parsedTo)
                end = parsedTo;
            else if (freshest > DateTime.MinValue)
                end = freshest;
            else
                end = period == "HOURLY"
                    ? new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0)
                    : now.Date;

            DateTime start;
            if (!string.IsNullOrWhiteSpace(explicitFrom) && ParseLoose(explicitFrom) is DateTime parsedFrom)
                start = parsedFrom;
            else
                start = period == "HOURLY" ? end.AddHours(-days * 24) : end.AddDays(-days);

            if (start >= end)
                start = period == "HOURLY" ? end.AddHours(-1) : end.AddDays(-1);

            return (start, end);
        }

        GoogleTypeDateTime ToGoogle(DateTime value)
        {
            var dt = new GoogleTypeDateTime
            {
                Year = value.Year,
                Month = value.Month,
                Day = value.Day,
                TimeZone = new GoogleTypeTimeZone { Id = period == "HOURLY" ? "UTC" : LosAngeles },
            };

            if (period == "HOURLY")
                dt.Hours = value.Hour;

            return dt;
        }

        static DateTime ToUtcHour(GoogleTypeDateTime value, string period)
        {
            var local = new DateTime(value.Year ?? 1, value.Month ?? 1, value.Day ?? 1, value.Hours ?? 0, 0, 0, DateTimeKind.Unspecified);

            if (period == "HOURLY")
                return DateTime.SpecifyKind(local, DateTimeKind.Utc);

            var zone = FindTimeZone(value.TimeZone?.Id ?? LosAngeles);
            return TimeZoneInfo.ConvertTimeToUtc(local, zone);
        }

        static TimeZoneInfo FindTimeZone(string id)
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch
            {
                // America/Los_Angeles is UTC-8/-7; falling back to UTC only shifts the
                // window by a few hours, which is better than crashing the export
                return TimeZoneInfo.Utc;
            }
        }

        static DateTime? ParseLoose(string value)
            => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;

        static string FormatDateTime(GoogleTypeDateTime? value, string period)
        {
            if (value is null)
                return "";

            var date = $"{value.Year ?? 0:0000}-{value.Month ?? 0:00}-{value.Day ?? 0:00}";
            return period == "HOURLY" ? $"{date} {value.Hours ?? 0:00}:00" : date;
        }

        // ---------------------------------------------------------------- output

        async Task WriteAsync(VitalsReport report, string format)
        {
            var directory = Config.VitalsOutputPath;
            Directory.CreateDirectory(directory);

            var stamp = $"{report.From}_{report.To}".Replace(":", "").Replace(" ", "-");
            var baseName = Path.Combine(directory, $"{Package}-vitals-{stamp}");

            var written = new List<string>();

            if (format is "md" or "both")
            {
                var path = baseName + ".md";
                await File.WriteAllTextAsync(path, VitalsMarkdown.Build(report));
                written.Add(path);
            }

            if (format is "json" or "both")
            {
                var path = baseName + ".json";
                await File.WriteAllTextAsync(path, JsonConvert.SerializeObject(report, Formatting.Indented));
                written.Add(path);
            }

            if (written.Count == 0)
            {
                Console.WriteLine($"unknown --format '{format}'. supported: md, json, both");
                return;
            }

            Console.WriteLine();
            foreach (var path in written)
                Console.WriteLine($"written: {Path.GetFullPath(path)}");

            foreach (var error in report.Errors)
                Console.WriteLine($"warning: {error}");
        }

        // ---------------------------------------------------------------- args

        static VitalsMetricSet[]? ResolveSets(string value)
        {
            if (value.Equals("all", StringComparison.OrdinalIgnoreCase))
                return VitalsMetricSet.All;

            var keys = Split(value);
            var unknown = keys.Where(k => !VitalsMetricSet.All.Any(s => s.Key == k)).ToArray();

            if (unknown.Length > 0)
            {
                Console.WriteLine($"unknown metric set(s): {string.Join(", ", unknown)}");
                Console.WriteLine($"supported: {string.Join(", ", VitalsMetricSet.All.Select(s => s.Key))}, all");
                return null;
            }

            return [.. VitalsMetricSet.All.Where(s => keys.Contains(s.Key))];
        }

        static string[] ResolveBreakdowns(string value)
        {
            if (value.Equals("none", StringComparison.OrdinalIgnoreCase))
                return [];

            if (value.Equals("all", StringComparison.OrdinalIgnoreCase))
                return VitalsMetricSet.CommonDimensions;

            return Split(value);
        }

        static string[] Split(string value)
            => [.. value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

        static int ParseInt(string value, int fallback)
            => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

        static string LastSegment(string? name)
            => string.IsNullOrEmpty(name) ? "" : name[(name.LastIndexOf('/') + 1)..];

        static string QuoteFilterValue(string value)
            => value.All(char.IsDigit) ? value : $"\"{value}\"";

        static string Describe(Exception ex)
            => ex.InnerException is null ? ex.Message : $"{ex.Message} ({ex.InnerException.Message})";

        // ---------------------------------------------------------------- help

        public override void PrintHelp()
        {
            Console.WriteLine("vitals [--days <n>] [--from <date>] [--to <date>] [--period DAILY|HOURLY] [--sets <list>]");
            Console.WriteLine("       [--by <dimensions>] [--top <n>] [--issues <n>] [--samples <n>] [--cohort <cohort>]");
            Console.WriteLine("       [--max-trace-lines <n>] [--filter <expr>] [--format md|json|both] [--out <dir>] [--max-pages <n>]");
            Console.WriteLine("       [--no-anomalies] [-v]");
            Console.WriteLine();
            Console.WriteLine();

            Console.WriteLine("description:");
            CommandLinesUtils.PrintDescription(Description);
            Console.WriteLine();
            CommandLinesUtils.PrintDescription(
                "Uses the Google Play Developer Reporting API. It is a separate API from the one used by the "
                + "other commands, so you have to enable 'Google Play Developer Reporting API' in your Google Cloud "
                + "project and grant the consent once more on the first run (the token is cached separately)."
            );

            Console.WriteLine();
            Console.WriteLine("options:");

            CommandLinesUtils.PrintOption(
                "--days <n>",
                "Length of the exported window, in days. Default is 28. Ignored when --from is given."
            );
            CommandLinesUtils.PrintOption(
                "--from <date> / --to <date>",
                "Explicit window bounds, 'YYYY-MM-DD' (or 'YYYY-MM-DD HH:00' for hourly). By default the window ends at the freshest data point the API reports."
            );
            CommandLinesUtils.PrintOption(
                "--period DAILY|HOURLY",
                "Aggregation period. DAILY buckets are in America/Los_Angeles (that is how Google aggregates them), HOURLY buckets are in UTC. Default is DAILY."
            );
            CommandLinesUtils.PrintOption(
                "--sets <list>",
                $"Comma separated metric sets to export, or 'all'. Default is 'crash,anr,errors'. Supported: {string.Join(", ", VitalsMetricSet.All.Select(s => s.Key))}."
            );
            CommandLinesUtils.PrintOption(
                "--by <dimensions>",
                "Comma separated dimensions to slice every metric set by, or 'none'/'all'. Default is 'versionCode,apiLevel,deviceModel,countryCode'."
            );
            CommandLinesUtils.PrintOption(
                "--top <n>",
                "How many values to keep per dimension breakdown, ranked by impact. Default is 15."
            );
            CommandLinesUtils.PrintOption(
                "--issues <n>",
                "How many top crash/ANR clusters to include. Default is 20, 0 disables the section."
            );
            CommandLinesUtils.PrintOption(
                "--samples <n>",
                "How many sample stack traces to fetch per issue. Default is 1, 0 disables them. The API currently returns at most 1."
            );
            CommandLinesUtils.PrintOption(
                "--max-trace-lines <n>",
                "Keep only the first <n> lines of every sample trace. Default is 150, 0 keeps everything. ANR reports dump every thread in the process and run into thousands of lines, while the blocked main thread sits at the top."
            );
            CommandLinesUtils.PrintOption(
                "--cohort <cohort>",
                "User cohort: OS_PUBLIC, OS_BETA or APP_TESTERS. Default is OS_PUBLIC."
            );
            CommandLinesUtils.PrintOption(
                "--filter <expr>",
                "Raw AIP-160 filter passed to the API, e.g. 'versionCode = 1234' or 'deviceType = \"PHONE\"'."
            );
            CommandLinesUtils.PrintOption(
                "--format md|json|both",
                "Output format. Default is 'md' - a single markdown file meant to be fed to an LLM."
            );
            CommandLinesUtils.PrintOption(
                "--out <dir>",
                "Directory to write the report into. Default is 'VitalsOutputPath' from config.json ('./vitals-export' next to it)."
            );
            CommandLinesUtils.PrintOption(
                "--max-pages <n>",
                "Safety limit of pages (1000 rows each) per query. Default is 5."
            );
            CommandLinesUtils.PrintOption(
                "--no-anomalies",
                "Skip the anomalies section."
            );
            CommandLinesUtils.PrintOption(
                "-v",
                "Include detailed verbose output"
            );

            Console.WriteLine();
            Console.WriteLine("examples:");
            Console.WriteLine();
            CommandLinesUtils.PrintDescription("vitals", 4);
            CommandLinesUtils.PrintDescription("vitals --days 56 --sets all --by all --top 25", 4);
            CommandLinesUtils.PrintDescription("vitals --period HOURLY --days 3 --sets crash,anr", 4);
            CommandLinesUtils.PrintDescription("vitals --filter 'versionCode = 1234' --issues 50", 4);

            CommandLinesUtils.PrintCommonOptions();
        }
    }
}
