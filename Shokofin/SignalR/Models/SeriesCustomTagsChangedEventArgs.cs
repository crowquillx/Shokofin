using System.Text.Json.Serialization;

namespace Shokofin.SignalR.Models;

/// <summary>
/// A <c>metadata:series.customTagsChanged</c> event from a daily server.
/// </summary>
public class SeriesCustomTagsChangedEventArgs {
    /// <summary>
    /// Shoko series id.
    /// </summary>
    [JsonInclude, JsonPropertyName("ShokoSeriesID")]
    public int SeriesId { get; set; }
}
