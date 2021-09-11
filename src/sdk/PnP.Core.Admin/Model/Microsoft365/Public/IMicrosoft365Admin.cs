﻿using System.Collections.Generic;
using System.Threading.Tasks;

namespace PnP.Core.Admin.Model.Microsoft365
{
    /// <summary>
    /// Microsoft 365 Admin features
    /// </summary>
    public interface IMicrosoft365Admin
    {
        /// <summary>
        /// Checks if this tenant is a multi-geo tenant
        /// </summary>
        /// <returns>True if multi-geo, false otherwise</returns>
        Task<bool> IsMultiGeoTenantAsync();

        /// <summary>
        /// Checks if this tenant is a multi-geo tenant
        /// </summary>
        /// <returns>True if multi-geo, false otherwise</returns>
        bool IsMultiGeoTenant();

        /// <summary>
        /// Returns a list of multi-geo locations for this tenant
        /// </summary>
        /// <returns>List of multi-geo locations if multi-geo, null otherwise</returns>
        Task<List<IGeoLocation>> GetMultiGeoLocationsAsync();

        /// <summary>
        /// Returns a list of multi-geo locations for this tenant
        /// </summary>
        /// <returns>List of multi-geo locations if multi-geo, null otherwise</returns>
        List<IGeoLocation> GetMultiGeoLocations();

    }
}
