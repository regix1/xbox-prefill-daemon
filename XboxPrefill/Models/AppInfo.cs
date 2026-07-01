namespace XboxPrefill.Models
{
    /// <summary>
    /// A prefillable Xbox/MS-Store title. <see cref="AppId"/> is the Store ProductId (the cache key);
    /// <see cref="BuildVersion"/> is the package version reported by GetBasePackage and is used to decide
    /// whether the app is already up to date.
    /// </summary>
    public sealed class AppInfo
    {
        /// <summary>The Store ProductId (big id), e.g. <c>9NBLGGH2JHXJ</c>. The primary cache key.</summary>
        public string AppId { get; set; }

        /// <summary>The package version, resolved from GetBasePackage. May be unknown until resolved.</summary>
        public string BuildVersion { get; set; }

        /// <summary>Package family name from titlehub, when known.</summary>
        public string Pfn { get; set; }

        public string Title { get; set; }

        /// <summary>When the account last played this title, per Xbox Live's title history. Null when never
        /// played, or when titlehub reported no history for it.</summary>
        public DateTimeOffset? LastTimePlayed { get; set; }

        public override string ToString()
        {
            if (Title == null)
            {
                return AppId;
            }
            return Title;
        }
    }
}
