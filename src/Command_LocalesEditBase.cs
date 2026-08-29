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
