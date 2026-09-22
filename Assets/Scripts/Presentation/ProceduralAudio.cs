using UnityEngine;

/// <summary>
/// All sound is synthesized at runtime with AudioClip.Create — no audio files.
/// Ping = soft sine sweep down (with random pitch), wall tick = tiny blip (pitch rises as the
/// ring sweeps), level clear = a rising arpeggio whose length reflects how cleanly you cleared,
/// plus star lost/gained, rewind, countdown and UI cues.
///
/// Two sources on purpose: gameplay cues share `_main`, while UI clicks get their own so a
/// button press can never cut a gameplay cue off mid-play.
/// </summary>
public class ProceduralAudio : MonoBehaviour
{
    private const int SampleRate = 44100;

    private AudioSource _main;   // ping / clear / star / rewind / countdown
    private AudioSource _tick;   // ticks (own source so they overlap the ping)
    private AudioSource _move;   // looping movement whoosh (volume rides speed)

    private AudioClip _ping, _tickClip, _star, _moveClip, _rewind, _lose, _timeWarn, _countTick;
    private AudioClip _starLost, _starGained, _whoosh, _button, _teach, _exitHit, _wheelTick;
    private AudioClip _praise0, _praise1, _praise2, _praise3, _overcharge;
    private AudioSource _ui;     // own source so UI never cuts off a gameplay cue mid-play
    private float _moveTargetVol, _moveLevel;

    public void Init()
    {
        _main = gameObject.AddComponent<AudioSource>();
        _main.playOnAwake = false; _main.spatialBlend = 0f;

        _tick = gameObject.AddComponent<AudioSource>();
        _tick.playOnAwake = false; _tick.spatialBlend = 0f; _tick.volume = 0.28f;

        _move = gameObject.AddComponent<AudioSource>();
        _move.playOnAwake = false; _move.spatialBlend = 0f; _move.loop = true; _move.volume = 0f;

        _ping     = BuildSweep(880f, 220f, 0.35f, 0.5f);
        _tickClip = BuildSweep(1400f, 1100f, 0.035f, 0.6f);
        _star     = BuildSweep(1200f, 1600f, 0.09f, 0.45f);
        _rewind   = BuildRewind();
        _lose     = BuildArpeggio(new[] { 392f, 311f, 233f }, 0.18f, 0.5f); // descending = "aww, failed"
        _timeWarn = BuildSweep(620f, 620f, 0.18f, 0.45f);                   // steady heads-up beep
        _countTick= BuildSweep(1000f, 1000f, 0.05f, 0.5f);                  // short countdown tick
        _moveClip = BuildMoveLoop();

        _ui = gameObject.AddComponent<AudioSource>();
        _ui.playOnAwake = false; _ui.spatialBlend = 0f; _ui.volume = 0.5f;

        // Losing a life is the harshest thing that happens in a level, so it gets the harshest
        // sound: three notes falling more than an octave.
        _starLost   = BuildArpeggio(new[] { 523.25f, 349.23f, 220f }, 0.10f, 0.55f);
        // Gaining one mirrors it exactly — same three notes, climbing.
        _starGained = BuildArpeggio(new[] { 523.25f, 783.99f, 1046.5f }, 0.09f, 0.5f);
        _whoosh     = BuildSweep(300f, 900f, 0.22f, 0.35f);   // level regenerated
        _button     = BuildSweep(1500f, 1900f, 0.035f, 0.30f); // dry UI click
        _teach      = BuildArpeggio(new[] { 659.25f, 880f }, 0.07f, 0.35f); // card appears
        // Level-wheel detent. Shorter and softer than the button click so a fast flick through
        // thirty levels is a texture rather than thirty separate clicks, and pitched a little above
        // it so the two never read as the same event. Falling, so each notch sounds like it lands.
        _wheelTick  = BuildSweep(2100f, 1650f, 0.022f, 0.22f);

        // Reaching the exit: a C4 bloom that bends into tune. Deliberately still ringing when the
        // praise sting lands ~0.4s later — it is the bass the sting resolves over, not a separate
        // announcement competing with it.
        _exitHit = BuildArrivalBloom(261.63f, 1.5f, 0.6f);

        // Level-clear stings, one per praise tier. All resolve upward on the major triad so the
        // clear always feels like an arrival — the tiers differ in how far they climb, so a
        // flawless run is audibly a bigger deal without being a different piece of music.
        _praise0 = BuildArpeggio(new[] { 523.25f, 659.25f, 783.99f }, 0.10f, 0.5f);
        _praise1 = BuildArpeggio(new[] { 523.25f, 659.25f, 783.99f, 1046.50f }, 0.09f, 0.55f);
        _praise2 = BuildArpeggio(new[] { 523.25f, 659.25f, 783.99f, 1046.50f, 1318.51f, 1567.98f }, 0.075f, 0.6f);
        // Overcharged clear: the same triad an octave up, running all the way to C7. Brighter and
        // higher than any other outcome, so the rarest result is instantly the best-sounding one.
        _praise3 = BuildArpeggio(new[] { 523.25f, 783.99f, 1046.50f, 1318.51f, 1567.98f, 2093.00f, 2637.02f },
                                 0.068f, 0.62f);

        // Picking the fourth star up mid-level. A fast bright rise so it lands as a windfall.
        _overcharge = BuildArpeggio(new[] { 783.99f, 1046.50f, 1318.51f, 1567.98f }, 0.065f, 0.6f);

        _move.clip = _moveClip;
        _move.Play(); // runs continuously at volume 0; SetMoveLevel opens it up
    }

    /// <summary>Time-rewind shimmer for the decoy penalty (kept gentle).</summary>
    public void PlayRewind() { if (_main) { _main.pitch = 1f; _main.PlayOneShot(_rewind, 0.6f); } }

    /// <summary>The moment of touching the exit — a C-major bloom that becomes the bass bed for
    /// the praise sting arriving on top of it.</summary>
    public void PlayExitReached() { if (_main) { _main.pitch = 1f; _main.PlayOneShot(_exitHit, 0.85f); } }

    /// <summary>The level-clear sting. <paramref name="starsLeft"/> picks how far it climbs.</summary>
    public void PlayPraise(int starsLeft)
    {
        if (!_main) return;
        _main.pitch = 1f;
        AudioClip c = starsLeft >= 4 ? _praise3
                    : starsLeft >= 3 ? _praise2
                    : starsLeft >= 2 ? _praise1
                                     : _praise0;
        _main.PlayOneShot(c, starsLeft >= 4 ? 1f : 0.9f);
    }

    /// <summary>The fourth star, taken at full health. The brightest cue in the game.</summary>
    public void PlayOvercharge() { if (_main) { _main.pitch = 1f; _main.PlayOneShot(_overcharge, 0.95f); } }

    /// <summary>A life spent to a decoy. Deliberately the loudest negative cue in the game.</summary>
    public void PlayStarLost() { if (_main) { _main.pitch = 1f; _main.PlayOneShot(_starLost, 0.9f); } }

    /// <summary>A life restored by a bonus echo — the star-lost motif played backwards.</summary>
    public void PlayStarGained() { if (_main) { _main.pitch = 1f; _main.PlayOneShot(_starGained, 0.85f); } }

    /// <summary>Level regenerated (reset button).</summary>
    public void PlayWhoosh() { if (_main) { _main.pitch = 1f; _main.PlayOneShot(_whoosh, 0.5f); } }

    /// <summary>Every UI button. On its own source so it can never cut a gameplay cue short.</summary>
    // Clip guarded as well as the source: this fires from menu chrome that can be driven before
    // the clips finish building, and PlayOneShot(null) logs a warning every single press.
    public void PlayButton() { if (_ui && _button) { _ui.pitch = 1f; _ui.PlayOneShot(_button, 0.6f); } }

    /// <summary>An explainer card appearing.</summary>
    public void PlayTeach() { if (_ui) { _ui.pitch = 1f; _ui.PlayOneShot(_teach, 0.7f); } }

    /// <summary>
    /// One detent of the level wheel. <paramref name="climb"/> nudges the pitch up as the number
    /// rises and down as it falls, so scrolling has a direction you can hear — a flat repeated
    /// blip is what makes a picker feel mechanical instead of tactile.
    /// </summary>
    public void PlayWheelTick(float climb)
    {
        if (!_ui || !_wheelTick) return;
        _ui.pitch = Mathf.Clamp(1f + climb * 0.06f, 0.82f, 1.22f);
        _ui.PlayOneShot(_wheelTick, 0.55f);
    }

    public void PlayLose() { if (_main) { _main.pitch = 1f; _main.PlayOneShot(_lose, 0.8f); } }
    public void PlayTimeWarning() { if (_main) { _main.pitch = 1f; _main.PlayOneShot(_timeWarn, 0.5f); } }
    /// <summary>Countdown tick; pitch rises as the last seconds run out for urgency.</summary>
    public void PlayCountdownTick(int secsLeft)
    {
        if (_main == null) return;
        _main.pitch = 1f + (5 - Mathf.Clamp(secsLeft, 1, 5)) * 0.12f;
        _main.PlayOneShot(_countTick, 0.55f);
    }

    /// <summary>0 = still (silent), 1 = full speed. Called by the player each frame.</summary>
    public void SetMoveLevel(float level)
    {
        _moveLevel = Mathf.Clamp01(level);
        _moveTargetVol = _moveLevel * GameConfig.MoveAudioMaxVol;
    }

    private void Update()
    {
        if (_move == null) return;
        // Idle most of the time (menus, a dot at rest): nothing to ramp, so skip the two native
        // property writes rather than re-asserting silence every frame.
        if (_moveTargetVol <= 0f && _move.volume <= 0f) return;
        // Smoothly open/close the whoosh and bend its pitch up with speed.
        _move.volume = Mathf.MoveTowards(_move.volume, _moveTargetVol, Time.unscaledDeltaTime * 0.8f);
        float targetPitch = 0.8f + 0.5f * _moveLevel;
        _move.pitch = Mathf.Lerp(_move.pitch, targetPitch, Time.unscaledDeltaTime * 6f);
    }

    public void PlayPing(float pitch = 1f) { if (_main) { _main.pitch = pitch; _main.PlayOneShot(_ping); } }
    public void PlayTick(float pitch = 1f) { if (_tick) { _tick.pitch = Mathf.Clamp(pitch, 0.6f, 2.5f); _tick.PlayOneShot(_tickClip); } }
    public void PlayStar(int index) { if (_main) { _main.pitch = 1f + index * 0.18f; _main.PlayOneShot(_star); } }

    private AudioClip BuildSweep(float startHz, float endHz, float duration, float volume)
    {
        int samples = Mathf.Max(1, Mathf.RoundToInt(SampleRate * duration));
        var data = new float[samples];
        double phase = 0.0;
        for (int i = 0; i < samples; i++)
        {
            float t = (float)i / samples;
            float freq = Mathf.Lerp(startHz, endHz, t);
            phase += 2.0 * Mathf.PI * freq / SampleRate;
            float attack = Mathf.Clamp01(t / 0.05f);
            float decay = Mathf.Exp(-3f * t);
            data[i] = Mathf.Sin((float)phase) * attack * decay * volume;
        }
        var clip = AudioClip.Create("sweep", samples, 1, SampleRate, false);
        clip.SetData(data, 0);
        return clip;
    }

    /// <summary>
    /// Contact. A warm C-major bloom of pure sines that bends UP into tune and rings out.
    ///
    /// This is the bass note the praise sting resolves over, not a rival to it. The sting is a
    /// C-major arpeggio from C5; this is rooted an octave below with its own octave partial
    /// landing on that same C5. They are one chord voiced across two events, which is why the
    /// bloom can still be ringing when the sting arrives without the two colliding — bass under
    /// melody is how music has always worked.
    ///
    /// The pitch bend is the hook. It starts 2.8% flat and settles over 130ms, so the sound
    /// audibly locks into place. That resolution is the satisfying part, and it mirrors what the
    /// player just did: the sonar has been searching in the dark, and this is contact landing.
    ///
    /// Three earlier attempts here failed for one shared reason — every other sound in this game
    /// is a pure sine, so a struck bell (inharmonic metal) and a snap (noise transient) were both
    /// foreign objects. "Different from the sting" was never the problem to solve; belonging to
    /// the same palette was.
    ///
    /// Rooted at C4 rather than lower on purpose: phone speakers roll off below roughly 300Hz,
    /// so a genuinely deep bloom would simply not exist on the device this ships to.
    /// </summary>
    private AudioClip BuildArrivalBloom(float rootHz, float duration, float volume)
    {
        int samples = Mathf.Max(1, Mathf.RoundToInt(SampleRate * duration));
        var data = new float[samples];

        float[] ratios = { 1f, 1.5f, 2f, 3f };        // root, fifth, octave, twelfth
        float[] gains  = { 1f, 0.42f, 0.55f, 0.14f };
        const float gainSum = 2.11f;
        var phases = new double[ratios.Length];

        for (int i = 0; i < samples; i++)
        {
            float t = (float)i / samples;
            float secs = i / (float)SampleRate;

            // Bend up into tune over the first 130ms — the "locking on" that makes it land.
            float bend = Mathf.Lerp(0.972f, 1f, Easing.OutCubic(Mathf.Clamp01(secs / 0.13f)));

            float sample = 0f;
            for (int p = 0; p < ratios.Length; p++)
            {
                phases[p] += 2.0 * Mathf.PI * (rootHz * ratios[p] * bend) / SampleRate;
                sample += Mathf.Sin((float)phases[p]) * gains[p];
            }
            sample /= gainSum;

            // Soft swell in (no click), long ring out, breathing slowly as it goes.
            float attack   = Mathf.Clamp01(secs / 0.028f);
            float body     = Mathf.Exp(-2.6f * t);
            float tremolo  = 1f + 0.08f * Mathf.Sin(2f * Mathf.PI * 5.5f * secs) * (1f - t);

            data[i] = sample * attack * body * tremolo * volume;
        }

        var clip = AudioClip.Create("arrivalBloom", samples, 1, SampleRate, false);
        clip.SetData(data, 0);
        return clip;
    }

    private AudioClip BuildArpeggio(float[] notesHz, float noteDuration, float volume)
    {
        int noteSamples = Mathf.RoundToInt(SampleRate * noteDuration);
        int total = noteSamples * notesHz.Length;
        var data = new float[total];
        for (int n = 0; n < notesHz.Length; n++)
        {
            double phase = 0.0;
            float freq = notesHz[n];
            for (int i = 0; i < noteSamples; i++)
            {
                float t = (float)i / noteSamples;
                phase += 2.0 * Mathf.PI * freq / SampleRate;
                float attack = Mathf.Clamp01(t / 0.04f);
                float decay = Mathf.Exp(-2.5f * t);
                data[n * noteSamples + i] = Mathf.Sin((float)phase) * attack * decay * volume;
            }
        }
        var clip = AudioClip.Create("arp", total, 1, SampleRate, false);
        clip.SetData(data, 0);
        return clip;
    }

    /// <summary>
    /// Seamless 1-second SPACE-THEME movement drone: a warm synth pad from detuned sine
    /// oscillators + gentle harmonics, with a slow tremolo. All frequencies are whole Hz so
    /// each completes an exact number of cycles in 1 second -> the loop is perfectly seamless
    /// (no noise, no cloth-rustle, no click). Pitch bends up with speed at playback time.
    /// </summary>
    private AudioClip BuildMoveLoop()
    {
        int n = SampleRate;
        var data = new float[n];
        const float TAU = 2f * Mathf.PI;

        for (int i = 0; i < n; i++)
        {
            float t = (float)i / SampleRate;
            // Two slightly detuned low sines beat together for a living "engine" body,
            // plus a soft octave and a fifth for a sci-fi shimmer.
            float body =
                  Mathf.Sin(TAU * 100f * t) * 0.5f
                + Mathf.Sin(TAU * 101f * t) * 0.5f   // 1 Hz beat
                + Mathf.Sin(TAU * 200f * t) * 0.14f  // octave
                + Mathf.Sin(TAU * 150f * t) * 0.10f; // fifth
            float tremolo = 0.82f + 0.18f * Mathf.Sin(TAU * 3f * t); // slow amplitude LFO
            data[i] = body * tremolo * 0.5f;
        }

        var clip = AudioClip.Create("move", n, 1, SampleRate, false);
        clip.SetData(data, 0);
        return clip;
    }

    /// <summary>
    /// Soft, dreamy time-rewind shimmer: two pure sines gliding downward together with a
    /// gentle sine tremolo (no square-wave stutter) and a smooth bell envelope. Quiet and
    /// non-harsh, so it reads as "rewinding" without being abrasive.
    /// </summary>
    private AudioClip BuildRewind()
    {
        float duration = 0.9f;
        int samples = Mathf.RoundToInt(SampleRate * duration);
        var data = new float[samples];
        double p1 = 0.0, p2 = 0.0;
        for (int i = 0; i < samples; i++)
        {
            float t = (float)i / samples;
            float freq = Mathf.Lerp(760f, 260f, Easing.OutQuad(t));      // smooth glide down
            p1 += 2.0 * Mathf.PI * freq / SampleRate;
            p2 += 2.0 * Mathf.PI * (freq * 1.5f) / SampleRate;           // a soft fifth on top
            float tremolo = 0.75f + 0.25f * Mathf.Sin(2f * Mathf.PI * 9f * t); // gentle wobble
            float env = Mathf.Sin(Mathf.PI * t);                         // smooth fade in AND out (bell)
            float s = Mathf.Sin((float)p1) + 0.35f * Mathf.Sin((float)p2);
            data[i] = s * tremolo * env * 0.16f;                         // low amplitude
        }
        var clip = AudioClip.Create("rewind", samples, 1, SampleRate, false);
        clip.SetData(data, 0);
        return clip;
    }
}
