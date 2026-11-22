using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using IDirectoryService = MediaBrowser.Controller.Providers.IDirectoryService;
using IProviderManager = MediaBrowser.Controller.Providers.IProviderManager;
using MetadataRefreshMode = MediaBrowser.Controller.Providers.MetadataRefreshMode;
using MetadataRefreshOptions = MediaBrowser.Controller.Providers.MetadataRefreshOptions;
using RefreshPriority = MediaBrowser.Controller.Providers.RefreshPriority;
using Microsoft.Extensions.Logging;
using Shokofin.API;
using Shokofin.API.Info;
using Shokofin.Configuration;
using Shokofin.Events.Interfaces;
using Shokofin.Events.Stub;
using Shokofin.Extensions;
using Shokofin.ExternalIds;
using Shokofin.Resolvers;
using Shokofin.Resolvers.Models;
using Shokofin.SignalR.Models;
using Shokofin.Utils;

using File = System.IO.File;
using Movie = MediaBrowser.Controller.Entities.Movies.Movie;
using Timer = System.Timers.Timer;
using TvEpisode = MediaBrowser.Controller.Entities.TV.Episode;
using TvSeason = MediaBrowser.Controller.Entities.TV.Season;
using TvSeries = MediaBrowser.Controller.Entities.TV.Series;

namespace Shokofin.Events;

public class EventDispatchService {
    private readonly ShokoApiManager ApiManager;

    private readonly ShokoApiClient ApiClient;

    private readonly ILibraryManager LibraryManager;

    private readonly ILibraryMonitor LibraryMonitor;

    private readonly LibraryScanWatcher LibraryScanWatcher;

    private readonly MetadataRefreshService MetadataRefreshService;

    private readonly MediaFolderConfigurationService ConfigurationService;

    private readonly VirtualFileSystemService ResolveManager;

    private readonly ILogger<EventDispatchService> Logger;

    private readonly UsageTracker UsageTracker;

    private readonly IProviderManager ProviderManager;

    private readonly IDirectoryService DirectoryService;

    private int ChangesDetectionSubmitterCount = 0;

    private readonly Timer ChangesDetectionTimer;

    private readonly Dictionary<string, (DateTime LastUpdated, List<IMetadataUpdatedEventArgs> List, Guid trackerId)> ChangesPerSeries = [];

    private readonly Dictionary<int, (DateTime LastUpdated, List<(UpdateReason Reason, int ManagedFolderId, string Path, IFileEventArgs Event)> List, Guid trackerId)> ChangesPerFile = [];

    private readonly Dictionary<int, (DateTime InsertedAt, DateTime LastChecked, List<(UpdateReason Reason, int ManagedFolderId, string Path, IFileEventArgs Event)> List, Guid trackerId)> UnresolvedFiles = [];

    private readonly ConcurrentDictionary<Guid, bool> RecentlyUpdatedEntitiesDict = new();

    private static readonly TimeSpan DetectChangesThreshold = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan UnresolvedFileRecheckInterval = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan UnresolvedFileMaxWait = TimeSpan.FromHours(1);

    public EventDispatchService(
        ShokoApiManager apiManager,
        ShokoApiClient apiClient,
        ILibraryManager libraryManager,
        ILibraryMonitor libraryMonitor,
        LibraryScanWatcher libraryScanWatcher,
        MetadataRefreshService metadataRefreshService,
        MediaFolderConfigurationService configurationService,
        VirtualFileSystemService resolveManager,
        ILogger<EventDispatchService> logger,
        UsageTracker usageTracker,
        IProviderManager providerManager,
        IDirectoryService directoryService
    ) {
        ApiManager = apiManager;
        ApiClient = apiClient;
        LibraryManager = libraryManager;
        LibraryMonitor = libraryMonitor;
        LibraryScanWatcher = libraryScanWatcher;
        MetadataRefreshService = metadataRefreshService;
        ConfigurationService = configurationService;
        ResolveManager = resolveManager;
        Logger = logger;
        UsageTracker = usageTracker;
        ProviderManager = providerManager;
        DirectoryService = directoryService;
        UsageTracker.Stalled += OnStalled;
        ChangesDetectionTimer = new() { AutoReset = true, Interval = TimeSpan.FromSeconds(4).TotalMilliseconds };
        ChangesDetectionTimer.Elapsed += OnIntervalElapsed;
    }

    ~EventDispatchService() {
        UsageTracker.Stalled -= OnStalled;
        ChangesDetectionTimer.Elapsed -= OnIntervalElapsed;
    }

    private void OnStalled(object? sender, EventArgs eventArgs) {
        Clear();
    }

    public void Clear() => RecentlyUpdatedEntitiesDict.Clear();

    #region Event Detection

    public IDisposable RegisterEventSubmitter() {
        var count = ChangesDetectionSubmitterCount++;
        if (count is 0)
            ChangesDetectionTimer.Start();

        return new DisposableAction(() => DeregisterEventSubmitter());
    }

    private void DeregisterEventSubmitter() {
        var count = --ChangesDetectionSubmitterCount;
        if (count is 0) {
            ChangesDetectionTimer.Stop();
            if (ChangesPerFile.Count > 0 || UnresolvedFiles.Count > 0)
                ClearFileEvents();
            if (ChangesPerSeries.Count > 0)
                ClearMetadataUpdatedEvents();
            if (TakeStructureChanges(force: true) is { } structureChanges)
                Task.Run(() => ProcessStructureEvents(structureChanges));
        }
    }

    private void OnIntervalElapsed(object? sender, ElapsedEventArgs eventArgs) {
        var filesToProcess = new List<(int, List<(UpdateReason Reason, int ManagedFolderId, string Path, IFileEventArgs Event)>, Guid trackerId)>();
        var unresolvedFilesToProcess = new List<(int, List<(UpdateReason Reason, int ManagedFolderId, string Path, IFileEventArgs Event)>, Guid trackerId, DateTime insertedAt)>();
        var seriesToProcess = new List<(string, List<IMetadataUpdatedEventArgs>, Guid trackerId)>();
        lock (ChangesPerFile) {
            if (ChangesPerFile.Count > 0) {
                var now = DateTime.Now;
                foreach (var (fileId, (lastUpdated, list, trackerId)) in ChangesPerFile) {
                    if (now - lastUpdated < DetectChangesThreshold)
                        continue;
                    filesToProcess.Add((fileId, list, trackerId));
                }
                foreach (var (fileId, _, _) in filesToProcess)
                    ChangesPerFile.Remove(fileId);
            }
        }
        lock (UnresolvedFiles) {
            if (UnresolvedFiles.Count > 0) {
                var now = DateTime.Now;
                foreach (var (fileId, (insertedAt, lastChecked, list, trackerId)) in UnresolvedFiles) {
                    if (now - lastChecked < UnresolvedFileRecheckInterval)
                        continue;
                    unresolvedFilesToProcess.Add((fileId, list, trackerId, insertedAt));
                }
                foreach (var (fileId, _, _, _) in unresolvedFilesToProcess)
                    UnresolvedFiles.Remove(fileId);
            }
        }
        lock (ChangesPerSeries) {
            if (ChangesPerSeries.Count > 0) {
                var now = DateTime.Now;
                foreach (var (metadataId, (lastUpdated, list, trackerId)) in ChangesPerSeries) {
                    if (now - lastUpdated < DetectChangesThreshold)
                        continue;
                    seriesToProcess.Add((metadataId, list, trackerId));
                }
                foreach (var (metadataId, _, _) in seriesToProcess)
                    ChangesPerSeries.Remove(metadataId);
            }
        }
        if (TakeStructureChanges() is { } structureChanges)
            Task.Run(() => ProcessStructureEvents(structureChanges));
        foreach (var (fileId, changes, trackerId) in filesToProcess) {
            var (allChanges, insertedAt) = TakeUnresolvedFile(fileId, changes);
            Task.Run(() => ProcessFileEvents(fileId, allChanges, trackerId, insertedAt));
        }
        foreach (var (fileId, changes, trackerId, insertedAt) in unresolvedFilesToProcess)
            Task.Run(() => ProcessFileEvents(fileId, changes, trackerId, insertedAt));
        foreach (var (metadataId, changes, trackerId) in seriesToProcess)
            Task.Run(() => ProcessMetadataEvents(metadataId, changes, trackerId));
    }

    private void ClearFileEvents() {
        var filesToProcess = new List<(int, List<(UpdateReason Reason, int ManagedFolderId, string Path, IFileEventArgs Event)>, Guid trackerId)>();
        lock (ChangesPerFile) {
            foreach (var (fileId, (lastUpdated, list, trackerId)) in ChangesPerFile) {
                filesToProcess.Add((fileId, list, trackerId));
            }
            ChangesPerFile.Clear();
        }
        lock (UnresolvedFiles) {
            foreach (var (fileId, (_, _, list, trackerId)) in UnresolvedFiles) {
                if (filesToProcess.FindIndex(tuple => tuple.Item1 == fileId) is var index && index is not -1) {
                    Plugin.Instance.Tracker.Remove(trackerId);
                    filesToProcess[index] = (fileId, [.. list, .. filesToProcess[index].Item2], filesToProcess[index].trackerId);
                }
                else {
                    filesToProcess.Add((fileId, list, trackerId));
                }
            }
            UnresolvedFiles.Clear();
        }
        foreach (var (fileId, changes, trackerId) in filesToProcess)
            Task.Run(() => ProcessFileEvents(fileId, changes, trackerId, skipUnresolvedCheck: true));
    }

    private (List<(UpdateReason Reason, int ManagedFolderId, string Path, IFileEventArgs Event)> Changes, DateTime? InsertedAt) TakeUnresolvedFile(int fileId, List<(UpdateReason Reason, int ManagedFolderId, string Path, IFileEventArgs Event)> changes) {
        lock (UnresolvedFiles) {
            if (!UnresolvedFiles.Remove(fileId, out var tuple))
                return (changes, null);
            Plugin.Instance.Tracker.Remove(tuple.trackerId);
            return ([.. tuple.List, .. changes], tuple.InsertedAt);
        }
    }

    private void AddUnresolvedFile(int fileId, List<(UpdateReason Reason, int ManagedFolderId, string Path, IFileEventArgs Event)> changes, Guid trackerId, DateTime insertedAt) {
        lock (UnresolvedFiles) {
            if (UnresolvedFiles.Remove(fileId, out var tuple)) {
                Plugin.Instance.Tracker.Remove(tuple.trackerId);
                changes = [.. tuple.List, .. changes];
                if (tuple.InsertedAt < insertedAt)
                    insertedAt = tuple.InsertedAt;
            }
            UnresolvedFiles.Add(fileId, (insertedAt, DateTime.Now, changes, trackerId));
        }
    }

    private void ClearMetadataUpdatedEvents() {
        var seriesToProcess = new List<(string, List<IMetadataUpdatedEventArgs>, Guid trackerId)>();
        lock (ChangesPerSeries) {
            foreach (var (metadataId, (lastUpdated, list, trackerId)) in ChangesPerSeries) {
                seriesToProcess.Add((metadataId, list, trackerId));
            }
            ChangesPerSeries.Clear();
        }
        foreach (var (metadataId, changes, trackerId) in seriesToProcess)
            Task.Run(() => ProcessMetadataEvents(metadataId, changes, trackerId));
    }

    #endregion

    #region File Events

    public void AddFileEvent(int fileId, UpdateReason reason, int managedFolderId, string filePath, IFileEventArgs eventArgs) {
        lock (ChangesPerFile) {
            if (ChangesPerFile.TryGetValue(fileId, out var tuple))
                tuple.LastUpdated = DateTime.Now;
            else
                ChangesPerFile.Add(fileId, tuple = (DateTime.Now, [], Plugin.Instance.Tracker.Add($"File event. (Reason=\"{reason}\",ManagedFolder={eventArgs.ManagedFolderId},RelativePath=\"{eventArgs.RelativePath}\")")));
            tuple.List.Add((reason, managedFolderId, filePath, eventArgs));
        }
    }

    private async Task ProcessFileEvents(int fileId, List<(UpdateReason Reason, int ManagedFolderId, string Path, IFileEventArgs Event)> changes, Guid trackerId, DateTime? insertedAt = null, bool skipUnresolvedCheck = false) {
        var keepTracker = false;
        try {
            if (LibraryScanWatcher.IsScanRunning) {
                Logger.LogInformation("Skipped processing {EventCount} file change events because a library scan is running. (File={FileId})", changes.Count, fileId);
                return;
            }

            // Hold back the file until Shoko has created the episodes and
            // series it's linked to, or until we've waited long enough.
            if (!skipUnresolvedCheck && changes.Last().Reason is not UpdateReason.MetadataRemoved && await HasUnresolvedCrossReferences(fileId, insertedAt.HasValue ? null : changes.Select(t => t.Event).LastOrDefault(e => e.HasCrossReferences))) {
                var firstInsertedAt = insertedAt ?? DateTime.Now;
                var waited = DateTime.Now - firstInsertedAt;
                if (waited < UnresolvedFileMaxWait) {
                    Logger.LogDebug("Holding back {EventCount} file change events until the linked episodes and series are available. (File={FileId},Waited={Waited})", changes.Count, fileId, waited);
                    AddUnresolvedFile(fileId, changes, trackerId, firstInsertedAt);
                    keepTracker = true;
                    return;
                }

                Logger.LogWarning("Processing {EventCount} file change events with unresolved cross-references after waiting {Waited}. (File={FileId})", changes.Count, waited, fileId);
            }

            Logger.LogInformation("Processing {EventCount} file change events… (File={FileId})", changes.Count, fileId);

            // Something was added or updated.
            var locationsToNotify = new List<string>();
            var seriesIds = await GetSeriesIdsForFile(fileId, changes.Select(t => t.Event).LastOrDefault(e => e.HasCrossReferences));
            var libraries = await ConfigurationService.GetAvailableMediaFoldersForLibraries(c => !c.IsIgnored && c.Library.IsFileEventsEnabled);
            var (reason, managedFolderId, relativePath, lastEvent) = changes.Last();
            if (reason is not UpdateReason.MetadataRemoved) {
                Logger.LogTrace("Processing file changed. (File={FileId})", fileId);
                foreach (var (vfsPath, collectionType, mediaConfigs) in libraries) {
                    foreach (var (managedFolderSubPath, vfsEnabled, mediaFolderPaths) in mediaConfigs.ToManagedFolderList(managedFolderId, relativePath)) {
                        foreach (var mediaFolderPath in mediaFolderPaths) {
                            var sourceLocation = Path.Join(mediaFolderPath, relativePath[managedFolderSubPath.Length..]);
                            if (!File.Exists(sourceLocation))
                                continue;

                            // Let the core logic handle the rest.
                            if (!vfsEnabled) {
                                locationsToNotify.Add(sourceLocation);
                                break;
                            }

                            var result = new LinkGenerationResult();
                            var topFolders = new HashSet<string>();
                            var vfsLocations = (await Task.WhenAll(seriesIds.Select(seriesId => ResolveManager.GenerateLocationsForFile(collectionType, vfsPath, sourceLocation, fileId.ToString(), seriesId))))
                                .Where(tuple => tuple.symbolicLinks.Length > 0 && tuple.importedAt.HasValue)
                                .ToList();
                            foreach (var (symLinks, importDate) in vfsLocations) {
                                result += ResolveManager.GenerateSymbolicLinks(vfsPath, sourceLocation, symLinks, importDate!.Value);
                                foreach (var path in symLinks.Select(path => Path.Join(vfsPath, path[(vfsPath.Length + 1)..].Split(Path.DirectorySeparatorChar).First())).Distinct())
                                    topFolders.Add(path);
                            }

                            // Remove old links for file.
                            var videos = LibraryManager
                                .GetItemList(
                                    new() {
                                        SourceTypes = [SourceType.Library],
                                        HasAnyProviderId = new Dictionary<string, string> { { ProviderNames.ShokoFile, fileId.ToString() } },
                                        DtoOptions = new(true),
                                    },
                                    true
                                );
                            Logger.LogTrace("Found {Count} potential videos to remove", videos.Count);
                            foreach (var video in videos) {
                                if (string.IsNullOrEmpty(video.Path) || !video.Path.StartsWith(vfsPath) || result.Paths.Contains(video.Path)) {
                                    Logger.LogTrace("Skipped a {Kind} to remove with path {Path}", video.GetBaseItemKind(), video.Path);
                                    continue;
                                }
                                Logger.LogTrace("Found a {Kind} to remove with path {Path}", video.GetBaseItemKind(), video.Path);
                                RemoveSymbolicLink(video.Path);
                                topFolders.Add(Path.Join(vfsPath, video.Path[(vfsPath.Length + 1)..].Split(Path.DirectorySeparatorChar).First()));
                                locationsToNotify.Add(video.Path);
                                result.RemovedVideos++;
                            }

                            result.Print(Logger, mediaFolderPath);

                            locationsToNotify.AddRange(vfsLocations.SelectMany(tuple => tuple.symbolicLinks));
                            break;
                        }
                    }
                }
            }
            // Something was removed, so assume the location is gone.
            else if (changes.FirstOrDefault(t => t.Reason is UpdateReason.MetadataRemoved).Event is IFileEventArgs firstRemovedEvent) {
                // If we don't know which series to remove, then add all of them to be scanned.
                if (seriesIds.Count is 0) {
                    Logger.LogTrace("No series found for file. Adding all libraries. (File={FileId})", fileId);
                    foreach (var (vfsPath, collectionType, mediaConfigs) in libraries) {
                        locationsToNotify.Add(vfsPath);
                    }

                    goto aLabelToReduceNesting;
                }

                Logger.LogTrace("Processing file removed. (File={FileId})", fileId);
                relativePath = firstRemovedEvent.RelativePath;
                managedFolderId = firstRemovedEvent.ManagedFolderId;
                foreach (var (vfsPath, collectionType, mediaConfigs) in libraries) {
                    foreach (var (managedFolderSubPath, vfsEnabled, mediaFolderPaths) in mediaConfigs.ToManagedFolderList(managedFolderId, relativePath)) {
                        foreach (var mediaFolderPath in mediaFolderPaths) {
                            // Let the core logic handle the rest.
                            if (!vfsEnabled) {
                                var sourceLocation = Path.Join(mediaFolderPath, relativePath[managedFolderSubPath.Length..]);
                                locationsToNotify.Add(sourceLocation);
                                break;
                            }

                            // Check if we can use another location for the file.
                            var result = new LinkGenerationResult();
                            var vfsSymbolicLinks = new HashSet<string>();
                            var topFolders = new HashSet<string>();
                            var newSourceLocation = await GetNewSourceLocation(managedFolderId, managedFolderSubPath, fileId, relativePath, mediaFolderPath);
                            if (!string.IsNullOrEmpty(newSourceLocation)) {
                                var vfsLocations = (await Task.WhenAll(seriesIds.Select(seriesId => ResolveManager.GenerateLocationsForFile(collectionType, vfsPath, newSourceLocation, fileId.ToString(), seriesId))))
                                .Where(tuple => tuple.symbolicLinks.Length > 0 && tuple.importedAt.HasValue)
                                    .ToList();
                                foreach (var (symLinks, importDate) in vfsLocations) {
                                    result += ResolveManager.GenerateSymbolicLinks(vfsPath, newSourceLocation, symLinks, importDate!.Value);
                                    foreach (var path in symLinks.Select(path => Path.Join(vfsPath, path[(vfsPath.Length + 1)..].Split(Path.DirectorySeparatorChar).First())).Distinct())
                                        topFolders.Add(path);
                                }
                                vfsSymbolicLinks = vfsLocations.SelectMany(tuple => tuple.symbolicLinks).ToHashSet();
                            }

                            // Remove old links for file.
                            var videos = LibraryManager
                                .GetItemList(
                                    new() {
                                        SourceTypes = [SourceType.Library],
                                        HasAnyProviderId = new Dictionary<string, string> { { ProviderNames.ShokoFile, fileId.ToString() } },
                                        DtoOptions = new(true),
                                    },
                                    true
                                );
                            Logger.LogTrace("Found {Count} potential videos to remove", videos.Count);
                            foreach (var video in videos) {
                                if (string.IsNullOrEmpty(video.Path) || !video.Path.StartsWith(vfsPath) || result.Paths.Contains(video.Path)) {
                                    Logger.LogTrace("Skipped a {Kind} to remove with path {Path}", video.GetBaseItemKind(), video.Path);
                                    continue;
                                }
                                Logger.LogTrace("Found a {Kind} to remove with path {Path}", video.GetBaseItemKind(), video.Path);
                                RemoveSymbolicLink(video.Path);
                                topFolders.Add(Path.Join(vfsPath, video.Path[(vfsPath.Length + 1)..].Split(Path.DirectorySeparatorChar).First()));
                                locationsToNotify.Add(video.Path);
                                result.RemovedVideos++;
                            }

                            result.Print(Logger, mediaFolderPath);

                            locationsToNotify.AddRange(vfsSymbolicLinks);
                            break;
                        }
                    }
                }
            }

            aLabelToReduceNesting:;
            if (LibraryScanWatcher.IsScanRunning) {
                Logger.LogDebug("Skipped notifying Jellyfin about {LocationCount} changes because a library scan is running. (File={FileId})", locationsToNotify.Count, fileId.ToString());
                return;
            }

            // We let jellyfin take it from here.
            Logger.LogDebug("Notifying Jellyfin about {LocationCount} changes. (File={FileId})", locationsToNotify.Count, fileId.ToString());
            foreach (var location in locationsToNotify) {
                Logger.LogTrace("Notifying Jellyfin about changes to {Location}. (File={FileId})", location, fileId.ToString());
                LibraryMonitor.ReportFileSystemChanged(location);
            }
            Logger.LogDebug("Notified Jellyfin about {LocationCount} changes. (File={FileId})", locationsToNotify.Count, fileId.ToString());
        }
        catch (Exception ex) {
            Logger.LogError(ex, "Error processing {EventCount} file change events. (File={FileId})", changes.Count, fileId);
        }
        finally {
            if (!keepTracker)
                Plugin.Instance.Tracker.Remove(trackerId);
        }
    }

    private async Task<bool> HasUnresolvedCrossReferences(int fileId, IFileEventArgs? fileEvent) {
        if (fileEvent is not null && fileEvent.CrossReferences.All(xref => xref.ShokoSeriesId.HasValue && xref.ShokoEpisodeId.HasValue))
            return false;

        // Refresh the cache so the processing that follows sees the same cross-references.
        var file = await ApiClient.GetFile(fileId.ToString(), refreshCache: true);
        if (file is null)
            return false;

        return file.CrossReferences.Any(xref => !xref.Series.Shoko.HasValue || xref.Episodes.Any(e => !e.Shoko.HasValue));
    }

    private async Task<IReadOnlySet<string>> GetSeriesIdsForFile(int fileId, IFileEventArgs? fileEvent) {
        HashSet<string> seriesIds;
        if (fileEvent is not null && fileEvent.CrossReferences.All(xref => xref.ShokoSeriesId.HasValue && xref.ShokoEpisodeId.HasValue)) {
            seriesIds = fileEvent.CrossReferences.Select(xref => xref.ShokoSeriesId!.Value.ToString())
                .Distinct()
                .ToHashSet();
        }
        else {
            var file = await ApiClient.GetFile(fileId.ToString());
            if (file is null)
                return new HashSet<string>();

            seriesIds = file.CrossReferences
                .Where(xref => xref.Series.Shoko.HasValue && xref.Episodes.All(e => e.Shoko.HasValue))
                .Select(xref => xref.Series.Shoko!.Value.ToString())
                .Distinct()
                .ToHashSet();
        }

        var filteredSeriesIds = new HashSet<string>();
        foreach (var seriesId in seriesIds) {
            var (primaryId, extraIds) = await ApiManager.GetSeriesIdsForShokoSeries(seriesId);
            if (await ApiManager.GetPathSetForSeries(primaryId) is { Count: > 0 }) {
                filteredSeriesIds.Add(seriesId);
            }
            else if (extraIds.Count > 0) {
                foreach (var extraId in extraIds) {
                    if (await ApiManager.GetPathSetForSeries(extraId) is { Count: > 0 }) {
                        filteredSeriesIds.Add(seriesId);
                        break;
                    }
                }
            }
        }

        // Return all series if we only have this file for all of them,
        // otherwise return only the series where we have other files that are
        // not linked to other series.
        return filteredSeriesIds.Count is 0 ? seriesIds : filteredSeriesIds;
    }

    private async Task<string?> GetNewSourceLocation(int managedFolderId, string managedFolderSubPath, int fileId, string relativePath, string mediaFolderPath) {
        // Check if the file still exists, and if it has any other locations we can use.
        var file = await ApiClient.GetFile(fileId.ToString());
        if (file is null)
            return null;

        var usableLocation = file.Locations
            .Where(loc => loc.ManagedFolderId == managedFolderId && (string.IsNullOrEmpty(managedFolderSubPath) || relativePath.StartsWith(managedFolderSubPath + Path.DirectorySeparatorChar)) && loc.RelativePath != relativePath)
            .FirstOrDefault();
        if (usableLocation is null)
            return null;

        var sourceLocation = Path.Join(mediaFolderPath, usableLocation.RelativePath[managedFolderSubPath.Length..]);
        if (!File.Exists(sourceLocation))
            return null;

        return sourceLocation;
    }

    private void RemoveSymbolicLink(string filePath) {
        // TODO: If this works better, then move it to an utility and also use it in the VFS if needed, or remove this comment if that's not needed.
        try {
            var fileExists = File.Exists(filePath);
            var fileInfo = new System.IO.FileInfo(filePath);
            var fileInfoExists = fileInfo.Exists;
            var reparseFlag = fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint);
            Logger.LogTrace(
                "Result for if file is a reparse point; {FilePath} (Exists1={FileExists},Exists2={FileInfoExists},ReparsePoint={IsReparsePoint},Attributes={AllAttributes})",
                filePath,
                fileExists,
                fileInfoExists,
                reparseFlag,
                fileInfo.Attributes
            );

            try {
                File.Delete(filePath);
            }
            catch (Exception ex) {
                Logger.LogError(ex, "Unable to remove symbolic link at path {Path}; {ErrorMessage}", filePath, ex.Message);
            }
        }
        catch (Exception ex) {
            Logger.LogTrace(ex, "Unable to check if file path exists and is a reparse point; {FilePath}", filePath);
        }
    }

    #endregion

    #region Refresh Events

    public void AddSeriesEvent(string metadataId, IMetadataUpdatedEventArgs eventArgs) {
        lock (ChangesPerSeries) {
            if (ChangesPerSeries.TryGetValue(metadataId, out var tuple))
                tuple.LastUpdated = DateTime.Now;
            else
                ChangesPerSeries.Add(metadataId, tuple = (DateTime.Now, [], Plugin.Instance.Tracker.Add($"Metadata event. (Reason=\"{eventArgs.Reason}\",Kind=\"{eventArgs.Kind}\",ProviderUId=\"{eventArgs.ProviderUId}\")")));
            tuple.List.Add(eventArgs);
        }
    }

    private async Task ProcessMetadataEvents(string metadataId, List<IMetadataUpdatedEventArgs> events, Guid trackerId) {
        try {
            var tasks = new List<Task>();
            if (events.Where(e => e.IsImageUpdate).ToList() is { Count: > 0 } imageEvents)
                tasks.Add(ProcessImageUpdateEvents(metadataId, imageEvents));
            if (events.Where(e => e.IsMetadataUpdate).ToList() is { Count: > 0 } metadataEvents)
                tasks.Add(ProcessMetadataUpdateEvents(metadataId, metadataEvents));
            await Task.WhenAll(tasks);
        }
        finally {
            Plugin.Instance.Tracker.Remove(trackerId);
        }
    }

    /// <summary>
    /// Gets the top parent (physical folder) ids for the Jellyfin libraries which have metadata refresh events
    /// enabled, suitable for use as <see cref="InternalItemsQuery.TopParentIds"/>.
    /// </summary>
    /// <remarks>
    /// Used to make sure metadata events only refresh items belonging to libraries with the
    /// "Metadata Events" setting enabled, mirroring how file events are filtered by
    /// <see cref="LibraryConfiguration.IsFileEventsEnabled"/>.
    /// </remarks>
    /// <remarks>
    /// <see cref="MediaFolderConfiguration.LibraryId"/>/<see cref="LibraryConfiguration.Id"/> is the id of the
    /// library itself (a <see cref="CollectionFolder"/>), which is *not* what <c>TopParentIds</c> matches against.
    /// We need to resolve each enabled library to its physical folder ids instead — the same translation Jellyfin
    /// itself performs for library-scoped queries (see <c>LibraryManager.GetTopParentIdsForQuery</c>). This works
    /// the same for VFS libraries, whose only physical folder is normally the generated VFS root (plus any ignored
    /// real media folders), and for legacy libraries, whose physical folders are the real media folders.
    /// </remarks>
    private async Task<Guid[]> GetTopParentIdsForLibrariesWithRefreshEventsEnabled() {
        var libraries = await ConfigurationService.GetAvailableMediaFoldersForLibraries(c => !c.IsIgnored && c.Library.IsRefreshEventsEnabled);
        return libraries
            .SelectMany(tuple => tuple.mediaList)
            .Select(mediaFolder => mediaFolder.LibraryId)
            .Distinct()
            .Select(libraryId => LibraryManager.GetItemById(libraryId) as CollectionFolder)
            .WhereNotNull()
            .SelectMany(collectionFolder => collectionFolder.PhysicalFolderIds)
            .Distinct()
            .ToArray();
    }

    private async Task ProcessImageUpdateEvents(string metadataId, List<IMetadataUpdatedEventArgs> changes) {
        try {
            if (!changes.Any(e => e.Kind is BaseItemKind.Episode or BaseItemKind.Movie && e.EpisodeIds.Count > 0 || e.Kind is BaseItemKind.Series && e.SeriesIds.Count > 0)) {
                Logger.LogDebug("Skipped processing {EventCount} image change events because no series or episode ids to use. (Metadata={ProviderUniqueId})", changes.Count, metadataId);
                return;
            }

            var topParentIds = await GetTopParentIdsForLibrariesWithRefreshEventsEnabled();
            if (topParentIds.Length is 0) {
                Logger.LogDebug("Skipped processing {EventCount} image change events because no libraries have refresh events enabled. (Metadata={ProviderUniqueId})", changes.Count, metadataId);
                return;
            }

            var allSeriesIds = changes.SelectMany(e => e.SeriesIds).ToHashSet();
            var seasonInfoDict = new Dictionary<string, SeasonInfo>();
            var seriesIdDict = new Dictionary<int, string[]>();
            foreach (var seriesId in allSeriesIds) {
                var seasonInfoList = await ApiManager.GetSeasonInfosForShokoSeries(seriesId.ToString());
                foreach (var seasonInfo in seasonInfoList) {
                    seasonInfoDict.Add(seasonInfo.Id, seasonInfo);
                }
                seriesIdDict.Add(seriesId, seasonInfoList.Select(s => s.Id).ToArray());
            }

            if (seasonInfoDict.Count is 0) {
                Logger.LogDebug("Unable to find season info for series id. (Metadata={ProviderUniqueId})", metadataId);
                return;
            }

            var showInfoList = (await Task.WhenAll(seasonInfoDict.Values.Select(s => ApiManager.GetShowInfoBySeasonId(s.Id))))
                .WhereNotNull()
                .DistinctBy(s => s.Id)
                .ToList();
            if (showInfoList.Count is 0) {
                Logger.LogDebug("Unable to find show info for series id. (Metadata={ProviderUniqueId})", metadataId);
                return;
            }

            Logger.LogInformation("Processing {EventCount} image change events… (Metadata={ProviderUniqueId})", changes.Count, metadataId);

            var updateCount = 0;
            var refreshFieldsMask = MetadataRefreshField.Images | MetadataRefreshField.PreferredImages;
            foreach (var showInfo in showInfoList)
                updateCount += await ProcessSeriesEvents(showInfo, changes, seriesIdDict, refreshFieldsMask, topParentIds);

            foreach (var seasonInfo in seasonInfoDict.Values)
                updateCount += await ProcessMovieEvents(seasonInfo, changes, refreshFieldsMask, topParentIds);

            Logger.LogInformation("Scheduled {UpdateCount} image updates for {EventCount} image change events. (Metadata={ProviderUniqueId})", updateCount, changes.Count, metadataId);
        }
        catch (Exception ex) {
            Logger.LogError(ex, "Error processing {EventCount} image change events. (Metadata={ProviderUniqueId})", changes.Count, metadataId);
        }
    }

    private async Task ProcessMetadataUpdateEvents(string metadataId, List<IMetadataUpdatedEventArgs> changes) {
        try {
            if (LibraryScanWatcher.IsScanRunning) {
                Logger.LogDebug("Skipped processing {EventCount} metadata change events because a library scan is running. (Metadata={ProviderUniqueId})", changes.Count, metadataId);
                return;
            }

            if (!changes.Any(e => e.Kind is BaseItemKind.Episode or BaseItemKind.Movie && e.EpisodeIds.Count > 0 || e.Kind is BaseItemKind.Series && e.SeriesIds.Count > 0)) {
                Logger.LogDebug("Skipped processing {EventCount} metadata change events because no series or episode ids to use. (Metadata={ProviderUniqueId})", changes.Count, metadataId);
                return;
            }

            var topParentIds = await GetTopParentIdsForLibrariesWithRefreshEventsEnabled();
            if (topParentIds.Length is 0) {
                Logger.LogDebug("Skipped processing {EventCount} metadata change events because no libraries have refresh events enabled. (Metadata={ProviderUniqueId})", changes.Count, metadataId);
                return;
            }

            var allSeriesIds = changes.SelectMany(e => e.SeriesIds).ToHashSet();
            var seasonInfoDict = new Dictionary<string, SeasonInfo>();
            var seriesIdDict = new Dictionary<int, string[]>();
            foreach (var seriesId in allSeriesIds) {
                var seasonInfoList = await ApiManager.GetSeasonInfosForShokoSeries(seriesId.ToString());
                foreach (var seasonInfo in seasonInfoList) {
                    seasonInfoDict.Add(seasonInfo.Id, seasonInfo);
                }
                seriesIdDict.Add(seriesId, seasonInfoList.Select(s => s.Id).ToArray());
            }

            if (seasonInfoDict.Count is 0) {
                Logger.LogDebug("Unable to find season info for series id. (Metadata={ProviderUniqueId})", metadataId);
                return;
            }

            var showInfoList = (await Task.WhenAll(seasonInfoDict.Values.Select(s => ApiManager.GetShowInfoBySeasonId(s.Id))))
                .WhereNotNull()
                .DistinctBy(s => s.Id)
                .ToList();
            if (showInfoList.Count is 0) {
                Logger.LogDebug("Unable to find show info for series id. (Metadata={ProviderUniqueId})", metadataId);
                return;
            }

            Logger.LogInformation("Processing {EventCount} metadata change events… (Metadata={ProviderUniqueId})", changes.Count, metadataId);

            var updateCount = 0;
            var refreshFieldsMask = ~(MetadataRefreshField.Images | MetadataRefreshField.PreferredImages);
            foreach (var showInfo in showInfoList)
                updateCount += await ProcessSeriesEvents(showInfo, changes, seriesIdDict, refreshFieldsMask, topParentIds);

            foreach (var seasonInfo in seasonInfoDict.Values)
                updateCount += await ProcessMovieEvents(seasonInfo, changes, refreshFieldsMask, topParentIds);

            Logger.LogInformation("Scheduled {UpdateCount} metadata updates for {EventCount} metadata change events. (Metadata={ProviderUniqueId})", updateCount, changes.Count, metadataId);
        }
        catch (Exception ex) {
            Logger.LogError(ex, "Error processing {EventCount} metadata change events. (Metadata={ProviderUniqueId})", changes.Count, metadataId);
        }
    }

    private async Task<int> ProcessSeriesEvents(ShowInfo showInfo, List<IMetadataUpdatedEventArgs> changes, IReadOnlyDictionary<int, string[]> seriesIdDict, MetadataRefreshField refreshFieldsMask, Guid[] topParentIds) {
        // Update the series if we got a series event.
        var updateCount = 0;
        if (changes.Find(e => e.Kind is BaseItemKind.Series) is not null) {
            var shows = LibraryManager
                .GetItemList(new() {
                    IncludeItemTypes = [BaseItemKind.Series],
                    SourceTypes = [SourceType.Library],
                    HasAnyProviderId = new Dictionary<string, string> { { ShokoInternalId.Name, showInfo.InternalId } },
                    TopParentIds = topParentIds,
                    DtoOptions = new(true),
                })
                .DistinctBy(s => s.Id)
                .OfType<TvSeries>()
                .ToList();
            foreach (var show in shows) {
                if (!RecentlyUpdatedEntitiesDict.TryAdd(show.Id, true)) {
                    Logger.LogTrace("Show {ShowName} is already being updated. (Show={ShowId},Series={SeriesId})", show.Name, show.Id, showInfo.Id);
                    continue;
                }

                Logger.LogInformation("Refreshing show {ShowName}. (Show={ShowId},Series={SeriesId})", show.Name, show.Id, showInfo.Id);
                await MetadataRefreshService.RefreshSeries(show, refreshFieldsMask);
                updateCount++;
            }
        }
        // Otherwise update all season/episodes where appropriate.
        else {
            var episodeIds = changes
                .Where(e => e.EpisodeIds.Count > 0 && e.Reason is not UpdateReason.MetadataRemoved)
                .SelectMany(e => new List<string>([
                    ..e.EpisodeIds.Select(eI => eI.ToString()),
                    ..(e.Kind is BaseItemKind.Movie && e.ProviderName is ProviderName.TMDB) ? [IdPrefix.TmdbMovie + e.ProviderId] : Array.Empty<string>(),
                    ..(e.Kind is BaseItemKind.Episode && e.ProviderName is ProviderName.TMDB) ? [IdPrefix.TmdbShow + e.ProviderId] : Array.Empty<string>(),
                ]))
                .ToHashSet();
            var seasonIds = changes
                .Where(e => e.EpisodeIds.Count > 0 && e.SeriesIds.Count > 0 && e.Reason is UpdateReason.MetadataRemoved)
                .SelectMany(e => e.SeriesIds.SelectMany(s => seriesIdDict[s]))
                .ToHashSet();
            var seasonList = showInfo.SeasonList
                .Where(seasonInfo => seasonIds.Contains(seasonInfo.Id) || seasonIds.Overlaps(seasonInfo.ExtraIds))
                .ToList();
            foreach (var seasonInfo in seasonList) {
                var seasons = LibraryManager
                    .GetItemList(new() {
                        IncludeItemTypes = [BaseItemKind.Season],
                        SourceTypes = [SourceType.Library],
                        HasAnyProviderId = new Dictionary<string, string> { { ShokoInternalId.Name, seasonInfo.InternalId } },
                        TopParentIds = topParentIds,
                        DtoOptions = new(true),
                    })
                    .DistinctBy(s => s.Id)
                    .OfType<TvSeason>()
                    .ToList();
                foreach (var season in seasons) {
                    var showId = season.SeriesId;
                    if (RecentlyUpdatedEntitiesDict.ContainsKey(showId)) {
                        Logger.LogTrace("Show is already being updated. (Check=1,Show={ShowId},TvSeason={SeasonId},Season={SeasonId})", showId, season.Id, seasonInfo.Id);
                        continue;
                    }

                    if (!RecentlyUpdatedEntitiesDict.TryAdd(season.Id, true)) {
                        Logger.LogTrace("Season is already being updated. (Check=2,Show={ShowId},TvSeason={SeasonId},Season={SeasonId})", showId, season.Id, seasonInfo.Id);
                        continue;
                    }

                    Logger.LogInformation("Refreshing season {SeasonName}. (TvSeason={SeasonId},Season={SeasonId},ExtraSeries={ExtraIds})", season.Name, season.Id, seasonInfo.Id, seasonInfo.ExtraIds);
                    await MetadataRefreshService.RefreshSeason(season, refreshFieldsMask);
                    updateCount++;
                }
            }
            var episodeList = showInfo.SeasonList
                .Except(seasonList)
                .SelectMany(seasonInfo => seasonInfo.EpisodeList.Concat(seasonInfo.AlternateEpisodesList).Concat(seasonInfo.SpecialsList))
                .Where(episodeInfo => episodeIds.Contains(episodeInfo.Id))
                .ToList();
            foreach (var episodeInfo in episodeList) {
                var episodes = LibraryManager
                    .GetItemList(new() {
                        IncludeItemTypes = [BaseItemKind.Episode],
                        SourceTypes = [SourceType.Library],
                        HasAnyProviderId = new Dictionary<string, string> { { ProviderNames.ShokoEpisode, episodeInfo.Id } },
                        TopParentIds = topParentIds,
                        DtoOptions = new(true),
                    })
                    .DistinctBy(e => e.Id)
                    .OfType<TvEpisode>()
                    .ToList();
                foreach (var episode in episodes) {
                    var showId = episode.SeriesId;
                    var seasonId = episode.SeasonId;
                    if (RecentlyUpdatedEntitiesDict.ContainsKey(showId)) {
                        Logger.LogTrace("Show is already being updated. (Check=1,Show={ShowId},Season={SeasonId},Episode={EpisodeId},Episode={EpisodeId},Season={SeasonId})", showId, seasonId, episode.Id, episodeInfo.Id, episodeInfo.SeasonId);
                        continue;
                    }

                    if (RecentlyUpdatedEntitiesDict.ContainsKey(seasonId)) {
                        Logger.LogTrace("Season is already being updated. (Check=2,Show={ShowId},Season={SeasonId},Episode={EpisodeId},Episode={EpisodeId},Season={SeasonId})", showId, seasonId, episode.Id, episodeInfo.Id, episodeInfo.SeasonId);
                        continue;
                    }

                    if (!RecentlyUpdatedEntitiesDict.TryAdd(episode.Id, true)) {
                        Logger.LogTrace("Episode is already being updated. (Check=3,Show={ShowId},Season={SeasonId},Episode={EpisodeId},Episode={EpisodeId},Season={SeasonId})", showId, seasonId, episode.Id, episodeInfo.Id, episodeInfo.SeasonId);
                        continue;
                    }

                    Logger.LogInformation("Refreshing episode {EpisodeName}. (Episode={EpisodeId},Episode={EpisodeId},Season={SeasonId})", episode.Name, episode.Id, episodeInfo.Id, episodeInfo.SeasonId);
                    await MetadataRefreshService.RefreshEpisode(episode, refreshFieldsMask);
                    updateCount++;
                }
            }
        }
        return updateCount;
    }

    private async Task<int> ProcessMovieEvents(SeasonInfo seasonInfo, List<IMetadataUpdatedEventArgs> changes, MetadataRefreshField refreshFieldsMask, Guid[] topParentIds) {
        // Find movies and refresh them.
        var updateCount = 0;
        var episodeIds = changes
            .Where(e => e.EpisodeIds.Count > 0 && e.Reason is not UpdateReason.MetadataRemoved)
            .SelectMany(e => new List<string>([
                ..e.EpisodeIds.Select(eI => eI.ToString()),
                ..(e.Kind is BaseItemKind.Movie && e.ProviderName is ProviderName.TMDB) ? [IdPrefix.TmdbMovie + e.ProviderId.ToString()] : Array.Empty<string>(),
                ..(e.Kind is BaseItemKind.Episode && e.ProviderName is ProviderName.TMDB) ? [IdPrefix.TmdbShow + e.ProviderId.ToString()] : Array.Empty<string>(),
            ]))
            .ToHashSet();
        var episodeList = seasonInfo.EpisodeList
            .Concat(seasonInfo.AlternateEpisodesList)
            .Concat(seasonInfo.SpecialsList)
            .Where(episodeInfo => episodeIds.Contains(episodeInfo.Id))
            .ToList();
        foreach (var episodeInfo in episodeList) {
            var movies = LibraryManager
                .GetItemList(new() {
                    IncludeItemTypes = [BaseItemKind.Movie],
                    SourceTypes = [SourceType.Library],
                    HasAnyProviderId = new Dictionary<string, string> { { ProviderNames.ShokoEpisode, episodeInfo.Id } },
                    TopParentIds = topParentIds,
                    DtoOptions = new(true),
                })
                .DistinctBy(e => e.Id)
                .OfType<Movie>()
                .ToList();
            foreach (var movie in movies) {
                if (!RecentlyUpdatedEntitiesDict.TryAdd(movie.Id, true)) {
                    Logger.LogTrace("Movie is already being updated. (Movie={MovieId},Episode={EpisodeId},Season={SeasonId},ExtraSeasons={ExtraIds})", movie.Id, episodeInfo.Id, seasonInfo.Id, seasonInfo.ExtraIds);
                    continue;
                }

                Logger.LogInformation("Refreshing movie {MovieName}. (Movie={MovieId},Episode={EpisodeId},Season={SeasonId},ExtraSeasons={ExtraIds})", movie.Name, movie.Id, episodeInfo.Id, seasonInfo.Id, seasonInfo.ExtraIds);
                await MetadataRefreshService.RefreshMovie(movie, refreshFieldsMask);
                updateCount++;
            }
        }
        return updateCount;
    }

    #endregion

    #region Structure Events

    // Fork: events. Daily Shoko servers report group changes, provider link
    // changes, hidden episodes and custom tag changes. Each can move files
    // between shows or seasons, or change which shows exist, so the files of
    // the affected series are linked again and their shows refreshed.

    /// <summary>
    /// Structure changes gathered until Shoko has been quiet for a moment.
    /// </summary>
    private sealed class StructureChanges {
        public DateTime LastUpdated = DateTime.Now;

        public bool GroupsRecreated;

        /// <summary>
        /// Groups whose shows need a metadata refresh, such as a renamed group.
        /// </summary>
        public readonly HashSet<int> RefreshGroupIds = [];

        /// <summary>
        /// Groups whose shows need an image refresh.
        /// </summary>
        public readonly HashSet<int> ImageGroupIds = [];

        /// <summary>
        /// Groups whose series' files need linking again, such as the old and
        /// new group of a moved series.
        /// </summary>
        public readonly HashSet<int> RelinkGroupIds = [];

        /// <summary>
        /// Series whose files need linking again.
        /// </summary>
        public readonly HashSet<int> RelinkSeriesIds = [];

        /// <summary>
        /// Episodes whose series' files need linking again.
        /// </summary>
        public readonly HashSet<int> RelinkEpisodeIds = [];

        public required Guid TrackerId { get; init; }
    }

    private readonly object StructureChangesLock = new();

    private StructureChanges? PendingStructureChanges;

    private void AddStructureChange(string description, Action<StructureChanges> apply) {
        lock (StructureChangesLock) {
            PendingStructureChanges ??= new() { TrackerId = Plugin.Instance.Tracker.Add($"Structure event. ({description})") };
            PendingStructureChanges.LastUpdated = DateTime.Now;
            apply(PendingStructureChanges);
        }
    }

    private StructureChanges? TakeStructureChanges(bool force = false) {
        lock (StructureChangesLock) {
            if (PendingStructureChanges is not { } changes)
                return null;
            if (!force && DateTime.Now - changes.LastUpdated < DetectChangesThreshold)
                return null;
            PendingStructureChanges = null;
            return changes;
        }
    }

    /// <summary>
    /// A group was added, updated or removed, or its images changed.
    /// </summary>
    public void AddGroupEvent(int groupId, UpdateReason reason) {
        if (groupId <= 0)
            return;

        switch (reason) {
            case UpdateReason.MetadataAdded or UpdateReason.MetadataUpdated:
                AddStructureChange($"Reason=\"{reason}\",Group={groupId}", changes => changes.RefreshGroupIds.Add(groupId));
                break;
            case UpdateReason.ImageAdded or UpdateReason.ImageUpdated or UpdateReason.ImageRemoved:
                AddStructureChange($"Reason=\"{reason}\",Group={groupId}", changes => changes.ImageGroupIds.Add(groupId));
                break;
            // Shoko only removes a group once it holds no series, and the
            // series moved out of it are reported on their own.
            default:
                Logger.LogTrace("Skipped group event with {UpdateReason}; no operation. (Group={GroupId})", reason, groupId);
                break;
        }
    }

    /// <summary>
    /// A series moved from one group to another.
    /// </summary>
    public void AddSeriesMovedEvent(int seriesId, int oldGroupId, int newGroupId)
        => AddStructureChange($"SeriesMoved,Series={seriesId},OldGroup={oldGroupId},NewGroup={newGroupId}", changes => {
            if (seriesId > 0)
                changes.RelinkSeriesIds.Add(seriesId);
            if (oldGroupId > 0)
                changes.RelinkGroupIds.Add(oldGroupId);
            if (newGroupId > 0)
                changes.RelinkGroupIds.Add(newGroupId);
        });

    /// <summary>
    /// Shoko recreated every group.
    /// </summary>
    public void AddGroupsRecreatedEvent()
        => AddStructureChange("GroupsRecreated", changes => changes.GroupsRecreated = true);

    /// <summary>
    /// Something about the given series changed that can change where their
    /// files belong, such as their provider links or custom tags.
    /// </summary>
    public void AddSeriesStructureEvent(IEnumerable<int> seriesIds, string reason) {
        var ids = seriesIds.Where(id => id > 0).ToList();
        if (ids.Count is 0)
            return;
        AddStructureChange($"Reason=\"{reason}\",Series={string.Join(',', ids)}", changes => changes.RelinkSeriesIds.UnionWith(ids));
    }

    /// <summary>
    /// An episode was hidden or shown again.
    /// </summary>
    public void AddEpisodeStructureEvent(int episodeId, string reason) {
        if (episodeId <= 0)
            return;
        AddStructureChange($"Reason=\"{reason}\",Episode={episodeId}", changes => changes.RelinkEpisodeIds.Add(episodeId));
    }

    private async Task<IReadOnlyList<int>> GetSeriesIdsInGroup(int groupId)
        => (await ApiClient.GetShokoSeriesInGroup(groupId.ToString(), recursive: true))
            .Select(series => series.IDs.Shoko)
            .Where(id => id > 0)
            .ToList();

    private async Task ProcessStructureEvents(StructureChanges changes) {
        try {
            if (LibraryScanWatcher.IsScanRunning) {
                Logger.LogInformation("Skipped processing structure change events because a library scan is running.");
                return;
            }

            // Shoko changed how its series are grouped or linked, so any
            // cached show, season or VFS data may be stale.
            ApiClient.Clear();
            ApiManager.Clear();

            if (changes.GroupsRecreated) {
                ScheduleLibraryCatchUp("Shoko recreated every group");
                return;
            }

            var relinkSeriesIds = new HashSet<int>(changes.RelinkSeriesIds);
            foreach (var groupId in changes.RelinkGroupIds)
                relinkSeriesIds.UnionWith(await GetSeriesIdsInGroup(groupId));
            foreach (var episodeId in changes.RelinkEpisodeIds) {
                if (await ApiClient.GetShokoEpisode(episodeId.ToString()) is { IDs.ParentSeries: > 0 } episode)
                    relinkSeriesIds.Add(episode.IDs.ParentSeries);
            }

            var refreshSeriesIds = new HashSet<int>(relinkSeriesIds);
            foreach (var groupId in changes.RefreshGroupIds)
                refreshSeriesIds.UnionWith(await GetSeriesIdsInGroup(groupId));

            var imageSeriesIds = new HashSet<int>();
            foreach (var groupId in changes.ImageGroupIds)
                imageSeriesIds.UnionWith(await GetSeriesIdsInGroup(groupId));

            Logger.LogInformation(
                "Processing structure change events… (RelinkSeries={RelinkCount},RefreshSeries={RefreshCount},ImageSeries={ImageCount})",
                relinkSeriesIds.Count,
                refreshSeriesIds.Count,
                imageSeriesIds.Count
            );

            // Link the files again. File events are only acted on for
            // libraries with file events enabled.
            var fileCount = 0;
            foreach (var seriesId in relinkSeriesIds) {
                foreach (var file in await ApiClient.GetFilesForShokoSeries(seriesId.ToString())) {
                    if ((file.Locations.FirstOrDefault(location => location.IsAccessible) ?? file.Locations.FirstOrDefault()) is not { } location)
                        continue;
                    var eventArgs = new FileEventArgsStub(location, file);
                    AddFileEvent(file.Id, UpdateReason.MetadataUpdated, eventArgs.ManagedFolderId, eventArgs.RelativePath, eventArgs);
                    fileCount++;
                }
            }

            // Refresh the shows. Metadata events are only acted on for
            // libraries with refresh events enabled.
            var seriesEventCount = 0;
            if (Plugin.Instance.Configuration.SignalR_EventSources.Contains(ProviderName.Shoko)) {
                foreach (var seriesId in refreshSeriesIds) {
                    AddSeriesEvent($"{ProviderName.Shoko}:{seriesId}", CreateShokoSeriesEvent(seriesId, UpdateReason.MetadataUpdated));
                    seriesEventCount++;
                }
                foreach (var seriesId in imageSeriesIds) {
                    AddSeriesEvent($"{ProviderName.Shoko}:{seriesId}", CreateShokoSeriesEvent(seriesId, UpdateReason.ImageUpdated));
                    seriesEventCount++;
                }
            }
            else if (refreshSeriesIds.Count > 0 || imageSeriesIds.Count > 0) {
                Logger.LogDebug("Skipped refreshing shows for structure change events because the Shoko event source is not enabled in the plugin settings.");
            }

            Logger.LogInformation("Queued {FileCount} file events and {SeriesCount} series events for structure change events.", fileCount, seriesEventCount);
        }
        catch (Exception ex) {
            Logger.LogError(ex, "Error processing structure change events.");
        }
        finally {
            Plugin.Instance.Tracker.Remove(changes.TrackerId);
        }
    }

    private static SeriesInfoUpdatedEventArgs CreateShokoSeriesEvent(int seriesId, UpdateReason reason)
        => new() {
            Reason = reason,
            ProviderName = ProviderName.Shoko,
            ProviderId = seriesId.ToString(),
            SeriesIds = [seriesId],
        };

    #endregion

    #region Library Catch-up

    // Fork: events. Shoko does not replay the events it sent while we were
    // not listening, so after a lost connection (and optionally on an
    // interval) the libraries we manage are scanned to catch up.

    /// <summary>
    /// How long to wait after the last request before scanning, so a
    /// flapping connection only scans once.
    /// </summary>
    private static readonly TimeSpan CatchUpDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The least time between two catch-up scans.
    /// </summary>
    private static readonly TimeSpan CatchUpMinimumInterval = TimeSpan.FromMinutes(10);

    private readonly object CatchUpLock = new();

    private System.Threading.CancellationTokenSource? CatchUpCancellation;

    private DateTime LastCatchUp = DateTime.MinValue;

    /// <summary>
    /// Schedules a scan of the libraries with file or refresh events enabled.
    /// Requests that come in before it starts are merged into one scan.
    /// </summary>
    /// <param name="reason">Why the scan is needed, for the logs.</param>
    public void ScheduleLibraryCatchUp(string reason) {
        lock (CatchUpLock) {
            CatchUpCancellation?.Cancel();
            var cancellation = CatchUpCancellation = new();
            var delay = LastCatchUp + CatchUpMinimumInterval - DateTime.Now;
            if (delay < CatchUpDelay)
                delay = CatchUpDelay;
            Logger.LogInformation("Scheduled a library scan to catch up with Shoko in {Delay}. (Reason={Reason})", delay, reason);
            _ = Task.Run(async () => {
                try {
                    await Task.Delay(delay, cancellation.Token);
                }
                catch (OperationCanceledException) {
                    return;
                }
                await RunLibraryCatchUp(reason);
            });
        }
    }

    private async Task RunLibraryCatchUp(string reason) {
        try {
            if (LibraryScanWatcher.IsScanRunning) {
                Logger.LogInformation("Skipped the library catch-up scan because a library scan is already running. (Reason={Reason})", reason);
                return;
            }

            var libraries = await ConfigurationService.GetAvailableMediaFoldersForLibraries(c => !c.IsIgnored && (c.Library.IsFileEventsEnabled || c.Library.IsRefreshEventsEnabled));
            var collectionFolders = libraries
                .SelectMany(tuple => tuple.mediaList)
                .Select(mediaFolder => mediaFolder.LibraryId)
                .Distinct()
                .Select(libraryId => LibraryManager.GetItemById(libraryId) as CollectionFolder)
                .WhereNotNull()
                .DistinctBy(folder => folder.Id)
                .ToList();
            if (collectionFolders.Count is 0) {
                Logger.LogDebug("Skipped the library catch-up scan because no libraries have file or refresh events enabled. (Reason={Reason})", reason);
                return;
            }

            lock (CatchUpLock)
                LastCatchUp = DateTime.Now;

            // Read everything fresh from Shoko, and let the VFS be generated
            // again.
            ApiClient.Clear();
            ApiManager.Clear();
            ResolveManager.Clear();

            foreach (var folder in collectionFolders) {
                Logger.LogInformation("Scanning library {LibraryName} to catch up with Shoko. (Library={LibraryId},Reason={Reason})", folder.Name, folder.Id, reason);
                ProviderManager.QueueRefresh(
                    folder.Id,
                    // Jellyfin validates the children of a refreshed collection
                    // folder, which regenerates the VFS and finds new and
                    // removed items.
                    new MetadataRefreshOptions(DirectoryService) {
                        MetadataRefreshMode = MetadataRefreshMode.Default,
                        ImageRefreshMode = MetadataRefreshMode.Default,
                        ReplaceAllMetadata = false,
                        ReplaceAllImages = false,
                    },
                    RefreshPriority.Normal
                );
            }
        }
        catch (Exception ex) {
            Logger.LogError(ex, "Error scheduling the library catch-up scan. (Reason={Reason})", reason);
        }
    }

    #endregion
}
