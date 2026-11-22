using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Shokofin.API.Info;
using Shokofin.API.Models;
using Shokofin.Configuration;

namespace Shokofin.API;

/// <summary>
/// Reads the show and specials posters chosen with the Season Posters Shoko
/// plugin (<see href="https://github.com/crowquillx/shoko-season-posters"/>).
/// Shoko gives a group only its main series' poster, so without it a show
/// repeats its main season's poster, and so does its specials season.
/// </summary>
/// <remarks>
/// The list is cached for <see cref="CacheDuration"/>. A Shoko server without
/// the plugin, or a failed lookup, yields no posters, so images never depend on
/// the plugin being there.
/// </remarks>
public class SeasonPostersLookup(ILogger<SeasonPostersLookup> logger, ShokoApiClient apiClient) {
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

    private readonly SemaphoreSlim _lock = new(1, 1);

    private IReadOnlyDictionary<string, SeasonPostersShow> _shows = new Dictionary<string, SeasonPostersShow>();

    private DateTime _fetchedAt = DateTime.MinValue;

    /// <summary>
    /// The Shoko image chosen as the show poster, if any.
    /// </summary>
    public async Task<Guid?> GetShowPosterId(ShowInfo showInfo, CancellationToken cancellationToken = default)
        => (await GetShow(showInfo, cancellationToken))?.ImageId;

    /// <summary>
    /// The Shoko image chosen as the specials season poster, if any.
    /// </summary>
    public async Task<Guid?> GetSpecialsPosterId(ShowInfo showInfo, CancellationToken cancellationToken = default)
        => (await GetShow(showInfo, cancellationToken))?.SpecialsImageId;

    private async Task<SeasonPostersShow?> GetShow(ShowInfo showInfo, CancellationToken cancellationToken) {
        if (!Plugin.Instance.Configuration.SeasonPosters_Enabled)
            return null;

        // Season Posters plans shows the way Shokofin's Shoko group structure
        // builds them, so other structures never match its keys.
        if (showInfo.DefaultSeason.StructureType is not SeriesStructureType.Shoko_Groups)
            return null;

        // A group without sub-groups is one show, keyed by the group. A series
        // in a group with sub-groups is a show of its own, keyed by the series.
        var key = showInfo.IsStandalone
            ? showInfo.ShokoSeriesId is { Length: > 0 } seriesId ? $"series:{seriesId}" : null
            : showInfo.ShokoGroupId is { Length: > 0 } groupId ? $"group:{groupId}" : null;
        if (key is null)
            return null;

        var shows = await GetShows(cancellationToken);
        return shows.TryGetValue(key, out var show) ? show : null;
    }

    private async Task<IReadOnlyDictionary<string, SeasonPostersShow>> GetShows(CancellationToken cancellationToken) {
        if (DateTime.UtcNow - _fetchedAt < CacheDuration)
            return _shows;

        await _lock.WaitAsync(cancellationToken);
        try {
            if (DateTime.UtcNow - _fetchedAt < CacheDuration)
                return _shows;

            var shows = new Dictionary<string, SeasonPostersShow>(StringComparer.Ordinal);
            try {
                var list = await apiClient.GetSeasonPostersShowList(cancellationToken);
                if (list is null) {
                    logger.LogDebug("Season Posters is not installed in Shoko, so shows keep their group posters.");
                }
                else {
                    foreach (var show in list.Shows) {
                        if (string.IsNullOrEmpty(show.Key) || show.Key.StartsWith("silo-", StringComparison.Ordinal))
                            continue;
                        if (show.ImageId is null && show.SpecialsImageId is null)
                            continue;
                        shows[show.Key] = show;
                    }
                    logger.LogDebug("Read {Count} show posters from Season Posters.", shows.Count);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                throw;
            }
            catch (Exception ex) {
                logger.LogDebug(ex, "Unable to read the Season Posters show poster list, so shows keep their group posters. {Message}", ex.Message);
            }

            _shows = shows;
            _fetchedAt = DateTime.UtcNow;
            return _shows;
        }
        finally {
            _lock.Release();
        }
    }
}
