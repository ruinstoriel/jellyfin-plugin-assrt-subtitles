using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssrtSubtitles.Configuration;
using Jellyfin.Plugin.AssrtSubtitles.Models;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;
using SharpCompress.Archives;
using SharpCompress.Common;
using Microsoft.Extensions.Caching.Memory;
using ZstdSharp.Unsafe;
namespace Jellyfin.Plugin.AssrtSubtitles;

/// <summary>
/// Subtitle provider for assrt.net.
/// </summary>
public class AssrtSubtitleProvider : ISubtitleProvider
{
    private static readonly HashSet<string> SubtitleExtensions = new(StringComparer.OrdinalIgnoreCase) { ".srt", ".ass", ".ssa", ".sub", ".vtt" };
    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase) { ".zip", ".rar", ".7z" };

    private static readonly Dictionary<string, string> LanguageKeyMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["langeng"] = "eng",
        ["langen"] = "eng",
        ["langzho"] = "zho",
        ["langchi"] = "zho",
        ["langchs"] = "zho",
        ["langcht"] = "zho",
        ["langdou"] = "zho",
        ["langkor"] = "kor",
        ["langjpn"] = "jpn",
        ["langjap"] = "jpn",
        ["langfre"] = "fra",
        ["langfra"] = "fra",
        ["langspa"] = "spa",
        ["langpor"] = "por",
        ["langpol"] = "pol",
        ["langdut"] = "nld",
        ["langger"] = "deu",
        ["langdeu"] = "deu",
        ["langita"] = "ita",
        ["langrus"] = "rus",
        ["langtha"] = "tha",
        ["langvie"] = "vie",
        ["langukr"] = "ukr"
    };

    private readonly IMemoryCache _queryCache = new MemoryCache(new MemoryCacheOptions());
    private readonly AssrtApiClient _apiClient;

    private readonly ILogger<AssrtSubtitleProvider> _logger;
    private PluginConfiguration _configuration = new ();
    private List<string> _preferredLanguages = new ();

    /// <summary>
    /// Initializes a new instance of the <see cref="AssrtSubtitleProvider"/> class.
    /// </summary>
    public AssrtSubtitleProvider(AssrtApiClient apiClient, ILogger<AssrtSubtitleProvider> logger)
    {
        _apiClient = apiClient;
        _logger = logger;
        Instance = this;
        if (Plugin.Instance?.Configuration is { } config)
        {
            ConfigurationChanged(config);
        }
    }

    /// <summary>
    /// Gets the singleton provider instance.
    /// </summary>
    public static AssrtSubtitleProvider? Instance { get; private set; }

    /// <inheritdoc />
    public string Name => "Assrt.net";

    /// <inheritdoc />
    public IEnumerable<VideoContentType> SupportedMediaTypes => new[] { VideoContentType.Episode, VideoContentType.Movie };

    /// <inheritdoc />
    public async Task<IEnumerable<RemoteSubtitleInfo>> Search(SubtitleSearchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var token = GetApiToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            _logger.LogWarning("Assrt API token not configured; skipping search");
            return Enumerable.Empty<RemoteSubtitleInfo>();
        }

        var query = BuildQuery(request);
        if (string.IsNullOrWhiteSpace(query))
        {
            _logger.LogDebug("Could not build a search query for the incoming request");
            return Enumerable.Empty<RemoteSubtitleInfo>();
        }

        var preferredLanguages = BuildPreferredLanguageList(request);
        var results = await _apiClient.SearchAsync(token, query, cancellationToken).ConfigureAwait(false);
        int year = request.ProductionYear   ?? DateTime.Now.Year;
        return results
        .Where(entry => !string.IsNullOrWhiteSpace(entry.VideoName))
        .Where(entry => entry.FileList is {Count: > 0 and < 100} )
        .OrderBy(entry => 
            {
                // 1. 确保字符串合法且至少有 4 位（能截出年份）
                if (entry.UploadTime is { Length: >= 4 } && int.TryParse(entry.UploadTime[0..4], out int uploadYear))
                {
                    return Math.Abs(uploadYear - year); // 返回与目标年份的绝对距离
                }
                return int.MaxValue; // 解析失败或为空的排到最后面
            })
        .Select(entry => MapToResult(entry, preferredLanguages, request));
    }

    /// <inheritdoc />
    public async Task<SubtitleResponse> GetSubtitles(string id, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Fetching subtitle details for id {SubtitleId}, preferred languages: {PreferredLanguages}", id, string.Join(", ", _preferredLanguages));
        if (!int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var subtitleId))
        {
            throw new ArgumentException("Subtitle id must be numeric", nameof(id));
        }

        var token = GetApiToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("Assrt API token is not configured.");
        }

        var detail = await _apiClient.GetDetailAsync(token, subtitleId, cancellationToken).ConfigureAwait(false);
        if (detail is null)
        {
            throw new FileNotFoundException($"Subtitle {subtitleId} could not be found on assrt.net");
        }

        var preferredLanguages = _preferredLanguages;
        SubtitleSearchRequest request;
        var language = ResolveLanguage(detail.LanguageInfo, preferredLanguages, null);
        var file = SelectFile(detail, preferredLanguages,_queryCache.TryGetValue(subtitleId, out request) ? request : null);
        if (file is null || string.IsNullOrWhiteSpace(file.Url))
        {
            throw new FileNotFoundException($"Subtitle {subtitleId} does not expose downloadable files.");
        }

        var data = await _apiClient.DownloadAsync(file.Url, cancellationToken).ConfigureAwait(false);
        var extension = GetExtension(file.FileName);

        // 默认zip 的 filelist 是解压文件，这里的zip 是 filelist 中的依然是压缩文件，这种情况不多见，但也不是没有，所以优先判断扩展名，如果是压缩包则尝试解压，否则直接当做字幕文件处理
        if (ArchiveExtensions.Contains(extension))
        {

            var (stream, format) = ExtractFromArchive(data, preferredLanguages,_queryCache.TryGetValue(subtitleId, out request) ? request : null);
            if (stream is null)
            {
                throw new InvalidDataException($"Unable to extract a usable subtitle from archive {file.FileName ?? file.Url}");
            }

            return new SubtitleResponse
            {
                Format = format ?? "srt",
                Language = language,
                Stream = stream
            };
        }

        return new SubtitleResponse
        {
            Format = NormalizeFormatFromExtension(extension),
            Language = language,
            Stream = new MemoryStream(data)
        };
    }

    /// <summary>
    /// Updates internal configuration cache.
    /// </summary>
    public void ConfigurationChanged(PluginConfiguration configuration)
    {
        _configuration = configuration;
        _logger.LogInformation("Configuration updated. Preferred languages: {PreferredLanguages}", string.Join(", ", configuration.PreferredLanguages));
        _preferredLanguages = configuration.PreferredLanguages
            .Select(l => l.Trim().ToLowerInvariant())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Distinct()
            .ToList();
    }
    private RemoteSubtitleInfo MapToResult(AssrtSubtitleEntry entry, IReadOnlyList<string> preferredLanguages, SubtitleSearchRequest request)
    {
        var language = ResolveLanguage(entry.LanguageInfo, preferredLanguages, request.Language);
        var name = entry switch
            {
                { NativeName: not (null or "") } => entry.NativeName,
                { VideoName:  not (null or "") } => entry.VideoName,
                { Title:      not (null or "") } => entry.Title,
                { FileName:   not (null or "") } => entry.FileName,
                _                                => $"Assrt #{entry.Id}"
            };
        _logger.LogInformation("Mapping subtitle entry {SubtitleId} with name '{EntryName}' and resolved language '{Language}'", entry.Id, name, language);
        if(ArchiveExtensions.Contains(GetExtension(entry.FileName)) && request.IndexNumber != null)
        {
            _logger.LogInformation("Caching search result for subtitle {SubtitleId} with request index {Index} to improve archive entry selection in GetSubtitles.", entry.Id, request.IndexNumber);
            // 设置缓存策略
            var cacheOptions = new MemoryCacheEntryOptions()
                // 绝对过期时间：从现在起 30 分钟后雷打不动必定过期
                .SetAbsoluteExpiration(TimeSpan.FromMinutes(10))
                // 滑动过期时间（可选）：如果 10 分钟内有人访问过，顺延 10 分钟
                .SetSlidingExpiration(TimeSpan.FromMinutes(5));
            _queryCache.Set(entry.Id, request, cacheOptions);
        }
        string dateStr = (entry.UploadTime != null && entry.UploadTime.Length >= 10) 
                 ? $" [{entry.UploadTime[0..10]}]" 
                 : "";
        
        return new RemoteSubtitleInfo
        {
            Id = entry.Id.ToString(CultureInfo.InvariantCulture),
            ProviderName = Name,
            Name = name+"-"+dateStr, // 添加上传日期到名称，格式为 YYYY-MM-DD
            Comment = entry.LanguageInfo?.Description ?? entry.ReleaseSite,
            Author = entry.ReleaseSite,
            ThreeLetterISOLanguageName = language ?? request.Language,
            DateCreated = ParseDate(entry.UploadTime),
            CommunityRating = entry.VoteScore.HasValue ? (float?)entry.VoteScore.Value : null,
            DownloadCount = entry.DownCount,
            Format = NormalizeFormat(entry.SubType),
            IsHashMatch = false,
            HearingImpaired = false,
            MachineTranslated = false,
            AiTranslated = false,
            Forced = false
        };
    }
    private string? GetApiToken()
    {
        var token = _configuration.ApiToken?.Trim();
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }

    private static string BuildQuery(SubtitleSearchRequest request)
    {
        if (request.ContentType == VideoContentType.Episode
            && !string.IsNullOrWhiteSpace(request.SeriesName)
            && request.ParentIndexNumber.HasValue
            && request.IndexNumber.HasValue)
        {
            return $"{request.SeriesName}";
        }

        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            return request.Name;
        }

        if (!string.IsNullOrWhiteSpace(request.MediaPath))
        {
            return Path.GetFileNameWithoutExtension(request.MediaPath);
        }

        return string.Empty;
    }



    private static DateTime? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
    }

    private IReadOnlyList<string> BuildPreferredLanguageList(SubtitleSearchRequest request)
    {
        var languages = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.Language))
        {
            languages.Add(request.Language.Trim().ToLowerInvariant());
        }

        languages.AddRange(_preferredLanguages);
        return languages.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? ResolveLanguage(AssrtLanguageInfo? languageInfo, IReadOnlyList<string> preferredLanguages, string? fallback)
    {
        var candidates = new List<string>();

        if (languageInfo?.LangList is not null)
        {
            foreach (var lang in languageInfo.LangList.Where(kv => kv.Value))
            {
                if (LanguageKeyMap.TryGetValue(lang.Key, out var mapped))
                {
                    candidates.Add(mapped);
                }
            }
        }

        // Try to guess from description text.
        if (!string.IsNullOrWhiteSpace(languageInfo?.Description))
        {
            var desc = languageInfo.Description.ToLowerInvariant();
            if (desc.Contains("英"))
            {
                candidates.Add("eng");
            }

            if (desc.Contains("中") || desc.Contains("简") || desc.Contains("繁"))
            {
                candidates.Add("zho");
            }

            if (desc.Contains("日"))
            {
                candidates.Add("jpn");
            }

            if (desc.Contains("韩"))
            {
                candidates.Add("kor");
            }
        }

        foreach (var preferred in preferredLanguages)
        {
            var hit = candidates.FirstOrDefault(c => string.Equals(c, preferred, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(hit))
            {
                return hit;
            }
        }

        return candidates.FirstOrDefault() ?? fallback;
    }

    private static string NormalizeFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            return "srt";
        }

        var lower = format.ToLowerInvariant();
        if (lower.Contains("ass"))
        {
            return "ass";
        }

        if (lower.Contains("vobsub"))
        {
            return "sub";
        }

        if (lower.Contains("ssa"))
        {
            return "ssa";
        }

        return lower.Contains("srt", StringComparison.OrdinalIgnoreCase) ? "srt" : lower;
    }

    private static string NormalizeFormatFromExtension(string extension)
    {
        var ext = extension.TrimStart('.').ToLowerInvariant();
        return string.IsNullOrWhiteSpace(ext) ? "srt" : ext;
    }

    private static string GetExtension(string? path)
    {
        return string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetExtension(path);
    }

    private AssrtFileEntry? SelectFile(AssrtSubtitleEntry detail, IReadOnlyList<string> preferredLanguages, SubtitleSearchRequest? request)
    {
        var files = detail.FileList?.Where(f => !string.IsNullOrWhiteSpace(f.Url)).ToList() ?? new List<AssrtFileEntry>();

        if (files.Count == 0 && !string.IsNullOrWhiteSpace(detail.Url))
        {
            files.Add(new AssrtFileEntry
            {
                Url = detail.Url,
                FileName = detail.FileName ?? detail.NativeName ?? $"assrt-{detail.Id}.zip"
            });
        }

        return files
            .OrderByDescending(f => ScoreFile(f, preferredLanguages, request))
            .ThenBy(f => f.FileName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private  int ScoreFile(AssrtFileEntry file, IReadOnlyList<string> preferredLanguages, SubtitleSearchRequest? request)
    {
        var name = file.FileName ?? string.Empty;
        var score = 0;
        var extension = GetExtension(file.FileName);

        if (SubtitleExtensions.Contains(extension))
        {
            score += 10;
        }
        else if (ArchiveExtensions.Contains(extension))
        {
            score += 5;
        }

        if (preferredLanguages.Count > 0)
        {
            if (TryGuessLanguageFromFileName(file.FileName) is { } guessed &&
                preferredLanguages.Any(l => string.Equals(l, guessed, StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogInformation("File {FileName} TryGuessLanguageFromFileName contains preferred language {Language}, increasing score.", name, guessed);
                score += 3;
            }
            // 文件名中有搜索过的索引   Regex.IsMatch(lowered, @"(?<![a-z])chs|chinese|zh|sc|tc", RegexOptions.IgnoreCase)
            if (request?.IndexNumber is int index && Regex.IsMatch(name, index.ToString("D2") + @"(?![a-zA-Z0-9])", RegexOptions.IgnoreCase))
            {
                _logger.LogInformation("File {FileName} contains the episode index {Index}, increasing score.", name, request.IndexNumber);
                score += 3;
            }
            // 仅提取最后一级目录名，例如 "Cyberpunk Edgerunners"
            string folderName = !string.IsNullOrEmpty(request?.MediaPath) 
                ? System.IO.Path.GetFileName(request.MediaPath.TrimEnd('\\', '/')) 
                : string.Empty;

            string content = string.Concat(folderName, request?.SeriesName, request?.Name);

            // 算分
            double finalScore = MediaMatcher.CalculateSimilarity(content, name);
            
            // 通常设定一个阈值（比如 0.6 或 0.75），大于该值判定为匹配成功
            bool isMatch = finalScore > 0.1; // 这个阈值可以根据实际情况调整
            if (isMatch)            {
                _logger.LogInformation("File {FileName} has a media similarity score of {Score:F2} against content '{Content}', which is above the threshold. Increasing score.", name, finalScore, content);
                score += (int)(finalScore * 10); // 根据相似度增加额外分数，最高可增加10分
            }
        }
        _logger.LogInformation("File {FileName} scored {Score} for subtitle selection.", name, score);
        return score;
    }

    private static string? TryGuessLanguageFromFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var lowered = name.ToLowerInvariant();
        if (Regex.IsMatch(lowered, @"(?<![a-z])chs|chinese|zh|sc|tc", RegexOptions.IgnoreCase))
        {
            return "zho";
        }

        if (Regex.IsMatch(lowered, @"(?<![a-z])eng|english|en(?![a-z])", RegexOptions.IgnoreCase))
        {
            return "eng";
        }

        if (Regex.IsMatch(lowered, @"(?<![a-z])jpn|japanese|jp", RegexOptions.IgnoreCase))
        {
            return "jpn";
        }

        if (Regex.IsMatch(lowered, @"(?<![a-z])kor|korean|kr", RegexOptions.IgnoreCase))
        {
            return "kor";
        }

        return null;
    }

    private  (MemoryStream? Stream, string? Format) ExtractFromArchive(byte[] data, IReadOnlyList<string> preferredLanguages, SubtitleSearchRequest? request)
    {
        using var archive = ArchiveFactory.Open(new MemoryStream(data));
        var entries = archive.Entries.Where(entry => !entry.IsDirectory).ToList();
        if (entries.Count == 0)
        {
            return (null, null);
        }

        var selected = entries
            .OrderByDescending(entry => ScoreArchiveEntry(entry.Key ?? string.Empty, preferredLanguages,request?.IndexNumber))
            .ThenBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .First();

        var output = new MemoryStream();
        selected.WriteTo(output);
        output.Position = 0;
        return (output, NormalizeFormatFromExtension(GetExtension(selected.Key)));
    }

    private  int ScoreArchiveEntry(string name, IReadOnlyList<string> preferredLanguages, int? requestIndex)
    {
        var extension = GetExtension(name);
        var score = SubtitleExtensions.Contains(extension) ? 5 : 1;

        if (preferredLanguages.Count > 0)
        {
            // 文件名中有喜好语言
            if (TryGuessLanguageFromFileName(name) is { } guessed &&
                preferredLanguages.Any(l => string.Equals(l, guessed, StringComparison.OrdinalIgnoreCase)))
            {
                score += 2;
            }
            // 文件名中有搜索过的索引
            if(requestIndex != null && (name.Contains($"{requestIndex.Value:D2}", StringComparison.OrdinalIgnoreCase) || name.Contains($"{requestIndex.Value:D2}", StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogInformation("Archive entry {EntryName} contains the episode index {Index}, increasing score.", name, requestIndex);
                score += 4;
            }

        }

        return score;
    }
}
