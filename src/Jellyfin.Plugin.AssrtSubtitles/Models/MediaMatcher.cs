using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

public static class MediaMatcher
{
    public static double CalculateSimilarity(string source, string target)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
            return 0.0;

        // 1. 结构化提取：提取季号 (Season) 和集号 (Episode)
        var sourceMeta = ExtractMediaMeta(source);
        var targetMeta = ExtractMediaMeta(target);

// 只有当“双方都有明确季号”且“季号冲突”时，才判定否决（避免 Movie 被 TV 规则误伤）
if (sourceMeta.Season.HasValue && targetMeta.Season.HasValue)
{
    if (sourceMeta.Season.Value != targetMeta.Season.Value)
        return 0.0; // 确认为 TV 且季号不同，安全归零
}

// 只有当“双方都有明确集号”且“集号冲突”时，才判定否决
if (sourceMeta.Episode.HasValue && targetMeta.Episode.HasValue)
{
    if (sourceMeta.Episode.Value != targetMeta.Episode.Value)
        return 0.0; // 确认为单集且集号不同，安全归零
}

        // 4. 文本清洗（只针对纯标题部分）
        string cleanSource = CleanMediaString(source);
        string cleanTarget = CleanMediaString(target);

        if (cleanSource == cleanTarget) return 1.0;

        // 5. 权重计算：如果季号与集号全对，给予基础保底分 0.5，剩下 0.5 由标题相似度决定
        double textScore = (GetDiceCoefficient(cleanSource, cleanTarget) * 0.6) +
                           (GetLevenshteinSimilarity(cleanSource, cleanTarget) * 0.4);

        double baseScore = 0.0;
        if (sourceMeta.Season == targetMeta.Season && sourceMeta.Episode == targetMeta.Episode && sourceMeta.Season.HasValue)
        {
            baseScore = 0.5; // 季集匹配成功，直接拿到 0.5 基础高分
            return baseScore + (textScore * 0.5);
        }

        return textScore;
    }

    private class MediaMeta
    {
        public int? Season { get; set; }
        public int? Episode { get; set; }
    }

    /// <summary>
    /// 专项提取 SxxExx 或 Sxx / Exx 格式
    /// </summary>
private static MediaMeta ExtractMediaMeta(string input)
{
    var meta = new MediaMeta();

    // 1. 优先提取强特征 S01E02 / S1 E2
    var sExMatch = Regex.Match(input, @"\b[sS](?<season>\d{1,2})\s*[eE](?<episode>\d{1,4})\b");
    if (sExMatch.Success)
    {
        meta.Season = int.Parse(sExMatch.Groups["season"].Value);
        meta.Episode = int.Parse(sExMatch.Groups["episode"].Value);
        return meta;
    }

    // 2. 提取严谨的季号：必须带有 S / Season / 第X季（排除单独数字，防止误伤电影名）
    var sMatch = Regex.Match(input, @"(?:\b[sS]|Season\s*|第)(?<season>\d{1,2})(?:\b|季)");
    if (sMatch.Success)
    {
        meta.Season = int.Parse(sMatch.Groups["season"].Value);
    }

    // 3. 提取严谨的集号：必须带有 E / Ep / Episode / 第X集，或者在连字符后的明确集数
    var eMatch = Regex.Match(input, @"(?:\b[eE]|[eE]p|Episode\s*|第)(?<episode>\d{1,4})(?:\b|集)|-\s*(?<episode>\d{2,3})\s*-");
    if (eMatch.Success)
    {
        meta.Episode = int.Parse(eMatch.Groups["episode"].Value);
    }

    return meta;
}

    private static string CleanMediaString(string input)
    {
        string result = input.ToLowerInvariant();

        // 移除扩展名
        result = Regex.Replace(result, @"\.(mp4|mkv|avi|ass|srt|m2ts)$", "");
        // 移除标签和噪声词 (扩展更多无用词)
        result = Regex.Replace(result, @"\b(bluray|bd|x264|x265|1080p|720p|4k|2160p|h264|hevc|opus|qaac|season pack|v2|10bit)\b", "");
        // 移除 SxxExx 等标识词，避免干扰标题匹配
        result = Regex.Replace(result, @"s\d+e\d+|s\d+|\b\d{2}\b", "");
        // 只留中英文字符
        result = Regex.Replace(result, @"[^\w\u4e00-\u9fa5]", "");

        return result;
    }

    private static double GetLevenshteinSimilarity(string s, string t)
    {
        int n = s.Length, m = t.Length;
        if (n == 0 || m == 0) return 0.0;
        int[,] d = new int[n + 1, m + 1];
        for (int i = 0; i <= n; d[i, 0] = i++) { }
        for (int j = 0; j <= m; d[0, j] = j++) { }
        for (int i = 1; i <= n; i++)
        {
            for (int j = 1; j <= m; j++)
            {
                int cost = (t[j - 1] == s[i - 1]) ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        }
        return 1.0 - ((double)d[n, m] / Math.Max(n, m));
    }

    private static double GetDiceCoefficient(string s, string t)
    {
        var sBigrams = GetBigrams(s);
        var tBigrams = GetBigrams(t);
        if (sBigrams.Count == 0 || tBigrams.Count == 0) return 0.0;
        int intersection = 0;
        var tCopy = new List<string>(tBigrams);
        foreach (var bigram in sBigrams)
        {
            if (tCopy.Remove(bigram)) intersection++;
        }
        return (2.0 * intersection) / (sBigrams.Count + tBigrams.Count);
    }

    private static List<string> GetBigrams(string input)
    {
        var bigrams = new List<string>();
        for (int i = 0; i < input.Length - 1; i++)
        {
            bigrams.Add(input.Substring(i, 2));
        }
        return bigrams;
    }
}