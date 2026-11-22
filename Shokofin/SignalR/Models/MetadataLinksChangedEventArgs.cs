using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Shokofin.SignalR.Models;

/// <summary>
/// A <c>metadata:links.changed</c> event from a daily server, sent when the
/// TMDB (or other provider) links of one or more series change.
/// </summary>
public class MetadataLinksChangedEventArgs {
    /// <summary>
    /// Shoko series ids whose links changed.
    /// </summary>
    [JsonInclude, JsonPropertyName("ShokoSeriesIDs")]
    public List<int> SeriesIds { get; set; } = [];
}
