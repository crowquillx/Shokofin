using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Extensions.Logging;
using Shokofin.ExternalIds;
using Shokofin.Extensions;
using Shokofin.Resolvers.Models;

using File = System.IO.File;

namespace Shokofin.Resolvers;

// Fork: theme songs in the VFS.
//
// Theme songs that something other than the plugin puts in a VFS show or
// season folder, such as the `theme.mp3` a theme song plugin downloads, are
// kept when the VFS is cleaned up. When theme song syncing is enabled, theme
// songs are also shared with the media folders: those in a media folder are
// linked into the matching VFS folder, and those written into the VFS are
// copied to the media folder and replaced with a link to the copy.
public partial class VirtualFileSystemService {
    /// <summary>
    /// The folder names a theme song may be placed in, in either a media
    /// folder or the VFS.
    /// </summary>
    private static readonly HashSet<string> ThemeFolderNames = new(StringComparer.OrdinalIgnoreCase) {
        "theme-music",
        "theme-songs",
        "themes",
        "theme songs",
        "theme music",
    };

    /// <summary>
    /// The theme folder Jellyfin reads, and the one new theme folders get.
    /// </summary>
    private const string ThemeMusicFolderName = "theme-music";

    [GeneratedRegex(@"^theme\d*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ThemeFileNameRegex();

    [GeneratedRegex(@"^Season \d+$", RegexOptions.CultureInvariant)]
    private static partial Regex VfsSeasonFolderRegex();

    [GeneratedRegex(@"^(?:season[ ._-]*\d{1,4}|specials?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MediaSeasonFolderRegex();

    private bool IsAudioFile(string path) {
        var extName = Path.GetExtension(path);
        return NamingOptions.AudioFileExtensions.Any(ext => string.Equals(ext, extName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Checks if the name, without the extension, is a theme song's, such as
    /// <c>theme</c> or <c>theme2</c>.
    /// </summary>
    internal static bool IsThemeFileName(string path)
        => ThemeFileNameRegex().IsMatch(Path.GetFileNameWithoutExtension(path));

    /// <summary>
    /// Finds the VFS show or season folder a theme song in the VFS belongs to.
    /// Only the layouts Jellyfin and theme song plugins use are recognised:
    /// <c>Show/theme.mp3</c>, <c>Show/theme-music/x.mp3</c>,
    /// <c>Show/Season 01/theme.mp3</c> and
    /// <c>Show/Season 01/theme-music/x.mp3</c>. The caller checks that the
    /// file is an audio file.
    /// </summary>
    internal static bool TryGetThemeOwnerDirectory(string vfsPath, string path, [NotNullWhen(true)] out string? ownerDirectory) {
        ownerDirectory = null;
        if (!path.StartsWith(vfsPath + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return false;

        var segments = path[(vfsPath.Length + 1)..].Split(Path.DirectorySeparatorChar);
        var isNamedTheme = IsThemeFileName(segments[^1]);
        switch (segments.Length) {
            case 2 when isNamedTheme:
                ownerDirectory = Path.Join(vfsPath, segments[0]);
                return true;
            case 3 when ThemeFolderNames.Contains(segments[1]):
                ownerDirectory = Path.Join(vfsPath, segments[0]);
                return true;
            case 3 when isNamedTheme && VfsSeasonFolderRegex().IsMatch(segments[1]):
                ownerDirectory = Path.Join(vfsPath, segments[0], segments[1]);
                return true;
            case 4 when VfsSeasonFolderRegex().IsMatch(segments[1]) && ThemeFolderNames.Contains(segments[2]):
                ownerDirectory = Path.Join(vfsPath, segments[0], segments[1]);
                return true;
            default:
                return false;
        }
    }

    #region Cleanup

    /// <summary>
    /// What the cleanup needs to know to decide what to do with theme songs.
    /// </summary>
    private sealed class ThemeCleanupContext {
        public required HashSet<string> KnownDirectories { get; init; }

        /// <summary>
        /// Known top-level VFS folders by their Shoko IDs, so a theme song can
        /// follow its show to a folder with a new name.
        /// </summary>
        public required Dictionary<string, string> TopDirectoriesByIds { get; init; }

        public required IReadOnlyList<string> MediaFolderPaths { get; init; }
    }

    private static ThemeCleanupContext CreateThemeCleanupContext(string vfsPath, IReadOnlyList<string> allKnownPaths, IReadOnlyList<string>? mediaFolderPaths) {
        var knownDirectories = AddParentDirectories(vfsPath, allKnownPaths);
        var topDirectoriesByIds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var directory in knownDirectories) {
            if (!string.Equals(Path.GetDirectoryName(directory), vfsPath, StringComparison.Ordinal))
                continue;
            if (GetTopDirectoryIds(Path.GetFileName(directory)) is { } ids)
                topDirectoriesByIds.TryAdd(ids, Path.GetFileName(directory));
        }

        return new() {
            KnownDirectories = knownDirectories,
            TopDirectoriesByIds = topDirectoriesByIds,
            MediaFolderPaths = mediaFolderPaths ?? [],
        };
    }

    private static string? GetTopDirectoryIds(string name) {
        if (!name.TryGetAttributeValue(ProviderNames.ShokoSeries, out var seriesId))
            return null;
        return name.TryGetAttributeValue(ProviderNames.ShokoEpisode, out var episodeId) ? $"{seriesId}:{episodeId}" : seriesId;
    }

    /// <summary>
    /// Decides what to do with a theme song the cleanup found in the VFS.
    /// </summary>
    /// <returns>
    /// True when the theme song was kept or moved, false when it should be
    /// removed.
    /// </returns>
    private bool TryKeepThemeSongDuringCleanup(ThemeCleanupContext context, string vfsPath, string location, string ownerDirectory, LinkGenerationResult result, bool preview) {
        // A link to a theme song that is gone from its media folder is removed,
        // but only while the media folder can be read, so an unreachable
        // network share does not empty the VFS of its theme songs.
        string? linkTarget = null;
        try {
            linkTarget = File.ResolveLinkTarget(location, false)?.FullName;
        }
        catch (Exception ex) {
            Logger.LogDebug(ex, "Unable to check if theme song {Path} is a link.", location);
        }
        if (linkTarget is not null && !File.Exists(linkTarget)) {
            if (IsMediaLocationReachable(linkTarget, context.MediaFolderPaths))
                return false;

            result.Paths.Add(location);
            result.SkippedExternalFiles++;
            return true;
        }

        if (context.KnownDirectories.Contains(ownerDirectory)) {
            result.Paths.Add(location);
            result.SkippedExternalFiles++;
            return true;
        }

        // The show or season is gone. If the show is still there under a new
        // name, then move the theme song along with it.
        var segments = location[(vfsPath.Length + 1)..].Split(Path.DirectorySeparatorChar);
        if (
            GetTopDirectoryIds(segments[0]) is not { } ids ||
            !context.TopDirectoriesByIds.TryGetValue(ids, out var newTopDirectory) ||
            string.Equals(newTopDirectory, segments[0], StringComparison.Ordinal)
        )
            return false;

        var ownerSegments = ownerDirectory[(vfsPath.Length + 1)..].Split(Path.DirectorySeparatorChar);
        var newOwnerDirectory = Path.Join(new[] { vfsPath, newTopDirectory }.Concat(ownerSegments[1..]).ToArray());
        var newLocation = Path.Join(new[] { vfsPath, newTopDirectory }.Concat(segments[1..]).ToArray());
        if (!context.KnownDirectories.Contains(newOwnerDirectory) || File.Exists(newLocation))
            return false;

        if (!preview) {
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(newLocation)!);
                File.Move(location, newLocation);
                Logger.LogDebug("Moved theme song {Path} to {NewPath} to follow its show.", location, newLocation);
            }
            catch (Exception ex) {
                // Keep it where it is rather than lose it.
                Logger.LogWarning(ex, "Unable to move theme song {Path} to {NewPath}.", location, newLocation);
                result.Paths.Add(location);
                result.SkippedExternalFiles++;
                return true;
            }
        }

        result.Paths.Add(newLocation);
        result.FixedExternalFiles++;
        return true;
    }

    /// <summary>
    /// Checks if the media folder a path is in can be read. A path outside
    /// every media folder counts as reachable.
    /// </summary>
    private bool IsMediaLocationReachable(string path, IReadOnlyList<string> mediaFolderPaths) {
        foreach (var mediaFolderPath in mediaFolderPaths) {
            if (!path.StartsWith(mediaFolderPath + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                continue;

            try {
                // An unmounted share often leaves an empty mount point behind.
                return Directory.Exists(mediaFolderPath) && ContainsFileSystemEntryPaths(mediaFolderPath);
            }
            catch {
                return false;
            }
        }

        return true;
    }

    #endregion

    #region Sync

    /// <summary>
    /// A VFS show or season folder and the media folder it shares theme songs
    /// with.
    /// </summary>
    internal readonly record struct ThemePair(string VfsDirectory, string SourceDirectory);

    /// <summary>
    /// Finds the media folders that clearly belong to a VFS show or season.
    /// </summary>
    /// <remarks>
    /// A folder qualifies only when every Shoko file under it belongs to the
    /// same show (or, for a season, the same series), and never when it is a
    /// media folder root, so a shared download folder or a flat library is
    /// never touched.
    /// <list type="bullet">
    /// <item>A show's folder is the deepest folder holding all of the show's
    /// files. When those are all in one folder named like a season, the
    /// show's folder is its parent.</item>
    /// <item>A season's folder is the one folder holding all of its files,
    /// when only that season's series has files there and it is not also the
    /// show's folder.</item>
    /// </list>
    /// Only the episodes in the VFS "Season N" folders are used, so specials
    /// and extras do not move a show's folder.
    /// </remarks>
    internal static IReadOnlyList<ThemePair> FindThemePairs(string vfsPath, IReadOnlyList<string> mediaFolderPaths, IEnumerable<(string sourceLocation, string[] symbolicLinks)> generatedLinks) {
        var shows = new Dictionary<string, ShowSources>(StringComparer.Ordinal);
        var directorySeries = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var treeShows = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (sourceLocation, symbolicLinks) in generatedLinks) {
            var root = mediaFolderPaths
                .Where(mediaFolderPath => sourceLocation.StartsWith(mediaFolderPath + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                .MaxBy(mediaFolderPath => mediaFolderPath.Length);
            if (root is null)
                continue;

            var directory = Path.GetDirectoryName(sourceLocation)!;
            foreach (var symbolicLink in symbolicLinks) {
                if (!symbolicLink.StartsWith(vfsPath + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    continue;

                var segments = symbolicLink[(vfsPath.Length + 1)..].Split(Path.DirectorySeparatorChar);
                if (segments.Length != 3 || !VfsSeasonFolderRegex().IsMatch(segments[1]) || !TryGetIdsForPath(symbolicLink, out _, out var seriesId))
                    continue;

                var showKey = segments[0];
                if (!shows.TryGetValue(showKey, out var show)) {
                    show = new() { Root = root };
                    shows[showKey] = show;
                }
                if (!string.Equals(show.Root, root, StringComparison.Ordinal))
                    show.Root = null; // Spread over several media folders, so there is no common folder.
                show.Directories.Add(directory);

                var seasonNumber = int.Parse(segments[1]["Season ".Length..]);
                if (seasonNumber > 0) {
                    var seasonKey = Path.Join(segments[0], segments[1]);
                    if (!show.Seasons.TryGetValue(seasonKey, out var season)) {
                        season = new();
                        show.Seasons[seasonKey] = season;
                    }
                    season.Directories.Add(directory);
                    season.SeriesIds.Add(seriesId);
                }

                if (!directorySeries.TryGetValue(directory, out var seriesIds))
                    directorySeries[directory] = seriesIds = new(StringComparer.Ordinal);
                seriesIds.Add(seriesId);

                for (var ancestor = directory; IsStrictlyUnder(ancestor, root); ancestor = Path.GetDirectoryName(ancestor)!) {
                    if (!treeShows.TryGetValue(ancestor, out var owners))
                        treeShows[ancestor] = owners = new(StringComparer.Ordinal);
                    owners.Add(showKey);
                }
            }
        }

        bool OwnedBy(string? directory, string showKey, string? root)
            => directory is not null &&
                root is not null &&
                IsStrictlyUnder(directory, root) &&
                treeShows.TryGetValue(directory, out var owners) &&
                owners.Count == 1 &&
                owners.Contains(showKey);

        var pairs = new List<ThemePair>();
        foreach (var (showKey, show) in shows) {
            var showDirectory = GetCommonDirectory(show.Directories);
            if (!OwnedBy(showDirectory, showKey, show.Root))
                showDirectory = null;
            if (
                showDirectory is not null &&
                show.Directories.Count == 1 &&
                MediaSeasonFolderRegex().IsMatch(Path.GetFileName(showDirectory)) &&
                OwnedBy(Path.GetDirectoryName(showDirectory), showKey, show.Root)
            )
                showDirectory = Path.GetDirectoryName(showDirectory);
            if (showDirectory is not null)
                pairs.Add(new(Path.Join(vfsPath, showKey), showDirectory));

            foreach (var (seasonKey, season) in show.Seasons) {
                if (season.Directories.Count != 1 || season.SeriesIds.Count != 1)
                    continue;

                var directory = season.Directories.First();
                var seriesId = season.SeriesIds.First();
                if (
                    !directorySeries.TryGetValue(directory, out var seriesIds) ||
                    seriesIds.Count != 1 ||
                    !seriesIds.Contains(seriesId) ||
                    string.Equals(directory, showDirectory, StringComparison.Ordinal) ||
                    !OwnedBy(directory, showKey, show.Root)
                )
                    continue;

                pairs.Add(new(Path.Join(vfsPath, seasonKey), directory));
            }
        }

        return pairs
            .OrderBy(pair => pair.VfsDirectory, StringComparer.Ordinal)
            .ToList();
    }

    private sealed class ShowSources {
        public string? Root { get; set; }

        public HashSet<string> Directories { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, SeasonSources> Seasons { get; } = new(StringComparer.Ordinal);
    }

    private sealed class SeasonSources {
        public HashSet<string> Directories { get; } = new(StringComparer.Ordinal);

        public HashSet<string> SeriesIds { get; } = new(StringComparer.Ordinal);
    }

    private static string? GetCommonDirectory(IEnumerable<string> directories) {
        string? common = null;
        foreach (var directory in directories) {
            if (common is null) {
                common = directory;
                continue;
            }
            while (!string.Equals(common, directory, StringComparison.Ordinal) && !IsStrictlyUnder(directory, common)) {
                var parent = Path.GetDirectoryName(common);
                if (string.IsNullOrEmpty(parent))
                    return null;
                common = parent;
            }
        }
        return common;
    }

    private static bool IsStrictlyUnder(string path, string root)
        => path.Length > root.Length + 1 &&
            path.StartsWith(root, StringComparison.Ordinal) &&
            (root.EndsWith(Path.DirectorySeparatorChar) || path[root.Length] == Path.DirectorySeparatorChar);

    /// <summary>
    /// Shares theme songs between the VFS and the media folders that clearly
    /// belong to a VFS show or season.
    /// </summary>
    private LinkGenerationResult SyncThemeSongs(string vfsPath, IReadOnlyList<string> mediaFolderPaths, IEnumerable<(string sourceLocation, string[] symbolicLinks)> generatedLinks, CancellationToken cancellationToken) {
        var start = DateTime.UtcNow;
        var result = new LinkGenerationResult();
        var pairs = FindThemePairs(vfsPath, mediaFolderPaths, generatedLinks);
        Logger.LogDebug("Found {Count} media folders to share theme songs with in {TimeSpent}. (VFS={Path})", pairs.Count, DateTime.UtcNow - start, vfsPath);

        var lockObject = new object();
        Parallelize(pairs, pair => {
            var subResult = new LinkGenerationResult();
            try {
                SyncThemeSongsForPair(pair, subResult);
            }
            catch (Exception ex) {
                Logger.LogWarning(ex, "Unable to share theme songs between {VfsPath} and {SourcePath}.", pair.VfsDirectory, pair.SourceDirectory);
            }
            lock (lockObject) {
                result += subResult;
            }
        }, cancellationToken).Wait(cancellationToken);

        Logger.LogDebug(
            "Shared theme songs with {Count} media folders in {TimeSpent}; linked {Linked} and copied {Copied}. (VFS={Path})",
            pairs.Count,
            DateTime.UtcNow - start,
            result.CreatedExternalFiles,
            result.FixedExternalFiles,
            vfsPath
        );
        return result;
    }

    private void SyncThemeSongsForPair(ThemePair pair, LinkGenerationResult result) {
        var (vfsDirectory, sourceDirectory) = pair;
        if (!Directory.Exists(vfsDirectory) || !Directory.Exists(sourceDirectory))
            return;

        var sourceThemes = ListThemeSongs(sourceDirectory);
        var linkedTargets = new HashSet<string>(StringComparer.Ordinal);
        var writtenToVfs = new List<string>();
        foreach (var vfsTheme in ListThemeSongs(vfsDirectory)) {
            // Theme songs Shoko knows about are linked like any other file.
            if (TryGetIdsForPath(vfsTheme, out _, out _))
                continue;

            var info = new FileInfo(vfsTheme);
            if (info.LinkTarget is not null) {
                if (info.ResolveLinkTarget(false)?.FullName is { } target)
                    linkedTargets.Add(target);
            }
            else {
                writtenToVfs.Add(vfsTheme);
            }
        }

        // Copy theme songs written into the VFS to the media folder, then link
        // to the copy, so the VFS follows the media folder from then on.
        foreach (var vfsTheme in writtenToVfs) {
            var destinationDirectory = FindThemeFolder(sourceDirectory) ?? Path.Join(sourceDirectory, ThemeMusicFolderName);
            var destination = Path.Join(destinationDirectory, Path.GetFileName(vfsTheme));
            if (File.Exists(destination) || Directory.Exists(destination)) {
                if (!FilesAreEqual(vfsTheme, destination)) {
                    Logger.LogWarning("Not copying theme song {Path} to {Destination}, since another file already has that name.", vfsTheme, destination);
                    continue;
                }
            }
            else {
                try {
                    Directory.CreateDirectory(destinationDirectory);
                    File.Copy(vfsTheme, destination, overwrite: false);
                    Logger.LogInformation("Copied theme song {Path} to {Destination}.", vfsTheme, destination);
                }
                catch (Exception ex) {
                    Logger.LogWarning(ex, "Unable to copy theme song {Path} to {Destination}.", vfsTheme, destination);
                    continue;
                }
            }

            try {
                File.Delete(vfsTheme);
                File.CreateSymbolicLink(vfsTheme, destination);
            }
            catch (Exception ex) {
                Logger.LogWarning(ex, "Unable to replace theme song {Path} with a link to {Destination}.", vfsTheme, destination);
            }
            linkedTargets.Add(destination);
            result.Paths.Add(vfsTheme);
            result.FixedExternalFiles++;
        }

        // Link the media folder's theme songs into the VFS.
        foreach (var sourceTheme in sourceThemes) {
            if (linkedTargets.Contains(sourceTheme))
                continue;

            var link = GetVfsThemeSongPath(vfsDirectory, sourceDirectory, sourceTheme);
            var info = new FileInfo(link);
            if (info.LinkTarget is not null) {
                var target = info.ResolveLinkTarget(false)?.FullName;
                if (string.Equals(target, sourceTheme, StringComparison.Ordinal)) {
                    result.Paths.Add(link);
                    result.SkippedExternalFiles++;
                    continue;
                }
                // Replace links to theme songs that are gone.
                if (target is null || !File.Exists(target)) {
                    try {
                        File.Delete(link);
                    }
                    catch (Exception ex) {
                        Logger.LogWarning(ex, "Unable to remove the broken theme song link {Path}.", link);
                        continue;
                    }
                }
                else {
                    Logger.LogInformation("Not linking theme song {Path} to {Link}, since another theme song already has that name.", sourceTheme, link);
                    continue;
                }
            }
            else if (info.Exists || Directory.Exists(link)) {
                Logger.LogInformation("Not linking theme song {Path} to {Link}, since another file already has that name.", sourceTheme, link);
                continue;
            }

            try {
                Directory.CreateDirectory(Path.GetDirectoryName(link)!);
                File.CreateSymbolicLink(link, sourceTheme);
                Logger.LogDebug("Linking {Link} → {LinkTarget}", link, sourceTheme);
            }
            catch (Exception ex) {
                Logger.LogWarning(ex, "Unable to link theme song {Path} to {Link}.", sourceTheme, link);
                continue;
            }
            result.Paths.Add(link);
            result.CreatedExternalFiles++;
        }
    }

    /// <summary>
    /// Lists the theme songs in a show or season folder: <c>theme.mp3</c>,
    /// numbered ones such as <c>theme2.mp3</c>, and the audio files in its
    /// theme folders.
    /// </summary>
    private List<string> ListThemeSongs(string directory) {
        var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = 0 };
        var themes = new List<string>();
        try {
            themes.AddRange(Directory.EnumerateFiles(directory, "*", options).Where(path => IsThemeFileName(path) && IsAudioFile(path)));
            foreach (var subDirectory in Directory.EnumerateDirectories(directory, "*", options)) {
                if (!ThemeFolderNames.Contains(Path.GetFileName(subDirectory)))
                    continue;
                themes.AddRange(Directory.EnumerateFiles(subDirectory, "*", options).Where(IsAudioFile));
            }
        }
        catch (Exception ex) {
            Logger.LogDebug(ex, "Unable to list the theme songs in {Path}.", directory);
        }
        return themes;
    }

    /// <summary>
    /// Finds the theme folder in a media folder, preferring the one Jellyfin
    /// reads.
    /// </summary>
    private static string? FindThemeFolder(string directory) {
        try {
            var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true };
            return Directory.EnumerateDirectories(directory, "*", options)
                .Where(path => ThemeFolderNames.Contains(Path.GetFileName(path)))
                .OrderBy(path => string.Equals(Path.GetFileName(path), ThemeMusicFolderName, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(path => path, StringComparer.Ordinal)
                .FirstOrDefault();
        }
        catch {
            return null;
        }
    }

    /// <summary>
    /// Where a media folder's theme song goes in the VFS. Jellyfin only reads
    /// <c>theme.*</c> next to a show or season and the files in its
    /// <c>theme-music</c> folder, so everything but <c>theme.*</c> goes in
    /// <c>theme-music</c>.
    /// </summary>
    internal static string GetVfsThemeSongPath(string vfsDirectory, string sourceDirectory, string sourceTheme) {
        var name = Path.GetFileName(sourceTheme);
        var isTopLevel = string.Equals(Path.GetDirectoryName(sourceTheme), sourceDirectory, StringComparison.Ordinal);
        return isTopLevel && string.Equals(Path.GetFileNameWithoutExtension(name), "theme", StringComparison.OrdinalIgnoreCase)
            ? Path.Join(vfsDirectory, name)
            : Path.Join(vfsDirectory, ThemeMusicFolderName, name);
    }

    private static bool FilesAreEqual(string pathA, string pathB) {
        try {
            var infoA = new FileInfo(pathA);
            var infoB = new FileInfo(pathB);
            if (!infoA.Exists || !infoB.Exists || infoA.Length != infoB.Length)
                return false;

            using var streamA = infoA.OpenRead();
            using var streamB = infoB.OpenRead();
            var bufferA = new byte[81920];
            var bufferB = new byte[81920];
            while (true) {
                var readA = streamA.ReadAtLeast(bufferA, bufferA.Length, throwOnEndOfStream: false);
                var readB = streamB.ReadAtLeast(bufferB, bufferB.Length, throwOnEndOfStream: false);
                if (readA != readB || !bufferA.AsSpan(0, readA).SequenceEqual(bufferB.AsSpan(0, readB)))
                    return false;
                if (readA == 0)
                    return true;
            }
        }
        catch {
            return false;
        }
    }

    #endregion
}
