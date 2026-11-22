using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Shokofin.API.Models;

/// <summary>
/// The show poster list of the Season Posters Shoko plugin
/// (<see href="https://github.com/crowquillx/shoko-season-posters"/>), as
/// returned by <c>GET /api/plugin/season-posters/show-posters</c>.
/// </summary>
public class SeasonPostersShowList {
    [JsonPropertyName("shows")]
    public List<SeasonPostersShow> Shows { get; set; } = [];
}

/// <summary>
/// The posters Season Posters chose for one show.
/// </summary>
public class SeasonPostersShow {
    /// <summary>
    /// The show key. <c>group:{id}</c> for a Shoko group shown as one show,
    /// <c>series:{id}</c> for a series shown as a show of its own because its
    /// group has sub-groups, and <c>silo-group:{id}</c> for a show only Silo
    /// sees, which Jellyfin never has.
    /// </summary>
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("groupId")]
    public int? GroupId { get; set; }

    [JsonPropertyName("mainSeriesId")]
    public int MainSeriesId { get; set; }

    /// <summary>
    /// The Shoko image to use as the show's poster, if any.
    /// </summary>
    [JsonPropertyName("imageId")]
    public Guid? ImageId { get; set; }

    /// <summary>
    /// The Shoko image to use as the specials season's poster, if any.
    /// </summary>
    [JsonPropertyName("specialsImageId")]
    public Guid? SpecialsImageId { get; set; }
}
