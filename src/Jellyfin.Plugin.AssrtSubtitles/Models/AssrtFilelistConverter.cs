using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.AssrtSubtitles.Models;

public class AssrtFilelistConverter : JsonConverter<List<AssrtFileEntry>>
{
    public override List<AssrtFileEntry> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // 1. 如果刚好是标准的数组格式 [ ... ]
        if (reader.TokenType == JsonTokenType.StartArray)
        {
            return JsonSerializer.Deserialize<List<AssrtFileEntry>>(ref reader, options) ?? new List<AssrtFileEntry>();
        }

        // 2. 如果射手网不讲武德，单文件时返回了单个对象 { ... }
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            var singleEntry = JsonSerializer.Deserialize<AssrtFileEntry>(ref reader, options);
            return singleEntry != null ? new List<AssrtFileEntry> { singleEntry } : new List<AssrtFileEntry>();
        }

        // 3. 如果是空字符串或其他奇葩情况 "" 或者 null，直接给个空列表保底，不让程序崩溃
        using (var doc = JsonDocument.ParseValue(ref reader))
        {
            return new List<AssrtFileEntry>();
        }
    }

    public override void Write(Utf8JsonWriter writer, List<AssrtFileEntry> value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value, options);
    }
}