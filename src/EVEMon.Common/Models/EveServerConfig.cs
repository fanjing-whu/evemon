using System;
using EVEMon.Common.Constants;
using EVEMon.Common.Enumerations;

namespace EVEMon.Common.Models
{
    /// <summary>
    /// Provides server-specific configuration for ESI, SSO, and Image services.
    /// Supports both Tranquility (International) and Seren (Chinese) servers.
    /// </summary>
    public static class EveServerConfig
    {
        /// <summary>
        /// Gets the ESI base URL for the current server.
        /// </summary>
        public static Uri ESIBase
        {
            get
            {
                var serverType = Settings.ServerType;
                return serverType switch
                {
                    EveServerType.Seren => new Uri(NetworkConstants.ESISerenBase),
                    _ => new Uri(NetworkConstants.ESIBase)
                };
            }
        }

        /// <summary>
        /// Gets the SSO base URL for the current server.
        /// </summary>
        public static Uri SSOBase
        {
            get
            {
                var serverType = Settings.ServerType;
                return serverType switch
                {
                    EveServerType.Seren => new Uri(NetworkConstants.SSOSerenBase),
                    _ => new Uri(NetworkConstants.SSOBase)
                };
            }
        }

        /// <summary>
        /// Gets the SSO v2 base URL for the current server.
        /// </summary>
        public static Uri SSOBaseV2
        {
            get
            {
                var serverType = Settings.ServerType;
                return serverType switch
                {
                    EveServerType.Seren => new Uri(NetworkConstants.SSOSerenBaseV2),
                    _ => new Uri(NetworkConstants.SSOBaseV2)
                };
            }
        }

        /// <summary>
        /// Gets the image server base URL for the current server.
        /// </summary>
        public static Uri ImageServerBase
        {
            get
            {
                var serverType = Settings.ServerType;
                return serverType switch
                {
                    EveServerType.Seren => new Uri(NetworkConstants.EVEImageServerBaseSeren),
                    _ => new Uri(NetworkConstants.EVEImageServerBase)
                };
            }
        }

        /// <summary>
        /// Gets the tenant name for image URLs.
        /// </summary>
        public static string Tenant
        {
            get => EveServerTypeHelper.GetTenant(Settings.ServerType);
        }

        /// <summary>
        /// Gets the display name for the current server.
        /// </summary>
        public static string ServerName
        {
            get => EveServerTypeHelper.GetDisplayName(Settings.ServerType);
        }

        /// <summary>
        /// Gets the current server type.
        /// </summary>
        public static EveServerType CurrentServerType => Settings.ServerType;

        /// <summary>
        /// Seren's image server only implements the legacy CCP image API and serves
        /// nothing below 32px, so clamp smaller requests up to 32.
        /// </summary>
        private static int SerenImageSize(int size) => size < 32 ? 32 : size;

        /// <summary>
        /// Creates a portrait URL path with the correct tenant parameter.
        /// </summary>
        /// <param name="characterId">The character ID.</param>
        /// <param name="size">The image size.</param>
        /// <returns>The formatted path string.</returns>
        public static string GetPortraitPath(long characterId, int size)
        {
            if (CurrentServerType == EveServerType.Seren)
                // Seren only supports the legacy CCP image paths; the new
                // /characters/{id}/portrait form redirects to an HTML error page
                return $"/Character/{characterId}_{SerenImageSize(size)}.jpg";
            return $"/characters/{characterId}/portrait?tenant={Tenant}&size={size}";
        }

        /// <summary>
        /// Creates a corporation logo URL path with the correct tenant parameter.
        /// </summary>
        /// <param name="corporationId">The corporation ID.</param>
        /// <param name="size">The image size.</param>
        /// <returns>The formatted path string.</returns>
        public static string GetCorporationLogoPath(long corporationId, int size)
        {
            if (CurrentServerType == EveServerType.Seren)
                return $"/Corporation/{corporationId}_{SerenImageSize(size)}.png";
            return $"/corporations/{corporationId}/logo?tenant={Tenant}&size={size}";
        }

        /// <summary>
        /// Creates an alliance logo URL path with the correct tenant parameter.
        /// </summary>
        /// <param name="allianceId">The alliance ID.</param>
        /// <param name="size">The image size.</param>
        /// <returns>The formatted path string.</returns>
        public static string GetAllianceLogoPath(long allianceId, int size)
        {
            if (CurrentServerType == EveServerType.Seren)
                return $"/Alliance/{allianceId}_{SerenImageSize(size)}.png";
            return $"/alliances/{allianceId}/logo?tenant={Tenant}&size={size}";
        }

        /// <summary>
        /// Creates a type icon URL path with the correct tenant parameter.
        /// </summary>
        /// <param name="typeId">The type ID.</param>
        /// <param name="size">The image size.</param>
        /// <returns>The formatted path string.</returns>
        public static string GetTypeIconPath(int typeId, int size)
        {
            if (CurrentServerType == EveServerType.Seren)
                return $"/Type/{typeId}_{SerenImageSize(size)}.png";
            return $"/types/{typeId}/icon?tenant={Tenant}&size={size}";
        }

        /// <summary>
        /// Creates a type render URL path with the correct tenant parameter.
        /// </summary>
        /// <param name="typeId">The type ID.</param>
        /// <param name="size">The image size.</param>
        /// <returns>The formatted path string.</returns>
        public static string GetTypeRenderPath(int typeId, int size)
        {
            if (CurrentServerType == EveServerType.Seren)
                return $"/Render/{typeId}_{SerenImageSize(size)}.png";
            return $"/types/{typeId}/render?tenant={Tenant}&size={size}";
        }
    }
}
