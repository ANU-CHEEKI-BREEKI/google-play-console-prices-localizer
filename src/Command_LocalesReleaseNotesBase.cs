using Google.Apis.AndroidPublisher.v3.Data;

namespace ANU.APIs.GoogleDeveloperAPI.IAPManaging
{
    /// <summary>
    /// The shared half of the release notes pair: which track, which release inside it, and the edit
    /// bookkeeping. Release notes live inside a track release, and a release is only reachable
    /// through an edit - a draft change list the api opens, reads or writes, and either commits or
    /// throws away. An export only ever throws its edit away, an import commits exactly one.
    /// </summary>
    public abstract class Command_LocalesReleaseNotesBase : CommandBase
    {
        /// <summary>
        /// the one key the csv carries. Same '&lt;id&gt;.&lt;field&gt;' shape as every other
        /// translations csv, so the same parser reads it back
        /// </summary>
        protected const string NotesKey = "release.notes";
        protected const string NotesField = "notes";

        /// <summary>what Google truncates release notes at, per language</summary>
        protected const int NotesLimit = 500;

        protected string TrackName => Args.TryGetOption("--track", "production");

        /// <summary>
        /// the --release option. The fallback differs on purpose: an export reads whatever is newest,
        /// an import defaults to the draft so a released version can never be touched by accident
        /// </summary>
        protected string ReleaseSelector(string fallback) => Args.TryGetOption("--release", fallback);

        /// <summary>a release players can already have: rolling out, halted mid rollout, or fully out</summary>
        protected static bool IsLive(TrackRelease release)
            => release.Status is "inProgress" or "halted" or "completed";

        /// <summary>
        /// what 'newest' means here. Normally the highest version code, but a draft with no bundle
        /// attached yet has no version codes at all - and that draft is the release being assembled
        /// right now, so it outranks everything
        /// </summary>
        static long Newness(TrackRelease release)
            => release.VersionCodes is { Count: > 0 } codes
                ? codes.Max() ?? long.MinValue
                : long.MaxValue;

        /// <summary>
        /// the release the selector points at, or null. 'draft' and 'live' select by status,
        /// 'latest' by newness, a plain number by version code. Anything else is nobody's release.
        /// </summary>
        protected static TrackRelease? SelectRelease(IList<TrackRelease> releases, string selector)
        {
            if (string.Equals(selector, "draft", StringComparison.OrdinalIgnoreCase))
                return releases.FirstOrDefault(r => string.Equals(r.Status, "draft", StringComparison.OrdinalIgnoreCase));

            if (string.Equals(selector, "live", StringComparison.OrdinalIgnoreCase))
                return releases.Where(IsLive).OrderByDescending(Newness).FirstOrDefault();

            if (string.Equals(selector, "latest", StringComparison.OrdinalIgnoreCase))
                return releases.OrderByDescending(Newness).FirstOrDefault();

            if (long.TryParse(selector, out var versionCode))
                return releases.FirstOrDefault(r => (r.VersionCodes ?? []).Contains(versionCode));

            return null;
        }

        protected static string Describe(TrackRelease release)
        {
            var codes = release.VersionCodes is { Count: > 0 } list
                ? string.Join(", ", list)
                : "no bundle yet";

            var notes = (release.ReleaseNotes ?? []).Count;

            return $"'{release.Name}' [{release.Status}] version code(s): {codes}, notes in {notes} language(s)";
        }

        /// <summary>
        /// the named track out of the edit, or null with the real track names printed - a typo in
        /// --track should answer with what exists, not with a bare 404
        /// </summary>
        protected async Task<Track?> FindTrack(string editId)
        {
            var tracks = (await Service!.Edits.Tracks.List(Package, editId).ExecuteAsync()).Tracks ?? [];

            var track = tracks.FirstOrDefault(t => string.Equals(t.TrackValue, TrackName, StringComparison.OrdinalIgnoreCase));
            if (track is not null)
                return track;

            Console.WriteLine($"no track '{TrackName}' in this app.");
            Console.WriteLine($"tracks that exist: {string.Join(", ", tracks.Select(t => t.TrackValue))}");
            return null;
        }

        /// <summary>
        /// prints why the selector found nothing, with every release the track actually has, so the
        /// next attempt can name one of them
        /// </summary>
        protected void PrintNoRelease(IList<TrackRelease> releases, string selector)
        {
            Console.WriteLine($"no release matches --release {selector} in the '{TrackName}' track.");

            if (releases.Count == 0)
            {
                Console.WriteLine("the track has no releases at all.");
                return;
            }

            Console.WriteLine("releases in the track:");
            foreach (var release in releases)
                Console.WriteLine($"        {Describe(release)}");

            Console.WriteLine("pick one with --release draft|latest|live|<versionCode>");
        }

        /// <summary>
        /// throws the draft edit away. An abandoned one would sit in the console as a pending change
        /// </summary>
        protected async Task DiscardEdit(AppEdit? edit, bool verbose)
        {
            if (edit is null)
                return;

            try
            {
                await Service!.Edits.Delete(Package, edit.Id).ExecuteAsync();
            }
            catch (Exception ex) when (verbose)
            {
                Console.WriteLine($"could not discard the edit {edit.Id}: {ex.Message}");
            }
            catch
            {
                // an already gone edit is exactly what was wanted
            }
        }
    }
}
