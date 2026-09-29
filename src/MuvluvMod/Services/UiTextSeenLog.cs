using System;
using System.Collections.Generic;

namespace MuvluvMod.Services;

/// <summary>
/// Records interface strings that the tables do not cover yet, so they can be added to the UI
/// translation file. Recording is deduplicated and bounded to keep diagnostics cheap.
/// </summary>
public sealed class UiTextSeenLog
{
    /// <summary>Maximum number of distinct strings that are remembered.</summary>
    public const int Capacity = 4096;

    private const int MaxDisplayLength = 160;

    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public int Count
    {
        get
        {
            lock (_gate)
                return _seen.Count;
        }
    }

    /// <summary>
    /// Returns <c>true</c> the first time a string is observed, and <c>false</c> for repeats or
    /// once the capacity has been reached.
    /// </summary>
    public bool ShouldRecord(string text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        lock (_gate)
        {
            if (_seen.Count >= Capacity)
                return false;

            return _seen.Add(text);
        }
    }

    /// <summary>Shortens a string so a single log line stays readable.</summary>
    public static string Truncate(string text) =>
        string.IsNullOrEmpty(text) || text.Length <= MaxDisplayLength
            ? text
            : text.Substring(0, MaxDisplayLength) + "…";
}
