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

    /// <summary>
    /// Gets or sets the bearer token used to call the TypeSafe evaluation API (https://docs.typesafe.ai/api).
    /// When empty the provider falls back to ordering search results by upload date.
    /// </summary>
    public string TypeSafeApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the TypeSafe evaluation endpoint. Keep empty to use https://api.typesafe.ai/v1/systemone.
    /// </summary>
    public string TypeSafeApiUrl { get; set; } = "https://api.typesafe.ai/v1/systemone";
}
