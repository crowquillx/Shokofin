using System.Collections.Generic;

namespace Shokofin.API.Models.Shoko;

/// <summary>
/// A Shoko user account, as returned by <c>/api/v3/User/Current</c>.
/// </summary>
public class ShokoUser {
    public int ID { get; set; }

    public string Username { get; set; } = string.Empty;

    public bool IsAdmin { get; set; }

    /// <summary>
    /// The tags whose groups and series Shoko hides from this user.
    /// </summary>
    public List<int> RestrictedTags { get; set; } = [];
}
