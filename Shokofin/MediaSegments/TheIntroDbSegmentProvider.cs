using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging;
using Shokofin.API;
using Shokofin.API.Info;

#if NET9_0_OR_GREATER
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.MediaSegments;
#else
using Jellyfin.Data.Enums;
using MediaBrowser.Controller;
#endif

namespace Shokofin.MediaSegments;

/// <summary>
/// Provides intro, recap, credits and preview segments from TheIntroDB for
/// items managed by Shokofin.
/// </summary>
/// <remarks>
/// A Shokofin episode is numbered by its place in the Shoko series or group,
/// which often is not TMDB's or TVDB's numbering, so looking it up by the
/// Jellyfin season and episode number would find another episode's markers.
/// This provider instead looks an episode up by the TMDB episode Shoko links it
/// to, numbered in that TMDB show's default order, and a movie by its linked
/// TMDB movie. An item without a TMDB link gets no segments. It never submits
/// anything to TheIntroDB.
/// </remarks>
public class TheIntroDbSegmentProvider(
    ILogger<TheIntroDbSegmentProvider> logger,
    ILibraryManager libraryManager,
    ShokoApiManager apiManager,
    ShokoIdLookup lookup,
    TheIntroDbClient client
) : IMediaSegmentProvider {
    /// <summary>
    /// How far a release TheIntroDB timed may differ in length from the file
    /// before its credits and previews stop applying. Intros and recaps are
    /// timed from the start, so they apply either way.
    /// </summary>
    private static readonly TimeSpan ReleaseTolerance = TimeSpan.FromMinutes(5);

    /// <inheritdoc />
    public string Name => "TheIntroDB (Shoko)";

    /// <inheritdoc />
    public ValueTask<bool> Supports(BaseItem item)
        => ValueTask.FromResult(
            Plugin.Instance.Configuration.IntroDB_Enabled &&
            item is Episode or Movie &&
            lookup.IsEnabledForItem(item)
        );

    /// <inheritdoc />
    public async Task<IReadOnlyList<MediaSegmentDto>> GetMediaSegments(MediaSegmentGenerationRequest request, CancellationToken cancellationToken) {
#if NET9_0_OR_GREATER
        // Jellyfin replaces this provider's segments with whatever we return,
        // so a failed lookup hands back what is already there.
        IReadOnlyList<MediaSegmentDto> existing = request.ExistingSegments ?? [];
#else
        IReadOnlyList<MediaSegmentDto> existing = [];
#endif
        if (!Plugin.Instance.Configuration.IntroDB_Enabled)
            return existing;

        if (libraryManager.GetItemById(request.ItemId) is not { } item || item is not (Episode or Movie))
            return existing;

        // An item that already has both an intro and an outro from us keeps
        // them, which saves TheIntroDB's daily lookup budget. A forced refresh
        // passes no existing segments and looks the item up again.
        if (existing.Any(s => s.Type is MediaSegmentType.Intro) && existing.Any(s => s.Type is MediaSegmentType.Outro))
            return existing;

        try {
            var target = await GetTarget(item).ConfigureAwait(false);
            if (target is null) {
                logger.LogDebug("No TMDB link to look up TheIntroDB markers by for item {ItemName}. (Item={ItemId})", item.Name, item.Id);
                return existing;
            }

            var duration = item.RunTimeTicks is long ticks and > 0 ? TimeSpan.FromTicks(ticks) : TimeSpan.Zero;
            if (duration > TimeSpan.FromHours(24))
                duration = TimeSpan.Zero;

            var media = await client.Lookup(target, (long)duration.TotalMilliseconds, Plugin.Instance.Configuration.IntroDB_ApiKey, cancellationToken).ConfigureAwait(false);
            var segments = ConvertMedia(media, duration, item.Id);
            logger.LogDebug("Found {Count} TheIntroDB segments for item {ItemName}. (Item={ItemId},Target={Target})", segments.Count, item.Name, item.Id, target);
            return segments.Count > 0 || existing.Count == 0 ? segments : existing;
        }
        catch (TheIntroDbRetryException ex) {
            logger.LogDebug("Skipped TheIntroDB lookup for item {ItemName}: {Message} (Item={ItemId})", item.Name, ex.Message, item.Id);
            return existing;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw;
        }
        catch (Exception ex) {
            logger.LogWarning(ex, "Unable to look up TheIntroDB markers for item {ItemName}. (Item={ItemId})", item.Name, item.Id);
            return existing;
        }
    }

#if NET10_0_OR_GREATER
    /// <inheritdoc />
    public Task CleanupExtractedData(Guid itemId, CancellationToken cancellationToken)
        => Task.CompletedTask;
#endif

    /// <summary>
    /// Chooses how to ask TheIntroDB for the item, or returns null when it
    /// cannot be addressed without risking another title's markers.
    /// </summary>
    private async Task<TheIntroDbTarget?> GetTarget(BaseItem item) {
        if (!lookup.TryGetEpisodeIdsFor(item, out var episodeIds) || episodeIds.Count == 0)
            return null;

        // A file linked to several episodes is looked up by the first. Its
        // credits only apply when the release length matches anyway.
        if (await apiManager.GetEpisodeInfo(episodeIds[0]).ConfigureAwait(false) is not { } episodeInfo)
            return null;

        if (item is Episode)
            return GetEpisodeTarget(episodeInfo) ?? GetMovieTarget(episodeInfo);
        return GetMovieTarget(episodeInfo) ?? GetEpisodeTarget(episodeInfo);
    }

    private static TheIntroDbTarget? GetEpisodeTarget(EpisodeInfo episodeInfo) {
        if (episodeInfo.TmdbEpisodes.FirstOrDefault() is not { } tmdbEpisode)
            return null;

        // TheIntroDB numbers episodes in TMDB's default order, not in an
        // alternate ordering picked in Shoko.
        var (season, episode) = tmdbEpisode.UsesAlternateOrdering
            ? (tmdbEpisode.OriginalSeasonNumber.Value, tmdbEpisode.OriginalEpisodeNumber.Value)
            : (tmdbEpisode.SeasonNumber, tmdbEpisode.EpisodeNumber);
        if (!IsPositiveId(tmdbEpisode.TmdbShowId) || season is < 0 or > 1000 || episode is < 1 or > 10000)
            return null;

        return new("tmdb_id", tmdbEpisode.TmdbShowId, IsEpisode: true, Season: season, Episode: episode);
    }

    private static TheIntroDbTarget? GetMovieTarget(EpisodeInfo episodeInfo)
        => episodeInfo.TmdbMovieId is { } movieId && IsPositiveId(movieId) ? new("tmdb_id", movieId) : null;

    private static bool IsPositiveId(string? value)
        => long.TryParse(value, out var id) && id > 0;

    /// <summary>
    /// Converts TheIntroDB's answer for a file of the given duration. Credits
    /// and previews are kept only when TheIntroDB timed them on a release about
    /// as long as the file, since they sit near its end.
    /// </summary>
    internal static List<MediaSegmentDto> ConvertMedia(TheIntroDbMedia? media, TimeSpan duration, Guid itemId) {
        var result = new List<MediaSegmentDto>();
        if (media is null)
            return result;

        var endsApply = true;
        if (media.GroupDurationMs is > 0 && duration > TimeSpan.Zero)
            endsApply = (TimeSpan.FromMilliseconds(media.GroupDurationMs.Value) - duration).Duration() <= ReleaseTolerance;

        result.AddRange(ConvertSegments(MediaSegmentType.Intro, media.Intro, duration, itemId));
        result.AddRange(ConvertSegments(MediaSegmentType.Recap, media.Recap, duration, itemId));
        if (endsApply) {
            result.AddRange(ConvertSegments(MediaSegmentType.Outro, media.Credits, duration, itemId));
            result.AddRange(ConvertSegments(MediaSegmentType.Preview, media.Preview, duration, itemId));
        }
        return result;
    }

    private static IEnumerable<MediaSegmentDto> ConvertSegments(MediaSegmentType type, List<TheIntroDbSegment>? segments, TimeSpan duration, Guid itemId) {
        var fromStart = type is MediaSegmentType.Intro or MediaSegmentType.Recap;
        foreach (var segment in segments ?? []) {
            if (segment is null || segment.StartMs is < 0 || segment.EndMs is < 0)
                continue;

            var start = TimeSpan.FromMilliseconds(segment.StartMs ?? 0);
            if (fromStart) {
                // An intro or recap needs an end; it may start with the file.
                if (segment.EndMs is null)
                    continue;
            }
            // A credits or preview segment starting at zero is TheIntroDB's way
            // of saying there is none.
            else if (segment.StartMs is null or 0) {
                continue;
            }

            // A segment without an end runs to the end of the file, which needs
            // a known duration.
            if (segment.EndMs is null && duration <= TimeSpan.Zero)
                continue;
            var end = segment.EndMs is { } endMs ? TimeSpan.FromMilliseconds(endMs) : duration;

            // A segment that runs to the end of a slightly longer release still
            // ends with this file.
            if (!fromStart && duration > TimeSpan.Zero && start < duration && end > duration && end - duration <= ReleaseTolerance)
                end = duration;
            if (end <= start || (duration > TimeSpan.Zero && (start >= duration || end > duration)))
                continue;

            yield return new() {
                Id = Guid.NewGuid(),
                ItemId = itemId,
                Type = type,
                StartTicks = start.Ticks,
                EndTicks = end.Ticks,
            };
        }
    }
}
