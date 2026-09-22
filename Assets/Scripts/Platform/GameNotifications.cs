using System;
using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using Unity.Notifications.Android;
#endif
#if UNITY_IOS && !UNITY_EDITOR
using Unity.Notifications.iOS;
#endif

/// <summary>
/// Local notifications. Entirely on-device: nothing is registered with a push service, no token is
/// generated, no network call is made, and nothing about the player leaves the phone. That matters
/// because the store listing and privacy policy both state the app collects and transmits nothing,
/// and this must not quietly make either untrue.
///
/// Three messages, and never more than three outstanding:
///
///   1. DAILY      — tomorrow morning, "today's maze is live".
///   2. STREAK     — tomorrow evening, and ONLY if a streak is actually at risk. This is the one
///                   that works, because it is the only one telling the player they are about to
///                   lose something they already own.
///   3. WIN-BACK   — three days out, naming the exact level and sector they stopped on.
///
/// Everything is cancelled the moment the app opens and rescheduled when it goes to the background,
/// so a notification can never fire at someone who is already playing, and they can never stack up
/// from repeated sessions.
///
/// HOW THEY REACH A CLOSED APP. On Android each message is an AlarmManager alarm owned by the OS,
/// not by our process: the app can be swiped out of recents, the phone can be rebooted (the
/// package's boot receiver re-registers them — "Reschedule on Device Restart" is on in Project
/// Settings > Mobile Notifications) and the alarm still fires. Two things the app cannot fix and
/// should not try to:
///
///   - The alarms are INEXACT on purpose. Exact alarms need SCHEDULE_EXACT_ALARM, which Android 14
///     denies by default and Google Play only permits for alarm-clock and calendar apps. Inexact
///     means "10:00" lands at 10:00 on an awake phone and at the next Doze maintenance window (or
///     the moment the phone is picked up) on one that sat untouched overnight. For a come-back
///     reminder that is the right trade.
///   - Some OEM skins (Xiaomi, Oppo/Realme, Vivo, and Samsung with "Put unused apps to sleep") kill
///     background alarms for apps they consider idle, or treat a swipe-from-recents as a force
///     stop. Nothing in code survives a force stop — Android drops every alarm on purpose. The
///     only remedy is the user excluding the app from battery optimisation.
///
/// PERMISSION. Android 13+ needs POST_NOTIFICATIONS granted at runtime. GameManager asks exactly
/// once, on the first tap of PLAY, and from then on only when the player switches REMINDERS on in
/// Settings. All three messages are scheduled regardless; the OS simply drops them at fire time if
/// permission is missing, so nothing else has to know.
///
/// Design note on restraint: it would be easy to add "you were 2 tiles away!" and a nudge every
/// evening. Three well-timed messages that each say something true is the difference between a
/// reminder and a nuisance, and a player who mutes the app is worth less than one who uninstalls
/// it — at least the uninstall is honest feedback.
/// </summary>
public static class GameNotifications
{
    private const string ChannelId = "sonarfall_default";

    /// <summary>Status-bar glyph, registered in Project Settings > Mobile Notifications > Android.
    /// Without one Android tints the launcher icon into a white blob.</summary>
    private const string SmallIconId = "icon_small";

    // Local hours-of-day the two daily messages aim for.
    private const int MorningHour = 10;   // "today's maze is live"
    private const int EveningHour = 20;   // "your streak ends tonight" — late enough to be urgent
    private const int WinBackDays = 3;

    private static bool _channelReady;

    /// <summary>Set by the REMINDERS long-press so a tester can prove delivery without waiting a day.
    /// Survives the background reschedule; cleared once it has fired.</summary>
    private static DateTime _testFireAt = DateTime.MinValue;

    /// <summary>Player-facing switch, mirrored in Settings alongside sound and haptics.</summary>
    public static bool Enabled
    {
        get => SaveData.NotificationsOn;
        set
        {
            SaveData.NotificationsOn = value;
            if (!value) CancelAll();
        }
    }

    // ---------------------------------------------------------------- permission

    /// <summary>Where the OS stands on letting this app post. Mirrors the package's enum without
    /// leaking it into code that compiles on every platform.</summary>
    public enum Permission
    {
        /// <summary>Editor, desktop, or a platform with no gate: treat as allowed.</summary>
        NotRequired,
        /// <summary>Never asked yet — the system prompt can still be shown.</summary>
        NotAsked,
        Allowed,
        /// <summary>Denied at the prompt (Android still lets us ask a second time).</summary>
        Denied,
        /// <summary>Blocked in system settings, or denied past the point where prompting is possible.
        /// Only Settings can fix this.</summary>
        Blocked,
        /// <summary>A prompt is on screen right now.</summary>
        Pending,
    }

    /// <summary>A permission prompt in flight. Poll <see cref="IsDone"/> from a coroutine — the OS
    /// reports back on its own thread, so nothing here touches Unity objects.</summary>
    public sealed class Request
    {
        internal bool done;
        internal bool prompted;   // true if a system dialog was actually shown
#if UNITY_ANDROID && !UNITY_EDITOR
        internal PermissionRequest inner;
#endif
        public bool IsDone
        {
            get
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                if (!done && inner != null && inner.Status != PermissionStatus.RequestPending) done = true;
#endif
                return done;
            }
        }

        /// <summary>Did a dialog appear? False when the answer was already known, so the caller
        /// can tell "they just said no" from "the OS never asked".</summary>
        public bool Prompted => prompted;
    }

    /// <summary>Current standing with the OS. Cheap enough to call from a Settings refresh.</summary>
    public static Permission Current
    {
        get
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                switch (AndroidNotificationCenter.UserPermissionToPost)
                {
                    case PermissionStatus.Allowed:                    return Permission.Allowed;
                    case PermissionStatus.NotRequested:               return Permission.NotAsked;
                    case PermissionStatus.RequestPending:             return Permission.Pending;
                    case PermissionStatus.NotificationsBlockedForApp: return Permission.Blocked;
                    default:                                          return Permission.Denied;
                }
            }
            catch { return Permission.NotRequired; }
#elif UNITY_IOS && !UNITY_EDITOR
            var s = iOSNotificationCenter.GetNotificationSettings();
            switch (s.AuthorizationStatus)
            {
                case AuthorizationStatus.Authorized:
                case AuthorizationStatus.Provisional:   return Permission.Allowed;
                case AuthorizationStatus.NotDetermined: return Permission.NotAsked;
                default:                                return Permission.Blocked;
            }
#else
            return Permission.NotRequired;
#endif
        }
    }

    /// <summary>True when the OS will actually show our messages.</summary>
    public static bool CanPost
    {
        get
        {
            var p = Current;
            return p == Permission.Allowed || p == Permission.NotRequired;
        }
    }

    /// <summary>True when asking now could still produce a system dialog.</summary>
    public static bool CanPrompt
    {
        get
        {
            var p = Current;
            return p == Permission.NotAsked || p == Permission.Denied;
        }
    }

    /// <summary>
    /// Ask the OS for permission to post. Returns a handle that completes when the player has
    /// answered — immediately if there was nothing to ask. Never throws; every failure path
    /// resolves to a completed request so a caller waiting on it cannot hang.
    ///
    /// Android 13+ only ever shows the dialog twice; after that the request silently resolves as
    /// denied and the only way back is the system settings page (<see cref="OpenSystemSettings"/>).
    /// </summary>
    public static Request BeginPermissionRequest()
    {
        var r = new Request();
        if (!Enabled) { r.done = true; return r; }
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            // The package handles everything version-specific: no dialog below API 33, the
            // "blocked in settings" case on API 24+, and the two-strikes rule on 33+.
            r.inner = new PermissionRequest();
            r.prompted = r.inner.Status == PermissionStatus.RequestPending;
            r.done = !r.prompted;
        }
        catch (Exception e)
        {
            Debug.LogWarning("[Sonarfall] notification permission request failed: " + e.Message);
            r.done = true;
        }
#elif UNITY_IOS && !UNITY_EDITOR
        try
        {
            // registerForRemoteNotifications must stay FALSE: true would fetch an APNs device
            // token, which is precisely the "no token, no push service" the policy promises.
            var op = new AuthorizationRequest(
                AuthorizationOption.Alert | AuthorizationOption.Badge | AuthorizationOption.Sound, false);
            r.prompted = true;
            // iOS reports through a polled object as well; wrap it so the caller sees one shape.
            _iosPending = op; _iosRequest = r;
        }
        catch { r.done = true; }
#else
        r.done = true;
#endif
        return r;
    }

#if UNITY_IOS && !UNITY_EDITOR
    private static AuthorizationRequest _iosPending;
    private static Request _iosRequest;
    /// <summary>iOS has no callback; GameManager's wait loop calls this each frame.</summary>
    public static void PollIOS()
    {
        if (_iosPending != null && _iosPending.IsFinished)
        {
            _iosRequest.done = true;
            _iosPending = null; _iosRequest = null;
        }
    }
#else
    public static void PollIOS() { }
#endif

    /// <summary>Open this app's notification page in the system Settings app. The only remedy
    /// once the OS has stopped prompting.</summary>
    public static void OpenSystemSettings()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try { AndroidNotificationCenter.OpenNotificationSettings(); } catch { }
#elif UNITY_IOS && !UNITY_EDITOR
        iOSNotificationCenter.OpenNotificationSettings();
#else
        Debug.Log("[Sonarfall] (notification) would open system notification settings");
#endif
    }

    // ---------------------------------------------------------------- lifecycle

    /// <summary>Call when the app becomes active. Clears anything pending so nothing fires mid-play.</summary>
    public static void OnAppForeground()
    {
        CancelAll();
        if (_testFireAt != DateTime.MinValue && _testFireAt <= DateTime.Now) _testFireAt = DateTime.MinValue;
    }

    /// <summary>
    /// Call when the app goes to the background or quits. Rebuilds the whole schedule from current
    /// save state, so the messages always describe where the player actually is.
    /// </summary>
    public static void OnAppBackground()
    {
        CancelAll();
        if (!Enabled) return;

        EnsureChannel();

        int level = SaveData.CurrentLevel;
        int streak = SaveData.DayStreak;
        bool playedToday = SaveData.PlayedToday;

        // 1. Daily maze. Only worth sending if they have unlocked it.
        if (SaveData.RunFinished)
        {
            Schedule("Today's maze is live",
                     "One layout, one attempt, and every player in the world gets the same dark.",
                     NextLocal(MorningHour, playedToday ? 1 : 0));
        }

        // 2. Streak at risk — the only message with real stakes, so it gets the prime slot.
        //    Fires the evening of the day the streak would lapse. If they already played today the
        //    streak is safe until tomorrow night; if they have not, it is tonight.
        if (streak >= 2)
        {
            Schedule(streak + "-day streak ends tonight",
                     "Open Sonarfall before midnight to keep it alive.",
                     NextLocal(EveningHour, playedToday ? 1 : 0));
        }

        // 3. Win-back. Names the exact spot they left, which beats any generic "we miss you".
        string sector = GameConfig.SectorName(level);
        Schedule("Level " + level + "  ·  " + sector,
                 "The dark hasn't moved. Neither have you.",
                 DateTime.Now.AddDays(WinBackDays).Date.AddHours(MorningHour));

        // 4. Tester probe, if one is armed. Re-issued here because CancelAll above wiped it.
        if (_testFireAt > DateTime.Now)
            Schedule("Reminders are working",
                     "This is the Sonarfall test reminder. The real ones arrive mornings and evenings.",
                     _testFireAt);
    }

    /// <summary>
    /// Arm a one-off probe <paramref name="seconds"/> from now. It is only ever delivered by
    /// <see cref="OnAppBackground"/> — the tester has to leave the app — which is exactly the case
    /// being tested: a reminder reaching a phone whose Sonarfall is closed.
    /// </summary>
    public static void ScheduleTest(float seconds)
    {
        _testFireAt = DateTime.Now.AddSeconds(seconds);
        EnsureChannel();
    }

    /// <summary>One line for the Settings banner and logcat: what the OS will let us do.</summary>
    public static string Status
    {
        get
        {
            string p;
            switch (Current)
            {
                case Permission.Allowed:     p = "permission granted"; break;
                case Permission.NotAsked:    p = "permission not asked"; break;
                case Permission.Denied:      p = "permission DENIED (can ask once more)"; break;
                case Permission.Blocked:     p = "BLOCKED in system settings"; break;
                case Permission.Pending:     p = "permission prompt open"; break;
                default:                     p = "no permission needed on this platform"; break;
            }
            return p + " | in-game toggle " + (Enabled ? "on" : "OFF");
        }
    }

    // ---------------------------------------------------------------- internals

    /// <summary>Next local occurrence of <paramref name="hour"/>, at least <paramref name="minDaysAhead"/> away.</summary>
    private static DateTime NextLocal(int hour, int minDaysAhead)
    {
        DateTime t = DateTime.Now.Date.AddDays(minDaysAhead).AddHours(hour);
        if (t <= DateTime.Now.AddMinutes(1)) t = t.AddDays(1);   // never schedule into the past
        return t;
    }

    private static void EnsureChannel()
    {
        if (_channelReady) return;
        _channelReady = true;
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            var channel = new AndroidNotificationChannel
            {
                Id = ChannelId,
                Name = "Reminders",
                Importance = Importance.Default,   // not High: this is a reminder, not an alarm
                Description = "Daily maze and streak reminders",
            };
            AndroidNotificationCenter.RegisterNotificationChannel(channel);
        }
        catch (Exception e)
        {
            _channelReady = false;
            Debug.LogWarning("[Sonarfall] notification channel failed: " + e.Message);
        }
#endif
    }

    private static void Schedule(string title, string body, DateTime when)
    {
        if (when <= DateTime.Now) return;

#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            var n = new AndroidNotification
            {
                Title = title,
                Text = body,
                FireTime = when,
                SmallIcon = SmallIconId,
                LargeIcon = "",
                ShowTimestamp = true,
                ShouldAutoCancel = true,                       // tapping it clears it
                Color = new Color(0.36f, 0.82f, 1f, 1f),       // the game's accent, in the shade
            };
            AndroidNotificationCenter.SendNotification(n, ChannelId);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[Sonarfall] notification schedule failed: " + e.Message);
        }
#elif UNITY_IOS && !UNITY_EDITOR
        var interval = when - DateTime.Now;
        if (interval.TotalSeconds < 1) return;
        var n = new iOSNotification
        {
            Title = title,
            Body = body,
            ShowInForeground = false,
            Trigger = new iOSNotificationTimeIntervalTrigger
            {
                TimeInterval = interval,
                Repeats = false,
            },
        };
        iOSNotificationCenter.ScheduleNotification(n);
#else
        // Editor and desktop: log it so the schedule can be inspected without a device.
        Debug.Log("[Sonarfall] (notification) " + when.ToString("ddd HH:mm") + "  " + title + " — " + body);
#endif
    }

    private static void CancelAll()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            AndroidNotificationCenter.CancelAllScheduledNotifications();
            AndroidNotificationCenter.CancelAllDisplayedNotifications();
        }
        catch { }
#elif UNITY_IOS && !UNITY_EDITOR
        iOSNotificationCenter.RemoveAllScheduledNotifications();
        iOSNotificationCenter.RemoveAllDeliveredNotifications();
#endif
    }
}
