using System.Net.Http.Headers;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Subtitles;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.AssrtSubtitles;

/// <summary>
/// Registers services required by the plugin.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <summary>
    /// Name used for the shared HTTP client.
    /// </summary>
    public const string HttpClientName = "AssrtSubtitles";

    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient(HttpClientName, client =>
        {
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(
                applicationHost.Name.Replace(' ', '_'),
                applicationHost.ApplicationVersionString));
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(
                "Jellyfin-Plugin-AssrtSubtitles",
                typeof(PluginServiceRegistrator).Assembly.GetName().Version?.ToString() ?? "1.0.0"));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        });

        serviceCollection.AddSingleton<AssrtApiClient>();
        serviceCollection.AddSingleton<ISubtitleProvider, AssrtSubtitleProvider>();
    }
}
