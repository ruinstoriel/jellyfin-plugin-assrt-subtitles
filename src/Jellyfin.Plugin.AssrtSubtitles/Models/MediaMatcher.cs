using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

public static class MediaMatcher
{
    /// <summary>
    /// 计算媒体文本的综合相似度评分 (0.0 - 1.0)
    /// </summary>
    public static double CalculateSimilarity(string source, string target)
    {
        // 1. 前置防空检查
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
            return 0.0;

        // 2. 文本归一化清洗
        string cleanSource = CleanMediaString(source);
        string cleanTarget = CleanMediaString(target);

        if (cleanSource == cleanTarget) return 1.0;
        if (cleanSource.Length == 0 || cleanTarget.Length == 0) return 0.0;

        // 3. 分别计算编辑距离和 Dice 系数
        double levScore = GetLevenshteinSimilarity(cleanSource, cleanTarget);
        double diceScore = GetDiceCoefficient(cleanSource, cleanTarget);

        // 4. 权重融合：Dice 系数对抗乱序表现更好，给予 60% 权重；编辑距离给予 40% 权重
        return (diceScore * 0.6) + (levScore * 0.4);
    }

    /// <summary>
    /// 媒体文本清洗：转小写、去标点、去空格、去特殊后缀
    /// </summary>
    private static string CleanMediaString(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        // 转小写
        string result = input.ToLowerInvariant();

        // 移除常见的媒体后缀/噪声（可根据需要扩展，如 bluray, 1080p 等）
        result = Regex.Replace(result, @"\.(mp4|mkv|avi|ass|srt|m2ts)$", "");
        result = Regex.Replace(result, @"\b(bluray|x264|x265|1080p|720p|4k|2160p|h264|hevc)\b", "");

        // 只保留字母、数字和中文字符，移除所有空格及标点
        result = Regex.Replace(result, @"[^\w\u4e00-\u9fa5]", "");

        return result;
    }

    /// <summary>
    /// 基于 Levenshtein 距离的相似度 (0.0 - 1.0)
    /// </summary>
    private static double GetLevenshteinSimilarity(string s, string t)
    {
        int n = s.Length;
        int m = t.Length;
        if (n == 0) return 0.0;
        if (m == 0) return 0.0;

        int[,] d = new int[n + 1, m + 1];

        for (int i = 0; i <= n; d[i, 0] = i++) { }
        for (int j = 0; j <= m; d[0, j] = j++) { }

        for (int i = 1; i <= n; i++)
        {
            for (int j = 1; j <= m; j++)
            {
                int cost = (t[j - 1] == s[i - 1]) ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        }

        int maxLen = Math.Max(n, m);
        return 1.0 - ((double)d[n, m] / maxLen);
    }

    /// <summary>
    /// 基于 Srensen-Dice Coefficient 的双字匹配相似度 (0.0 - 1.0)
    /// </summary>
    private static double GetDiceCoefficient(string s, string t)
    {
        var sBigrams = GetBigrams(s);
        var tBigrams = GetBigrams(t);

        if (sBigrams.Count == 0 || tBigrams.Count == 0) return 0.0;

        int intersection = 0;

        // 寻找交集（考虑重复项）
        var tCopy = new List<string>(tBigrams);
        foreach (var bigram in sBigrams)
        {
            if (tCopy.Remove(bigram))
            {
                intersection++;
            }
        }

        return (2.0 * intersection) / (sBigrams.Count + tBigrams.Count);
    }

    /// <summary>
    /// 生成字符串的 Bigrams (两字切片)
    /// </summary>
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