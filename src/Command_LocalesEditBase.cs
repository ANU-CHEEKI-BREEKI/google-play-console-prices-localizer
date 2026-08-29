using Google.Apis.AndroidPublisher.v3.Data;

namespace ANU.APIs.GoogleDeveloperAPI.IAPManaging
{
    /// <summary>
    /// Base for every 'locales' subcommand that reaches its data through an app edit - the draft
    /// change list the publisher api opens, reads or writes, and either commits or throws away.
    /// An export only ever throws its edit away, an import commits exactly one.
    /// </summary>
    public abstract class Command_LocalesEditBase : CommandBase
    {
        /// <summary>the --review flag: send the changes to Google review right away instead of leaving a console draft</summary>
        protected bool SendForReview => Args.HasFlag("--review");

        /// <summary>
        /// Commits the edit - as a draft by default: the changes land in the Play Console under
        /// 'Publishing overview' as changes not yet sent for review, and wait for a human. Only
        /// --review commits them straight into Google review, after which they publish on their own.
        ///
        /// Not every change can be held as a draft. When Google refuses, the commit is NOT retried
        /// the other way around here - that would publish exactly what the default promises not to.
        /// The refusal surfaces as an api error, and <see cref="PrintCommitHint"/> says which flag
        /// to re-run with.
        /// </summary>
        protected async Task CommitEdit(string editId)
        {
            var commit = Service!.Edits.Commit(Package, editId);

            if (!SendForReview)
                commit.ChangesNotSentForReview = true;

            await commit.ExecuteAsync();
        }

        /// <summary>where the committed changes ended up, so nobody waits for a review that was never asked for</summary>
        protected void PrintCommitted()
        {
            if (SendForReview)
            {
                Console.WriteLine("sent for Google review. After approval the changes go live on their own.");
                return;
            }

            Console.WriteLine("committed as a draft, nothing was sent for review.");
            Console.WriteLine("Play Console -> Publishing overview -> 'Send for review' ships it when you are ready.");
        }

        /// <summary>
        /// the way out of a commit Google refused over review handling, said next to Google's own
        /// error. Both directions exist: some changes cannot be held as a draft, some cannot be
        /// sent for review from the api at all
        /// </summary>
        protected void PrintCommitHint(Google.GoogleApiException ex)
        {
            if (!ex.Message.Contains("review", StringComparison.OrdinalIgnoreCase))
                return;

            Console.WriteLine(SendForReview
                ? "        Google refuses to send these changes for review from the api. Drop --review and send them from the Play Console instead."
                : "        Google cannot hold these changes as a draft. Re-run with --review to send them straight to Google review.");
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
