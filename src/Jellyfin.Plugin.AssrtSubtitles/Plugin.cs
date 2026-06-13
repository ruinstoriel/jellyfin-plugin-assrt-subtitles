using System;
using System.Collections.Generic;
using Jellyfin.Plugin.AssrtSubtitles.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.AssrtSubtitles;

/// <summary>
/// Plugin entry point for the Assrt subtitle provider.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Jellyfin application paths.</param>
    /// <param name="xmlSerializer">Serializer instance.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        ConfigurationChanged += (_, _) => AssrtSubtitleProvider.Instance?.ConfigurationChanged(Configuration);
        // AssrtSubtitleProvider.Instance?.ConfigurationChanged(Configuration);
    }

    /// <summary>
    /// Gets the plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Assrt Subtitles";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("6c94cceb-4ebc-4d5c-bbb6-2d1c9d0c2e40");

    /// <inheritdoc />
    public override string Description => "Search and download subtitles from assrt.net.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        var assemblyNamespace = GetType().Namespace;

        return new[]
        {
            new PluginPageInfo
            {
                Name = "assrt-subtitles",
                EmbeddedResourcePath = $"{assemblyNamespace}.Configuration.configPage.html"
            }
        };
    }
}
