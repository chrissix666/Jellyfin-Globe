using System;
using System.Linq;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Globe.FileTransformation;

public class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHostedService<TransformationRegistration>();
        serviceCollection.AddHostedService<GlobeService>();
    }
}

/// <summary>
/// Jellyfin Web loads no plugin scripts on its own, so the globe button script is added to index.html through the
/// File Transformation plugin. It is found by reflection, so Globe has no hard dependency on it.
/// </summary>
public class TransformationRegistration : IHostedService
{
    private readonly ILogger<TransformationRegistration> _logger;

    public TransformationRegistration(ILogger<TransformationRegistration> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var assemblies = AssemblyLoadContext.All.SelectMany(context => context.Assemblies).ToList();
            var register = assemblies
                .FirstOrDefault(a => a.GetName().Name == "Jellyfin.Plugin.FileTransformation")
                ?.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface")
                ?.GetMethod("RegisterTransformation");
            var parse = assemblies
                .FirstOrDefault(a => a.GetName().Name == "Newtonsoft.Json")
                ?.GetType("Newtonsoft.Json.Linq.JObject")
                ?.GetMethod("Parse", new[] { typeof(string) });
            if (register is null || parse is null)
            {
                _logger.LogWarning("Globe: File Transformation is not installed, the globe button will not appear");
                return Task.CompletedTask;
            }

            var payload = JsonSerializer.Serialize(new
            {
                id = Plugin.Instance!.Id,
                fileNamePattern = "index.html",
                callbackAssembly = typeof(IndexHtml).Assembly.FullName,
                callbackClass = typeof(IndexHtml).FullName,
                callbackMethod = nameof(IndexHtml.Transform)
            });
            register.Invoke(null, new[] { parse.Invoke(null, new object?[] { payload }) });
            _logger.LogInformation("Globe: index.html transformation registered");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Globe: registering the index.html transformation failed");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public static class IndexHtml
{
    // Relative to /web/index.html, so it also works when the server runs under a base URL.
    public const string ScriptTag = "<script defer src=\"../Globe/script.js\"></script>";

    /// <summary>
    /// Called by File Transformation with a JObject; returns the file with the script tag before its one "&lt;/body&gt;".
    /// Anything else (other files that match the pattern, a page that already has the tag) comes back unchanged.
    /// </summary>
    public static string Transform(object payload)
    {
        // JObject has no "contents" property, only the string indexer.
        var contents = payload.GetType().GetProperty("Item", new[] { typeof(string) })
            ?.GetValue(payload, new object[] { "contents" })?.ToString()
            ?? throw new InvalidOperationException("Globe: File Transformation handed no readable contents");

        var body = contents.IndexOf("</body>", StringComparison.Ordinal);
        if (body < 0 || contents.IndexOf("</body>", body + 1, StringComparison.Ordinal) >= 0 || contents.Contains(ScriptTag, StringComparison.Ordinal))
        {
            return contents;
        }

        return contents.Insert(body, ScriptTag);
    }
}
