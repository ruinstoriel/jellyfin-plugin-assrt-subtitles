using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AssrtSubtitles.Configuration;

/// <summary>
/// Plugin configuration for the Assrt subtitle provider.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the API token used to talk to assrt.net.
    /// </summary>
    public string ApiToken { get; set; } = "tNjXZUnOJWcHznHDyalNMYqqP6IdDdpQ";

    /// <summary>
    /// Gets or sets the preferred subtitle languages (ISO 639-3 codes).
    /// </summary>
    public List<string> PreferredLanguages { get; set; } = new() { "zho"};
}
