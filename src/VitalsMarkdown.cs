using System.Globalization;
using System.Text;

namespace ANU.APIs.GoogleDeveloperAPI.IAPManaging
{
    /// <summary>
    /// Renders the collected data as one markdown document. The layout is optimized for being
    /// pasted into an LLM: units are spelled out, every table is small, and the raw stack traces
    /// sit at the bottom in fenced blocks.
    /// </summary>
    public static class VitalsMarkdown
    {
        public static string Build(VitalsReport report)
        {
            var sb = new StringBuilder();

            sb.AppendLine($"# Android vitals — {report.Package}");
            sb.AppendLine();
            sb.AppendLine($"- window: **{report.From} .. {report.To}** ({report.AggregationPeriod}, timezone {report.TimeZone}, end is exclusive)");
            sb.AppendLine($"- user cohort: {report.UserCohort}");
            sb.AppendLine($"- filter: {report.Filter ?? "—"}");
            sb.AppendLine($"- generated: {report.GeneratedAtUtc:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine($"- source: Google Play Developer Reporting API v1beta1");
            sb.AppendLine();

            AppendLegend(sb);

            if (report.Errors.Count > 0)
            {
                sb.AppendLine("## Warnings");
                sb.AppendLine();
                foreach (var error in report.Errors)
                    sb.AppendLine($"- {error}");
                sb.AppendLine();
            }

            AppendReleases(sb, report);
            AppendFreshness(sb, report);
            AppendSummary(sb, report);

            foreach (var section in report.Sections)
                AppendSection(sb, report, section);

            AppendIssues(sb, report);
            AppendAnomalies(sb, report);

            return sb.ToString();
        }

        static void AppendLegend(StringBuilder sb)
        {
            sb.AppendLine("## How to read this");
            sb.AppendLine();
            sb.AppendLine("- every `*Rate` metric is a **percentage of distinct users** in the bucket that hit the problem at least once, rendered here as a percentage.");
            sb.AppendLine("- `userPerceived*` variants only count events that happened while the user was actively using the app. Those are the ones Google uses for the bad-behaviour thresholds (2% user-perceived crash rate / 0.47% user-perceived ANR rate).");
            sb.AppendLine("- `*7dUserWeighted` / `*28dUserWeighted` are rolling averages the API returns per bucket, not something computed here.");
            sb.AppendLine("- `userDays` is the sum of `distinctUsers` over all buckets. It is a **weight**, not a distinct user count — the same user counts once per bucket.");
            sb.AppendLine("- `impact` is `Σ(rate × distinctUsers)` for rates, i.e. roughly how many user-days were hit. Breakdowns are ranked by it, so a 40% crash rate on 3 users does not outrank a 3% crash rate on the whole install base.");
            sb.AppendLine("- period totals of rates are user-weighted averages over the window, computed here from the daily rows.");
            sb.AppendLine("- `distinctUsers` is rounded by Google (to 10/100/1K/1M depending on magnitude), so small slices are approximate.");
            sb.AppendLine();
        }

        static void AppendReleases(StringBuilder sb, VitalsReport report)
        {
            if (report.Releases.Count == 0)
                return;

            sb.AppendLine("## Releases currently serving");
            sb.AppendLine();
            sb.AppendLine("| track | release | version codes |");
            sb.AppendLine("|---|---|---|");
            foreach (var release in report.Releases)
                sb.AppendLine($"| {Escape(release.Track)} | {Escape(release.DisplayName)} | {string.Join(", ", release.VersionCodes)} |");
            sb.AppendLine();
        }

        static void AppendFreshness(StringBuilder sb, VitalsReport report)
        {
            if (report.Freshness.Count == 0)
                return;

            sb.AppendLine("## Data freshness");
            sb.AppendLine();
            sb.AppendLine("| metric set | aggregation | latest available |");
            sb.AppendLine("|---|---|---|");
            foreach (var item in report.Freshness)
                sb.AppendLine($"| {item.MetricSet} | {item.AggregationPeriod} | {item.LatestEndTime} |");
            sb.AppendLine();
        }

        static void AppendSummary(StringBuilder sb, VitalsReport report)
        {
            var sections = report.Sections.Where(s => s.Totals.Count > 0).ToArray();
            if (sections.Length == 0)
                return;

            sb.AppendLine("## Summary over the whole window");
            sb.AppendLine();
            sb.AppendLine("| metric set | metric | value |");
            sb.AppendLine("|---|---|---|");

            foreach (var section in sections)
            {
                foreach (var (metric, value) in section.Totals)
                    sb.AppendLine($"| {section.Title} | {metric} | {Format(metric, value)} |");
            }

            sb.AppendLine();
        }

        static void AppendSection(StringBuilder sb, VitalsReport report, VitalsSection section)
        {
            sb.AppendLine($"## {section.Title}");
            sb.AppendLine();

            if (!string.IsNullOrEmpty(section.To) && section.To != report.To)
            {
                sb.AppendLine($"> this metric set is less fresh than the rest — window trimmed to **{section.From} .. {section.To}**");
                sb.AppendLine();
            }

            if (!string.IsNullOrEmpty(section.Note))
            {
                sb.AppendLine($"> {section.Note}");
                sb.AppendLine();
            }

            if (section.Timeline.Count > 0)
            {
                var hasSeries = section.Timeline.Any(p => !string.IsNullOrEmpty(p.Series));
                var metrics = section.Metrics;

                sb.AppendLine("### Timeline");
                sb.AppendLine();
                sb.Append("| period |");
                if (hasSeries)
                    sb.Append(" series |");
                foreach (var metric in metrics)
                    sb.Append($" {metric} |");
                sb.AppendLine();

                sb.Append("|---|");
                if (hasSeries)
                    sb.Append("---|");
                foreach (var _ in metrics)
                    sb.Append("---|");
                sb.AppendLine();

                foreach (var point in section.Timeline)
                {
                    sb.Append($"| {point.Time} |");
                    if (hasSeries)
                        sb.Append($" {point.Series} |");
                    foreach (var metric in metrics)
                        sb.Append($" {(point.Values.TryGetValue(metric, out var value) ? Format(metric, value) : "—")} |");
                    sb.AppendLine();
                }

                sb.AppendLine();
            }

            foreach (var breakdown in section.Breakdowns)
                AppendBreakdown(sb, section, breakdown);
        }

        static void AppendBreakdown(StringBuilder sb, VitalsSection section, VitalsBreakdown breakdown)
        {
            if (breakdown.Slices.Count == 0)
                return;

            sb.AppendLine($"### By {breakdown.Dimension} — top {breakdown.Slices.Count} of {breakdown.TotalSlices} by impact");
            sb.AppendLine();

            if (breakdown.IsRate)
            {
                sb.AppendLine($"| {breakdown.Dimension} | {breakdown.Metric} | userDays | share of userDays | impact (affected userDays) |");
                sb.AppendLine("|---|---|---|---|---|");
                foreach (var slice in breakdown.Slices)
                {
                    sb.AppendLine($"| {Escape(slice.Value)} | {Percent(slice.Rate)} | {Number(slice.UserDays)} | {Percent(slice.UserShare)} | {Number(slice.Impact)} |");
                }
            }
            else
            {
                sb.AppendLine($"| {breakdown.Dimension} | {breakdown.Metric} | userDays | share of userDays |");
                sb.AppendLine("|---|---|---|---|");
                foreach (var slice in breakdown.Slices)
                {
                    sb.AppendLine($"| {Escape(slice.Value)} | {Number(slice.Impact)} | {Number(slice.UserDays)} | {Percent(slice.UserShare)} |");
                }
            }

            sb.AppendLine();
        }

        static void AppendIssues(StringBuilder sb, VitalsReport report)
        {
            if (report.Issues.Count == 0)
                return;

            sb.AppendLine("## Top error issues (clusters)");
            sb.AppendLine();
            sb.AppendLine("| # | type | cause | location | reports | users | % of affected users | versions | api levels |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|");

            var index = 0;
            foreach (var issue in report.Issues)
            {
                index++;
                sb.AppendLine(
                    $"| {index} | {issue.Type} | {Escape(Truncate(issue.Cause, 90))} | {Escape(Truncate(issue.Location, 70))} "
                    + $"| {issue.ErrorReportCount} | {issue.DistinctUsers} | {PercentRaw(issue.DistinctUsersPercent)} "
                    + $"| {Range(issue.FirstVersionCode, issue.LastVersionCode)} | {Range(issue.FirstApiLevel, issue.LastApiLevel)} |");
            }

            sb.AppendLine();

            index = 0;
            foreach (var issue in report.Issues)
            {
                index++;

                sb.AppendLine($"### {index}. {issue.Type}: {Truncate(issue.Cause, 200)}");
                sb.AppendLine();
                sb.AppendLine($"- location: `{issue.Location}`");
                var share = string.IsNullOrEmpty(issue.DistinctUsersPercent)
                    ? ""
                    : $" ({PercentRaw(issue.DistinctUsersPercent)} of affected users)";

                sb.AppendLine($"- reports: {issue.ErrorReportCount} · users: {issue.DistinctUsers}{share}");
                sb.AppendLine($"- versionCode: {Range(issue.FirstVersionCode, issue.LastVersionCode)} · apiLevel: {Range(issue.FirstApiLevel, issue.LastApiLevel)}");

                if (!string.IsNullOrEmpty(issue.LastErrorReportTime))
                    sb.AppendLine($"- last seen: {issue.LastErrorReportTime}");
                if (!string.IsNullOrEmpty(issue.ConsoleUri))
                    sb.AppendLine($"- console: {issue.ConsoleUri}");

                foreach (var annotation in issue.Annotations)
                    sb.AppendLine($"- google hint ({annotation.Category}) **{annotation.Title}**: {annotation.Body}");

                sb.AppendLine();

                if (!string.IsNullOrEmpty(issue.SampleReportsNote))
                {
                    sb.AppendLine($"> {issue.SampleReportsNote}");
                    sb.AppendLine();
                }

                foreach (var sample in issue.SampleReports)
                {
                    sb.AppendLine($"sample — versionCode {sample.VersionCode ?? "?"}, apiLevel {sample.ApiLevel ?? "?"}, {sample.DeviceModel ?? "?"}, {sample.EventTime ?? "?"}");
                    sb.AppendLine();
                    sb.AppendLine("```");
                    sb.AppendLine(sample.Text.Replace("```", "'''"));
                    sb.AppendLine("```");

                    if (sample.TrimmedFromLines is int total)
                        sb.AppendLine($"*trimmed — first lines of {total}; run with `--max-trace-lines 0` for the full dump*");

                    sb.AppendLine();
                }
            }
        }

        static void AppendAnomalies(StringBuilder sb, VitalsReport report)
        {
            if (report.Anomalies.Count == 0)
                return;

            sb.AppendLine("## Anomalies detected by Google");
            sb.AppendLine();
            sb.AppendLine("| metric set | metric | value | from | to | dimensions |");
            sb.AppendLine("|---|---|---|---|---|---|");

            foreach (var anomaly in report.Anomalies)
            {
                sb.AppendLine(
                    $"| {anomaly.MetricSet} | {anomaly.Metric} | {anomaly.Value ?? "—"} "
                    + $"| {anomaly.From} | {anomaly.To} | {Escape(anomaly.Dimensions)} |");
            }

            sb.AppendLine();
        }

        // ---------------------------------------------------------------- formatting

        static string Format(string metric, decimal value)
            => metric.Contains("Rate", StringComparison.Ordinal) ? Percent(value) : Number(value);

        static string Percent(decimal value)
            => (value * 100).ToString("0.####", CultureInfo.InvariantCulture) + "%";

        static string PercentRaw(string? value)
        {
            if (string.IsNullOrEmpty(value))
                return "—";

            return decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
                ? Percent(parsed / 100)
                : value;
        }

        static string Number(decimal value)
            => value == decimal.Truncate(value)
                ? value.ToString("0", CultureInfo.InvariantCulture)
                : value.ToString("0.##", CultureInfo.InvariantCulture);

        static string Range(string? first, string? last)
        {
            if (string.IsNullOrEmpty(first) && string.IsNullOrEmpty(last))
                return "—";
            if (first == last || string.IsNullOrEmpty(last))
                return first ?? "—";
            if (string.IsNullOrEmpty(first))
                return last;
            return $"{first} → {last}";
        }

        static string Truncate(string value, int max)
            => value.Length <= max ? value : value[..max] + "…";

        static string Escape(string value)
            => value.Replace("|", "\\|").Replace("\n", " ");
    }
}
