using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AssrtSubtitles.Models;

/// <summary>
/// Root response from assrt.net APIs.
/// </summary>
public class AssrtResponse
{
    [JsonPropertyName("status")]
    public int Status { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("sub")]
    public AssrtSubPayload? Sub { get; set; }
}

/// <summary>
/// Container for the subtitle payload.
/// </summary>
public class AssrtSubPayload
{
    [JsonPropertyName("subs")]
    public List<AssrtSubtitleEntry> Subs { get; set; } = new ();
}

/// <summary>
/// Subtitle entry returned by search/detail endpoints.
/// </summary>
public class AssrtSubtitleEntry
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("native_name")]
    public string? NativeName { get; set; }

    [JsonPropertyName("videoname")]
    public string? VideoName { get; set; }

    [JsonPropertyName("filename")]
    [JsonConverter(typeof(AssrtFilelistConverter))]
    public string? FileName { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("subtype")]
    public string? SubType { get; set; }

    [JsonPropertyName("upload_time")]
    public string? UploadTime { get; set; }

    [JsonPropertyName("vote_score")]
    public double? VoteScore { get; set; }

    [JsonPropertyName("release_site")]
    public string? ReleaseSite { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("size")]
    public long? Size { get; set; }

    [JsonPropertyName("down_count")]
    public int? DownCount { get; set; }

    [JsonPropertyName("view_count")]
    public int? ViewCount { get; set; }

    [JsonPropertyName("lang")]
    public AssrtLanguageInfo? LanguageInfo { get; set; }

    [JsonPropertyName("filelist")]
    public List<AssrtFileEntry>? FileList { get; set; }
}

/// <summary>
/// Language metadata returned by assrt.net.
/// </summary>
public class AssrtLanguageInfo
{
    [JsonPropertyName("langlist")]
    public Dictionary<string, bool>? LangList { get; set; }

    [JsonPropertyName("desc")]
    public string? Description { get; set; }
}

/// <summary>
/// Individual file entry inside a subtitle package.
/// </summary>
public class AssrtFileEntry
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("f")]
    public string? FileName { get; set; }

    [JsonPropertyName("s")]
    public string? Size { get; set; }
}
