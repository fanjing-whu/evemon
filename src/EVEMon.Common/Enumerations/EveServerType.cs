namespace EVEMon.Common.Enumerations
{
    /// <summary>
    /// Represents the EVE server type (Tranquility or Seren).
    /// </summary>
    public enum EveServerType
    {
        /// <summary>
        /// EVE Online International Server (Tranquility)
        /// </summary>
        Tranquility = 0,

        /// <summary>
        /// EVE Online Chinese Server (Seren) operated by NetEase
        /// </summary>
        Seren = 1
    }

    /// <summary>
    /// Helper class for EveServerType operations.
    /// </summary>
    public static class EveServerTypeHelper
    {
        /// <summary>
        /// Gets the display name for the server type.
        /// </summary>
        /// <param name="serverType">The server type.</param>
        /// <returns>The display name.</returns>
        public static string GetDisplayName(EveServerType serverType)
        {
            return serverType switch
            {
                EveServerType.Tranquility => "Tranquility",
                EveServerType.Seren => "Seren (网易国服)",
                _ => "Unknown"
            };
        }

        /// <summary>
        /// Gets the tenant name used in image URLs.
        /// </summary>
        /// <param name="serverType">The server type.</param>
        /// <returns>The tenant name.</returns>
        public static string GetTenant(EveServerType serverType)
        {
            return serverType switch
            {
                EveServerType.Tranquility => "tranquility",
                EveServerType.Seren => "seren",
                _ => "tranquility"
            };
        }
    }
}
