using System;

namespace MuvluvMod.Services;

/// <summary>
/// Tracks the text assigned to one rendered object so animation frames are not mistaken for
/// finished labels.
/// </summary>
/// <remarks>
/// The game writes dialogue lines and animated counters through the text setter one step at a time,
/// so every intermediate value is observed. A value that continues the previous one is a frame of
/// the same animation: translating it would corrupt what the game is still writing, and reporting it
/// would bury the finished strings in the diagnostics.
/// </remarks>
public sealed class UiTextStreamTracker
{
    /// <summary>Longest gap between two values that still belongs to one animation.</summary>
    public const double StreamWindowSeconds = 1.0;

    /// <summary>Quiet time after which a value can no longer grow.</summary>
    public const double SettleSeconds = 2.0;

    private string _pending;
    private double _pendingTime;

    /// <summary>The value that has not settled yet, or <c>null</c> when nothing is pending.</summary>
    public string Pending => _pending;

    /// <summary>
    /// Records one assignment and reports how the caller must treat it.
    /// </summary>
    /// <param name="text">The value the game is about to write.</param>
    /// <param name="now">Current time in seconds.</param>
    /// <param name="isAnimationFrame">
    /// <c>true</c> when the value continues the previous one, which means the game is still writing
    /// this text.
    /// </param>
    /// <returns>
    /// The previous value when it can no longer grow, otherwise <c>null</c>. The caller stops waiting
    /// for the returned value and treats it as finished.
    /// </returns>
    public string Observe(string text, double now, out bool isAnimationFrame)
    {
        isAnimationFrame =
            _pending != null
            && now - _pendingTime <= StreamWindowSeconds
            && IsContinuation(_pending, text);

        string settled = isAnimationFrame ? null : _pending;
        _pending = text;
        _pendingTime = now;
        return settled;
    }

    /// <summary>
    /// Returns the pending value once it stayed unchanged for <see cref="SettleSeconds"/>, otherwise
    /// <c>null</c>. This is how text that the game stopped writing becomes observable.
    /// </summary>
    public string TakeSettled(double now) =>
        _pending != null && now - _pendingTime >= SettleSeconds ? TakePending() : null;

    /// <summary>Takes the pending value regardless of how long it has been quiet.</summary>
    public string TakePending()
    {
        string pending = _pending;
        _pending = null;
        return pending;
    }

    /// <summary>Forgets the pending value, for text that is already handled.</summary>
    public void ClearPending() => _pending = null;

    /// <summary>
    /// Returns <c>true</c> when one value is a prefix of the other, which is how typed text and
    /// growing counters appear. Equal values continue the same animation as well.
    /// </summary>
    public static bool IsContinuation(string previous, string current)
    {
        if (string.IsNullOrEmpty(previous) || string.IsNullOrEmpty(current))
            return false;

        return current.StartsWith(previous, StringComparison.Ordinal)
            || previous.StartsWith(current, StringComparison.Ordinal);
    }
}
