using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using HarmonyLib;
using Il2CppTMPro;
using MelonLoader;
using MuvluvMod.Services;
using UnityEngine;

namespace MuvluvMod.Patches;

/// <summary>
/// Replaces interface text with the translations published for the UI tables.
/// </summary>
/// <remarks>
/// The game renders every label, button and menu entry through <see cref="TMP_Text"/>, which is
/// what makes interface text that never reaches the master data hooks translatable. The property
/// setter is patched so the replacement is already in place when the text is measured, and one scan
/// after the tables load refreshes the text that was rendered before they became available. Text the
/// game is still writing, such as a typed dialogue line, is left alone until it settles, so the
/// animation stays intact and the diagnostics keep only finished strings.
/// </remarks>
[HarmonyPatch]
public static class UiTextPatch
{
    private const float InitialRefreshTimeoutSeconds = 30f;

    /// <summary>Number of rendered objects whose writing state is remembered.</summary>
    private const int MaxTrackedStreams = 512;

    /// <summary>Shortest interval between two sweeps for text the game stopped writing.</summary>
    private const float SettleSweepIntervalSeconds = 0.5f;

    private static readonly UiTextSeenLog SeenLog = new();
    private static readonly Dictionary<int, TrackedText> TrackedTexts = new();
    private static readonly List<TrackedText> SettledTexts = new();
    private static int _refreshScheduled;
    private static float _nextSweepTime;
    private static bool _sweeping;

    /// <summary>A rendered object together with the state of the text it is showing.</summary>
    private sealed class TrackedText
    {
        public TMP_Text Target;
        public string Settled;

        public UiTextStreamTracker Tracker { get; } = new();
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(TMP_Text), "set_text")]
    private static void TranslateSetter(TMP_Text __instance, ref string __0)
    {
        if (
            !Config.TranslationEnabled.Value
            || !Config.TranslationUiEnabled.Value
            || string.IsNullOrEmpty(__0)
        )
            return;

        // Cheap gate: text that has no Japanese left is never sent to the tables.
        if (!UiTextResolver.ContainsJapanese(__0))
            return;

        float now = Time.realtimeSinceStartup;
        string original = __0;
        bool animated = TrackAssignment(__instance, original, now);
        SweepSettledText(now);

        // The game is still writing this text; the sweep handles the value it settles on.
        if (animated)
            return;

        var translations = Core.Translations;
        if (
            translations == null
            || !translations.TryTranslateUiText(original, out string translated)
        )
            return;

        ForgetPending(__instance);
        __0 = translated;
    }

    /// <summary>Remembers the value just assigned to one object and reports the value it replaced.</summary>
    /// <returns>
    /// <c>true</c> when the value continues an animation that is still running, which means it must
    /// not be translated.
    /// </returns>
    private static bool TrackAssignment(TMP_Text target, string text, float now)
    {
        if (target == null)
            return false;

        TrackedText tracked = GetTrackedText(target);
        string settled = tracked.Tracker.Observe(text, now, out bool animated);
        if (settled != null)
            Report(settled);

        return animated;
    }

    private static TrackedText GetTrackedText(TMP_Text target)
    {
        int id = target.GetInstanceID();
        if (TrackedTexts.TryGetValue(id, out TrackedText tracked))
            return tracked;

        if (TrackedTexts.Count >= MaxTrackedStreams)
            ForgetAllTrackedTexts();

        tracked = new TrackedText { Target = target };
        TrackedTexts[id] = tracked;
        return tracked;
    }

    /// <summary>Drops the writing state once too many objects have been seen.</summary>
    private static void ForgetAllTrackedTexts()
    {
        foreach (TrackedText tracked in TrackedTexts.Values)
        {
            string pending = tracked.Tracker.TakePending();
            if (pending != null)
                Report(pending);
        }

        TrackedTexts.Clear();
    }

    /// <summary>Keeps a handled value out of the diagnostics.</summary>
    private static void ForgetPending(TMP_Text target)
    {
        if (
            target != null
            && TrackedTexts.TryGetValue(target.GetInstanceID(), out TrackedText tracked)
        )
            tracked.Tracker.ClearPending();
    }

    /// <summary>
    /// Translates or reports text the game stopped writing, such as the value a typed label settles
    /// on.
    /// </summary>
    private static void SweepSettledText(float now)
    {
        if (_sweeping || TrackedTexts.Count == 0 || now < _nextSweepTime)
            return;

        _nextSweepTime = now + SettleSweepIntervalSeconds;
        _sweeping = true;
        try
        {
            // Collect first: assigning text re-enters the setter, which must not walk the table again.
            SettledTexts.Clear();
            foreach (TrackedText tracked in TrackedTexts.Values)
            {
                string settled = tracked.Tracker.TakeSettled(now);
                if (settled == null)
                    continue;

                tracked.Settled = settled;
                SettledTexts.Add(tracked);
            }

            foreach (TrackedText tracked in SettledTexts)
                ApplySettledText(tracked);
        }
        finally
        {
            _sweeping = false;
        }
    }

    private static void ApplySettledText(TrackedText tracked)
    {
        string settled = tracked.Settled;
        tracked.Settled = null;
        if (settled == null)
            return;

        try
        {
            var translations = Core.Translations;
            if (
                translations != null
                && tracked.Target != null
                && tracked.Target.text == settled
                && translations.TryTranslateUiText(settled, out string translated)
            )
            {
                // The label finished animating, so it is safe to replace the value it settled on.
                tracked.Target.text = translated;
                return;
            }
        }
        catch (Exception e)
        {
            Logger.Warn($"UI text settle skipped an object: {e.Message}");
        }

        Report(settled);
    }

    /// <summary>Adds a finished string to the diagnostics when it has no translation yet.</summary>
    private static void Report(string text)
    {
        if (!Config.TranslationUiLogSeenText.Value || !SeenLog.ShouldRecord(text))
            return;

        Logger.Info($"[UI] untranslated text: \"{UiTextSeenLog.Truncate(text)}\"");
    }

    /// <summary>Logs whether the setter hook is installed, so a missing target stays visible.</summary>
    public static void VerifyPatch(string harmonyId)
    {
        var target = AccessTools.Method(typeof(TMP_Text), "set_text");
        bool patched =
            target != null && Harmony.GetPatchInfo(target)?.Owners?.Contains(harmonyId) == true;
        if (patched)
            Logger.Info("UI text setter patch verified: TMPro.TMP_Text.set_text");
        else
            Logger.Warn("UI text setter patch is missing: TMPro.TMP_Text.set_text");
    }

    /// <summary>
    /// Reapplies the tables once to the text that was rendered before they finished loading.
    /// </summary>
    public static void ScheduleInitialRefresh()
    {
        if (Interlocked.Exchange(ref _refreshScheduled, 1) != 0)
            return;

        MelonCoroutines.Start(RefreshRenderedTextCoroutine());
    }

    private static IEnumerator RefreshRenderedTextCoroutine()
    {
        if (!Config.TranslationEnabled.Value || !Config.TranslationUiEnabled.Value)
            yield break;

        float deadline = Time.realtimeSinceStartup + InitialRefreshTimeoutSeconds;
        while (Time.realtimeSinceStartup < deadline)
        {
            var pending = Core.Translations;
            if (pending != null && pending.UiText.StringCount + pending.UiText.TemplateCount > 0)
                break;

            yield return null;
        }

        var texts = Resources.FindObjectsOfTypeAll<TMP_Text>();
        int scanned = 0;
        int replaced = 0;
        foreach (var text in texts)
        {
            if (text == null)
                continue;

            scanned++;

            string current = text.text;
            if (string.IsNullOrEmpty(current) || !UiTextResolver.ContainsJapanese(current))
                continue;

            var translations = Core.Translations;
            if (
                translations == null
                || !translations.TryTranslateUiText(current, out string translated)
            )
                continue;

            try
            {
                // Assigning through the property keeps the game's own layout and font logic intact.
                text.text = translated;
                replaced++;
            }
            catch (Exception e)
            {
                Logger.Warn($"UI text refresh skipped an object: {e.Message}");
            }
        }

        Logger.Info($"UI text refresh finished. Scanned: {scanned}, Replaced: {replaced}");
    }
}
