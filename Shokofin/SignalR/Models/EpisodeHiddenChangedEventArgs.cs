using System.Text.Json.Serialization;

namespace Shokofin.SignalR.Models;

/// <summary>
/// A <c>metadata:episode.hiddenChanged</c> event from a daily server.
/// </summary>
public class EpisodeHiddenChangedEventArgs {
    /// <summary>
    /// Shoko episode id, if the hidden entry is a Shoko episode.
    /// </summary>
    [JsonInclude, JsonPropertyName("ShokoEpisodeID")]
    public int? ShokoEpisodeId { get; set; }

    /// <summary>
    /// Whether the episode is now hidden.
    /// </summary>
    [JsonInclude, JsonPropertyName("IsHidden")]
    public bool IsHidden { get; set; }
}
