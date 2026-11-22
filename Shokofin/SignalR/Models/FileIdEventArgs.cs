using System.Text.Json.Serialization;

namespace Shokofin.SignalR.Models;

/// <summary>
/// An event that only names the affected file, such as <c>release:removed</c>
/// or <c>file:updated</c> on daily servers. The file id may be missing.
/// </summary>
public class FileIdEventArgs {
    /// <summary>
    /// Shoko file id, if the event carries one.
    /// </summary>
    [JsonInclude, JsonPropertyName("FileID")]
    public int? FileId { get; set; }
}
