using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;

namespace Jellyfin.Plugin.AssrtSubtitles.Models;

/// <summary>
/// Pure helpers backing the JEV (TypeSafe) ranking of search results.
/// Kept free of side effects so the prompt building and ordering rules can be unit tested without HTTP traffic.
/// </summary>
public static class JevRanker
{
    /// <summary>
    /// Builds the natural language <c>state</c> describing the media the subtitles must match.
    /// </summary>
    public static string BuildState(SubtitleSearchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var isEpisode = request.ContentType == VideoContentType.Episode;
        var title = ResolveRequestTitle(request, isEpisode);

        var builder = new StringBuilder();
        builder.Append(isEpisode ? "找到最匹配剧集的字幕。" : "找到最匹配电影的字幕。");

        if (!string.IsNullOrWhiteSpace(title))
        {
            builder.Append("作品名字是").Append(title.Trim()).Append('。');
        }

        if (request.ProductionYear is int year && year > 0)
        {
            builder.Append("发行年份是").Append(year.ToString(CultureInfo.InvariantCulture)).Append('。');
        }

        if (isEpisode)
        {
            if (request.ParentIndexNumber is int season && season > 0)
            {
                builder.Append("季是第").Append(season.ToString(CultureInfo.InvariantCulture)).Append("季。");
            }

            if (request.IndexNumber is int episode)
            {
                builder.Append("集是第").Append(episode.ToString(CultureInfo.InvariantCulture)).Append("集。");
            }
        }

        var mediaFileName = GetMediaFileName(request);
        if (!string.IsNullOrWhiteSpace(mediaFileName))
        {
            builder.Append("文件是 ").Append(mediaFileName).Append('。');
        }
        else if (!string.IsNullOrWhiteSpace(request.Name))
        {
            builder.Append("文件名是 ").Append(request.Name.Trim()).Append('。');
        }

        return builder.ToString();
    }

    /// <summary>
    /// Builds the choice question asked to JEV for the supplied request.
    /// </summary>
    public static string BuildInstructions(SubtitleSearchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.ContentType == VideoContentType.Episode
            ? "哪一个最有可能是本剧集的字幕。"
            : "哪一个最有可能是本电影的字幕。";
    }

    /// <summary>
    /// Key used for a candidate in the TypeSafe <c>criteria</c> map.
    /// </summary>
    public static string BuildCandidateKey(AssrtSubtitleEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Id.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Human readable name of a search result, mirroring what is shown to the user.
    /// </summary>
    public static string ResolveDisplayName(AssrtSubtitleEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (!string.IsNullOrWhiteSpace(entry.NativeName))
        {
            return entry.NativeName;
        }

        if (!string.IsNullOrWhiteSpace(entry.VideoName))
        {
            return entry.VideoName;
        }

        if (!string.IsNullOrWhiteSpace(entry.Title))
        {
            return entry.Title;
        }

        if (!string.IsNullOrWhiteSpace(entry.FileName))
        {
            return entry.FileName;
        }

        return $"Assrt #{entry.Id}";
    }

    /// <summary>
    /// Orders candidates by JEV probability (descending). Options the model did not score keep their relative
    /// upload-time ordering. When probabilities are unavailable the model's explicit <paramref name="choice"/> wins.
    /// </summary>
    public static IReadOnlyList<AssrtSubtitleEntry> Order(
        IReadOnlyList<AssrtSubtitleEntry> entries,
        IReadOnlyDictionary<string, double>? probabilities,
        string? choice,
        int year)
    {
        ArgumentNullException.ThrowIfNull(entries);

        double Score(AssrtSubtitleEntry entry)
        {
            var key = BuildCandidateKey(entry);
            if (probabilities is { Count: > 0 } && probabilities.TryGetValue(key, out var probability))
            {
                return probability;
            }

            return string.Equals(choice, key, StringComparison.Ordinal) ? 1d : 0d;
        }

        return entries
            .OrderByDescending(Score)
            .ThenBy(entry => UploadYearDistance(entry, year))
            .ThenBy(entry => entry.Id)
            .ToList();
    }

    /// <summary>
    /// Legacy fallback ordering: distance between the upload year and the target year.
    /// Entries without a parsable upload date sort last.
    /// </summary>
    public static int UploadYearDistance(AssrtSubtitleEntry? entry, int year)
    {
        if (entry?.UploadTime is { Length: >= 4 } uploadTime
            && int.TryParse(uploadTime.AsSpan(0, 4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var uploadYear))
        {
            return Math.Abs(uploadYear - year);
        }

        return int.MaxValue;
    }

    private static string? ResolveRequestTitle(SubtitleSearchRequest request, bool isEpisode)
    {
        if (isEpisode && !string.IsNullOrWhiteSpace(request.SeriesName))
        {
            return request.SeriesName;
        }

        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            return request.Name;
        }

        if (!string.IsNullOrWhiteSpace(request.SeriesName))
        {
            return request.SeriesName;
        }

        return null;
    }

    private static string? GetMediaFileName(SubtitleSearchRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.MediaPath))
        {
            return null;
        }

        var fileName = Path.GetFileName(request.MediaPath.Trim());
        return string.IsNullOrWhiteSpace(fileName) ? null : fileName;
    }
}
