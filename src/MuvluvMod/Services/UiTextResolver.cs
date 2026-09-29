using System;
using System.Collections.Generic;
using System.Text;

namespace MuvluvMod.Services;

using UiTranslationTables = Dictionary<string, Dictionary<string, string>>;

/// <summary>
/// Resolves interface text through the tables published by the translation repository.
/// </summary>
/// <remarks>
/// Exact matches are looked up directly. Templates additionally allow a single table entry to
/// cover strings that embed numbers: every digit run is replaced by '#' before the lookup and the
/// original runs are substituted back into the result. Full-width digits become half-width when the
/// translated template has no Japanese characters, so an English entry renders half-width numbers
/// while a Japanese entry keeps the width the game produced. A template that does not have exactly
/// one placeholder per digit run is ignored, so a missing or literal '#' can never produce a partial
/// replacement. Text between '&lt;' and '&gt;' is rich text markup and is never treated as numbers or
/// placeholders, which keeps the game's color codes such as &lt;color=#ff5858&gt; usable inside an
/// entry.
/// </remarks>
public sealed class UiTextResolver
{
    /// <summary>Table holding exact string replacements.</summary>
    public const string StringsTable = "strings";

    /// <summary>Table holding templates whose digit runs are substituted back at runtime.</summary>
    public const string TemplatesTable = "templates";

    private const char Placeholder = '#';
    private const int MaxSubstitutions = 8;

    private readonly IReadOnlyDictionary<string, string> _strings;
    private readonly IReadOnlyDictionary<string, string> _templates;

    public UiTextResolver(
        IReadOnlyDictionary<string, string> strings,
        IReadOnlyDictionary<string, string> templates
    )
    {
        _strings = strings ?? new Dictionary<string, string>();
        _templates = templates ?? new Dictionary<string, string>();
    }

    /// <summary>Resolver that leaves every string unchanged.</summary>
    public static UiTextResolver Empty { get; } =
        new(new Dictionary<string, string>(), new Dictionary<string, string>());

    public int StringCount => _strings.Count;

    public int TemplateCount => _templates.Count;

    /// <summary>
    /// Builds a resolver from the deserialized table file, dropping entries that could not change
    /// anything. Unknown tables are treated as exact string tables, so new categories keep working
    /// without a plugin change.
    /// </summary>
    public static (
        UiTextResolver Resolver,
        int StringCount,
        int TemplateCount,
        int SkippedIdentityCount,
        int SkippedEmptyCount
    ) Create(UiTranslationTables tables)
    {
        var strings = new Dictionary<string, string>();
        var templates = new Dictionary<string, string>();
        int skippedIdentityCount = 0;
        int skippedEmptyCount = 0;

        if (tables != null)
        {
            foreach (var (tableName, entries) in tables)
            {
                if (entries == null)
                    continue;

                var target = string.Equals(tableName, TemplatesTable, StringComparison.Ordinal)
                    ? templates
                    : strings;
                foreach (var (original, translated) in entries)
                {
                    if (string.IsNullOrEmpty(original))
                        continue;

                    if (string.IsNullOrEmpty(translated))
                    {
                        skippedEmptyCount++;
                        continue;
                    }

                    if (string.Equals(original, translated, StringComparison.Ordinal))
                    {
                        skippedIdentityCount++;
                        continue;
                    }

                    target[original] = translated;
                }
            }
        }

        return (
            new UiTextResolver(strings, templates),
            strings.Count,
            templates.Count,
            skippedIdentityCount,
            skippedEmptyCount
        );
    }

    /// <summary>
    /// Returns the translated text for <paramref name="original"/>, or leaves it unchanged when the
    /// tables do not cover the string.
    /// </summary>
    public bool TryResolve(string original, out string translated)
    {
        translated = original;
        if (string.IsNullOrEmpty(original))
            return false;

        if (
            _strings.TryGetValue(original, out string exact)
            && !string.Equals(exact, original, StringComparison.Ordinal)
        )
        {
            translated = exact;
            return true;
        }

        string normalized = NormalizeTemplate(original);
        if (
            !string.Equals(normalized, original, StringComparison.Ordinal)
            && _templates.TryGetValue(normalized, out string template)
            && TrySubstituteDigits(template, original, out string substituted)
        )
        {
            translated = substituted;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Reports whether the string contains Japanese text, which is the cheap gate used before a
    /// lookup so already translated strings never reach the tables.
    /// </summary>
    public static bool ContainsJapanese(string text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        foreach (char current in text)
        {
            if (IsJapanese(current))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Replaces every digit run with a single '#' placeholder, leaving rich text markup alone.
    /// </summary>
    public static string NormalizeTemplate(string text)
    {
        if (string.IsNullOrEmpty(text) || !ContainsDigit(text))
            return text;

        var builder = new StringBuilder(text.Length);
        bool inDigits = false;
        bool inTag = false;
        foreach (char current in text)
        {
            UpdateTagState(current, ref inTag);
            if (!inTag && IsDigit(current))
            {
                if (!inDigits)
                    builder.Append(Placeholder);
                inDigits = true;
                continue;
            }

            inDigits = false;
            builder.Append(current);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Advances the rich text state for one character. Text between '&lt;' and '&gt;' is markup, so
    /// its color codes and any '#' or digit inside them are copied through untouched.
    /// </summary>
    private static void UpdateTagState(char value, ref bool inTag)
    {
        if (value == '<')
            inTag = true;
        else if (inTag && value == '>')
            inTag = false;
    }

    private static bool TrySubstituteDigits(string template, string original, out string result)
    {
        result = null;
        if (string.IsNullOrEmpty(template))
            return false;

        // A translated template without Japanese characters is meant to render as Latin text, so the
        // game's full-width digits become half-width there.
        bool convertDigits = !ContainsJapanese(template);

        var runs = new List<string>(4);
        int runStart = -1;
        bool inTag = false;
        for (int index = 0; index < original.Length; index++)
        {
            char current = original[index];
            UpdateTagState(current, ref inTag);
            if (!inTag && IsDigit(current))
            {
                if (runStart < 0)
                    runStart = index;
                continue;
            }

            if (runStart >= 0)
            {
                runs.Add(original.Substring(runStart, index - runStart));
                runStart = -1;
            }
        }

        if (runStart >= 0)
            runs.Add(original.Substring(runStart));

        if (runs.Count == 0 || runs.Count > MaxSubstitutions)
            return false;

        var builder = new StringBuilder(template.Length + original.Length);
        int runIndex = 0;
        bool inTemplateTag = false;
        foreach (char current in template)
        {
            UpdateTagState(current, ref inTemplateTag);
            if (current != Placeholder || inTemplateTag)
            {
                builder.Append(current);
                continue;
            }

            if (runIndex >= runs.Count)
                return false;

            builder.Append(convertDigits ? ToAsciiDigits(runs[runIndex]) : runs[runIndex]);
            runIndex++;
        }

        if (runIndex != runs.Count)
            return false;

        result = builder.ToString();
        return true;
    }

    private static bool ContainsDigit(string text)
    {
        foreach (char current in text)
        {
            if (IsDigit(current))
                return true;
        }

        return false;
    }

    private static string ToAsciiDigits(string run)
    {
        bool hasFullWidth = false;
        foreach (char current in run)
        {
            if (current >= '\uff10' && current <= '\uff19')
            {
                hasFullWidth = true;
                break;
            }
        }

        if (!hasFullWidth)
            return run;

        var builder = new StringBuilder(run.Length);
        foreach (char current in run)
        {
            bool isFullWidth = current >= '\uff10' && current <= '\uff19';
            builder.Append(isFullWidth ? (char)(current - '\uff10' + '0') : current);
        }

        return builder.ToString();
    }

    private static bool IsDigit(char value) =>
        (value >= '0' && value <= '9') || (value >= '\uff10' && value <= '\uff19');

    private static bool IsJapanese(char value) =>
        (value >= '\u3000' && value <= '\u30ff')
        || (value >= '\u3400' && value <= '\u4dbf')
        || (value >= '\u4e00' && value <= '\u9fff')
        || (value >= '\uf900' && value <= '\ufaff')
        || (value >= '\uff00' && value <= '\uffef');
}
