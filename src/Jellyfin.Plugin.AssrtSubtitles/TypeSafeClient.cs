using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssrtSubtitles.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AssrtSubtitles;

/// <summary>
/// Minimal client for the TypeSafe evaluation endpoint (https://docs.typesafe.ai/api).
/// Used to rank assrt.net search results with the JEV model.
/// </summary>
public class TypeSafeClient
{
    /// <summary>
    /// Model alias. TypeSafe keeps the newest JEV version behind "jev-latest".
    /// </summary>
    public const string DefaultModel = "jev-latest";

    /// <summary>
    /// Default evaluation endpoint.
    /// </summary>
    public const string DefaultEndpoint = "https://api.typesafe.ai/v1/systemone";

    /// <summary>
    /// Question id used for the subtitle ranking question.
    /// </summary>
    public const string RankingQuestionId = "ranking";

    private const int MaxAttempts = 3;

    private static readonly JsonSerializerOptions SerializerOptions = new ()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TypeSafeClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TypeSafeClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Injected HTTP client factory.</param>
    /// <param name="logger">Logger.</param>
    public TypeSafeClient(IHttpClientFactory httpClientFactory, ILogger<TypeSafeClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Asks JEV to pick the most likely candidate for the supplied state.
    /// </summary>
    /// <param name="apiKey">TypeSafe bearer token.</param>
    /// <param name="endpoint">Evaluation endpoint; falls back to <see cref="DefaultEndpoint"/> when empty.</param>
    /// <param name="state">Natural language description of the media the subtitles must match.</param>
    /// <param name="instructions">The choice question to answer.</param>
    /// <param name="criteria">Map of candidate key to candidate description.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The choice answer, or <c>null</c> when the key is missing or the request failed.</returns>
    public async Task<TypeSafeChoiceAnswer?> EvaluateChoiceAsync(
        string apiKey,
        string? endpoint,
        string state,
        string instructions,
        IReadOnlyDictionary<string, string?> criteria,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || criteria is null || criteria.Count == 0)
        {
            return null;
        }

        var request = new TypeSafeRequest
        {
            Model = DefaultModel,
            State = state,
            Questions = new Dictionary<string, TypeSafeQuestion>(StringComparer.Ordinal)
            {
                [RankingQuestionId] = new TypeSafeQuestion
                {
                    Type = "choice",
                    Instructions = instructions,
                    Criteria = new Dictionary<string, string?>(criteria, StringComparer.Ordinal)
                }
            }
        };

        var url = string.IsNullOrWhiteSpace(endpoint) ? DefaultEndpoint : endpoint.Trim();
        var payload = JsonSerializer.SerializeToUtf8Bytes(request, SerializerOptions);

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new ByteArrayContent(payload)
                };
                httpRequest.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                httpRequest.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

                var client = _httpClientFactory.CreateClient(PluginServiceRegistrator.HttpClientName);
                using var response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);

                // 429 Too Many Requests / 529 Overloaded 都是临时错误，按官方建议做指数退避重试
                if (response.StatusCode is HttpStatusCode.TooManyRequests || (int)response.StatusCode == 529)
                {
                    if (attempt < MaxAttempts)
                    {
                        var backoff = TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt - 1));
                        _logger.LogWarning(
                            "TypeSafe returned {StatusCode}; retrying in {DelayMs} ms (attempt {Attempt}/{MaxAttempts})",
                            (int)response.StatusCode,
                            backoff.TotalMilliseconds,
                            attempt,
                            MaxAttempts);
                        await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    _logger.LogWarning("TypeSafe is still rate limited ({StatusCode}) after {MaxAttempts} attempts; skipping JEV ranking", (int)response.StatusCode, MaxAttempts);
                    return null;
                }

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    _logger.LogWarning("TypeSafe rejected the configured API key ({StatusCode}); skipping JEV ranking", (int)response.StatusCode);
                    return null;
                }

                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    _logger.LogWarning("TypeSafe ranking request failed with {StatusCode}: {Body}", (int)response.StatusCode, body);
                    return null;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                var parsed = await JsonSerializer.DeserializeAsync<TypeSafeResponse>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false);
                if (parsed?.Answers is null || !parsed.Answers.TryGetValue(RankingQuestionId, out var answer))
                {
                    _logger.LogWarning("TypeSafe response did not contain an answer for the '{QuestionId}' question", RankingQuestionId);
                    return null;
                }

                if (attempt > 1)
                {
                    _logger.LogInformation("TypeSafe request succeeded on attempt {Attempt}", attempt);
                }

                return answer;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
            {
                if (attempt >= MaxAttempts)
                {
                    _logger.LogError(ex, "TypeSafe ranking request to {Url} failed after {MaxAttempts} attempts", url, MaxAttempts);
                    return null;
                }

                var backoff = TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt - 1));
                _logger.LogWarning(ex, "TypeSafe ranking request failed; retrying in {DelayMs} ms (attempt {Attempt}/{MaxAttempts})", backoff.TotalMilliseconds, attempt, MaxAttempts);
                await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
            }
        }

        return null;
    }
}
