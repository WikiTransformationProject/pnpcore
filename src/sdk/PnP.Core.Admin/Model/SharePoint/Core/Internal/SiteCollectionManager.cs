﻿using PnP.Core.Services;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace PnP.Core.Admin.Model.SharePoint
{
    internal class SiteCollectionManager : ISiteCollectionManager
    {
        private readonly PnPContext context;

        internal SiteCollectionManager(PnPContext pnpContext)
        {
            context = pnpContext;
        }

        public async Task<List<ISiteCollection>> GetSiteCollectionsAsync(bool ignoreUserIsSharePointAdmin = false)
        {
            return await SiteCollectionEnumerator.GetAsync(context, ignoreUserIsSharePointAdmin).ConfigureAwait(false);
        }

        public List<ISiteCollection> GetSiteCollections(bool ignoreUserIsSharePointAdmin = false)
        {
            return GetSiteCollectionsAsync(ignoreUserIsSharePointAdmin).GetAwaiter().GetResult();
        }

        public async Task<List<ISiteCollectionWithDetails>> GetSiteCollectionsWithDetailsAsync()
        {
            return await SiteCollectionEnumerator.GetWithDetailsViaTenantAdminHiddenListAsync(context).ConfigureAwait(false);
        }

        public List<ISiteCollectionWithDetails> GetSiteCollectionsWithDetails()
        {
            return GetSiteCollectionsWithDetailsAsync().GetAwaiter().GetResult();
        }

        public async Task<List<IRecycledSiteCollection>> GetRecycledSiteCollectionsAsync()
        {
            return await SiteCollectionEnumerator.GetRecycledWithDetailsViaTenantAdminHiddenListAsync(context).ConfigureAwait(false);
        }

        public List<IRecycledSiteCollection> GetRecycledSiteCollections()
        {
            return GetRecycledSiteCollectionsAsync().GetAwaiter().GetResult();
        }

        public async Task<PnPContext> CreateSiteCollectionAsync(CommonSiteOptions siteToCreate, SiteCreationOptions creationOptions = null)
        {
            if (siteToCreate == null)
            {
                throw new ArgumentNullException(nameof(siteToCreate));
            }

            return await SiteCollectionCreator.CreateSiteCollectionAsync(context, siteToCreate, creationOptions).ConfigureAwait(false);
        }

        public PnPContext CreateSiteCollection(CommonSiteOptions siteToCreate, SiteCreationOptions creationOptions = null)
        {
            return CreateSiteCollectionAsync(siteToCreate, creationOptions).GetAwaiter().GetResult();
        }

        public async Task RecycleSiteCollectionAsync(Uri siteToDelete)
        {
            if (siteToDelete == null)
            {
                throw new ArgumentNullException(nameof(siteToDelete));
            }
            
            await SiteCollectionManagement.RecycleSiteCollectionAsync(context, siteToDelete).ConfigureAwait(false);
        }

        public void RecycleSiteCollection(Uri siteToDelete)
        {
            RecycleSiteCollectionAsync(siteToDelete).GetAwaiter().GetResult();
        }

        public void RestoreSiteCollection(Uri siteToRestore)
        {
            RestoreSiteCollectionAsync(siteToRestore).GetAwaiter().GetResult();
        }

        public async Task RestoreSiteCollectionAsync(Uri siteToRestore)
        {
            if (siteToRestore == null)
            {
                throw new ArgumentNullException(nameof(siteToRestore));
            }

            await SiteCollectionManagement.RestoreSiteCollectionAsync(context, siteToRestore).ConfigureAwait(false);
        }

        public async Task DeleteSiteCollectionAsync(Uri siteToDelete)
        {
            if (siteToDelete == null)
            {
                throw new ArgumentNullException(nameof(siteToDelete));
            }

            await SiteCollectionManagement.DeleteSiteCollectionAsync(context, siteToDelete).ConfigureAwait(false);
        }

        public void DeleteSiteCollection(Uri siteToDelete)
        {
            DeleteSiteCollectionAsync(siteToDelete).GetAwaiter().GetResult();
        }

        public async Task<ISiteCollectionProperties> GetSiteCollectionPropertiesAsync(Uri site)
        {
            if (site == null)
            {
                throw new ArgumentNullException(nameof(site));
            }

            return await SiteCollectionManagement.GetSiteCollectionPropertiesByUrlAsync(context, site, true).ConfigureAwait(true);
        }

        public ISiteCollectionProperties GetSiteCollectionProperties(Uri site)
        {
            return GetSiteCollectionPropertiesAsync(site).GetAwaiter().GetResult();
        }

        public async Task ConnectSiteCollectionToGroupAsync(ConnectSiteToGroupOptions siteGroupConnectOptions, CreationOptions creationOptions = null)
        {
            if (siteGroupConnectOptions == null)
            {
                throw new ArgumentNullException(nameof(siteGroupConnectOptions));
            }

            await SiteCollectionCreator.ConnectGroupToSiteAsync(context, siteGroupConnectOptions, creationOptions).ConfigureAwait(true);
        }

        public void ConnectSiteCollectionToGroup(ConnectSiteToGroupOptions siteGroupConnectOptions, CreationOptions creationOptions = null)
        {
            ConnectSiteCollectionToGroupAsync(siteGroupConnectOptions, creationOptions).GetAwaiter().GetResult();
        }
    }
}
