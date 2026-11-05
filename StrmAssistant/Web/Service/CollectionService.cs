using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using StrmAssistant.Web.Api;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StrmAssistant.Web.Service
{
    public class CollectionService : BaseApiService
    {
        private static readonly string[] CollectedItemTypes =
        {
            nameof(BoxSet),
            nameof(Playlist)
        };

        private readonly ILibraryManager _libraryManager;
        private readonly IDtoService _dtoService;

        public CollectionService(ILibraryManager libraryManager, IDtoService dtoService)
        {
            _libraryManager = libraryManager;
            _dtoService = dtoService;
        }

        /// <summary>
        /// Collections a series belongs to. Emby only links a series to the collections it was added
        /// to directly, so collections containing one of its seasons are merged in here. Playlists
        /// linked to the series are kept as well so that replacing the web client's appears-on
        /// query does not drop them.
        /// </summary>
        public object Get(GetItemCollections request)
        {
            if (!(_libraryManager.GetItemById(request.Id) is Series series))
            {
                return new QueryResult<BaseItemDto>();
            }

            var collections = new List<BaseItem>();
            var seen = new HashSet<long>();

            try
            {
                var direct = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    ListItemIds = new[] { series.InternalId },
                    IncludeItemTypes = CollectedItemTypes,
                    Recursive = true
                });

                foreach (var item in direct)
                {
                    if ((item is BoxSet || item is Playlist) && seen.Add(item.InternalId))
                    {
                        collections.Add(item);
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.Debug("Collections - Direct query failed: " + ex.Message);
            }

            try
            {
                var seasons = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    ParentIds = new[] { series.InternalId },
                    IncludeItemTypes = new[] { nameof(Season) },
                    Recursive = false
                });

                foreach (var season in seasons.OfType<Season>())
                {
                    foreach (var linked in season.GetAllKnownCollections())
                    {
                        if (!seen.Add(linked.Id)) continue;

                        if (_libraryManager.GetItemById(linked.Id) is BoxSet boxSet)
                        {
                            collections.Add(boxSet);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.Debug("Collections - Season query failed: " + ex.Message);
            }

            var ordered = collections
                .OrderBy(i => i.SortName ?? i.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var items = _dtoService.GetBaseItemDtos(ordered, new DtoOptions(), GetUserForRequest(null));

            return new QueryResult<BaseItemDto>
            {
                Items = items,
                TotalRecordCount = items.Length
            };
        }
    }
}
