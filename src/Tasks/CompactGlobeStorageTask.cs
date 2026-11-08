using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.Globe.Tasks;

/// <summary>Compacts the image store after globes were deleted; started from the admin page or the task list.</summary>
public class CompactGlobeStorageTask : IScheduledTask
{
    public string Name => "Compact Globe Storage";

    public string Key => "GlobeCompact";

    public string Description => "Frees the disk space of images no globe uses any more.";

    public string Category => "Globe";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken) =>
        Task.Run(() => GlobeService.Instance!.Compact(progress, cancellationToken), CancellationToken.None);

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();
}
