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

        return request.ContentType == VideoContentType.Episode
            ? BuildEpisodeState(request)
            : BuildMovieState(request);
    }

    /// <summary>
    /// 剧集：搜索到的字幕条目几乎都是整季/整剧包，同名条目之间靠集数无法区分，
    /// 具体是哪一集在下载完成后由归档内部的文件名挑选（见 ScoreArchiveEntry）。
    /// 因此这里只描述剧名（和年份、季），刻意不写入集号、单集标题和视频文件名，
    /// 避免 JEV 被集数带偏而选中“同名但不同剧”的结果。
    /// </summary>
    private static string BuildEpisodeState(SubtitleSearchRequest request)
    {
        var builder = new StringBuilder("找到最匹配剧集的字幕。");

        if (!string.IsNullOrWhiteSpace(request.SeriesName))
        {
            builder.Append("剧名是").Append(request.SeriesName.Trim()).Append('。');
        }

        if (request.ProductionYear is int year && year > 0)
        {
            builder.Append("发行年份是").Append(year.ToString(CultureInfo.InvariantCulture)).Append('。');
        }

        if (request.ParentIndexNumber is int season && season > 0)
        {
            builder.Append("季是第").Append(season.ToString(CultureInfo.InvariantCulture)).Append("季。");
        }

        return builder.ToString();
    }

    /// <summary>
    /// 电影：片名 + 年份 + 视频文件名一起给出，便于 JEV 区分同名/重制版本。
    /// </summary>
    private static string BuildMovieState(SubtitleSearchRequest request)
    {
        var builder = new StringBuilder("找到最匹配电影的字幕。");

        var title = !string.IsNullOrWhiteSpace(request.Name) ? request.Name : request.SeriesName;
        if (!string.IsNullOrWhiteSpace(title))
        {
            builder.Append("作品名字是").Append(title.Trim()).Append('。');
        }

        if (request.ProductionYear is int year && year > 0)
        {
            builder.Append("发行年份是").Append(year.ToString(CultureInfo.InvariantCulture)).Append('。');
        }

        var mediaFileName = GetMediaFileName(request);
        if (!string.IsNullOrWhiteSpace(mediaFileName))
        {
            builder.Append("文件是 ").Append(mediaFileName).Append('。');
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
            ? "哪一个最有可能是本剧的字幕。"
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
