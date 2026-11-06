using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Services;
using System.Collections.Generic;

namespace StrmAssistant.Web.Api
{
    /// <summary>
    /// Drop-in replacement for Emby's POST /Users/{UserId}/FavoriteItems/{Id}. Only moved in
    /// front of Emby's route when cross-library favorite separation is enabled at startup.
    /// </summary>
    [Route("/Users/{UserId}/FavoriteItems/{Id}", "POST", Summary = "Marks an item as a favorite")]
    [Authenticated]
    public class MarkVersionFavoriteItem : IReturn<UserItemDataDto>
    {
        [ApiMember(Name = "UserId", Description = "User Id", IsRequired = true, DataType = "string",
            ParameterType = "path")]
        public string UserId { get; set; }

        [ApiMember(Name = "Id", Description = "Item Id", IsRequired = true, DataType = "string",
            ParameterType = "path")]
        public string Id { get; set; }
    }

    /// <summary>
    /// Drop-in replacement for Emby's DELETE /Users/{UserId}/FavoriteItems/{Id} and
    /// POST /Users/{UserId}/FavoriteItems/{Id}/Delete.
    /// </summary>
    [Route("/Users/{UserId}/FavoriteItems/{Id}", "DELETE", Summary = "Unmarks an item as a favorite")]
    [Route("/Users/{UserId}/FavoriteItems/{Id}/Delete", "POST", Summary = "Unmarks an item as a favorite")]
    [Authenticated]
    public class UnmarkVersionFavoriteItem : IReturn<UserItemDataDto>
    {
        [ApiMember(Name = "UserId", Description = "User Id", IsRequired = true, DataType = "string",
            ParameterType = "path")]
        public string UserId { get; set; }

        [ApiMember(Name = "Id", Description = "Item Id", IsRequired = true, DataType = "string",
            ParameterType = "path")]
        public string Id { get; set; }
    }

    [Route("/StrmAssistant/VersionFavorites/Query", "GET")]
    [Authenticated]
    public class GetVersionFavorites : IReturn<VersionFavoriteResponse>
    {
        [ApiMember(Name = "Ids", Description = "Comma separated item ids", IsRequired = false,
            DataType = "string", ParameterType = "query")]
        public string Ids { get; set; }
    }

    public class VersionFavoriteResponse
    {
        public List<VersionFavoriteInfo> Items { get; set; } = new List<VersionFavoriteInfo>();
    }

    public class VersionFavoriteInfo
    {
        public string ItemId { get; set; }
        public bool IsFavorite { get; set; }
    }
}
