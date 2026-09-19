using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AssrtSubtitles.Models;

/// <summary>
/// Request body for the TypeSafe evaluation endpoint (https://docs.typesafe.ai/api).
/// </summary>
public class TypeSafeRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "jev-latest";

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("questions")]
    public Dictionary<string, TypeSafeQuestion> Questions { get; set; } = new ();
}

/// <summary>
/// A single typed question sent to TypeSafe.
/// </summary>
public class TypeSafeQuestion
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "choice";

    [JsonPropertyName("instructions")]
    public string? Instructions { get; set; }

    /// <summary>
    /// Gets or sets the map of option to rubric description (a null value means "no extra detail").
    /// </summary>
    [JsonPropertyName("criteria")]
    public Dictionary<string, string?>? Criteria { get; set; }
}

/// <summary>
/// Response body returned by the TypeSafe evaluation endpoint.
/// </summary>
public class TypeSafeResponse
{
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("answers")]
    public Dictionary<string, TypeSafeChoiceAnswer>? Answers { get; set; }
}

/// <summary>
/// Answer of a <c>choice</c> question.
/// </summary>
public class TypeSafeChoiceAnswer
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("choice")]
    public string? Choice { get; set; }

    [JsonPropertyName("confidence")]
    public double? Confidence { get; set; }

    [JsonPropertyName("probabilities")]
    public Dictionary<string, double>? Probabilities { get; set; }
}
