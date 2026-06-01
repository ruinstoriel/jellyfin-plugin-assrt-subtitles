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
        
        return results.Where(entry => !string.IsNullOrWhiteSpace(entry.NativeName)).Select(entry => MapToResult(entry, preferredLanguages, request.Language,request.IndexNumber));
    }

    /// <inheritdoc />
    public async Task<SubtitleResponse> GetSubtitles(string id, CancellationToken cancellationToken)
    {
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
        var language = ResolveLanguage(detail.LanguageInfo, preferredLanguages, null);
        var file = SelectFile(detail, preferredLanguages);
        if (file is null || string.IsNullOrWhiteSpace(file.Url))
        {
            throw new FileNotFoundException($"Subtitle {subtitleId} does not expose downloadable files.");
        }

        var data = await _apiClient.DownloadAsync(file.Url, cancellationToken).ConfigureAwait(false);
        var extension = GetExtension(file.FileName);

        if (ArchiveExtensions.Contains(extension))
        {
            var (stream, format) = ExtractFromArchive(data, preferredLanguages,_queryCache.TryGetValue(subtitleId, out var idx) ? (int?)idx : (int?)null);
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
        _preferredLanguages = configuration.PreferredLanguages
            .Select(l => l.Trim().ToLowerInvariant())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Distinct()
            .ToList();
    }
    private RemoteSubtitleInfo MapToResult(AssrtSubtitleEntry entry, IReadOnlyList<string> preferredLanguages, string? requestLanguage, int? requestIndex)
    {
        var language = ResolveLanguage(entry.LanguageInfo, preferredLanguages, requestLanguage);
        var name = entry.NativeName ?? entry.VideoName ?? entry.Title ?? entry.FileName ?? $"Assrt #{entry.Id}";
        _logger.LogDebug("Mapping subtitle entry {SubtitleId} with name '{EntryName}' and resolved language '{Language}'", entry.Id, name, language);
        if(ArchiveExtensions.Contains(GetExtension(entry.FileName)) && requestIndex != null)
        {
            _logger.LogInformation("Caching search result for subtitle {SubtitleId} with request index {Index} to improve archive entry selection in GetSubtitles.", entry.Id, requestIndex);
            // 设置缓存策略
            var cacheOptions = new MemoryCacheEntryOptions()
                // 绝对过期时间：从现在起 30 分钟后雷打不动必定过期
                .SetAbsoluteExpiration(TimeSpan.FromMinutes(10))
                // 滑动过期时间（可选）：如果 10 分钟内有人访问过，顺延 10 分钟
                .SetSlidingExpiration(TimeSpan.FromMinutes(5));
            _queryCache.Set(entry.Id, requestIndex, cacheOptions);
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
            ThreeLetterISOLanguageName = language ?? requestLanguage,
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

    private AssrtFileEntry? SelectFile(AssrtSubtitleEntry detail, IReadOnlyList<string> preferredLanguages)
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
            .OrderByDescending(f => ScoreFile(f, preferredLanguages))
            .ThenBy(f => f.FileName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static int ScoreFile(AssrtFileEntry file, IReadOnlyList<string> preferredLanguages)
    {
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
                score += 3;
            }
        }

        return score;
    }

    private static string? TryGuessLanguageFromFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var lowered = name.ToLowerInvariant();
        if (Regex.IsMatch(lowered, @"(?<![a-z])chs|chinese|zh", RegexOptions.IgnoreCase))
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

    private  (MemoryStream? Stream, string? Format) ExtractFromArchive(byte[] data, IReadOnlyList<string> preferredLanguages, int? requestIndex)
    {
        using var archive = ArchiveFactory.Open(new MemoryStream(data));
        var entries = archive.Entries.Where(entry => !entry.IsDirectory).ToList();
        if (entries.Count == 0)
        {
            return (null, null);
        }

        var selected = entries
            .OrderByDescending(entry => ScoreArchiveEntry(entry.Key ?? string.Empty, preferredLanguages,requestIndex))
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
