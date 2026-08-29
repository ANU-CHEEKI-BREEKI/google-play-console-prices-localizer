namespace ANU.APIs.GoogleDeveloperAPI.IAPManaging
{
    /// <summary>
    /// The shared half of the store listing pair: the csv keys, what Google caps each field at, and
    /// which fields exist at all. The listing is the store page itself - app name, short and full
    /// description, promo video - one per language, reachable only through an edit.
    /// </summary>
    public abstract class Command_LocalesListingBase : Command_LocalesEditBase
    {
        /// <summary>
        /// the id half of every key, 'listing.title' and so on. There is only one store page, so
        /// there is only one id
        /// </summary>
        protected const string ListingId = "listing";

        protected const string TitleField = "title";
        protected const string ShortField = "short_description";
        protected const string FullField = "full_description";
        protected const string VideoField = "video";

        /// <summary>google's own limits. A listing over any of them is rejected outright</summary>
        protected const int TitleLimit = 30;
        protected const int ShortLimit = 80;
        protected const int FullLimit = 4000;

        /// <summary>the limit of one field, or null for the video url, which has none</summary>
        protected static int? LimitOf(string field) => field switch
        {
            TitleField => TitleLimit,
            ShortField => ShortLimit,
            FullField => FullLimit,
            _ => null,
        };
    }
}
