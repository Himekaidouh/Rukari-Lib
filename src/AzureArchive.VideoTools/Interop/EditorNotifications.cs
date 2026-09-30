using System;
using System.Collections.Generic;
using AzureArchive.VideoTools.Core;
using Studio.Scripts;

namespace AzureArchive.VideoTools.Interop;

/// <summary>
/// Bridges Mod diagnostics into the game's own Studio sidebar toast
/// (<c>Studio.Scripts.NotificationManager.Notify</c>). Every path fails
/// open: when the toast carrier is unavailable (Studio scene not loaded,
/// singleton not initialized, interop failure) the caller keeps its own
/// text surface and the visual editor mirrors the message. Main thread only.
/// </summary>
internal static class EditorNotifications
{
    private const int ToastCooldownMilliseconds = 4000;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, long> LastToastTicks =
        new(StringComparer.Ordinal);
    private static string? _playbackErrorKey;
    private static string? _playbackErrorToast;
    private static string? _playbackErrorText;
    private static bool _playbackErrorAnnounced;
    private static string? _playbackNoticeKey;
    private static string? _playbackNoticeText;
    private static bool _carrierFailureLogged;

    public static bool TryToast(string text, string reasonKey)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        long now = Environment.TickCount64;
        lock (Gate)
        {
            if (LastToastTicks.TryGetValue(reasonKey, out long last)
                && now - last < ToastCooldownMilliseconds)
            {
                return false;
            }

            LastToastTicks[reasonKey] = now;
        }

        try
        {
            NotificationManager? manager = InteropMemberAccess.GetStatic<NotificationManager>(
                typeof(Singleton<NotificationManager>),
                "Instance");
            if (!InteropObjectGuard.IsAlive(manager))
            {
                return false;
            }

            manager!.Notify(text);
            return true;
        }
        catch (Exception ex)
        {
            if (!_carrierFailureLogged)
            {
                _carrierFailureLogged = true;
                Plugin.Logger.LogWarning(
                    "Studio notification toast unavailable: " + PatchGuard.Describe(ex));
            }

            return false;
        }
    }

    /// <summary>
    /// Playback-side failure: stored until the visual editor announces it
    /// (the Studio toast carrier usually does not exist during playback).
    /// Re-announces only when the reason key or toast text changes; detail
    /// text updates silently so retry loops with volatile diagnostics never
    /// spam the toast carrier.
    /// </summary>
    public static void ReportPlaybackError(string reasonKey, string toastText, string detailText)
    {
        lock (Gate)
        {
            bool changed =
                !string.Equals(_playbackErrorKey, reasonKey, StringComparison.Ordinal)
                || !string.Equals(_playbackErrorToast, toastText, StringComparison.Ordinal);
            _playbackErrorKey = reasonKey;
            _playbackErrorToast = toastText;
            _playbackErrorText = string.IsNullOrWhiteSpace(detailText)
                ? toastText
                : $"{toastText}\n{detailText}";
            if (!changed)
            {
                return;
            }

            _playbackErrorAnnounced = false;
        }

        TryToast(toastText, "playback-" + reasonKey);
    }

    public static void ClearPlaybackError()
    {
        lock (Gate)
        {
            _playbackErrorKey = null;
            _playbackErrorToast = null;
            _playbackErrorText = null;
            _playbackErrorAnnounced = false;
        }
    }

    /// <summary>
    /// Playback-side notice: a state the user resolves with a normal action
    /// rather than a failure — a project saved but not yet built, above all
    /// (2026-09-18). It is stored on the same surface as the error (the Studio
    /// toast carrier usually does not exist during playback, so the visual
    /// editor is the durable one), but in its own slot: the editor renders it
    /// as information and a real error always outranks it. The toast is
    /// attempted once per distinct notice — re-reporting the same text is
    /// stored but not re-announced, so a save that is repeatedly reclassified
    /// as pending while the user keeps editing does not chatter.
    /// </summary>
    public static void ReportPlaybackNotice(string reasonKey, string noticeText)
    {
        if (string.IsNullOrWhiteSpace(noticeText))
        {
            return;
        }

        lock (Gate)
        {
            bool changed =
                !string.Equals(_playbackNoticeKey, reasonKey, StringComparison.Ordinal)
                || !string.Equals(_playbackNoticeText, noticeText, StringComparison.Ordinal);
            _playbackNoticeKey = reasonKey;
            _playbackNoticeText = noticeText;
            if (!changed)
            {
                return;
            }
        }

        TryToast(noticeText, "playback-notice-" + reasonKey);
    }

    public static void ClearPlaybackNotice()
    {
        lock (Gate)
        {
            _playbackNoticeKey = null;
            _playbackNoticeText = null;
        }
    }

    public static string? PeekPlaybackNoticeText()
    {
        lock (Gate)
        {
            return _playbackNoticeText;
        }
    }

    public static string? PeekPlaybackErrorText()
    {
        lock (Gate)
        {
            return _playbackErrorText;
        }
    }

    /// <summary>Announces a pending playback error once through the toast
    /// carrier; returns true when this call performed the announcement.</summary>
    public static bool AnnouncePendingPlaybackError()
    {
        string key;
        string toast;
        lock (Gate)
        {
            if (_playbackErrorKey == null || _playbackErrorAnnounced)
            {
                return false;
            }

            key = _playbackErrorKey;
            toast = _playbackErrorToast ?? FirstLine(_playbackErrorText ?? string.Empty);
        }

        if (!TryToast(toast, "playback-announce-" + key))
        {
            return false;
        }

        lock (Gate)
        {
            _playbackErrorAnnounced = true;
        }

        return true;
    }

    public static string FirstLine(string text)
    {
        int newline = text.IndexOf('\n');
        string line = newline >= 0 ? text[..newline] : text;
        return line.Length <= 64 ? line : line[..64] + "…";
    }
}
