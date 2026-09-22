using UnityEngine;
using System.Runtime.InteropServices;

/// <summary>
/// Cross-platform haptics for Android and iOS.
///
/// Six intents, mapped to whatever each platform considers idiomatic:
///
///   Selection  UI tick — buttons, toggles
///   Light      ping fired, wall brushed, star popped, clock ticking
///   Medium     something happened that matters — orb, echo return
///   Heavy      failure, rewind
///   Success    level cleared
///   Wrong      hit a decoy
///
/// ANDROID uses the system Vibrator with VibrationEffect (API 26+). Since Android exposes raw
/// duration and amplitude rather than named effects, the six intents become tuned pulses.
///
/// iOS uses UIFeedbackGenerator through a small native bridge (Assets/Plugins/iOS). These are the
/// same generators the OS uses for its own UI, so the feel is native and Apple's own guidance is
/// simply to call them — they no-op on hardware without a Taptic Engine instead of failing.
///
/// Vibration is a motor, not a speaker: neither platform routes this through the media volume, so
/// it works with the phone muted. What legitimately silences it is the user's own OS setting
/// (iOS "System Haptics", Android's vibration-intensity sliders), which an app cannot and should
/// not override.
///
/// Four traps this implementation exists to avoid, all of them learned the hard way:
///
///  1. MISSING PERMISSION (Android). Reaching the vibrator through AndroidJavaObject is invisible
///     to Unity's manifest scanner, so android.permission.VIBRATE never lands in the APK and every
///     call is dropped silently — no exception, nothing in logcat. AndroidManifestPostProcessor
///     injects it, and Handheld.Vibrate() is referenced below because that IS a pattern the
///     scanner recognises.
///
///  2. PER-CALL JNI ALLOCATION (Android). Building a new AndroidJavaClass + VibrationEffect on
///     every buzz burns JNI local references, which are capped at 512 — the classic "works for a
///     while, then stops". Everything here is built once and replayed; VibrationEffect is
///     immutable, so that is safe.
///
///  3. PULSES TOO SHORT TO FEEL (Android). A 12ms pulse does nothing on a rotary (ERM) motor — the
///     mass barely starts turning before it ends. Devices without amplitude control get a
///     longer, wider-spaced ladder, since duration is then the only thing distinguishing tiers.
///
///  4. FLOODING THE MOTOR (Android). Vibrator.vibrate() does not queue — it CANCELS whatever is
///     playing. Firing faster than a pulse takes to play leaves the motor stuttering on/off,
///     producing nothing perceptible while every log line reports success. Fire() below refuses to
///     interrupt a playing effect unless the new one outranks it.
///
/// Editor and other platforms: no-op.
/// </summary>
public static class Haptics
{
    /// <summary>The six haptic intents, ordered light to heavy.</summary>
    public enum Kind { Selection, Light, Medium, Heavy, Success, Wrong }

    public static bool Enabled = true;

    /// <summary>What the platform layer negotiated at startup — fixed once the vibrator is found.</summary>
    private static string _platformNote = "editor / unsupported platform";

    /// <summary>
    /// The full diagnostic, assembled live on every read.
    ///
    /// This used to be a plain string baked at startup, which made it lie about the two values
    /// that can change AFTER init: the in-game vibration toggle (set later by
    /// SaveData.ApplySettings) and the OS touch-feedback setting (the user can change it while
    /// the app runs). A tester with vibration switched off would feel nothing and read
    /// "in-game toggle on" — sending you hunting for a device fault that did not exist.
    /// </summary>
    public static string Status
    {
        get
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return _platformNote
                 + " | " + TierName(_attrTier)
                 + " | system touch-feedback " + TouchFeedbackSetting()
                 + " | media vibration " + MediaVibrationSetting()
                 + " | in-game toggle " + (Enabled ? "on" : "OFF");
#else
            return _platformNote + " | in-game toggle " + (Enabled ? "on" : "OFF");
#endif
        }
    }

    // Louder events may cut off quieter ones, never the reverse.
    private const int PrioAmbient = 1;   // UI tick, ping, wall brush
    private const int PrioEvent   = 2;   // orb, echo return, countdown
    private const int PrioMajor   = 3;   // clear, fail, rewind, decoy

    private static float _busyUntil = -10f;
    private static int _busyPriority;

    // ---- public API -------------------------------------------------------------------

    /// <summary>Faint tick for UI selection — buttons, toggles.</summary>
    public static void Selection() { Fire(Kind.Selection, PrioAmbient); }

    /// <summary>Light tap: ping fired, wall brushed, star popped, clock ticking down.</summary>
    public static void Light() { Fire(Kind.Light, PrioAmbient); }

    /// <summary>Mid tap: something happened that matters — orb, echo return.</summary>
    public static void Medium() { Fire(Kind.Medium, PrioEvent); }

    /// <summary>The heaviest single thump: failure, rewind.</summary>
    public static void Heavy() { Fire(Kind.Heavy, PrioMajor); }

    /// <summary>Celebratory pattern for a level clear.</summary>
    public static void Success() { Fire(Kind.Success, PrioMajor); }

    /// <summary>Blunt "that was bad" for touching a decoy.</summary>
    public static void Wrong() { Fire(Kind.Wrong, PrioMajor); }

    /// <summary>
    /// Gate every haptic on the motor being free.
    ///
    /// On Android this is essential — a new vibrate() cancels the one still playing, so an
    /// unguarded stream of taps cancels itself into silence. On iOS the system schedules its own
    /// events, but the same spacing keeps rapid-fire feedback from turning into mush.
    /// </summary>
    private static void Fire(Kind kind, int priority)
    {
        if (!Enabled || !PlatformReady) return;

        // Unscaled: the rewind penalty sets timeScale to 0 and still wants feedback.
        float now = Time.unscaledTime;
        if (now < _busyUntil && priority <= _busyPriority) return;

        _busyUntil = now + (DurationMs(kind) + RestMs) * 0.001f;
        _busyPriority = priority;

        PlatformPlay(kind);
    }

    private static void Report()
    {
        Debug.Log("[Sonarfall] Haptics: " + Status);
    }

    /// <summary>
    /// Fire every effect in sequence so a device that feels dead can be told apart from one whose
    /// OS is muting us. Not wired to any button — call from a debug hook when needed.
    /// </summary>
    public static void SelfTest(MonoBehaviour host)
    {
        if (host != null) host.StartCoroutine(SelfTestRoutine());
    }

    private static System.Collections.IEnumerator SelfTestRoutine()
    {
        Selection(); yield return new WaitForSecondsRealtime(0.4f);
        Light();     yield return new WaitForSecondsRealtime(0.4f);
        Medium();    yield return new WaitForSecondsRealtime(0.4f);
        Heavy();     yield return new WaitForSecondsRealtime(0.6f);
        Wrong();     yield return new WaitForSecondsRealtime(0.7f);
        Success();
    }

    // ======================================================================================
    //  ANDROID
    // ======================================================================================
#if UNITY_ANDROID && !UNITY_EDITOR

    // Two ladders, because a large share of Android devices cannot vary vibration strength.
    //
    // WITH amplitude control: short pulses, strength carries the difference.
    // Amplitudes raised roughly 1.35x against the old ladder. USAGE_MEDIA is scaled less
    // generously than USAGE_TOUCH by the OS on many devices (dumpsys vibrator_manager prints the
    // per-class table), and moving gameplay onto MEDIA would otherwise land softer than before.
    private const long MsSelect = 14, MsLight = 22, MsMedium = 34, MsHeavy = 58;
    private const int AmpSelect = 95, AmpLight = 150, AmpMedium = 225, AmpHeavy = 255;
    //
    // WITHOUT it: every pulse plays at full power and the amplitude argument is discarded, so
    // duration is the ONLY thing separating the tiers — spread much wider, and long enough that a
    // rotary motor has time to spin up at all.
    private const long MsSelectFlat = 25, MsLightFlat = 40, MsMediumFlat = 65, MsHeavyFlat = 110;

    private const int DefaultAmplitude = -1;   // VibrationEffect.DEFAULT_AMPLITUDE

    // Total wall-clock length of each waveform, so the guard knows how long to hold off.
    private const long SuccessMs = 305;   // 0+35+60+50+60+100
    private const long WrongMs   = 185;   // 0+55+70+60

    /// <summary>Silence after each effect so consecutive taps read as separate events.</summary>
    private const float RestMs = 45f;

    private static AndroidJavaObject _vibrator;     // held for the app's lifetime, never disposed
    private static AndroidJavaObject _attributes;   // gameplay events  -> USAGE_MEDIA
    private static AndroidJavaObject _attrTouch;    // UI button taps   -> USAGE_TOUCH
    private static AndroidJavaObject _select, _light, _medium, _heavy, _success, _wrong;

    private static bool _ready;
    private static bool _amplitudeControl;
    private static string _vibratorSource = "none";
    private static string _activitySource = "none";
    private static bool _handheldFallback;   // Vibrator unreachable; use Handheld.Vibrate for big beats

    private static bool PlatformReady { get { return _ready || _handheldFallback; } }

    // How the vibration is classified, best first.
    //
    // MEDIA for gameplay, TOUCH only for UI taps — and this split is the whole reason haptics
    // worked on some testers' phones and not others.
    //
    // USAGE_TOUCH is governed by the system "Touch feedback" / "Touch vibration" toggle. That
    // switch is about UI taps, plenty of people turn it off, and some OEM skins ship it off. When
    // it is off, Android silently drops EVERY USAGE_TOUCH vibration: no exception, nothing in
    // logcat, and the app cannot tell. Filing a game's rumble under it therefore made the entire
    // feature depend on an unrelated preference.
    //
    // Android's own wording for USAGE_MEDIA settles it: "media vibrations, such as music, movie,
    // soundtrack, animations, GAMES, or any interactive media that isn't for touch feedback
    // specifically". Gameplay feedback is media; a button press genuinely is touch feedback, so
    // Kind.Selection keeps USAGE_TOUCH and correctly obeys the user's choice there.
    //
    // This was originally TOUCH because `adb shell dumpsys vibrator_manager` showed TOUCH scaling
    // 1.4x against MEDIA's 1.0x on the development handset. That is real, but it traded "slightly
    // stronger where it works" for "completely silent where it doesn't" — a bad trade. The
    // amplitude ladder below is raised to compensate.
    private const int TierVibrationAttributes = 2;   // android.os.VibrationAttributes
    private const int TierAudioAttributes     = 1;   // android.media.AudioAttributes, SONIFICATION
    private const int TierNone                = 0;   // bare vibrate(effect)

    private static int _attrTier = TierNone;

    private static long DurationMs(Kind kind)
    {
        switch (kind)
        {
            case Kind.Selection: return _amplitudeControl ? MsSelect : MsSelectFlat;
            case Kind.Light:     return _amplitudeControl ? MsLight  : MsLightFlat;
            case Kind.Medium:    return _amplitudeControl ? MsMedium : MsMediumFlat;
            case Kind.Heavy:     return _amplitudeControl ? MsHeavy  : MsHeavyFlat;
            case Kind.Success:   return SuccessMs;
            default:             return WrongMs;
        }
    }

    private static AndroidJavaObject EffectFor(Kind kind)
    {
        switch (kind)
        {
            case Kind.Selection: return _select;
            case Kind.Light:     return _light;
            case Kind.Medium:    return _medium;
            case Kind.Heavy:     return _heavy;
            case Kind.Success:   return _success;
            default:             return _wrong;
        }
    }

    /// <summary>Runs once, before the first scene, on Unity's main thread (the JNI-attached one).</summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Init()
    {
        try
        {
            int sdk;
            using (var version = new AndroidJavaClass("android.os.Build$VERSION"))
                sdk = version.GetStatic<int>("SDK_INT");

            var activity = GetActivity();   // NOT disposed — Unity may own it
            if (activity == null) { _platformNote = "no Android activity (" + _activitySource + ")"; Report(); return; }

            // Android 12 deprecated getSystemService("vibrator") in favour of the manager. The
            // old call still works on most builds, but a few hand back a vibrator that ignores
            // amplitude, so prefer the supported route where it exists.
            //
            // Both routes are tried, and — importantly — a vibrator is only accepted if it also
            // reports hasVibrator(). The previous version took the manager's default vibrator on
            // faith and gave up entirely if IT said no, never trying the legacy service. On any
            // device where the manager exposes an odd default (multi-actuator handsets, some
            // foldables) that turned a working motor into "device reports no vibrator".
            if (sdk >= 31)
            {
                using (var mgr = activity.Call<AndroidJavaObject>("getSystemService", "vibrator_manager"))
                    if (mgr != null)
                        _vibrator = AcceptVibrator(mgr.Call<AndroidJavaObject>("getDefaultVibrator"),
                                                   "VibratorManager");
            }
            if (_vibrator == null)
                _vibrator = AcceptVibrator(activity.Call<AndroidJavaObject>("getSystemService", "vibrator"),
                                           "legacy service");

            if (_vibrator == null)
            {
                _platformNote = "no usable vibrator (activity via " + _activitySource + ", sdk " + sdk + ")";
                Report(); return;
            }

            _amplitudeControl = _vibrator.Call<bool>("hasAmplitudeControl");

            // Pick the tier by SDK rather than by catching a failure.
            //
            // vibrate(VibrationEffect, VibrationAttributes) only exists from API 33. Below that it
            // throws, and the old code discovered this by losing the first haptic of every session
            // to an exception before stepping down. Since minSdk is 26, most installs took that
            // path — so ask the OS version instead.
            BuildAttributes(sdk >= 33 ? TierVibrationAttributes : TierAudioAttributes);

            using (var fx = new AndroidJavaClass("android.os.VibrationEffect"))
            {
                if (_amplitudeControl)
                {
                    _select  = fx.CallStatic<AndroidJavaObject>("createOneShot", MsSelect, AmpSelect);
                    _light   = fx.CallStatic<AndroidJavaObject>("createOneShot", MsLight,  AmpLight);
                    _medium  = fx.CallStatic<AndroidJavaObject>("createOneShot", MsMedium, AmpMedium);
                    _heavy   = fx.CallStatic<AndroidJavaObject>("createOneShot", MsHeavy,  AmpHeavy);
                    // Rising three-tap for a clear, blunt double-thud for a mistake.
                    _success = fx.CallStatic<AndroidJavaObject>("createWaveform",
                                   new long[] { 0, 30, 55, 40, 55, 90 }, new int[] { 0, 150, 0, 205, 0, 255 }, -1);
                    _wrong   = fx.CallStatic<AndroidJavaObject>("createWaveform",
                                   new long[] { 0, 45, 60, 50 }, new int[] { 0, 210, 0, 230 }, -1);
                }
                else
                {
                    // DEFAULT_AMPLITUDE is the documented "device decides" value; a number here
                    // would simply be discarded.
                    _select  = fx.CallStatic<AndroidJavaObject>("createOneShot", MsSelectFlat, DefaultAmplitude);
                    _light   = fx.CallStatic<AndroidJavaObject>("createOneShot", MsLightFlat,  DefaultAmplitude);
                    _medium  = fx.CallStatic<AndroidJavaObject>("createOneShot", MsMediumFlat, DefaultAmplitude);
                    _heavy   = fx.CallStatic<AndroidJavaObject>("createOneShot", MsHeavyFlat,  DefaultAmplitude);
                    // On/off overload — rhythm and pulse length stand in for rising amplitude.
                    _success = fx.CallStatic<AndroidJavaObject>("createWaveform",
                                   new long[] { 0, 35, 60, 50, 60, 100 }, -1);
                    _wrong   = fx.CallStatic<AndroidJavaObject>("createWaveform",
                                   new long[] { 0, 55, 70, 60 }, -1);
                }
            }

            _ready = true;
            // Everything a remote tester's report needs to be actionable. Each field has caused a
            // real "haptics don't work" at some point, and they are indistinguishable without it.
            // Only the values fixed at startup. The tier, the OS touch-feedback setting and the
            // in-game toggle are appended live by the Status property, because all three can
            // change after this point.
            _platformNote = "android ok"
                   + " | sdk " + sdk
                   + " | activity via " + _activitySource
                   + " | vibrator via " + _vibratorSource
                   + " | amplitude control " + (_amplitudeControl ? "yes" : "NO");
        }
        catch (System.Exception e)
        {
            // Couldn't reach the Vibrator at all (unexpected OEM shape, blocked reflection...).
            // Fall back rather than going silent.
            _ready = false;
            _handheldFallback = true;
            _platformNote = "android init failed (" + e.Message + ") — Handheld.Vibrate fallback";
        }
        Report();
    }

    /// <summary>
    /// Build the attributes object for a tier, returning false if that tier isn't available.
    /// Constants are read off the Java class rather than hardcoded, so a wrong guess about an
    /// integer value can't silently mis-classify every buzz.
    /// </summary>
    private static bool BuildAttributes(int tier)
    {
        _attributes = null;
        _attrTouch = null;
        _attrTier = TierNone;

        if (tier >= TierVibrationAttributes)
        {
            try
            {
                // Read both constants off the class rather than hardcoding 0x13 / 0x12 — a wrong
                // literal here mis-files every buzz in the game and is invisible at runtime.
                using (var vaClass = new AndroidJavaClass("android.os.VibrationAttributes"))
                {
                    _attributes = MakeAttributes(vaClass.GetStatic<int>("USAGE_MEDIA"));
                    _attrTouch  = MakeAttributes(vaClass.GetStatic<int>("USAGE_TOUCH"));
                }
                if (_attributes == null) { _attrTouch = null; }
                else { _attrTier = TierVibrationAttributes; return true; }
            }
            catch { _attributes = null; _attrTouch = null; }
        }

        if (tier >= TierAudioAttributes)
        {
            try
            {
                // The API 26..32 route: vibrate(VibrationEffect, AudioAttributes).
                //
                // AOSP's VibrationAttributes.Builder.setUsage(AudioAttributes) translates the audio
                // usage into a vibration usage, and the mapping is what matters here:
                //     USAGE_GAME                   -> USAGE_MEDIA
                //     USAGE_MEDIA                  -> USAGE_MEDIA
                //     USAGE_ASSISTANCE_SONIFICATION-> USAGE_TOUCH
                // This tier previously used SONIFICATION for everything, which is why the earlier
                // MEDIA fix changed nothing below Android 13 — the translation put it straight back
                // under the touch-feedback setting. GAME lands on MEDIA, which is what we want.
                using (var aaClass = new AndroidJavaClass("android.media.AudioAttributes"))
                {
                    int usageGame  = aaClass.GetStatic<int>("USAGE_GAME");
                    int usageSonif = aaClass.GetStatic<int>("USAGE_ASSISTANCE_SONIFICATION");
                    int sonifType  = aaClass.GetStatic<int>("CONTENT_TYPE_SONIFICATION");
                    _attributes = MakeAudioAttributes(usageGame,  sonifType);
                    _attrTouch  = MakeAudioAttributes(usageSonif, sonifType);
                }
                if (_attributes != null) { _attrTier = TierAudioAttributes; return true; }
            }
            catch { _attributes = null; _attrTouch = null; }
        }

        return false;
    }

    /// <summary>
    /// Reads Settings.System.HAPTIC_FEEDBACK_ENABLED purely for the log line. It is NOT used to
    /// gate anything — gameplay haptics are filed as media precisely so this setting doesn't
    /// silence them. It is here because "haptics don't work on my phone" is otherwise
    /// undiagnosable remotely, and this one value distinguishes "the app is broken" from "the
    /// tester has touch vibration switched off", which were indistinguishable before.
    /// </summary>
    private static string TouchFeedbackSetting()
    {
        try
        {
            var activity = GetActivity();
            if (activity == null) return "unknown";
            using (var resolver = activity.Call<AndroidJavaObject>("getContentResolver"))
            using (var system = new AndroidJavaClass("android.provider.Settings$System"))
            {
                int v = system.CallStatic<int>("getInt", resolver, "haptic_feedback_enabled", 1);
                return v == 0 ? "OFF" : "on";
            }
        }
        catch { return "unknown"; }
    }

    /// <summary>
    /// The two OS switches that silence USAGE_MEDIA — the class every gameplay buzz is filed
    /// under. Android 13+ exposes a "Media vibration" intensity slider (Samsung, Pixel, Motorola
    /// all surface it under Sounds and vibration) and Android 14 added a master "Use vibration
    /// and haptics" toggle. Either at OFF drops our vibrations with no error, and neither is
    /// visible to the app except by reading the setting back. Log line only, never a gate: a
    /// player who turned media vibration off has made a choice the game must respect.
    /// </summary>
    private static string MediaVibrationSetting()
    {
        try
        {
            var activity = GetActivity();
            if (activity == null) return "unknown";
            using (var resolver = activity.Call<AndroidJavaObject>("getContentResolver"))
            using (var system = new AndroidJavaClass("android.provider.Settings$System"))
            {
                int master = system.CallStatic<int>("getInt", resolver, "vibrate_on", 1);
                if (master == 0) return "MASTER OFF";
                // AOSP key since API 33; OEMs that predate it or rename it report -1 -> "n/a".
                int v = system.CallStatic<int>("getInt", resolver, "media_vibration_intensity", -1);
                if (v < 0) return "n/a";
                return v == 0 ? "OFF" : "intensity " + v;
            }
        }
        catch { return "unknown"; }
    }

    /// <summary>
    /// The current Android Activity. <b>Never Dispose the result.</b>
    ///
    /// This project ships the GameActivity entry point (ProjectSettings androidApplicationEntry: 2,
    /// the Unity 6 default for new projects), where the running activity is
    /// UnityPlayerGameActivity rather than the classic UnityPlayerActivity. The old
    /// `com.unity3d.player.UnityPlayer.currentActivity` static is kept for plugin compatibility but
    /// is not the supported route there and is reported to come back null on some builds — which
    /// would drop the vibrator lookup into the crude Handheld.Vibrate fallback with nothing in the
    /// log to explain it.
    ///
    /// UnityEngine.Android.AndroidApplication.currentActivity is Unity's documented accessor and
    /// is explicitly specified to work with BOTH entry points, so it is tried first. Unity owns
    /// that object, hence the no-Dispose rule; the legacy fallback returns a wrapper we
    /// deliberately keep for the app's lifetime, exactly like _vibrator.
    /// </summary>
    private static AndroidJavaObject GetActivity()
    {
        try
        {
            var a = UnityEngine.Android.AndroidApplication.currentActivity;
            if (a != null) { _activitySource = "AndroidApplication"; return a; }
        }
        catch { /* older Unity, or no such API — fall through */ }

        try
        {
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            {
                var a = player.GetStatic<AndroidJavaObject>("currentActivity");
                if (a != null) { _activitySource = "UnityPlayer.currentActivity"; return a; }
            }
        }
        catch { }

        _activitySource = "NONE";
        return null;
    }

    /// <summary>Return the vibrator only if it exists AND reports a motor; otherwise null so the
    /// caller can try the other route.</summary>
    private static AndroidJavaObject AcceptVibrator(AndroidJavaObject v, string source)
    {
        if (v == null) return null;
        try
        {
            if (!v.Call<bool>("hasVibrator")) return null;
        }
        catch { return null; }
        _vibratorSource = source;
        return v;
    }

    private static AndroidJavaObject MakeAttributes(int usage)
    {
        using (var b = new AndroidJavaObject("android.os.VibrationAttributes$Builder"))
        using (var b1 = b.Call<AndroidJavaObject>("setUsage", usage))
            return b1.Call<AndroidJavaObject>("build");
    }

    private static AndroidJavaObject MakeAudioAttributes(int usage, int contentType)
    {
        using (var b = new AndroidJavaObject("android.media.AudioAttributes$Builder"))
        using (var b1 = b.Call<AndroidJavaObject>("setUsage", usage))
        using (var b2 = b1.Call<AndroidJavaObject>("setContentType", contentType))
            return b2.Call<AndroidJavaObject>("build");
    }

    private static string TierName(int tier)
    {
        if (tier == TierVibrationAttributes) return "VibrationAttributes(MEDIA/TOUCH)";
        if (tier == TierAudioAttributes) return "AudioAttributes(GAME/SONIFICATION)";
        return "no attributes";
    }

    private static void PlatformPlay(Kind kind)
    {
        if (!_ready) { FallbackVibrate(kind); return; }

        var effect = EffectFor(kind);
        if (effect == null) return;

        // A button press IS touch feedback and should honour the user's touch-vibration setting.
        // Everything else is gameplay and must not.
        var attrs = (kind == Kind.Selection && _attrTouch != null) ? _attrTouch : _attributes;

        try
        {
            if (_attrTier != TierNone) _vibrator.Call("vibrate", effect, attrs);
            else                       _vibrator.Call("vibrate", effect);
        }
        catch (System.Exception e)
        {
            // Building an attributes object proves the CLASS exists, not that this Vibrator exposes
            // a matching vibrate() overload. Step down a tier and retry rather than abandoning
            // classification, which would hand us back to whatever default scaling the OEM applies.
            if (_attrTier != TierNone && BuildAttributes(_attrTier - 1))
            {
                // No note to write: Status reports TierName(_attrTier) live, so the change is
                // already visible. Overwriting the note here used to erase the sdk / activity /
                // vibrator fields — exactly the information needed to diagnose the fallback.
                Report();
                try { _vibrator.Call("vibrate", effect, _attributes); }   // media tier on retry
                catch { BuildAttributes(TierNone); try { _vibrator.Call("vibrate", effect); } catch { _ready = false; } }
            }
            else if (_attrTier != TierNone)
            {
                BuildAttributes(TierNone);
                Report();   // tier is live in Status; see the note above
                try { _vibrator.Call("vibrate", effect); } catch { _ready = false; }
            }
            else
            {
                _ready = false;
                _platformNote = "vibrate failed: " + e.Message;
                Report();
            }
        }

    }

    /// <summary>
    /// Crude whole-device buzz, used only when the Vibrator service could not be obtained at all.
    /// Doubles as the reason Unity's manifest scanner adds VIBRATE — it recognises this API, but
    /// cannot see our JNI calls. AndroidManifestPostProcessor is the actual guarantee; this is the
    /// belt to its braces.
    /// </summary>
    private static void FallbackVibrate(Kind kind)
    {
        // Handheld.Vibrate is a fixed ~500ms buzz, far too blunt for taps — only the big beats.
        if (kind == Kind.Heavy || kind == Kind.Success || kind == Kind.Wrong) Handheld.Vibrate();
    }

    // ======================================================================================
    //  iOS
    // ======================================================================================
#elif UNITY_IOS && !UNITY_EDITOR

    // MarshalAs(I1) is required, not decorative: C#'s default marshalling for bool is the 4-byte
    // Win32 BOOL, while the C++ side returns a 1-byte bool. Without this the runtime reads three
    // bytes of whatever happened to be adjacent, so _ready would be decided by stack garbage.
    [DllImport("__Internal")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool _EchoHapticsInit();
    [DllImport("__Internal")] private static extern void _EchoHapticsImpact(int style);
    [DllImport("__Internal")] private static extern void _EchoHapticsNotification(int type);
    [DllImport("__Internal")] private static extern void _EchoHapticsSelection();

    private static bool _ready;
    private static bool PlatformReady { get { return _ready; } }

    // iOS schedules its own haptic events and UIFeedbackGenerator is built to be called repeatedly
    // (a picker wheel ticks on every notch), so this spacing exists only to stop a sonar sweep from
    // smearing into one continuous rattle — not to protect the motor as on Android.
    private const float RestMs = 20f;

    private static long DurationMs(Kind kind)
    {
        switch (kind)
        {
            case Kind.Selection: return 30;
            case Kind.Light:     return 40;
            case Kind.Medium:    return 55;
            case Kind.Heavy:     return 75;
            case Kind.Success:   return 350;   // the system success pattern is a multi-tap
            default:             return 300;   // ...as is the error pattern
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Init()
    {
        try
        {
            _ready = _EchoHapticsInit();
            _platformNote = _ready
                ? "ios ok (UIFeedbackGenerator)"
                : "ios unavailable (requires iOS 10+)";
        }
        catch (System.Exception e)
        {
            _ready = false;
            _platformNote = "ios init failed: " + e.Message;
        }
        Report();
    }

    private static void PlatformPlay(Kind kind)
    {
        // Map each intent onto the generator Apple intends for it, so the game feels like the rest
        // of the OS rather than inventing its own vocabulary.
        switch (kind)
        {
            case Kind.Selection: _EchoHapticsSelection();      break;  // selectionChanged
            case Kind.Light:     _EchoHapticsImpact(0);        break;  // impact .light
            case Kind.Medium:    _EchoHapticsImpact(1);        break;  // impact .medium
            case Kind.Heavy:     _EchoHapticsImpact(2);        break;  // impact .heavy
            case Kind.Success:   _EchoHapticsNotification(0);  break;  // notification .success
            default:             _EchoHapticsNotification(2);  break;  // notification .error
        }
    }

    // ======================================================================================
    //  EDITOR / EVERYTHING ELSE
    // ======================================================================================
#else

    private const float RestMs = 0f;
    private static bool PlatformReady { get { return false; } }
    private static long DurationMs(Kind kind) { return 0; }
    private static void PlatformPlay(Kind kind) { }

#endif
}
