using System.Text.Json.Serialization;

namespace Shokofin.SignalR.Models;

/// <summary>
/// A <c>group:group.*</c> event from a daily server. The reason is taken from
/// the event name, so it is not read from the payload.
/// </summary>
public class GroupInfoUpdatedEventArgs {
    /// <summary>
    /// Shoko group id.
    /// </summary>
    [JsonInclude, JsonPropertyName("GroupID")]
    public int GroupId { get; set; }
}
