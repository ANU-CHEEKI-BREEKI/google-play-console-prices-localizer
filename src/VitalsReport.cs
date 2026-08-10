namespace ANU.APIs.GoogleDeveloperAPI.IAPManaging
{
    /// <summary>
    /// Everything the export collected. Serialized as-is for --format json,
    /// and rendered by <see cref="VitalsMarkdown"/> for --format md.
    /// </summary>
    public sealed class VitalsReport
    {
        public string Package { get; set; } = "";
        public string From { get; set; } = "";
        public string To { get; set; } = "";
        public string AggregationPeriod { get; set; } = "";
        public string TimeZone { get; set; } = "";
        public string UserCohort { get; set; } = "";
        public string? Filter { get; set; }
        public DateTime GeneratedAtUtc { get; set; }

        public List<VitalsFreshness> Freshness { get; set; } = [];
        public List<VitalsSection> Sections { get; set; } = [];
        public List<VitalsIssue> Issues { get; set; } = [];
        public List<VitalsAnomaly> Anomalies { get; set; } = [];
        public List<string> Errors { get; set; } = [];
    }

    public sealed class VitalsFreshness
    {
        public string MetricSet { get; set; } = "";
        public string AggregationPeriod { get; set; } = "";
        public string LatestEndTime { get; set; } = "";
    }

    public sealed class VitalsSection
    {
        public string Key { get; set; } = "";
        public string Title { get; set; } = "";
        /// <summary>Window actually queried for this set - it can end earlier than the report window when the set is less fresh.</summary>
        public string From { get; set; } = "";
        public string To { get; set; } = "";
        public string? Note { get; set; }
        public List<string> Metrics { get; set; } = [];
        public Dictionary<string, decimal> Totals { get; set; } = [];
        public List<VitalsTimelinePoint> Timeline { get; set; } = [];
        public List<VitalsBreakdown> Breakdowns { get; set; } = [];
    }

    public sealed class VitalsTimelinePoint
    {
        public string Time { get; set; } = "";
        /// <summary>Value of the metric set's required dimension (reportType, startType), empty when there is none.</summary>
        public string Series { get; set; } = "";
        public Dictionary<string, decimal> Values { get; set; } = [];
    }

    public sealed class VitalsBreakdown
    {
        public string Dimension { get; set; } = "";
        public string Metric { get; set; } = "";
        public bool IsRate { get; set; }
        public int TotalSlices { get; set; }
        public List<VitalsSlice> Slices { get; set; } = [];
    }

    public sealed class VitalsSlice
    {
        public string Value { get; set; } = "";
        /// <summary>Sum of distinctUsers over all periods - a weight, not a distinct user count.</summary>
        public decimal UserDays { get; set; }
        public decimal UserShare { get; set; }
        /// <summary>User-weighted rate over the window, meaningless for count metrics.</summary>
        public decimal Rate { get; set; }
        /// <summary>Affected user-days for rates, plain sum for counts. Used for ranking.</summary>
        public decimal Impact { get; set; }
    }

    public sealed class VitalsIssue
    {
        public string Id { get; set; } = "";
        public string Type { get; set; } = "";
        public string Cause { get; set; } = "";
        public string Location { get; set; } = "";
        public string ErrorReportCount { get; set; } = "";
        public string DistinctUsers { get; set; } = "";
        public string? DistinctUsersPercent { get; set; }
        public string? FirstVersionCode { get; set; }
        public string? LastVersionCode { get; set; }
        public string? FirstApiLevel { get; set; }
        public string? LastApiLevel { get; set; }
        public string? LastErrorReportTime { get; set; }
        public string? ConsoleUri { get; set; }
        public List<VitalsAnnotation> Annotations { get; set; } = [];
        public List<VitalsReportSample> SampleReports { get; set; } = [];
        public string? SampleReportsNote { get; set; }
    }

    public sealed class VitalsAnnotation
    {
        public string Category { get; set; } = "";
        public string Title { get; set; } = "";
        public string Body { get; set; } = "";
    }

    public sealed class VitalsReportSample
    {
        public string? VersionCode { get; set; }
        public string? ApiLevel { get; set; }
        public string? DeviceModel { get; set; }
        public string? EventTime { get; set; }
        public string Text { get; set; } = "";
        /// <summary>Original line count, set only when the trace was trimmed. ANR dumps carry every thread and run into thousands of lines.</summary>
        public int? TrimmedFromLines { get; set; }
    }

    public sealed class VitalsAnomaly
    {
        public string MetricSet { get; set; } = "";
        public string Metric { get; set; } = "";
        public string? Value { get; set; }
        public string From { get; set; } = "";
        public string To { get; set; } = "";
        public string Dimensions { get; set; } = "";
    }
}
