using System.Collections.Generic;
using System.Linq;

using File = Shokofin.API.Models.File;

namespace Shokofin.Resolvers;

/// <summary>
/// Leaves out files that Shoko marks as a variation for the episodes another
/// file already covers.
/// </summary>
/// <remarks>
/// A variation is usually another version of an episode, but Shoko users also
/// mark the parts of an episode that is split across several files as
/// variations, to clear them from Shoko's release review, and those parts are
/// then the episode's only files. So files that are not variations are passed
/// through straight away, variations are held back until every other file has
/// been seen, and a variation is only dropped when every episode it is linked
/// to already has a file that is not a variation.
///
/// An instance is meant for one single-threaded enumeration of files.
/// </remarks>
internal sealed class VariationFilter {
    private readonly bool _enabled;

    private readonly HashSet<int> _covered = [];

    private readonly List<(string sourceLocation, File file, string seriesId, HashSet<int> episodeIds)> _deferred = [];

    public VariationFilter(bool enabled) {
        _enabled = enabled;
    }

    public static VariationFilter FromConfiguration()
        => new(Plugin.Instance.Configuration.VFS_SkipCoveredVariations);

    /// <summary>
    /// The number of variations left out so far.
    /// </summary>
    public int Skipped { get; private set; }

    /// <summary>
    /// Records the file and reports whether the caller should use it now. A
    /// variation is held back, to be handed out by <see cref="TakeRemaining"/>
    /// if it is still needed.
    /// </summary>
    public bool Accept(File file, string sourceLocation, string seriesId) {
        if (!_enabled)
            return true;

        var episodeIds = GetEpisodeIds(file, seriesId);
        if (!file.IsVariation) {
            _covered.UnionWith(episodeIds);
            return true;
        }

        _deferred.Add((sourceLocation, file, seriesId, episodeIds));
        return false;
    }

    /// <summary>
    /// Hands out the held back variations that are linked to at least one
    /// episode no other file covers. Call it after every file has been passed
    /// to <see cref="Accept"/>.
    /// </summary>
    public IReadOnlyList<(string sourceLocation, File file, string seriesId)> TakeRemaining() {
        var remaining = new List<(string sourceLocation, File file, string seriesId)>();
        foreach (var (sourceLocation, file, seriesId, episodeIds) in _deferred) {
            if (episodeIds.Count > 0 && episodeIds.All(_covered.Contains)) {
                Skipped++;
                continue;
            }
            remaining.Add((sourceLocation, file, seriesId));
        }
        _deferred.Clear();
        return remaining;
    }

    private static HashSet<int> GetEpisodeIds(File file, string seriesId)
        => file.CrossReferences
            .Where(xref => xref.Series.Shoko.HasValue && xref.Series.Shoko.Value.ToString() == seriesId)
            .SelectMany(xref => xref.Episodes)
            .Where(episode => episode.Shoko.HasValue)
            .Select(episode => episode.Shoko!.Value)
            .ToHashSet();
}
