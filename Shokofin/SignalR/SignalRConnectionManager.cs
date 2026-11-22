using System;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shokofin.API;
using Shokofin.Configuration;
using Shokofin.Events;
using Shokofin.Events.Interfaces;
using Shokofin.Events.Stub;
using Shokofin.Extensions;
using Shokofin.SignalR.Models;
using Shokofin.Utils;

namespace Shokofin.SignalR;

public class SignalRConnectionManager {
    // Fork: events. The group feed is only on daily servers; others skip it.
    private const string HubUrl = "/signalr/aggregate?feeds=shoko,metadata,file,release,group";

    private readonly ILogger<SignalRConnectionManager> Logger;

    private readonly ShokoApiClient ApiClient;

    private readonly EventDispatchService Events;

    private readonly LibraryScanWatcher LibraryScanWatcher;

    private IDisposable? EventSubmitterLease = null;

    private HubConnection? Connection = null;

    private string CachedKey = string.Empty;

    /// <summary>
    /// Set when the connection was lost, so the next successful connection
    /// catches up on what was missed.
    /// </summary>
    private bool HasLostConnection = false;

    private System.Threading.Timer? PeriodicCatchUpTimer = null;

#pragma warning disable CA1822
    public bool IsUsable => CanConnect(Plugin.Instance.Configuration);
#pragma warning restore CA1822

    public bool IsActive => Connection != null;

    public HubConnectionState State => Connection == null ? HubConnectionState.Disconnected : Connection.State;

    public SignalRConnectionManager(
        ILogger<SignalRConnectionManager> logger,
        ShokoApiClient apiClient,
        EventDispatchService events,
        LibraryScanWatcher libraryScanWatcher
    ) {
        Logger = logger;
        ApiClient = apiClient;
        Events = events;
        LibraryScanWatcher = libraryScanWatcher;
    }

    #region Connection

    private async Task ConnectAsync(PluginConfiguration config) {
        if (Connection != null || !CanConnect(config))
            return;

        var builder = new HubConnectionBuilder()
            .WithUrl(config.Url + HubUrl, connectionOptions =>
                connectionOptions.AccessTokenProvider = () => Task.FromResult<string?>(config.ApiKey)
            )
            .AddJsonProtocol();

        if (config.SignalR_AutoReconnectInSeconds.Length > 0)
            builder = builder.WithAutomaticReconnect(new SignalrRetryPolicy([.. config.SignalR_AutoReconnectInSeconds.Select(seconds => TimeSpan.FromSeconds(seconds))]));

        var connection = Connection = builder.Build();

        connection.ServerTimeout = TimeSpan.FromMinutes(1);
        connection.Closed += OnDisconnected;
        connection.Reconnecting += OnReconnecting;
        connection.Reconnected += OnReconnected;

        if (Plugin.Instance.Configuration.HasPluginsExposed) {
            // Attach metadata events.
            connection.On<EpisodeInfoUpdatedEventArgs>("metadata:episode.added", OnInfoUpdated);
            connection.On<EpisodeInfoUpdatedEventArgs>("metadata:episode.updated", OnInfoUpdated);
            connection.On<EpisodeInfoUpdatedEventArgs>("metadata:episode.removed", OnInfoUpdated);
            connection.On<SeriesInfoUpdatedEventArgs>("metadata:series.added", OnInfoUpdated);
            connection.On<SeriesInfoUpdatedEventArgs>("metadata:series.updated", OnInfoUpdated);
            connection.On<SeriesInfoUpdatedEventArgs>("metadata:series.removed", OnInfoUpdated);
            connection.On<MovieInfoUpdatedEventArgs>("metadata:movie.added", OnInfoUpdated);
            connection.On<MovieInfoUpdatedEventArgs>("metadata:movie.updated", OnInfoUpdated);
            connection.On<MovieInfoUpdatedEventArgs>("metadata:movie.removed", OnInfoUpdated);

            // Fork: events. Daily servers name image events after their
            // reason. Older daily servers sent them in lower case.
            foreach (var (suffix, reason) in DailyImageEventSuffixes) {
                connection.On<EpisodeInfoUpdatedEventArgs>($"metadata:episode.{suffix}", e => OnInfoUpdated(WithReason(e, reason)));
                connection.On<SeriesInfoUpdatedEventArgs>($"metadata:series.{suffix}", e => OnInfoUpdated(WithReason(e, reason)));
                connection.On<MovieInfoUpdatedEventArgs>($"metadata:movie.{suffix}", e => OnInfoUpdated(WithReason(e, reason)));
            }

            // Fork: events. Changes that can move files between shows.
            connection.On<MetadataLinksChangedEventArgs>("metadata:links.changed", OnLinksChanged);
            connection.On<EpisodeHiddenChangedEventArgs>("metadata:episode.hiddenChanged", OnEpisodeHiddenChanged);
            connection.On<SeriesCustomTagsChangedEventArgs>("metadata:series.customTagsChanged", OnSeriesCustomTagsChanged);

            // Attach release events.
            connection.On<ReleaseSavedEventArgs>("release:saved", OnReleaseSaved);
            // Fork: events. release:removed was release:deleted on earlier daily servers.
            connection.On<FileIdEventArgs>("release:removed", OnReleaseRemoved);
            connection.On<FileIdEventArgs>("release:deleted", OnReleaseRemoved);

            // Attach file events.
            connection.On<FileEventArgs>("file:deleted", OnFileDeleted);
            connection.On<FileMovedEventArgs>("file:relocated", OnFileRelocated);
            // Fork: events.
            connection.On<FileEventArgs>("file:hashed", OnFileHashed);
            connection.On<FileIdEventArgs>("file:updated", OnFileUpdated);

            // Fork: events. Attach group events.
            foreach (var (suffix, reason) in DailyGroupEventSuffixes)
                connection.On<GroupInfoUpdatedEventArgs>($"group:group.{suffix}", e => OnGroupUpdated(e, reason));
            connection.On<SeriesMovedEventArgs>("group:series.moved", OnSeriesMoved);
            connection.On("group:recreated", OnGroupsRecreated);
        }
        else {
            // Attach refresh events.
            connection.On<EpisodeInfoUpdatedEventArgs>("ShokoEvent:EpisodeUpdated", OnInfoUpdated);
            connection.On<SeriesInfoUpdatedEventArgs>("ShokoEvent:SeriesUpdated", OnInfoUpdated);
            connection.On<MovieInfoUpdatedEventArgs>("ShokoEvent:MovieUpdated", OnInfoUpdated);

            // Attach file events.
            connection.On<FileEventArgs>("ShokoEvent:FileMatched", OnFileMatched);
            connection.On<FileEventArgs>("ShokoEvent:FileDeleted", OnFileDeleted);
            connection.On<FileMovedEventArgs>("ShokoEvent:FileMoved", OnFileRelocated);
            connection.On<FileRenamedEventArgs>("ShokoEvent:FileRenamed", OnFileRelocated);
            // Fork: events.
            connection.On<FileEventArgs>("ShokoEvent:FileHashed", OnFileHashed);
            connection.On<FileEventArgs>("ShokoEvent:FileNotMatched", OnFileNotMatched);
        }

        EventSubmitterLease = Events.RegisterEventSubmitter();
        try {
            Logger.LogInformation("Connecting to Shoko Server.");

            await connection.StartAsync();

            Logger.LogInformation("Connected to Shoko Server.");

            ResetPeriodicCatchUpTimer(config);
            if (HasLostConnection) {
                HasLostConnection = false;
                if (config.SignalR_CatchUpOnReconnect)
                    Events.ScheduleLibraryCatchUp("Connected again after losing the connection");
            }
        }
        catch (HttpRequestException ex) when (ex is { HttpRequestError: HttpRequestError.ConnectionError, InnerException: SocketException { SocketErrorCode: SocketError.ConnectionRefused } }) {
            Logger.LogWarning("Unable to connect to Shoko Server due to a connection error. Please reconnect manually.");
            await DisconnectAsync();
        }
        catch (Exception ex) {
            Logger.LogError(ex, "An unexpected error occurred while attempting to connect to Shoko Server. Please reconnect manually.");
            await DisconnectAsync();
        }
    }

    private Task OnReconnected(string? connectionId) {
        Logger.LogInformation("Reconnected to Shoko Server. (Connection={ConnectionId})", connectionId);

        // Fork: events. Shoko doesn't replay the events we missed.
        HasLostConnection = false;
        if (Plugin.Instance.Configuration.SignalR_CatchUpOnReconnect)
            Events.ScheduleLibraryCatchUp("Reconnected to Shoko Server");
        return Task.CompletedTask;
    }

    private Task OnReconnecting(Exception? exception) {
        Logger.LogWarning(exception, "Disconnected from Shoko Server. Attempting to reconnect…");
        HasLostConnection = true;
        return Task.CompletedTask;
    }

    private Task OnDisconnected(Exception? exception) {
        // Graceful disconnection.
        if (exception == null) {
            Logger.LogInformation("Gracefully disconnected from Shoko Server.");
        }
        else {
            Logger.LogWarning(exception, "Abruptly disconnected from Shoko Server.");
            HasLostConnection = true;
        }
        return Task.CompletedTask;
    }

    #region Periodic Catch-up

    // Fork: events.

    private void ResetPeriodicCatchUpTimer(PluginConfiguration config) {
        PeriodicCatchUpTimer?.Dispose();
        PeriodicCatchUpTimer = null;
        if (Connection is null || config.SignalR_PeriodicCatchUpIntervalInHours <= 0)
            return;

        var interval = TimeSpan.FromHours(config.SignalR_PeriodicCatchUpIntervalInHours);
        PeriodicCatchUpTimer = new(OnPeriodicCatchUp, null, interval, interval);
        Logger.LogDebug("Scheduled a periodic library catch-up scan every {Interval}.", interval);
    }

    private void OnPeriodicCatchUp(object? state) {
        if (State is not HubConnectionState.Connected)
            return;
        Events.ScheduleLibraryCatchUp("Periodic catch-up");
    }

    #endregion

    public async Task DisconnectAsync() {
        if (Connection == null)
            return;

        var connection = Connection;
        Connection = null;

        PeriodicCatchUpTimer?.Dispose();
        PeriodicCatchUpTimer = null;

        if (connection.State != HubConnectionState.Disconnected)
            await connection.StopAsync();

        await connection.DisposeAsync();

        if (EventSubmitterLease is not null) {
            EventSubmitterLease.Dispose();
            EventSubmitterLease = null;
        }
    }

    public Task ResetConnectionAsync()
        => ResetConnectionAsync(Plugin.Instance.Configuration, true);

    private void ResetConnection(PluginConfiguration config, bool shouldConnect)
        => Task.Run(() => ResetConnectionAsync(config, shouldConnect)).GetAwaiter().GetResult();

    private async Task ResetConnectionAsync(PluginConfiguration config, bool shouldConnect) {
        await DisconnectAsync();
        if (shouldConnect)
            await ConnectAsync(config);
    }

    public async Task RunAsync() {
        var config = Plugin.Instance.Configuration;
        CachedKey = ConstructKey(config);
        Plugin.Instance.ConfigurationChanged += OnConfigurationChanged;

        await ResetConnectionAsync(config, config.SignalR_AutoConnectEnabled);
    }

    public async Task StopAsync() {
        Plugin.Instance.ConfigurationChanged -= OnConfigurationChanged;
        await DisconnectAsync();
    }

    private void OnConfigurationChanged(object? sender, PluginConfiguration config) {
        var currentKey = ConstructKey(config);
        if (!string.Equals(currentKey, CachedKey)) {
            Logger.LogDebug("Detected change in SignalR configuration! (Config={Config})", currentKey);
            CachedKey = currentKey;
            ResetConnection(config, Connection != null);
        }
        else if (Connection is not null) {
            ResetPeriodicCatchUpTimer(config);
        }
    }

    private static bool CanConnect(PluginConfiguration config)
        => !string.IsNullOrEmpty(config.Url) && !string.IsNullOrEmpty(config.ApiKey) && config.ServerVersion != null;

    private static string ConstructKey(PluginConfiguration config)
        => $"CanConnect={CanConnect(config)},AutoReconnect={config.SignalR_AutoReconnectInSeconds.Select(s => s.ToString()).Join(',')}";

    #endregion

    #region Events

    #region File Events

    private void OnFileMatched(IFileEventArgs eventArgs) {
        Logger.LogDebug(
            "File matched; {ManagedFolderId} {Path} (File={FileId},Location={LocationId},CrossReferences={HasCrossReferences})",
            eventArgs.ManagedFolderId,
            eventArgs.RelativePath,
            eventArgs.FileId,
            eventArgs.FileLocationId,
            eventArgs.HasCrossReferences
        );

        if (LibraryScanWatcher.IsScanRunning) {
            Logger.LogTrace(
                "Library scan is running. Skipping emit of file event. (File={FileId},Location={LocationId})",
                eventArgs.FileId,
                eventArgs.FileLocationId
            );
            return;
        }

        Events.AddFileEvent(eventArgs.FileId, UpdateReason.MetadataUpdated, eventArgs.ManagedFolderId, eventArgs.RelativePath, eventArgs);
    }

    private async Task OnReleaseSaved(IReleaseSavedEventArgs eventArgs0) {
        if (await ApiClient.GetFile(eventArgs0.FileId.ToString()) is not { } file) {
            Logger.LogDebug("File not found; {VideoId}", eventArgs0.FileId);
            return;
        }
        
        if ((file.Locations.FirstOrDefault(location => location.IsAccessible) ?? file.Locations.FirstOrDefault()) is not { } fileLocation) {
            Logger.LogDebug("File location not found; {VideoId}", eventArgs0.FileId);
            return;
        }

        var eventArgs = new FileEventArgsStub(fileLocation, file);
        Logger.LogDebug(
            "Release saved; {ManagedFolderId} {Path} (File={FileId},Location={LocationId},CrossReferences={HasCrossReferences})",
            eventArgs.ManagedFolderId,
            eventArgs.RelativePath,
            eventArgs.FileId,
            eventArgs.FileLocationId,
            eventArgs.HasCrossReferences
        );

        if (LibraryScanWatcher.IsScanRunning) {
            Logger.LogTrace(
                "Library scan is running. Skipping emit of file event. (File={FileId},Location={LocationId})",
                eventArgs.FileId,
                eventArgs.FileLocationId
            );
            return;
        }

        Events.AddFileEvent(eventArgs.FileId, UpdateReason.MetadataUpdated, eventArgs.ManagedFolderId, eventArgs.RelativePath, eventArgs);
    }

    private void OnFileRelocated(IFileRelocationEventArgs eventArgs) {
        Logger.LogDebug(
            "File relocated; {ManagedFolderIdA} {PathA} → {ManagedFolderIdB} {PathB} (File={FileId},Location={LocationId},CrossReferences={HasCrossReferences})",
            eventArgs.PreviousManagedFolderId,
            eventArgs.PreviousRelativePath,
            eventArgs.ManagedFolderId,
            eventArgs.RelativePath,
            eventArgs.FileId,
            eventArgs.FileLocationId,
            eventArgs.HasCrossReferences
        );

        if (LibraryScanWatcher.IsScanRunning) {
            Logger.LogTrace(
                "Library scan is running. Skipping emit of file event. (File={FileId},Location={LocationId})",
                eventArgs.FileId,
                eventArgs.FileLocationId
            );
            return;
        }

        Events.AddFileEvent(eventArgs.FileId, UpdateReason.MetadataRemoved, eventArgs.PreviousManagedFolderId, eventArgs.PreviousRelativePath, eventArgs);
        Events.AddFileEvent(eventArgs.FileId, UpdateReason.MetadataAdded, eventArgs.ManagedFolderId, eventArgs.RelativePath, eventArgs);
    }

    // Fork: events.
    private void OnFileHashed(IFileEventArgs eventArgs) {
        // A new file is matched later, and that event covers it. A file whose
        // contents Shoko already knew, such as one removed and added back,
        // keeps its stored release and gets no other event, so act on hashed
        // files that already have episodes.
        if (!eventArgs.HasCrossReferences || eventArgs.CrossReferences.Count is 0) {
            Logger.LogTrace(
                "File hashed without cross-references; skipped. (File={FileId},Location={LocationId})",
                eventArgs.FileId,
                eventArgs.FileLocationId
            );
            return;
        }

        Logger.LogDebug(
            "File hashed with known cross-references; {ManagedFolderId} {Path} (File={FileId},Location={LocationId})",
            eventArgs.ManagedFolderId,
            eventArgs.RelativePath,
            eventArgs.FileId,
            eventArgs.FileLocationId
        );
        OnFileMatched(eventArgs);
    }

    // Fork: events.
    private void OnFileNotMatched(IFileEventArgs eventArgs) {
        Logger.LogDebug(
            "File not matched; {ManagedFolderId} {Path} (File={FileId},Location={LocationId})",
            eventArgs.ManagedFolderId,
            eventArgs.RelativePath,
            eventArgs.FileId,
            eventArgs.FileLocationId
        );
        OnFileMatched(eventArgs);
    }

    // Fork: events.
    private Task OnReleaseRemoved(FileIdEventArgs eventArgs) {
        if (eventArgs.FileId is not > 0) {
            Logger.LogDebug("Release removed without a file; skipped.");
            return Task.CompletedTask;
        }

        // The file keeps existing but loses its episodes, so reading it again
        // removes its links.
        return EmitFileEventForFileId(eventArgs.FileId.Value, "Release removed");
    }

    // Fork: events. Sent when a file is marked ignored or as a variation.
    private Task OnFileUpdated(FileIdEventArgs eventArgs) {
        if (eventArgs.FileId is not > 0)
            return Task.CompletedTask;
        return EmitFileEventForFileId(eventArgs.FileId.Value, "File updated");
    }

    // Fork: events.
    private async Task EmitFileEventForFileId(int fileId, string description) {
        if (await ApiClient.GetFile(fileId.ToString(), refreshCache: true) is not { } file) {
            Logger.LogDebug("{Description}; file not found. (File={FileId})", description, fileId);
            return;
        }

        if ((file.Locations.FirstOrDefault(location => location.IsAccessible) ?? file.Locations.FirstOrDefault()) is not { } fileLocation) {
            Logger.LogDebug("{Description}; file location not found. (File={FileId})", description, fileId);
            return;
        }

        var eventArgs = new FileEventArgsStub(fileLocation, file);
        Logger.LogDebug(
            "{Description}; {ManagedFolderId} {Path} (File={FileId},Location={LocationId},CrossReferences={CrossReferenceCount})",
            description,
            eventArgs.ManagedFolderId,
            eventArgs.RelativePath,
            eventArgs.FileId,
            eventArgs.FileLocationId,
            eventArgs.CrossReferences.Count
        );

        if (LibraryScanWatcher.IsScanRunning) {
            Logger.LogTrace(
                "Library scan is running. Skipping emit of file event. (File={FileId},Location={LocationId})",
                eventArgs.FileId,
                eventArgs.FileLocationId
            );
            return;
        }

        Events.AddFileEvent(eventArgs.FileId, UpdateReason.MetadataUpdated, eventArgs.ManagedFolderId, eventArgs.RelativePath, eventArgs);
    }

    private void OnFileDeleted(IFileEventArgs eventArgs) {
        Logger.LogDebug(
            "File deleted; {ManagedFolderId} {Path} (File={FileId},Location={LocationId},CrossReferences={HasCrossReferences})",
            eventArgs.ManagedFolderId,
            eventArgs.RelativePath,
            eventArgs.FileId,
            eventArgs.FileLocationId,
            eventArgs.HasCrossReferences
        );

        if (LibraryScanWatcher.IsScanRunning) {
            Logger.LogTrace(
                "Library scan is running. Skipping emit of file event. (File={FileId},Location={LocationId})",
                eventArgs.FileId,
                eventArgs.FileLocationId
            );
            return;
        }

        Events.AddFileEvent(eventArgs.FileId, UpdateReason.MetadataRemoved, eventArgs.ManagedFolderId, eventArgs.RelativePath, eventArgs);
    }

    #endregion

    #region Refresh Events

    private void OnInfoUpdated(IMetadataUpdatedEventArgs eventArgs) {
        if (eventArgs.IsUnknownUpdate) {
            Logger.LogTrace(
                "{ProviderName} {MetadataType} {ProviderId} ({ProviderParentId}) skipped event with {UpdateReason}; no operation. (Episode={EpisodeId},Series={SeriesId})",
                eventArgs.ProviderName,
                eventArgs.Kind,
                eventArgs.ProviderId,
                eventArgs.ProviderParentId,
                eventArgs.Reason,
                eventArgs.EpisodeIds,
                eventArgs.SeriesIds
            );
            return;
        }

        if (!Plugin.Instance.Configuration.SignalR_EventSources.Contains(eventArgs.ProviderName)) {
            Logger.LogTrace(
                "{ProviderName} {MetadataType} {ProviderId} ({ProviderParentId}) skipped event with {UpdateReason}; provider is not enabled in the plugin settings. (Episode={EpisodeId},Series={SeriesId})",
                eventArgs.ProviderName,
                eventArgs.Kind,
                eventArgs.ProviderId,
                eventArgs.ProviderParentId,
                eventArgs.Reason,
                eventArgs.EpisodeIds,
                eventArgs.SeriesIds
            );
            return;
        }

        Logger.LogDebug(
            "{ProviderName} {MetadataType} {ProviderId} ({ProviderParentId}) dispatched event with {UpdateReason}. (Episode={EpisodeId},Series={SeriesId})",
            eventArgs.ProviderName,
            eventArgs.Kind,
            eventArgs.ProviderId,
            eventArgs.ProviderParentId,
            eventArgs.Reason,
            eventArgs.EpisodeIds,
            eventArgs.SeriesIds
        );

        if (LibraryScanWatcher.IsScanRunning) {
            Logger.LogTrace(
                "Library scan is running. Skipping emit of refresh event. (Episode={EpisodeId},Series={SeriesId})",
                eventArgs.EpisodeIds,
                eventArgs.SeriesIds
            );
            return;
        }

        if (eventArgs.Kind is BaseItemKind.Episode or BaseItemKind.Series or BaseItemKind.Movie)
            Events.AddSeriesEvent(eventArgs.ProviderParentUId ?? eventArgs.ProviderUId, eventArgs);
    }

    #endregion

    #region Structure Events

    // Fork: events. Daily-only events that can change which show or season a
    // file belongs to.

    private static readonly (string Suffix, UpdateReason Reason)[] DailyImageEventSuffixes = [
        ("imageAdded", UpdateReason.ImageAdded),
        ("imageUpdated", UpdateReason.ImageUpdated),
        ("imageRemoved", UpdateReason.ImageRemoved),
        ("imageadded", UpdateReason.ImageAdded),
        ("imageupdated", UpdateReason.ImageUpdated),
        ("imageremoved", UpdateReason.ImageRemoved),
    ];

    private static readonly (string Suffix, UpdateReason Reason)[] DailyGroupEventSuffixes = [
        ("added", UpdateReason.MetadataAdded),
        ("updated", UpdateReason.MetadataUpdated),
        ("removed", UpdateReason.MetadataRemoved),
        ..DailyImageEventSuffixes,
    ];

    /// <summary>
    /// Daily servers put the reason in the event name. Trust the name when
    /// the payload's reason isn't an image reason.
    /// </summary>
    private static T WithReason<T>(T eventArgs, UpdateReason reason) where T : IMetadataUpdatedEventArgs {
        if (eventArgs.IsImageUpdate)
            return eventArgs;
        switch (eventArgs) {
            case EpisodeInfoUpdatedEventArgs episodeArgs:
                episodeArgs.Reason = reason;
                break;
            case SeriesInfoUpdatedEventArgs seriesArgs:
                seriesArgs.Reason = reason;
                break;
            case MovieInfoUpdatedEventArgs movieArgs:
                movieArgs.Reason = reason;
                break;
        }
        return eventArgs;
    }

    private bool ShouldSkipStructureEvent(string description) {
        if (LibraryScanWatcher.IsScanRunning) {
            Logger.LogTrace("Library scan is running. Skipping emit of structure event. ({Description})", description);
            return true;
        }
        return false;
    }

    private void OnGroupUpdated(GroupInfoUpdatedEventArgs eventArgs, UpdateReason reason) {
        Logger.LogDebug("Group event with {UpdateReason}. (Group={GroupId})", reason, eventArgs.GroupId);
        if (ShouldSkipStructureEvent($"Group={eventArgs.GroupId}"))
            return;
        Events.AddGroupEvent(eventArgs.GroupId, reason);
    }

    private void OnSeriesMoved(SeriesMovedEventArgs eventArgs) {
        Logger.LogDebug("Series moved to another group. (Series={SeriesId},OldGroup={OldGroupId},NewGroup={NewGroupId})", eventArgs.SeriesId, eventArgs.OldGroupId, eventArgs.NewGroupId);
        if (ShouldSkipStructureEvent($"Series={eventArgs.SeriesId}"))
            return;
        Events.AddSeriesMovedEvent(eventArgs.SeriesId, eventArgs.OldGroupId, eventArgs.NewGroupId);
    }

    private void OnGroupsRecreated() {
        Logger.LogInformation("Shoko Server recreated every group.");
        if (ShouldSkipStructureEvent("GroupsRecreated"))
            return;
        Events.AddGroupsRecreatedEvent();
    }

    private void OnLinksChanged(MetadataLinksChangedEventArgs eventArgs) {
        Logger.LogDebug("Provider links changed. (Series={SeriesIds})", eventArgs.SeriesIds);
        if (ShouldSkipStructureEvent("LinksChanged"))
            return;
        Events.AddSeriesStructureEvent(eventArgs.SeriesIds, "Links changed");
    }

    private void OnEpisodeHiddenChanged(EpisodeHiddenChangedEventArgs eventArgs) {
        Logger.LogDebug("Episode hidden changed. (Episode={EpisodeId},IsHidden={IsHidden})", eventArgs.ShokoEpisodeId, eventArgs.IsHidden);
        if (eventArgs.ShokoEpisodeId is not > 0 || ShouldSkipStructureEvent($"Episode={eventArgs.ShokoEpisodeId}"))
            return;
        Events.AddEpisodeStructureEvent(eventArgs.ShokoEpisodeId.Value, eventArgs.IsHidden ? "Episode hidden" : "Episode shown");
    }

    private void OnSeriesCustomTagsChanged(SeriesCustomTagsChangedEventArgs eventArgs) {
        Logger.LogDebug("Series custom tags changed. (Series={SeriesId})", eventArgs.SeriesId);
        if (ShouldSkipStructureEvent($"Series={eventArgs.SeriesId}"))
            return;
        Events.AddSeriesStructureEvent([eventArgs.SeriesId], "Custom tags changed");
    }

    #endregion

    #endregion
}
