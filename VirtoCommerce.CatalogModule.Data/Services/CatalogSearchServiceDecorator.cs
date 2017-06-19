﻿using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VirtoCommerce.CatalogModule.Data.Search;
using VirtoCommerce.Domain.Catalog.Model;
using VirtoCommerce.Domain.Catalog.Services;
using VirtoCommerce.Platform.Core.Settings;
using SearchCriteria = VirtoCommerce.Domain.Catalog.Model.SearchCriteria;

namespace VirtoCommerce.CatalogModule.Data.Services
{
    /// <summary>
    /// Another implementation for ICatalogSearchService. Combines indexed and DB search providers.
    /// </summary>
    public class CatalogSearchServiceDecorator : ICatalogSearchService
    {
        private readonly CatalogSearchServiceImpl _catalogSearchService;
        private readonly IItemService _itemService;
        private readonly IProductSearchService _productSearchService;
        private readonly ISettingsManager _settingsManager;

        public CatalogSearchServiceDecorator(
            CatalogSearchServiceImpl catalogSearchService,
            IItemService itemService,
            IProductSearchService productSearchService,
            ISettingsManager settingsManager)
        {
            _catalogSearchService = catalogSearchService;
            _itemService = itemService;
            _productSearchService = productSearchService;
            _settingsManager = settingsManager;
        }

        public SearchResult Search(SearchCriteria criteria)
        {
            SearchResult result;

            var useIndexedSearch = _settingsManager.GetValue("VirtoCommerce.SearchApi.UseCatalogIndexedSearchInManager", true);
            var searchProducts = criteria.ResponseGroup.HasFlag(SearchResponseGroup.WithProducts);

            if (useIndexedSearch && searchProducts && !string.IsNullOrEmpty(criteria.Keyword))
            {
                result = new SearchResult();

                // TODO: create outline for category
                // TODO: implement sorting

                var serviceCriteria = new ProductSearchCriteria
                {
                    SearchPhrase = criteria.Keyword,
                    Catalog = criteria.CatalogId,
                    Outline = criteria.CategoryId,
                    WithHidden = criteria.WithHidden,
                    Skip = criteria.Skip,
                    Take = criteria.Take,
                    ResponseGroup = ItemResponseGroup.ItemInfo | ItemResponseGroup.Outlines,
                };

                SearchItems(result, serviceCriteria);
            }
            else
            {
                // use original implementation from catalog module
                result = _catalogSearchService.Search(criteria);
            }

            return result;
        }


        private void SearchItems(SearchResult result, ProductSearchCriteria criteria)
        {
            Task.Factory.StartNew(s => SearchItemsAsync(result, criteria), this, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default).Unwrap().GetAwaiter().GetResult();
        }

        private async Task SearchItemsAsync(SearchResult result, ProductSearchCriteria criteria)
        {
            var items = new List<CatalogProduct>();
            var itemsOrderedList = new List<string>();

            var foundItemCount = 0;
            var dbItemCount = 0;
            var searchRetry = 0;

            ProductSearchResult searchResults;

            do
            {
                // Search using criteria, it will only return IDs of the items
                searchResults = await _productSearchService.SearchAsync(criteria);

                searchRetry++;

                if (searchResults?.Items == null)
                {
                    continue;
                }

                //Get only new found itemIds
                var uniqueKeys = searchResults.Items.Select(x => x.Id.ToString()).Except(itemsOrderedList).ToArray();
                foundItemCount = uniqueKeys.Length;

                if (!searchResults.Items.Any())
                {
                    continue;
                }

                itemsOrderedList.AddRange(uniqueKeys);

                // if we can determine catalog, pass it to the service
                var catalog = criteria.Catalog;

                // Now load items from repository
                var currentItems = _itemService.GetByIds(uniqueKeys.ToArray(), criteria.ResponseGroup, catalog);

                var orderedList = currentItems.OrderBy(i => itemsOrderedList.IndexOf(i.Id));
                items.AddRange(orderedList);
                dbItemCount = currentItems.Length;

                //If some items were removed and search is out of sync try getting extra items
                if (foundItemCount > dbItemCount)
                {
                    criteria.Take += (foundItemCount - dbItemCount);
                }
            }
            while (foundItemCount > dbItemCount && searchResults?.Items != null && searchResults.Items.Any() && searchRetry <= 3 &&
                (criteria.Take + criteria.Skip) < searchResults.TotalCount);

            result.Products = items.ToArray();

            if (searchResults != null)
                result.ProductsTotalCount = (int)searchResults.TotalCount;
        }
    }
}