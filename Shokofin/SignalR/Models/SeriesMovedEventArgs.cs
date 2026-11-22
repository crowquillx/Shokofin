using System.Text.Json.Serialization;

namespace Shokofin.SignalR.Models;

/// <summary>
/// A <c>group:series.moved</c> event from a daily server, sent when a series
/// moves to another group.
/// </summary>
public class SeriesMovedEventArgs {
    /// <summary>
    /// Shoko series id.
    /// </summary>
    [JsonInclude, JsonPropertyName("SeriesID")]
    public int SeriesId { get; set; }

    /// <summary>
    /// The Shoko group id the series was in.
    /// </summary>
    [JsonInclude, JsonPropertyName("OldGroupID")]
    public int OldGroupId { get; set; }

    /// <summary>
    /// The Shoko group id the series is in now.
    /// </summary>
    [JsonInclude, JsonPropertyName("NewGroupID")]
    public int NewGroupId { get; set; }
}
