using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AssrtSubtitles.Models;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using Xunit;

namespace Jellyfin.Plugin.AssrtSubtitles.Tests;

public class JevRankerTests
{
    [Fact]
    public void BuildState_ForMovie_IncludesTitleYearAndFileName()
    {
        var request = new SubtitleSearchRequest
        {
            ContentType = VideoContentType.Movie,
            Name = "Captain America: The First Avenger",
            ProductionYear = 2011,
            MediaPath = @"D:\Movies\Captain America The First Avenger (2011) [tmdbid-1771] [WEBDL-1080p]-PiR.mkv"
        };

        var state = JevRanker.BuildState(request);

        Assert.Contains("找到最匹配电影的字幕", state, StringComparison.Ordinal);
        Assert.Contains("作品名字是Captain America: The First Avenger。", state, StringComparison.Ordinal);
        Assert.Contains("发行年份是2011。", state, StringComparison.Ordinal);
        Assert.Contains("文件是 Captain America The First Avenger (2011) [tmdbid-1771] [WEBDL-1080p]-PiR.mkv。", state, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildState_ForEpisode_MatchesOnSeriesNameOnly()
    {
        var request = new SubtitleSearchRequest
        {
            ContentType = VideoContentType.Episode,
            SeriesName = "Severance",
            Name = "Good News About Hell",
            ProductionYear = 2022,
            ParentIndexNumber = 1,
            IndexNumber = 2,
            MediaPath = @"D:\TV\Severance\Season 1\Severance S01E02 Good News About Hell 1080p WEB-DL.mkv"
        };

        var state = JevRanker.BuildState(request);

        Assert.Contains("找到最匹配剧集的字幕", state, StringComparison.Ordinal);
        Assert.Contains("剧名是Severance。", state, StringComparison.Ordinal);
        Assert.Contains("发行年份是2022。", state, StringComparison.Ordinal);
        Assert.Contains("季是第1季。", state, StringComparison.Ordinal);

        // 集数/单集标题/视频文件名都不应进入提示词：具体是哪一集在下载后由归档内文件名挑选
        Assert.DoesNotContain("第2集", state, StringComparison.Ordinal);
        Assert.DoesNotContain("Good News About Hell", state, StringComparison.Ordinal);
        Assert.DoesNotContain("S01E02", state, StringComparison.Ordinal);
        Assert.Equal("哪一个最有可能是本剧的字幕。", JevRanker.BuildInstructions(request));
    }

    [Fact]
    public void BuildState_ForMovieWithoutMediaPath_OmitsFileLine()
    {
        var request = new SubtitleSearchRequest
        {
            ContentType = VideoContentType.Movie,
            Name = "美国队长",
            ProductionYear = 2011
        };

        var state = JevRanker.BuildState(request);

        Assert.Contains("作品名字是美国队长。", state, StringComparison.Ordinal);
        Assert.Contains("发行年份是2011。", state, StringComparison.Ordinal);
        Assert.DoesNotContain("文件", state, StringComparison.Ordinal);
        Assert.Equal("哪一个最有可能是本电影的字幕。", JevRanker.BuildInstructions(request));
    }

    [Fact]
    public void ResolveDisplayName_PrefersNativeNameThenVideoName()
    {
        Assert.Equal("美国队长", JevRanker.ResolveDisplayName(new AssrtSubtitleEntry
        {
            Id = 1,
            NativeName = "美国队长",
            VideoName = "Captain America",
            Title = "Title",
            FileName = "file.srt"
        }));

        Assert.Equal("Captain America", JevRanker.ResolveDisplayName(new AssrtSubtitleEntry
        {
            Id = 2,
            VideoName = "Captain America",
            Title = "Title",
            FileName = "file.srt"
        }));

        Assert.Equal("Assrt #3", JevRanker.ResolveDisplayName(new AssrtSubtitleEntry { Id = 3 }));
    }

    [Fact]
    public void Order_SortsCandidatesByProbabilityDescending()
    {
        var entries = new[]
        {
            new AssrtSubtitleEntry { Id = 719649, UploadTime = "2014-04-01 00:00:00" },
            new AssrtSubtitleEntry { Id = 719640, UploadTime = "2011-07-22 00:00:00" },
            new AssrtSubtitleEntry { Id = 719648, UploadTime = "2016-05-06 00:00:00" }
        };

        var probabilities = new Dictionary<string, double>
        {
            ["719649"] = 0.0,
            ["719640"] = 1.0,
            ["719648"] = 0.0
        };

        var ordered = JevRanker.Order(entries, probabilities, "719640", 2011);

        Assert.Equal(719640, ordered[0].Id);
    }

    [Fact]
    public void Order_PromotesExplicitChoice_WhenProbabilitiesAreMissing()
    {
        var entries = new[]
        {
            new AssrtSubtitleEntry { Id = 10, UploadTime = "2020-01-01 00:00:00" },
            new AssrtSubtitleEntry { Id = 20, UploadTime = "2021-01-01 00:00:00" }
        };

        var ordered = JevRanker.Order(entries, null, "20", 2021);

        Assert.Equal(20, ordered[0].Id);
    }

    [Fact]
    public void Order_FallsBackToUploadYearDistance_WhenProbabilitiesAreUnavailable()
    {
        var entries = new[]
        {
            new AssrtSubtitleEntry { Id = 1, UploadTime = "2025-01-01 00:00:00" },
            new AssrtSubtitleEntry { Id = 2, UploadTime = "2011-01-01 00:00:00" },
            new AssrtSubtitleEntry { Id = 3, UploadTime = null }
        };

        var ordered = JevRanker.Order(entries, null, null, 2011);

        Assert.Equal(new[] { 2, 1, 3 }, ordered.Select(e => e.Id));
    }

    [Fact]
    public void UploadYearDistance_ReturnsMaxValue_WhenUploadTimeIsMissingOrInvalid()
    {
        Assert.Equal(int.MaxValue, JevRanker.UploadYearDistance(new AssrtSubtitleEntry { UploadTime = null }, 2011));
        Assert.Equal(int.MaxValue, JevRanker.UploadYearDistance(new AssrtSubtitleEntry { UploadTime = "ab" }, 2011));
        Assert.Equal(4, JevRanker.UploadYearDistance(new AssrtSubtitleEntry { UploadTime = "2015-06-01 00:00:00" }, 2011));
    }
}
