using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using JarvisUtils.Core;

namespace JarvisUtils.Modules.PlainTextPaste;

public sealed class PlainTextRules
{
    [JsonPropertyName("rules")]
    public List<Rule> Rules { get; set; } = [];

    public sealed class Rule
    {
        [JsonPropertyName("pattern")] public string Pattern { get; set; } = "";
        [JsonPropertyName("replace")] public string Replace { get; set; } = "";
        [JsonPropertyName("desc")] public string? Desc { get; set; }
        [JsonPropertyName("multiline")] public bool Multiline { get; set; }
    }

    public string Apply(string input)
    {
        foreach (var rule in Rules) // применяются последовательно по порядку массива
        {
            if (string.IsNullOrEmpty(rule.Pattern)) continue;
            try
            {
                var options = rule.Multiline ? RegexOptions.Multiline : RegexOptions.None;
                input = Regex.Replace(input, rule.Pattern, rule.Replace, options);
            }
            catch (ArgumentException ex)
            {
                Log.Warn($"Правило '{rule.Desc}': некорректный regex — {ex.Message}");
            }
        }
        return input;
    }

    public static string FilePath => Path.Combine(AppPaths.RulesDir, "plaintext.json");

    public static PlainTextRules Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                Directory.CreateDirectory(AppPaths.RulesDir);
                File.WriteAllText(FilePath, DefaultJson);
                return Parse(DefaultJson);
            }
            return Parse(File.ReadAllText(FilePath));
        }
        catch (Exception ex) { Log.Error($"plaintext.json: {ex.Message}"); return new PlainTextRules(); }
    }

    public static void ResetToDefault()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.RulesDir);
            File.WriteAllText(FilePath, DefaultJson);
        }
        catch (Exception ex) { Log.Error(ex.Message); }
    }

    private static PlainTextRules Parse(string json) =>
        JsonSerializer.Deserialize<PlainTextRules>(json, SettingsStore.JsonOptions) ?? new PlainTextRules();

    // Дефолт ровно из спецификации. Дефисы списков ("- item") не трогаем,
    // URL-паттерны [text](url) не парсим — осознанное решение ТЗ.
    public const string DefaultJson = """
        {
          "rules": [
            { "pattern": "\\*\\*(.+?)\\*\\*", "replace": "$1", "desc": "bold **" },
            { "pattern": "__(.+?)__", "replace": "$1", "desc": "bold __" },
            { "pattern": "(?<!\\*)\\*(?!\\*)(.+?)\\*(?!\\*)", "replace": "$1", "desc": "italic *" },
            { "pattern": "(?<!_)_(?!_)(.+?)_(?!_)", "replace": "$1", "desc": "italic _" },
            { "pattern": "^#{1,6}\\s+", "replace": "", "desc": "headers", "multiline": true },
            { "pattern": "`([^`]+)`", "replace": "$1", "desc": "inline code" },
            { "pattern": "^>\\s?", "replace": "", "desc": "blockquote", "multiline": true },
            { "pattern": "—", "replace": "-", "desc": "em-dash" },
            { "pattern": "–", "replace": "-", "desc": "en-dash" }
          ]
        }
        """;
}