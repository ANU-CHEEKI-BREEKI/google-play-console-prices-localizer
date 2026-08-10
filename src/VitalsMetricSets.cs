using Google.Apis.Playdeveloperreporting.v1beta1;
using Google.Apis.Playdeveloperreporting.v1beta1.Data;

namespace ANU.APIs.GoogleDeveloperAPI.IAPManaging
{
    /// <summary>
    /// One page of a metric set query. Every metric set has its own request/response type
    /// in the generated client, but they all carry the very same shape - so we unify them here.
    /// </summary>
    public sealed class VitalsPage
    {
        public IList<GooglePlayDeveloperReportingV1beta1MetricsRow> Rows { get; set; } = [];
        public string? NextPageToken { get; set; }
    }

    public sealed class VitalsQuery
    {
        public List<string> Metrics { get; set; } = [];
        public List<string> Dimensions { get; set; } = [];
        public string? Filter { get; set; }
        public string? UserCohort { get; set; }
        public GooglePlayDeveloperReportingV1beta1TimelineSpec Timeline { get; set; } = null!;
        public int PageSize { get; set; } = 1000;
        public string? PageToken { get; set; }
    }

    public sealed class VitalsMetricSet
    {
        /// <summary>Short name used in --sets.</summary>
        public required string Key { get; init; }
        public required string Title { get; init; }
        /// <summary>Last segment of the resource name, e.g. 'crashRateMetricSet'.</summary>
        public required string ResourceId { get; init; }
        public required string[] Metrics { get; init; }
        /// <summary>Metrics the API rejects for HOURLY aggregation (the rolling averages).</summary>
        public string[] DailyOnlyMetrics { get; init; } = [];
        /// <summary>Dimensions the API requires in every query for this set.</summary>
        public string[] RequiredDimensions { get; init; } = [];
        public bool SupportsHourly { get; init; }
        /// <summary>Metric used to rank dimension breakdowns.</summary>
        public required string PrimaryMetric { get; init; }
        /// <summary>True when the primary metric is a 0..1 rate that must be user-weighted when aggregated.</summary>
        public bool PrimaryIsRate { get; init; } = true;

        public required Func<PlaydeveloperreportingService, string, VitalsQuery, Task<VitalsPage>> QueryAsync { get; init; }
        public required Func<PlaydeveloperreportingService, string, Task<GooglePlayDeveloperReportingV1beta1FreshnessInfo?>> GetFreshnessAsync { get; init; }

        public string ResourceName(string package) => $"apps/{package}/{ResourceId}";

        public string[] MetricsFor(string aggregationPeriod)
            => aggregationPeriod == "HOURLY"
                ? Metrics.Where(m => !DailyOnlyMetrics.Contains(m)).ToArray()
                : Metrics;

        public static readonly VitalsMetricSet[] All =
        [
            new()
            {
                Key = "crash",
                Title = "Crash rate",
                ResourceId = "crashRateMetricSet",
                SupportsHourly = true,
                PrimaryMetric = "userPerceivedCrashRate",
                Metrics =
                [
                    "crashRate", "crashRate7dUserWeighted", "crashRate28dUserWeighted",
                    "userPerceivedCrashRate", "userPerceivedCrashRate7dUserWeighted", "userPerceivedCrashRate28dUserWeighted",
                    "distinctUsers",
                ],
                DailyOnlyMetrics =
                [
                    "crashRate7dUserWeighted", "crashRate28dUserWeighted",
                    "userPerceivedCrashRate7dUserWeighted", "userPerceivedCrashRate28dUserWeighted",
                ],
                QueryAsync = async (svc, name, q) =>
                {
                    var response = await svc.Vitals.Crashrate.Query(new GooglePlayDeveloperReportingV1beta1QueryCrashRateMetricSetRequest
                    {
                        Metrics = q.Metrics,
                        Dimensions = q.Dimensions,
                        Filter = q.Filter,
                        UserCohort = q.UserCohort,
                        TimelineSpec = q.Timeline,
                        PageSize = q.PageSize,
                        PageToken = q.PageToken,
                    }, name).ExecuteAsync();
                    return new VitalsPage { Rows = response.Rows ?? [], NextPageToken = response.NextPageToken };
                },
                GetFreshnessAsync = async (svc, name) => (await svc.Vitals.Crashrate.Get(name).ExecuteAsync()).FreshnessInfo,
            },

            new()
            {
                Key = "anr",
                Title = "ANR rate",
                ResourceId = "anrRateMetricSet",
                SupportsHourly = true,
                PrimaryMetric = "userPerceivedAnrRate",
                Metrics =
                [
                    "anrRate", "anrRate7dUserWeighted", "anrRate28dUserWeighted",
                    "userPerceivedAnrRate", "userPerceivedAnrRate7dUserWeighted", "userPerceivedAnrRate28dUserWeighted",
                    "distinctUsers",
                ],
                DailyOnlyMetrics =
                [
                    "anrRate7dUserWeighted", "anrRate28dUserWeighted",
                    "userPerceivedAnrRate7dUserWeighted", "userPerceivedAnrRate28dUserWeighted",
                ],
                QueryAsync = async (svc, name, q) =>
                {
                    var response = await svc.Vitals.Anrrate.Query(new GooglePlayDeveloperReportingV1beta1QueryAnrRateMetricSetRequest
                    {
                        Metrics = q.Metrics,
                        Dimensions = q.Dimensions,
                        Filter = q.Filter,
                        UserCohort = q.UserCohort,
                        TimelineSpec = q.Timeline,
                        PageSize = q.PageSize,
                        PageToken = q.PageToken,
                    }, name).ExecuteAsync();
                    return new VitalsPage { Rows = response.Rows ?? [], NextPageToken = response.NextPageToken };
                },
                GetFreshnessAsync = async (svc, name) => (await svc.Vitals.Anrrate.Get(name).ExecuteAsync()).FreshnessInfo,
            },

            new()
            {
                Key = "errors",
                Title = "Error report counts",
                ResourceId = "errorCountMetricSet",
                SupportsHourly = true,
                PrimaryMetric = "errorReportCount",
                PrimaryIsRate = false,
                RequiredDimensions = ["reportType"],
                Metrics = ["errorReportCount", "distinctUsers"],
                QueryAsync = async (svc, name, q) =>
                {
                    // errorCountMetricSet is the one set that does not take a user cohort
                    var response = await svc.Vitals.Errors.Counts.Query(new GooglePlayDeveloperReportingV1beta1QueryErrorCountMetricSetRequest
                    {
                        Metrics = q.Metrics,
                        Dimensions = q.Dimensions,
                        Filter = q.Filter,
                        TimelineSpec = q.Timeline,
                        PageSize = q.PageSize,
                        PageToken = q.PageToken,
                    }, name).ExecuteAsync();
                    return new VitalsPage { Rows = response.Rows ?? [], NextPageToken = response.NextPageToken };
                },
                GetFreshnessAsync = async (svc, name) => (await svc.Vitals.Errors.Counts.Get(name).ExecuteAsync()).FreshnessInfo,
            },

            new()
            {
                Key = "slow-start",
                Title = "Slow start rate",
                ResourceId = "slowStartRateMetricSet",
                PrimaryMetric = "slowStartRate",
                RequiredDimensions = ["startType"],
                Metrics = ["slowStartRate", "slowStartRate7dUserWeighted", "slowStartRate28dUserWeighted", "distinctUsers"],
                DailyOnlyMetrics = ["slowStartRate7dUserWeighted", "slowStartRate28dUserWeighted"],
                QueryAsync = async (svc, name, q) =>
                {
                    var response = await svc.Vitals.Slowstartrate.Query(new GooglePlayDeveloperReportingV1beta1QuerySlowStartRateMetricSetRequest
                    {
                        Metrics = q.Metrics,
                        Dimensions = q.Dimensions,
                        Filter = q.Filter,
                        UserCohort = q.UserCohort,
                        TimelineSpec = q.Timeline,
                        PageSize = q.PageSize,
                        PageToken = q.PageToken,
                    }, name).ExecuteAsync();
                    return new VitalsPage { Rows = response.Rows ?? [], NextPageToken = response.NextPageToken };
                },
                GetFreshnessAsync = async (svc, name) => (await svc.Vitals.Slowstartrate.Get(name).ExecuteAsync()).FreshnessInfo,
            },

            new()
            {
                Key = "slow-rendering",
                Title = "Slow rendering rate",
                ResourceId = "slowRenderingRateMetricSet",
                PrimaryMetric = "slowRenderingRate20Fps",
                Metrics =
                [
                    "slowRenderingRate20Fps", "slowRenderingRate20Fps7dUserWeighted", "slowRenderingRate20Fps28dUserWeighted",
                    "slowRenderingRate30Fps", "slowRenderingRate30Fps7dUserWeighted", "slowRenderingRate30Fps28dUserWeighted",
                    "distinctUsers",
                ],
                DailyOnlyMetrics =
                [
                    "slowRenderingRate20Fps7dUserWeighted", "slowRenderingRate20Fps28dUserWeighted",
                    "slowRenderingRate30Fps7dUserWeighted", "slowRenderingRate30Fps28dUserWeighted",
                ],
                QueryAsync = async (svc, name, q) =>
                {
                    var response = await svc.Vitals.Slowrenderingrate.Query(new GooglePlayDeveloperReportingV1beta1QuerySlowRenderingRateMetricSetRequest
                    {
                        Metrics = q.Metrics,
                        Dimensions = q.Dimensions,
                        Filter = q.Filter,
                        UserCohort = q.UserCohort,
                        TimelineSpec = q.Timeline,
                        PageSize = q.PageSize,
                        PageToken = q.PageToken,
                    }, name).ExecuteAsync();
                    return new VitalsPage { Rows = response.Rows ?? [], NextPageToken = response.NextPageToken };
                },
                GetFreshnessAsync = async (svc, name) => (await svc.Vitals.Slowrenderingrate.Get(name).ExecuteAsync()).FreshnessInfo,
            },

            new()
            {
                Key = "wakeups",
                Title = "Excessive wakeup rate",
                ResourceId = "excessiveWakeupRateMetricSet",
                PrimaryMetric = "excessiveWakeupRate",
                Metrics = ["excessiveWakeupRate", "excessiveWakeupRate7dUserWeighted", "excessiveWakeupRate28dUserWeighted", "distinctUsers"],
                DailyOnlyMetrics = ["excessiveWakeupRate7dUserWeighted", "excessiveWakeupRate28dUserWeighted"],
                QueryAsync = async (svc, name, q) =>
                {
                    var response = await svc.Vitals.Excessivewakeuprate.Query(new GooglePlayDeveloperReportingV1beta1QueryExcessiveWakeupRateMetricSetRequest
                    {
                        Metrics = q.Metrics,
                        Dimensions = q.Dimensions,
                        Filter = q.Filter,
                        UserCohort = q.UserCohort,
                        TimelineSpec = q.Timeline,
                        PageSize = q.PageSize,
                        PageToken = q.PageToken,
                    }, name).ExecuteAsync();
                    return new VitalsPage { Rows = response.Rows ?? [], NextPageToken = response.NextPageToken };
                },
                GetFreshnessAsync = async (svc, name) => (await svc.Vitals.Excessivewakeuprate.Get(name).ExecuteAsync()).FreshnessInfo,
            },

            new()
            {
                Key = "wakelocks",
                Title = "Stuck background wakelock rate",
                ResourceId = "stuckBackgroundWakelockRateMetricSet",
                PrimaryMetric = "stuckBgWakelockRate",
                Metrics = ["stuckBgWakelockRate", "stuckBgWakelockRate7dUserWeighted", "stuckBgWakelockRate28dUserWeighted", "distinctUsers"],
                DailyOnlyMetrics = ["stuckBgWakelockRate7dUserWeighted", "stuckBgWakelockRate28dUserWeighted"],
                QueryAsync = async (svc, name, q) =>
                {
                    var response = await svc.Vitals.Stuckbackgroundwakelockrate.Query(new GooglePlayDeveloperReportingV1beta1QueryStuckBackgroundWakelockRateMetricSetRequest
                    {
                        Metrics = q.Metrics,
                        Dimensions = q.Dimensions,
                        Filter = q.Filter,
                        UserCohort = q.UserCohort,
                        TimelineSpec = q.Timeline,
                        PageSize = q.PageSize,
                        PageToken = q.PageToken,
                    }, name).ExecuteAsync();
                    return new VitalsPage { Rows = response.Rows ?? [], NextPageToken = response.NextPageToken };
                },
                GetFreshnessAsync = async (svc, name) => (await svc.Vitals.Stuckbackgroundwakelockrate.Get(name).ExecuteAsync()).FreshnessInfo,
            },

            new()
            {
                Key = "lmk",
                Title = "Low memory kill rate",
                ResourceId = "lmkRateMetricSet",
                PrimaryMetric = "userPerceivedLmkRate",
                Metrics = ["userPerceivedLmkRate", "userPerceivedLmkRate7dUserWeighted", "userPerceivedLmkRate28dUserWeighted", "distinctUsers"],
                DailyOnlyMetrics = ["userPerceivedLmkRate7dUserWeighted", "userPerceivedLmkRate28dUserWeighted"],
                QueryAsync = async (svc, name, q) =>
                {
                    var response = await svc.Vitals.Lmkrate.Query(new GooglePlayDeveloperReportingV1beta1QueryLmkRateMetricSetRequest
                    {
                        Metrics = q.Metrics,
                        Dimensions = q.Dimensions,
                        Filter = q.Filter,
                        UserCohort = q.UserCohort,
                        TimelineSpec = q.Timeline,
                        PageSize = q.PageSize,
                        PageToken = q.PageToken,
                    }, name).ExecuteAsync();
                    return new VitalsPage { Rows = response.Rows ?? [], NextPageToken = response.NextPageToken };
                },
                GetFreshnessAsync = async (svc, name) => (await svc.Vitals.Lmkrate.Get(name).ExecuteAsync()).FreshnessInfo,
            },
        ];

        /// <summary>Dimensions every rate metric set understands. 'errors' is the odd one out - see ErrorCountDimensions.</summary>
        public static readonly string[] CommonDimensions =
        [
            "versionCode", "apiLevel", "countryCode", "deviceModel", "deviceBrand", "deviceType",
            "deviceRamBucket", "deviceSocMake", "deviceSocModel", "deviceCpuMake", "deviceCpuModel",
            "deviceGpuMake", "deviceGpuModel", "deviceGpuVersion", "deviceVulkanVersion",
            "deviceGlEsVersion", "deviceScreenSize", "deviceScreenDpi",
        ];

        /// <summary>errorCountMetricSet has no countryCode and adds issueId.</summary>
        public static readonly string[] ErrorCountDimensions =
        [
            "versionCode", "apiLevel", "deviceModel", "deviceType", "issueId",
            "deviceRamBucket", "deviceSocMake", "deviceSocModel", "deviceCpuMake", "deviceCpuModel",
            "deviceGpuMake", "deviceGpuModel", "deviceGpuVersion", "deviceVulkanVersion",
            "deviceGlEsVersion", "deviceScreenSize", "deviceScreenDpi",
        ];

        public string[] SupportedDimensions => Key == "errors" ? ErrorCountDimensions : CommonDimensions;
    }
}
