using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

#if NET9_0_OR_GREATER
using MediaBrowser.Controller.MediaSegments;
#endif

namespace Shokofin;

/// <inheritdoc />
public class PluginServiceRegistrator : IPluginServiceRegistrator {
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost) {
        serviceCollection.AddSingleton<Utils.UsageTracker>();
        serviceCollection.AddSingleton<Utils.LibraryScanWatcher>();
        serviceCollection.AddSingleton<API.ShokoApiClient>();
        serviceCollection.AddSingleton<API.ShokoApiManager>();
        serviceCollection.AddSingleton<API.ShokoIdLookup>();
        serviceCollection.AddSingleton<API.SeasonPostersLookup>();
        serviceCollection.AddSingleton<Configuration.MediaFolderConfigurationService>();
        serviceCollection.AddSingleton<Configuration.SeriesConfigurationService>();
        serviceCollection.AddSingleton<Sync.UserDataSyncManager>();
        serviceCollection.AddSingleton<MergeVersions.MergeVersionsManager>();
        serviceCollection.AddSingleton<Collections.CollectionManager>();
        serviceCollection.AddSingleton<Playlists.PlaylistManager>();
        serviceCollection.AddSingleton<Resolvers.VirtualFileSystemService>();
        serviceCollection.AddSingleton<Events.MetadataRefreshService>();
        serviceCollection.AddSingleton<Events.EventDispatchService>();
        serviceCollection.AddSingleton<SignalR.SignalRConnectionManager>();
        serviceCollection.AddSingleton<Database.UserDataRepositoryService>();
        serviceCollection.AddSingleton<Database.UserDataMigrationService>();
        // Fork: TheIntroDB
        serviceCollection.AddSingleton<MediaSegments.TheIntroDbClient>();
        serviceCollection.AddSingleton<IMediaSegmentProvider, MediaSegments.TheIntroDbSegmentProvider>();
        serviceCollection.AddHostedService<SignalR.SignalREntryPoint>();
        serviceCollection.AddHostedService<Resolvers.ShokoLibraryMonitor>();
        serviceCollection.AddControllers(options => {
            options.Filters.Add<Web.ImageHostUrl>();
            options.Filters.Add<Web.VfsActionFilter>();
        });
    }
}
