using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.Globe.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Globe;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private readonly string _metadataPath;

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        _metadataPath = applicationPaths is IServerApplicationPaths serverPaths ? serverPaths.InternalMetadataPath : applicationPaths.DataPath;
    }

    public static Plugin? Instance { get; private set; }

    /// <summary>The default place of the globe data: Jellyfin's metadata folder + "Globe".</summary>
    public string DefaultDataFolder => Path.Combine(_metadataPath, "Globe");

    public override string Name => "Globe";

    public override string Description => "Shows the artwork of your libraries on globes you can turn and zoom; a double-click opens the title.";

    public override Guid Id => Guid.Parse("b37f9bfa-5337-424d-adbc-114488b2190d");

    public override string ConfigurationFileName => "Jellyfin.Plugin.Globe.xml";

    public string ResolveDataFolder() =>
        string.IsNullOrWhiteSpace(Configuration.DataFolder) ? DefaultDataFolder : Configuration.DataFolder.Trim();

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
        };
    }
}
