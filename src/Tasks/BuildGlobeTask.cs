using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.Globe.Tasks;

/// <summary>Builds the globes waiting in the queue; a run with an empty queue (its schedule, or a start from the task
/// list) is the automatic update of the globes with "update automatically" ticked.</summary>
public class BuildGlobeTask : IScheduledTask
{
    public string Name => "Update Globes";

    public string Key => "GlobeBuild";

    public string Description => "Builds the globes that wait for it, or updates the globes set to update automatically.";

    public string Category => "Globe";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken) =>
        Task.Run(() => GlobeService.Instance!.RunQueue(progress, cancellationToken), CancellationToken.None);

    // No schedule of its own: the admin sets one in Jellyfin's task list if the globes should update by themselves.
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();
}
