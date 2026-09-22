using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Builds and animates the whole HUD in code (legacy uGUI Text so nothing needs
/// importing). Safe-area aware. Owns all the "readout" juice: rolling score, streak
/// flame, star pops, NEW BEST callout, ping flash/darken. GameManager tells it WHAT
/// happened; UIManager decides how it looks.
/// </summary>
public class UIManager : MonoBehaviour
{
    private TMP_FontAsset _tmpFont;      // HUD / body
    private TMP_FontAsset _displayFont;  // titles and headlines

    /// <summary>Body typeface, for overlays built outside this class (the tutorial).</summary>
    public TMP_FontAsset BodyFont => _tmpFont;
    /// <summary>Display typeface, for overlays built outside this class (the tutorial).</summary>
    public TMP_FontAsset TitleFont => _displayFont != null ? _displayFont : _tmpFont;

    /// <summary>
    /// Build an SDF font asset from a TTF in Resources. Generated at runtime so the project keeps
    /// no baked font assets; TMP renders it from a signed-distance field either way, so it stays
    /// sharp at any size. Returns null if the font is missing, letting callers fall back.
    /// </summary>
    private static TMP_FontAsset LoadFont(string resourcePath)
    {
        var ttf = Resources.Load<Font>(resourcePath);
        if (ttf == null)
        {
            Debug.LogWarning("[Sonarfall] Font not found: Resources/" + resourcePath + " — falling back.");
            return TMP_Settings.defaultFontAsset;
        }
        return TMP_FontAsset.CreateFontAsset(ttf);
    }

    /// <summary>Promote a label to the display typeface (titles, headlines, callouts).</summary>
    private TMP_Text Display(TMP_Text t)
    {
        if (t != null && _displayFont != null) t.font = _displayFont;
        return t;
    }
    private Canvas _canvas;
    private RectTransform _safe;
    private CanvasGroup _hudGroup;

    private RectTransform _hudBlock;          // gameplay-touch dead zone over the readouts
    private const float HudBlockHeight = 260f; // reference units; caption bottom is at -240

    /// <summary>
    /// True when a screen point is over the HUD readout band, where a drag or tap should belong to
    /// the interface rather than to the player dot.
    /// </summary>
    public bool IsOverHud(Vector2 screenPos)
    {
        if (_hudBlock == null) return false;
        // Screen-space-overlay canvas: pass a null camera, per RectTransformUtility's contract.
        return RectTransformUtility.RectangleContainsScreenPoint(_hudBlock, screenPos, null);
    }

    private TMP_Text _levelText, _timerText, _sectorText;
    private Image[] _hudStars;             // the live star row (lives), top-left
    private Image _starLossGlow;
    private float _starLossT = -1f;
    private int _starsShown;
    private TMP_Text _pingCountText;   // numeric reveal counter ("10  o")
    private Image _pingIcon;       // single circle icon next to the count
    private Sprite _dotSprite;

    private Image _timerRing;      // bloom behind the clock; reddens as time runs out
    private TMP_Text _timerCaption;
    // Menu / settings / daily.
    public System.Action OnPlay, OnDaily, OnDailyResultClosed, OnResetLevel, OnHome;

    /// <summary>Set by GameManager. Lets every button click and card make a sound — testers said
    /// audio is the main thing keeping them playing, and silent UI was the obvious hole.</summary>
    public ProceduralAudio Audio;

    /// <summary>Unscaled time of the most recent UI button press.</summary>
    public float LastUiPressTime { get; private set; } = -10f;
    /// <summary>True just after any UI button was pressed — gameplay should ignore that tap.</summary>
    public bool UiJustPressed => Time.unscaledTime - LastUiPressTime < 0.3f;
    private Sprite _roundRect;                    // panel cards only
    private Sprite _brackets;                     // the game's button frame
    private TMP_Text _playLabel;
    private RectTransform _playBtnRT;
    private Button _dailyBtn;
    private TMP_Text _dailyLabel;
    private TMP_Text _streakText;   // consecutive-day streak + what it currently pays out
    private Image _dailyFlame;
    private Image _dailyCheck;
    private GameObject _settingsPanel, _dailyResultPanel;

    // Teach card — the one blocking explainer, reused by every "you have never seen this" moment.
    private GameObject _teachPanel, _teachMarker;
    private RectTransform _teachCard;
    private Image _teachVeil, _teachFill, _teachFrame, _teachSwatch, _teachRing;
    private CanvasGroup _teachGroup;     // fades the veil and card together on open/close
    private TMP_Text _teachTitle, _teachBody, _teachOkLabel, _teachMarkerLabel;
    private RectTransform _teachOkRT;
    private Button _teachOkBtn;

    /// <summary>
    /// How long OK stays locked after a card appears.
    ///
    /// Players were dismissing explainers reflexively without reading them — the button is where
    /// their thumb already is, and tapping it is the fastest way back to the game. Holding it shut
    /// briefly forces the text into view. It counts DOWN visibly rather than just sitting dead, so
    /// it reads as deliberate rather than as an unresponsive button.
    /// </summary>
    private const float TeachOkLockSeconds = 3f;
    private System.Action _onTeachClosed;
    private Camera _teachCam;
    private Vector3 _teachWorld;
    private RectTransform _teachUiTarget;   // set instead of _teachCam to point at a HUD element
    private RectTransform _teachUiTarget2;  // optional second element; the ring centres on both
    private float _teachT;

    // HUD elements the tutorial points at. Exposed as rects rather than as "show the clock hint"
    // methods so the teach card stays generic and the tutorial owns the wording.
    public RectTransform TimerRect { get { return _timerText != null ? _timerText.rectTransform : null; } }
    public RectTransform RevealsRect { get { return _pingCountText != null ? _pingCountText.rectTransform : null; } }

    /// <summary>The dot beside the reveal count. The readout is a digit AND an icon, so the
    /// tutorial passes both and the reticle centres on the pair rather than on the number.</summary>
    public RectTransform RevealsIconRect { get { return _pingIcon != null ? _pingIcon.rectTransform : null; } }
    /// <summary>
    /// The middle of the THREE base stars — index 1, not the middle of the array.
    ///
    /// The array is four long because of the overcharge slot, so `Length / 2` pointed at the third
    /// star and threw the tutorial reticle off to the right of a row that is normally only three
    /// wide. The fourth star is hidden unless earned, so it must not influence the centre.
    /// </summary>
    public RectTransform StarsRect
    {
        get
        {
            if (_hudStars == null || _hudStars.Length == 0) return null;
            return _hudStars[Mathf.Min(GameConfig.MaxStars / 2, _hudStars.Length - 1)].rectTransform;
        }
    }
    private TMP_Text _soundLabel, _hapticsLabel, _notifLabel, _dailyResultText;
    private GameObject _gearInGame;

    private GameObject _startOverlay, _celebOverlay;
    private TMP_Text _startSub, _celebTitle, _celebScore;
    private TMP_Text _celebPraise;         // "FLAWLESS", "NICE ONE" — replaces the star tally
    private Image _praiseGlow;
    private float _praiseT = -1f;
    private TMP_Text _rewindText;
    private float _rewindT = -1f;
    private TMP_Text _bannerText;          // sector intro / orb / near-miss callouts
    private Image _bannerBg;               // scrim so the banner stays readable over a lit maze
    private Image _bannerFrame;            // corner brackets, matching the button language
    private float _bannerT = -1f, _bannerHold;
    private Color _bannerColor = Color.white;
    private Color _bannerSourceColor = Color.white;
    private float _dailyStreakGlow;

    private Image _flash, _dark, _cover;
    private Image _rewindOverlay, _scanBar;
    private float _rewindFxT = -1f;

    // Animation state (all mutated in Update, no per-frame allocation).
    private float _flashA, _darkA;
    private Color _flashColor = Color.white;
    private float _coverA, _coverTarget;
    private TMP_Text _streakLost;          // "STREAK LOST" callout
    /// <summary>Resting Y of the streak-lost callout. Clears the score line, which ends at -140.</summary>
    private const float StreakLostY = -250f;
    private float _streakLostT = -1f;
    private int _lastTimer = int.MinValue;
    private int _teachOkShown = int.MinValue;   // last digit written to the OK countdown
    private bool _timerUrgent;
    private float _pingFlashT = -1f;

    private Image _celebGlow;

    private static readonly Color PingLostCol = new Color(1f, 0.35f, 0.3f, 1f);

    private static readonly Color DotFull = new Color(0.6f, 0.9f, 1f, 1f);
    private static readonly Color DotUsed = new Color(0.6f, 0.9f, 1f, 0.16f);
    private static readonly Color TextCol = new Color(0.85f, 0.92f, 1f, 0.9f);

    public void BuildUI()
    {
        // Two typefaces, each doing the job it's good at:
        //  * Chakra Petch — HUD/body. Techy and angular but with clean, evenly-spaced numerals,
        //    which matters when the score and countdown change every frame.
        //  * Orbitron — display only. Wide geometric caps give the title and headlines their
        //    sci-fi identity; too wide for readouts, hence the split.
        _tmpFont = LoadFont("Fonts/ChakraPetch");
        _displayFont = LoadFont("Fonts/Orbitron") ?? _tmpFont;
        _dotSprite = VisualUtils.Disc();
        _roundRect = VisualUtils.RoundedRect();
        _brackets = VisualUtils.CornerBrackets();

        var canvasGO = new GameObject("HUD Canvas");
        canvasGO.transform.SetParent(transform, false);
        _canvas = canvasGO.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 100;
        var scaler = canvasGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1080f, 1920f); // portrait reference
        scaler.matchWidthOrHeight = 0.5f;
        canvasGO.AddComponent<GraphicRaycaster>();

        // Safe-area container for the readouts.
        var safeGO = new GameObject("SafeArea");
        safeGO.transform.SetParent(_canvas.transform, false);
        _safe = safeGO.AddComponent<RectTransform>();
        _safe.anchorMin = Vector2.zero; _safe.anchorMax = Vector2.one;
        _safe.offsetMin = Vector2.zero; _safe.offsetMax = Vector2.zero;
        safeGO.AddComponent<SafeArea>();
        // Everything in the safe area is gameplay HUD, so one group toggles it all off for the menu.
        _hudGroup = safeGO.AddComponent<CanvasGroup>();

        // ---- Top-left: the star row (lives) ----
        // This replaced the score readout. Score was a number you glanced at and forgot; stars are
        // a resource you are actively spending, so they have to be visible at all times and read
        // instantly — filled vs hollow, no counting required.
        // Four slots, not three: the fourth is the overcharge star, hidden until a bonus echo is
        // collected at full health. Built up front so it can simply appear rather than having to
        // be created mid-level.
        _hudStars = new Image[GameConfig.OverchargeStars];
        for (int i = 0; i < GameConfig.OverchargeStars; i++)
        {
            var sGO = new GameObject("HudStar" + i);
            sGO.transform.SetParent(_safe, false);
            var img = sGO.AddComponent<Image>();
            img.sprite = VisualUtils.PingStar();
            img.raycastTarget = false;
            img.color = StarLit;
            var srt = img.rectTransform;
            srt.anchorMin = srt.anchorMax = new Vector2(0, 1); srt.pivot = new Vector2(0.5f, 0.5f);
            srt.anchoredPosition = new Vector2(46 + i * 62, -46);
            srt.sizeDelta = new Vector2(54, 54);
            _hudStars[i] = img;
        }

        // Sits behind the row and flares red when one is lost — a number ticking down is easy to
        // miss mid-drag, a flash is not.
        var starGlowGO = new GameObject("StarLossGlow");
        starGlowGO.transform.SetParent(_safe, false);
        _starLossGlow = starGlowGO.AddComponent<Image>();
        _starLossGlow.sprite = VisualUtils.RadialGlow();
        _starLossGlow.raycastTarget = false;
        _starLossGlow.color = new Color(Danger.r, Danger.g, Danger.b, 0f);
        var sgrt = _starLossGlow.rectTransform;
        sgrt.anchorMin = sgrt.anchorMax = new Vector2(0, 1); sgrt.pivot = new Vector2(0.5f, 0.5f);
        sgrt.anchoredPosition = new Vector2(108, -46); sgrt.sizeDelta = new Vector2(320, 220);
        _starLossGlow.transform.SetAsFirstSibling();

        // ---- Top-centre: level, then the TIMER directly beneath it ----
        // The timer used to sit in a corner and playtesters simply never noticed there was a time
        // limit. Centre-stage under the level makes the pressure impossible to miss.
        var lvlGlowGO = new GameObject("LevelGlow");
        lvlGlowGO.transform.SetParent(_safe, false);
        var lvlGlow = lvlGlowGO.AddComponent<Image>();
        lvlGlow.sprite = VisualUtils.RadialGlow();
        lvlGlow.raycastTarget = false;
        lvlGlow.color = new Color(Accent.r, Accent.g, Accent.b, 0.10f);
        var lgrt = lvlGlow.rectTransform;
        lgrt.anchorMin = lgrt.anchorMax = new Vector2(0.5f, 1f); lgrt.pivot = new Vector2(0.5f, 1f);
        lgrt.anchoredPosition = new Vector2(0, 40); lgrt.sizeDelta = new Vector2(760, 320);

        _levelText = Display(Text_("Level", _safe, new Vector2(0.5f, 1), new Vector2(0, -14), new Vector2(880, 76), 50, TextAnchor.UpperCenter, Spaced("LEVEL 1")));
        _levelText.color = new Color(0.88f, 0.97f, 1f, 1f);
        Neon(_levelText, Accent, 0.7f);

        // Sector caption between the level and the clock.
        _sectorText = Text_("Sector", _safe, new Vector2(0.5f, 1), new Vector2(0, -74), new Vector2(700, 36), 24, TextAnchor.UpperCenter, "");
        Neon(_sectorText, Accent, 0.45f);

        // The clock itself — the biggest thing in the HUD.
        _timerRing = new GameObject("TimerGlow").AddComponent<Image>();
        _timerRing.transform.SetParent(_safe, false);
        _timerRing.sprite = VisualUtils.RadialGlow();
        _timerRing.raycastTarget = false;
        _timerRing.color = new Color(Accent.r, Accent.g, Accent.b, 0f);
        var trt = _timerRing.rectTransform;
        trt.anchorMin = trt.anchorMax = new Vector2(0.5f, 1f); trt.pivot = new Vector2(0.5f, 0.5f);
        trt.anchoredPosition = new Vector2(0, -168); trt.sizeDelta = new Vector2(420, 300);

        _timerText = Display(Text_("Timer", _safe, new Vector2(0.5f, 1), new Vector2(0, -104), new Vector2(520, 130), 92, TextAnchor.UpperCenter, ""));
        Neon(_timerText, Accent, 0.55f);

        // A bare number under the level could read as anything; this names it as a countdown.
        _timerCaption = Text_("TimerCaption", _safe, new Vector2(0.5f, 1), new Vector2(0, -206), new Vector2(400, 34), 20, TextAnchor.UpperCenter, Spaced("SECONDS"));
        _timerCaption.color = new Color(0.6f, 0.78f, 0.92f, 0.55f);

        // Invisible geometry probe over the whole HUD band (stars, level, clock, reveals). Gameplay
        // asks this whether a touch landed on the readouts rather than the maze — every element up
        // here has raycastTarget off so it can't be found by the normal UI hit test, which meant a
        // thumb parked over the clock was still steering the player. Height covers the caption's
        // bottom edge at -240 plus a margin. Anchored inside _safe so notches and scaling are
        // Unity's problem, not arithmetic here.
        var hudBlockGO = new GameObject("HudTouchBlock");
        hudBlockGO.transform.SetParent(_safe, false);
        _hudBlock = hudBlockGO.AddComponent<RectTransform>();
        _hudBlock.anchorMin = new Vector2(0f, 1f); _hudBlock.anchorMax = new Vector2(1f, 1f);
        _hudBlock.pivot = new Vector2(0.5f, 1f);
        _hudBlock.offsetMin = new Vector2(0f, -HudBlockHeight);
        _hudBlock.offsetMax = Vector2.zero;

        // Reveal counter (top-right): a number followed by a single circle icon, e.g. "10  o".
        _pingCountText = Text_("PingCount", _safe, new Vector2(1, 1), new Vector2(-82, -24), new Vector2(240, 74), 52, TextAnchor.UpperRight, "0");
        _pingCountText.color = DotFull;
        Neon(_pingCountText, Accent, 0.5f);
        var iconGO = new GameObject("PingIcon");
        iconGO.transform.SetParent(_safe, false);
        _pingIcon = iconGO.AddComponent<Image>();
        // Sonar arcs, not a dot: a filled circle beside a number reads as a generic counter and
        // never told anyone the number meant "pings you can still fire".
        _pingIcon.sprite = VisualUtils.SonarIcon(); _pingIcon.color = DotFull; _pingIcon.raycastTarget = false;
        var irt = _pingIcon.rectTransform;
        irt.anchorMin = irt.anchorMax = new Vector2(1, 1); irt.pivot = new Vector2(1, 1);
        // Slightly larger than the old dot — the arcs need the extra pixels to stay legible.
        irt.anchoredPosition = new Vector2(-32, -36); irt.sizeDelta = new Vector2(52, 52);

        // Full-screen effect layers (outside safe area on purpose).
        _dark  = FullScreen("Dark",  new Color(0, 0, 0, 0));
        _flash = FullScreen("Flash", new Color(1, 1, 1, 0));

        // Rewind screen effect: a soft cyan tint + a scan bar that sweeps down.
        _rewindOverlay = FullScreen("RewindTint", new Color(0.3f, 0.7f, 1f, 0f));
        var barGO = new GameObject("ScanBar");
        barGO.transform.SetParent(_canvas.transform, false);
        _scanBar = barGO.AddComponent<Image>();
        _scanBar.color = new Color(0.6f, 0.9f, 1f, 0f);
        _scanBar.raycastTarget = false;
        var brt = _scanBar.rectTransform;
        brt.anchorMin = new Vector2(0f, 0.5f); brt.anchorMax = new Vector2(1f, 0.5f);
        brt.pivot = new Vector2(0.5f, 0.5f);
        brt.sizeDelta = new Vector2(0f, 16f); // full width, 16px tall

        _gearInGame = BuildInGameGear();
        _startOverlay = BuildStart();
        _celebOverlay = BuildCeleb();
        _failPanel = BuildFailPanel();
        _levelPanel = BuildLevelSelect();
        _dailyResultPanel = BuildDailyResult();
        _settingsPanel = BuildSettings();   // built late so it draws above the menu
        _teachPanel = BuildTeachCard();     // later still — an explainer outranks everything

        _rewindText = Display(Text_("Rewind", _canvas.transform as RectTransform, new Vector2(0.5f, 0.5f), new Vector2(0, 140), new Vector2(1000, 120), 74, TextAnchor.MiddleCenter, "REWIND  -" + Mathf.RoundToInt(GameConfig.RewindSeconds) + "s"));
        _rewindText.color = new Color(0.5f, 0.85f, 1f, 0f);
        Neon(_rewindText, Accent, 0.7f);

        // Lower-centre: clear of the top HUD, and clear of the celebration panel's title/stars/score
        // (which occupy roughly +260 down to -120).
        //
        // The scrim behind it is not decoration. The fail banner fires while RevealAll lights the
        // ENTIRE maze, so the text was competing with bright wall glow directly behind it and was
        // unreadable exactly when it matters most.
        // Sharp-cornered slab, not a rounded card: it is the same shape language as the buttons,
        // and it is sized to hug the text rather than spanning the screen and burying whatever
        // sits behind it. Colour is the near-black navy of every other panel.
        var bannerBgGO = new GameObject("BannerScrim");
        bannerBgGO.transform.SetParent(_canvas.transform, false);
        _bannerBg = bannerBgGO.AddComponent<Image>();
        _bannerBg.raycastTarget = false;
        _bannerBg.color = new Color(PanelScrim.r, PanelScrim.g, PanelScrim.b, 0f);
        var bbrt = _bannerBg.rectTransform;
        bbrt.anchorMin = bbrt.anchorMax = new Vector2(0.5f, 0.5f);
        bbrt.pivot = new Vector2(0.5f, 0.5f);
        bbrt.anchoredPosition = new Vector2(0, -270);
        bbrt.sizeDelta = new Vector2(880, 220);

        // Corner brackets, exactly as on every button — this is what makes it read as part of the
        // game rather than a grey box dropped on top of it.
        var bannerFrameGO = new GameObject("BannerFrame");
        bannerFrameGO.transform.SetParent(_canvas.transform, false);
        _bannerFrame = bannerFrameGO.AddComponent<Image>();
        _bannerFrame.sprite = _brackets;
        _bannerFrame.type = Image.Type.Sliced;
        _bannerFrame.raycastTarget = false;
        _bannerFrame.color = new Color(Accent.r, Accent.g, Accent.b, 0f);
        var bfrt = _bannerFrame.rectTransform;
        bfrt.anchorMin = bfrt.anchorMax = new Vector2(0.5f, 0.5f);
        bfrt.pivot = new Vector2(0.5f, 0.5f);
        bfrt.anchoredPosition = new Vector2(0, -270);
        bfrt.sizeDelta = new Vector2(896, 236);

        _bannerText = Display(Text_("Banner", _canvas.transform as RectTransform, new Vector2(0.5f, 0.5f),
                            new Vector2(0, -270), new Vector2(1040, 250), 54, TextAnchor.MiddleCenter, ""));
        _bannerText.color = new Color(1f, 1f, 1f, 0f);
        Neon(_bannerText, Accent, 0.7f);

        // Top-most: opaque cover for masking the between-level swap (created last = drawn last).
        _cover = FullScreen("Cover", new Color(0, 0, 0, 0));

        SetStars(GameConfig.StartStars, GameConfig.MaxStars);
    }

    // ---------- public API ----------
    public void SetLevel(int level) => _levelText.text = Spaced("LEVEL " + level);

    /// <summary>
    /// Header for the Daily Maze. It is a standalone challenge, so it shows the date rather than
    /// a level number and sector — those belong to the endless run and only confuse the two modes.
    /// </summary>
    public void SetDailyHeader()
    {
        _levelText.text = Spaced("DAILY MAZE");
        if (_sectorText != null)
        {
            _sectorText.text = System.DateTime.Now.ToString("MMM d").ToUpperInvariant();
            _sectorText.color = Color.Lerp(Gold, Color.white, 0.35f);
            NeonColor(_sectorText, Gold, 0.6f);
        }
    }

    /// <summary>Small sector caption under the level number, e.g. "THE DEEP  ·  3/5".</summary>
    public void SetSector(string sectorName, int levelInSector, int levelsPerSector, Color color)
    {
        if (_sectorText == null) return;
        _sectorText.text = sectorName + "   ·   " + levelInSector + "/" + levelsPerSector;
        _sectorText.color = Color.Lerp(color, Color.white, 0.35f);
        NeonColor(_sectorText, color, 0.6f);   // caption glows in its sector's colour
    }

    /// <summary>Update the live star row. Spent stars go hollow rather than disappearing, so the
    /// player can always see how much they have already lost, not just what is left.</summary>
    public void SetStars(int remaining, int max)
    {
        _starsShown = remaining;
        if (_hudStars == null) return;
        for (int i = 0; i < _hudStars.Length; i++)
        {
            bool overchargeSlot = i >= max;          // the fourth star
            bool lit = i < remaining;

            // The overcharge slot is invisible until earned — an empty fourth socket sitting
            // there permanently would read as a life the player has somehow already lost.
            _hudStars[i].gameObject.SetActive(!overchargeSlot || lit);
            if (overchargeSlot && !lit) continue;

            Color c = overchargeSlot ? GameConfig.BonusOrbColor : StarLit;
            _hudStars[i].color = lit ? c : new Color(c.r, c.g, c.b, 0.16f);
            _hudStars[i].rectTransform.localScale = Vector3.one * (lit ? 1f : 0.82f);
        }
    }

    /// <summary>Flare the star row red — called the instant a life is lost.</summary>
    public void PulseStarLost() { _starLossT = 0f; }

    /// <summary>Reset the reveal counter for a new level (kept the old name so callers don't change).</summary>
    public void BuildPingDots(int total)
    {
        _pingFlashT = -1f;
        _pingIcon.rectTransform.localScale = Vector3.one;
        SetPingsRemaining(total);
    }

    public void SetPingsRemaining(int remaining)
    {
        _pingCountText.text = remaining.ToString();
        Color col = remaining > 0 ? DotFull : new Color(1f, 0.45f, 0.45f, 1f);
        _pingCountText.color = col;
        _pingIcon.color = new Color(col.r, col.g, col.b, remaining > 0 ? 1f : 0.45f);
    }

    /// <summary>Lose a reveal: update the count and flash the counter red.</summary>
    public void LosePing(int remaining)
    {
        SetPingsRemaining(remaining);
        _pingFlashT = 0f;
        _pingCountText.color = PingLostCol;
        _pingIcon.color = PingLostCol;
        _pingIcon.rectTransform.localScale = Vector3.one * 1.5f;
    }

    public void SetTimer(int seconds)
    {
        if (seconds == _lastTimer) return;   // only touch the string when it changes
        _lastTimer = seconds;
        if (seconds < 0)
        {
            _timerText.text = "";
            if (_timerCaption != null) _timerCaption.text = "";
            if (_timerRing != null) _timerRing.color = new Color(Accent.r, Accent.g, Accent.b, 0f);
            _timerUrgent = false;
            return;
        }
        _timerText.text = seconds.ToString();
        if (_timerCaption != null && _timerCaption.text.Length == 0) _timerCaption.text = Spaced("SECONDS");

        // The clock escalates in three stages so the pressure is legible at a glance: calm cyan,
        // amber warning, then hostile red with a pulsing bloom behind it.
        _timerUrgent = seconds <= GameConfig.TimerTickFrom;
        bool warn = seconds <= GameConfig.TimerWarnAt;

        Color c = _timerUrgent ? Danger : (warn ? Gold : Accent);
        _timerText.color = Color.Lerp(c, Color.white, 0.35f);   // saturated, not washed out
        NeonColor(_timerText, c, _timerUrgent ? 0.95f : (warn ? 0.75f : 0.5f));
        if (_timerCaption != null)
            _timerCaption.color = new Color(c.r, c.g, c.b, _timerUrgent ? 0.85f : 0.5f);
        if (_timerRing != null)
            _timerRing.color = new Color(c.r, c.g, c.b, _timerUrgent ? 0.20f : (warn ? 0.11f : 0.05f));
    }

    public void ShowStart(int currentLevel, int dayStreak, bool dailyDone, bool dailyUnlocked)
    {
        // Where they are, and what they are heading for. A "best" figure was dropped from here —
        // it only ever tells a player they were once further along than they are now — and the
        // space is far better spent naming the next milestone, which is something to play TOWARD
        // rather than a record of the past.
        if (currentLevel > 1)
        {
            int toNext = GameConfig.LevelsToNextSector(currentLevel);
            _startSub.text = "Level  " + currentLevel + "        "
                           + GameConfig.NextSectorName(currentLevel) + "  in  " + toNext;
        }
        else _startSub.text = "Drag to move   •   Tap to ping";
        if (_playLabel != null) _playLabel.text = Spaced(currentLevel > 1 ? "CONTINUE" : "PLAY");

        // Name the streak and — the part that was missing entirely — what it is paying out. The
        // bonus reveals were granted silently, so players received a reward they never knew they
        // had earned and could not tell they were about to lose.
        if (_streakText != null)
        {
            int bonus = Mathf.Min(GameConfig.DailyBonusPingsMax, Mathf.Max(0, dayStreak - 1));
            if (dayStreak >= 2)
                _streakText.text = dayStreak + "-DAY STREAK"
                                 + (bonus > 0 ? "   ·   +" + bonus + " reveal" + (bonus == 1 ? "" : "s") + " next level" : "");
            else
                _streakText.text = "Play tomorrow to start a streak";
            _streakText.color = dayStreak >= 2
                ? GameConfig.StreakColor
                : new Color(0.55f, 0.62f, 0.72f, 0.8f);
        }

        // Daily button reflects today's state at a glance, and locks once it's been played —
        // one attempt per day is what makes the daily meaningful.
        if (!dailyUnlocked)
        {
            // One attempt, no retries, full difficulty — a miserable first experience for someone
            // who has never fired a ping. It opens up once they've seen a run through to the end.
            _dailyLabel.text = "LOCKED  ·  PLAY A RUN";
            _dailyLabel.fontSize = 30;
            _dailyLabel.color = new Color(0.55f, 0.60f, 0.70f, 0.9f);
            NeonColor(_dailyLabel, new Color(0.5f, 0.6f, 0.75f, 1f), 0.35f);
            _dailyLabel.rectTransform.anchoredPosition = Vector2.zero;
            _dailyCheck.gameObject.SetActive(false);
            _dailyBtn.interactable = false;
        }
        else if (dailyDone)
        {
            _dailyLabel.fontSize = 38;
            _dailyLabel.text = "DAILY DONE";
            _dailyLabel.color = DailyDoneCol;
            _dailyBtn.interactable = false;

            // Shift the text left by half the tick's footprint and hang the tick off its right
            // edge, so text + tick read as one centred group.
            const float icon = 34f, gap = 16f;
            _dailyLabel.ForceMeshUpdate();
            float w = _dailyLabel.preferredWidth;
            _dailyLabel.rectTransform.anchoredPosition = new Vector2(-(icon + gap) * 0.5f, 0f);
            _dailyCheck.rectTransform.anchoredPosition = new Vector2(w * 0.5f + gap * 0.5f, 0f);
            _dailyCheck.gameObject.SetActive(true);
        }
        else
        {
            _dailyLabel.fontSize = 38;
            _dailyLabel.text = dayStreak > 1 ? "DAILY MAZE   " + dayStreak + "×" : "DAILY MAZE";
            _dailyLabel.color = new Color(1f, 0.85f, 0.4f, 1f);
            _dailyLabel.rectTransform.anchoredPosition = Vector2.zero;
            _dailyCheck.gameObject.SetActive(false);
            _dailyBtn.interactable = true;
        }
        _dailyStreakGlow = dayStreak > 1 && !dailyDone ? Mathf.Clamp01(0.25f + dayStreak * 0.08f) : 0f;

        _startOverlay.SetActive(true);
        _celebOverlay.SetActive(false);
        ShowInGameGear(false);
        ShowHud(false);          // no gameplay readouts behind the menu
    }
    public void HideStart() { _startOverlay.SetActive(false); ShowInGameGear(true); ShowHud(true); }

    /// <summary>Toggle the entire gameplay HUD (readouts + in-game gear).</summary>
    public void ShowHud(bool show)
    {
        if (_hudGroup == null) return;
        _hudGroup.alpha = show ? 1f : 0f;
        _hudGroup.blocksRaycasts = show;
        _hudGroup.interactable = show;
    }


    /// <summary>
    /// Retitle the celebration panel (e.g. "SECTOR CLEAR" on a finale).
    ///
    /// The big soft glow behind the title stays on the UI palette no matter what is passed in.
    /// Callers hand this the SECTOR colour, and sector tints include teal and sea-green — piping
    /// one of those into a full-width panel put a green wash behind the text and made the whole
    /// celebration look like it belonged to a different game. The tint is allowed on the text
    /// itself (that reads as identity); the chrome behind it is not.
    /// </summary>
    public void SetCelebrationTitle(string title, Color color)
    {
        _celebTitle.text = Spaced(title);
        _celebTitle.color = Color.Lerp(color, Color.white, 0.55f);
        NeonColor(_celebTitle, color, 0.9f);
        if (_celebGlow != null) _celebGlow.color = new Color(Accent.r, Accent.g, Accent.b, 0.16f);
    }

    public void ShowCelebration(int level)
    {
        _celebTitle.text = Spaced("LEVEL " + level + " CLEAR");
        _celebTitle.color = TitleText;
        NeonColor(_celebTitle, Accent, 0.85f);
        if (_celebGlow != null) _celebGlow.color = new Color(Accent.r, Accent.g, Accent.b, 0.16f);
        _celebScore.text = "";
        _celebPraise.text = "";
        _celebPraise.transform.localScale = Vector3.zero;
        _praiseT = -1f;
        if (_praiseGlow != null) _praiseGlow.color = new Color(Gold.r, Gold.g, Gold.b, 0f);
        _celebOverlay.SetActive(true);
    }
    public void SetCelebrationScoreLine(string line) => _celebScore.text = line;
    public void HideCelebration()
    {
        _celebOverlay.SetActive(false);
    }

    /// <summary>
    /// Slam the praise word in. Deliberately not a fade: it overshoots, snaps back and drags a
    /// bloom with it, because this is the one moment per level that has to feel like a reward.
    /// </summary>
    public void ShowPraise(string word, Color col)
    {
        if (_celebPraise == null) return;
        _celebPraise.text = Spaced(word);
        _celebPraise.color = col;
        Neon(_celebPraise, col, 0.95f);
        if (_praiseGlow != null) _praiseGlow.color = new Color(col.r, col.g, col.b, 0f);
        _praiseT = 0f;
    }

    /// <summary>
    /// Big centered callout used for sector intros, CLUTCH clears and the near-miss line on a fail.
    /// Pops in, holds, then fades — all on unscaled time so it survives hitstop.
    /// </summary>
    public void ShowBanner(string message, Color color, float holdSeconds = 1.1f)
    {
        // A banner fired while an explainer is up prints straight through the card — entering a
        // new sector does both at once, so "SECTOR 2 / THE DEEP" landed on top of the card title.
        // Hold it and replay it once the card is dismissed; the two are sequential beats, not
        // simultaneous ones.
        if (TeachOpen)
        {
            _pendingBanner = message;
            _pendingBannerColor = color;
            _pendingBannerHold = holdSeconds;
            return;
        }

        _bannerText.text = message;
        _bannerSourceColor = color;      // un-lerped, so a held banner can be replayed exactly
        _bannerColor = Color.Lerp(color, Color.white, 0.3f);
        NeonColor(_bannerText, color, 0.8f);
        _bannerHold = holdSeconds;
        _bannerT = 0f;
        if (_bannerBg != null) _bannerBg.color = new Color(PanelScrim.r, PanelScrim.g, PanelScrim.b, 0f);
        if (_bannerFrame != null) _bannerFrame.color = new Color(_bannerColor.r, _bannerColor.g, _bannerColor.b, 0f);
    }

    /// <summary>
    /// The other half of the banner/teach-card collision, and the half that actually bites: a new
    /// sector calls ShowBanner from BuildLevel and only *then* runs MaybeTeachLevel, so the guard in
    /// ShowBanner sees TeachOpen == false and lets the banner through — the card opens over it a
    /// frame later. Snatch any in-flight banner back when a card opens and requeue it for the close.
    /// </summary>
    private void HoldLiveBanner()
    {
        if (_bannerT < 0f) return;                    // nothing on screen

        _pendingBanner = _bannerText.text;
        _pendingBannerColor = _bannerSourceColor;
        _pendingBannerHold = _bannerHold;

        _bannerT = -1f;
        _bannerText.color = new Color(_bannerColor.r, _bannerColor.g, _bannerColor.b, 0f);
        if (_bannerBg != null) _bannerBg.color = new Color(PanelScrim.r, PanelScrim.g, PanelScrim.b, 0f);
        if (_bannerFrame != null) _bannerFrame.color = new Color(_bannerColor.r, _bannerColor.g, _bannerColor.b, 0f);
    }


    public void ShowRewind() => _rewindT = 0f;
    public void PlayRewindEffect() => _rewindFxT = 0f;
    public void Flash(float strength) { _flashColor = Color.white; _flashA = Mathf.Max(_flashA, strength); }
    public void FlashColor(Color c, float strength) { _flashColor = c; _flashA = Mathf.Max(_flashA, strength); }
    public void DarkFlash() => _darkA = Mathf.Max(_darkA, GameConfig.PingDarkenAmount);

    /// <summary>0 = clear, 1 = full black. Used to mask the between-level swap.</summary>
    public void SetCover(float target) => _coverTarget = target;

    // ---------- animation ----------
    private void Update()
    {
        float dt = Time.unscaledDeltaTime; // keep the UI alive during hitstop (timeScale=0)

        TickLevelSelect();   // wheel detents + snap; no-ops when the picker is closed

        // Teach card: the marker breathes so the eye is pulled to it, and re-tracks every frame
        // because the camera is never quite still (shake, punch-zoom, the menu's idle drift).
        if (TeachOpen)
        {
            _teachT += dt;
            if (_teachMarker.activeSelf)
            {
                TrackTeachMarker();
                float markerPulse = 1f + 0.12f * Mathf.Sin(_teachT * 4.2f);
                _teachMarker.transform.localScale = Vector3.one * markerPulse;
                var rc = _teachRing.color;
                _teachRing.color = new Color(rc.r, rc.g, rc.b, 0.65f + 0.35f * Mathf.Sin(_teachT * 4.2f));
            }
            // ---- open / close animation ----
            // Deliberately short and snappy: 0.20s in, 0.13s out. Long enough to read as a move
            // rather than a cut, short enough that a player dismissing five cards in a row never
            // waits on it. Scale uses OutBack for the same overshoot the banners and buttons use.
            if (_teachClosing)
            {
                _teachCloseT += dt;
                float k = Mathf.Clamp01(_teachCloseT / TeachOutSeconds);
                float ease = k * k;                 // ease-in: slow release, then it's gone
                _teachGroup.alpha = 1f - ease;
                _teachCard.localScale = Vector3.one * Mathf.Lerp(1f, 0.93f, ease);
                if (k >= 1f) FinishTeachClose();
            }
            else
            {
                float pop = Easing.OutBack(Mathf.Clamp01(_teachT / TeachInSeconds));
                _teachCard.localScale = Vector3.one * Mathf.Lerp(0.86f, 1f, pop);
                // Alpha resolves faster than the scale so the card is readable while it settles.
                _teachGroup.alpha = Mathf.Clamp01(_teachT / (TeachInSeconds * 0.65f));
            }

            // OK counts down, then unlocks.
            if (_teachOkBtn != null && !_teachOkBtn.interactable)
            {
                float left = TeachOkLockSeconds - _teachT;
                if (left <= 0f)
                {
                    _teachOkBtn.interactable = true;
                    _teachOkLabel.text = Spaced("OK");
                    _teachOkLabel.color = new Color(0.95f, 0.99f, 1f, 1f);
                    _teachOkRT.localScale = Vector3.one * 1.12f;   // small pop as it becomes live
                }
                else
                {
                    int shown = Mathf.CeilToInt(left);
                    if (shown != _teachOkShown) { _teachOkShown = shown; _teachOkLabel.text = shown.ToString(); }
                }
            }
            else if (_teachOkRT != null && _teachOkRT.localScale.x > 1f)
            {
                float s = Mathf.MoveTowards(_teachOkRT.localScale.x, 1f, dt * 0.8f);
                _teachOkRT.localScale = Vector3.one * s;
            }
        }

        // Flash / darken decay.
        if (_flashA > 0f) { _flashA = Mathf.Max(0f, _flashA - dt * 2.2f); _flash.color = new Color(_flashColor.r, _flashColor.g, _flashColor.b, _flashA); }
        if (_darkA > 0f)  { _darkA  = Mathf.Max(0f, _darkA  - dt / GameConfig.PingDarkenTime); _dark.color = new Color(0, 0, 0, _darkA); }

        // Level-transition cover.
        if (_coverA != _coverTarget)
        {
            _coverA = Mathf.MoveTowards(_coverA, _coverTarget, dt / 0.2f);
            _cover.color = new Color(0, 0, 0, _coverA);
        }

        // Praise word: slams in oversized, settles with a wobble, bloom flares and eases off.
        if (_praiseT >= 0f)
        {
            _praiseT += dt;
            float t = _praiseT;

            float scale;
            if (t < 0.26f) scale = Mathf.Lerp(2.1f, 1f, Easing.OutCubic(t / 0.26f));  // slam
            else if (t < 0.62f)                                                        // wobble
                scale = 1f + 0.055f * Mathf.Sin((t - 0.26f) * 34f) * (1f - (t - 0.26f) / 0.36f);
            else scale = 1f;
            _celebPraise.transform.localScale = Vector3.one * scale;

            // Slight tilt on the way in — a dead-straight word reads as a label, not a cheer.
            float tilt = t < 0.35f ? Mathf.Lerp(-7f, 0f, Easing.OutCubic(t / 0.35f)) : 0f;
            _celebPraise.transform.localRotation = Quaternion.Euler(0, 0, tilt);

            if (_praiseGlow != null)
            {
                float g = t < 0.18f ? t / 0.18f : Mathf.Max(0.28f, 1f - (t - 0.18f) / 0.9f);
                var pc = _celebPraise.color;
                _praiseGlow.color = new Color(pc.r, pc.g, pc.b, g * 0.30f);
                _praiseGlow.rectTransform.localScale = Vector3.one * Mathf.Lerp(0.75f, 1.1f, Mathf.Min(1f, t / 0.4f));
            }
        }

        // Star-loss flare: red bloom behind the row, and the row itself kicks. Fires on the frame
        // a life is spent, because a hollow star appearing is easy to miss mid-drag.
        if (_starLossT >= 0f)
        {
            _starLossT += dt;
            float t = _starLossT;
            float a = Mathf.Clamp01(1f - t / 0.55f);
            _starLossGlow.color = new Color(Danger.r, Danger.g, Danger.b, a * 0.55f);
            _starLossGlow.rectTransform.localScale = Vector3.one * Mathf.Lerp(0.7f, 1.35f, 1f - a);

            // The star that just went hollow is the one at index _starsShown.
            if (_hudStars != null && _starsShown >= 0 && _starsShown < _hudStars.Length)
            {
                float kick = t < 0.3f ? 1f + 0.45f * (1f - t / 0.3f) : 1f;
                _hudStars[_starsShown].rectTransform.localScale = Vector3.one * (0.82f * kick);
            }
            if (t > 0.6f)
            {
                _starLossT = -1f;
                _starLossGlow.color = new Color(Danger.r, Danger.g, Danger.b, 0f);
                // MaxStars, not _hudStars.Length. The array is 4 long because of the overcharge
                // slot; passing 4 as the max made SetStars treat that slot as an ordinary star and
                // leave it on screen dimmed — so spending a gold star showed a fourth empty socket
                // that reads as a life you never had.
                SetStars(_starsShown, GameConfig.MaxStars);
            }
        }

        // Lost-reveal counter flash: red pop settling back to normal.
        if (_pingFlashT >= 0f && _pingFlashT < 1f)
        {
            _pingFlashT = Mathf.Min(1f, _pingFlashT + dt / 0.45f);
            float e = Easing.OutCubic(_pingFlashT);
            _pingIcon.rectTransform.localScale = Vector3.one * Mathf.Lerp(1.5f, 1f, e);
            Color col = Color.Lerp(PingLostCol, DotFull, e);
            _pingIcon.color = col; _pingCountText.color = col;
            if (_pingFlashT >= 1f) { _pingIcon.rectTransform.localScale = Vector3.one; _pingFlashT = -1f; }
        }

        // Final seconds: the clock throbs. Motion reads faster than colour in peripheral vision.
        if (_timerUrgent && _timerText != null)
        {
            float beat = 1f + 0.10f * Mathf.Sin(Time.unscaledTime * 9f);
            _timerText.rectTransform.localScale = new Vector3(beat, beat, 1f);
            if (_timerRing != null)
                _timerRing.rectTransform.localScale = Vector3.one * (1f + 0.12f * Mathf.Sin(Time.unscaledTime * 9f));
        }
        else if (_timerText != null && _timerText.rectTransform.localScale.x != 1f)
        {
            _timerText.rectTransform.localScale = Vector3.one;
            if (_timerRing != null) _timerRing.rectTransform.localScale = Vector3.one;
        }

        // Main-menu attract animation: the PLAY button breathes so it's unmistakably the thing to
        // press, and the daily flame flickers when a streak is running.
        if (_startOverlay != null && _startOverlay.activeSelf)
        {
            float beat = Mathf.Sin(Time.unscaledTime * 3.4f);
            float s = 1f + 0.045f * beat;
            if (_playBtnRT != null) _playBtnRT.localScale = new Vector3(s, s, 1f);
            if (_playLabel != null)
                _playLabel.color = new Color(1f, 1f, 1f, 0.85f + 0.15f * (0.5f + 0.5f * beat));

            if (_dailyFlame != null)
            {
                float flicker = _dailyStreakGlow * (0.8f + 0.2f * Mathf.Sin(Time.unscaledTime * 7f));
                _dailyFlame.color = new Color(GameConfig.StreakColor.r, GameConfig.StreakColor.g,
                                              GameConfig.StreakColor.b, flicker);
            }
        }

        // Banner callout (sector intro / orb / near-miss): pop in, hold, fade.
        if (_bannerT >= 0f)
        {
            _bannerT += dt;
            float pop = _bannerT < 0.28f ? Easing.OutBack(_bannerT / 0.28f) : 1f;
            float fadeStart = 0.28f + _bannerHold;
            float alpha = _bannerT < fadeStart ? 1f : Mathf.Clamp01(1f - (_bannerT - fadeStart) / 0.5f);
            _bannerText.transform.localScale = Vector3.one * pop;
            _bannerText.color = new Color(_bannerColor.r, _bannerColor.g, _bannerColor.b, alpha);
            if (_bannerBg != null)
            {
                _bannerBg.transform.localScale = Vector3.one * pop;
                _bannerBg.color = new Color(PanelScrim.r, PanelScrim.g, PanelScrim.b, alpha * PanelScrim.a);
            }
            if (_bannerFrame != null)
            {
                // Frame picks up the banner's own hue (red on a fail, gold on the Daily), so the
                // callout is colour-coded the same way the buttons are.
                _bannerFrame.transform.localScale = Vector3.one * pop;
                _bannerFrame.color = new Color(_bannerColor.r, _bannerColor.g, _bannerColor.b, alpha * 0.85f);
            }
            if (_bannerT > fadeStart + 0.5f) _bannerT = -1f;
        }

        // Rewind callout: pop in, hold, fade (unscaled so it shows during the time-freeze).
        if (_rewindT >= 0f)
        {
            _rewindT += dt;
            float t = _rewindT;
            float pop = t < 0.3f ? Easing.OutBack(t / 0.3f) : 1f;
            float alpha = t < 0.9f ? 1f : Mathf.Clamp01(1f - (t - 0.9f) / 0.5f);
            _rewindText.transform.localScale = Vector3.one * pop;
            _rewindText.color = new Color(0.5f, 0.85f, 1f, alpha);
            if (t > 1.4f) _rewindT = -1f;
        }

        // Rewind screen effect: gentle cyan tint + a scan bar sweeping down a few times.
        if (_rewindFxT >= 0f)
        {
            _rewindFxT += dt;
            float dur = GameConfig.RewindDuration;
            float t = Mathf.Clamp01(_rewindFxT / dur);
            float fade = 1f - t; // ease the whole effect out toward the end

            float tint = (0.08f + 0.03f * Mathf.Sin(Time.unscaledTime * 12f)) * fade; // soft, no strobe
            _rewindOverlay.color = new Color(0.3f, 0.7f, 1f, tint);

            float frac = Mathf.Repeat(t * 3f, 1f);                       // 3 downward sweeps
            _scanBar.rectTransform.anchoredPosition = new Vector2(0f, Mathf.Lerp(960f, -960f, frac));
            _scanBar.color = new Color(0.6f, 0.9f, 1f, 0.45f * fade);

            if (_rewindFxT >= dur)
            {
                _rewindFxT = -1f;
                _rewindOverlay.color = new Color(0.3f, 0.7f, 1f, 0f);
                _scanBar.color = new Color(0.6f, 0.9f, 1f, 0f);
            }
        }
    }

    // ---------- builders ----------
    /// <summary>
    /// Build a TextMeshPro label. TMP renders from a signed-distance field, so glyphs stay crisp
    /// at any size or DPI — unlike legacy uGUI Text, whose "glow" had to be faked with an Outline
    /// component that literally draws the text four extra times and smears it.
    /// </summary>
    private TMP_Text Text_(string name, RectTransform parent, Vector2 anchor, Vector2 pos, Vector2 size, int fontSize, TextAnchor align, string init)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var t = go.AddComponent<TextMeshProUGUI>();
        t.font = _tmpFont;
        t.fontSize = fontSize;
        t.alignment = MapAlign(align);
        t.color = TextCol;
        t.text = init;
        t.raycastTarget = false;
        t.textWrappingMode = TextWrappingModes.NoWrap;
        t.overflowMode = TextOverflowModes.Overflow;
        var rt = t.rectTransform;
        rt.anchorMin = rt.anchorMax = anchor; rt.pivot = anchor;
        rt.anchoredPosition = pos; rt.sizeDelta = size;
        return t;
    }

    private static TextAlignmentOptions MapAlign(TextAnchor a)
    {
        switch (a)
        {
            case TextAnchor.UpperLeft:    return TextAlignmentOptions.TopLeft;
            case TextAnchor.UpperCenter:  return TextAlignmentOptions.Top;
            case TextAnchor.UpperRight:   return TextAlignmentOptions.TopRight;
            case TextAnchor.MiddleLeft:   return TextAlignmentOptions.Left;
            case TextAnchor.MiddleRight:  return TextAlignmentOptions.Right;
            case TextAnchor.LowerLeft:    return TextAlignmentOptions.BottomLeft;
            case TextAnchor.LowerCenter:  return TextAlignmentOptions.Bottom;
            case TextAnchor.LowerRight:   return TextAlignmentOptions.BottomRight;
            default:                      return TextAlignmentOptions.Center;
        }
    }

    private Image FullScreen(string name, Color c)
    {
        var go = new GameObject(name);
        go.transform.SetParent(_canvas.transform, false);
        var img = go.AddComponent<Image>();
        img.color = c; img.raycastTarget = false;
        var rt = img.rectTransform;
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
        return img;
    }

    // ---------- theme ----------
    // Sonar-HUD look: near-black translucent fills with a bright glowing stroke, so panels read
    // like readouts on the same display the maze is drawn on, not like stock mobile-app buttons.
    // The three topic hues are public: callers outside this file (the teach card's subject, for
    // one) must be able to name a palette entry rather than inventing a Color inline.
    public  static readonly Color Accent   = new Color(0.45f, 0.85f, 1.00f, 1f); // cyan
    public  static readonly Color Gold     = new Color(1.00f, 0.82f, 0.35f, 1f);
    public  static readonly Color Danger   = new Color(1.00f, 0.42f, 0.42f, 1f);
    private static readonly Color StarLit  = new Color(0.70f, 0.95f, 1.00f, 1f); // sonar-marker rating

    // Derived tints. Everything the UI draws resolves to one of these — no ad-hoc colours, so a
    // palette change lands everywhere at once.
    private static readonly Color TitleText   = new Color(0.90f, 0.97f, 1.00f, 1f);  // near-white, cyan-leaning
    private static readonly Color OnState     = new Color(0.62f, 0.92f, 1.00f, 1f);  // enabled  = Accent family
    private static readonly Color OffState    = new Color(1.00f, 0.55f, 0.55f, 1f);  // disabled = Danger family
    private static readonly Color DailyDoneCol= new Color(0.85f, 0.72f, 0.38f, 0.85f); // spent Daily = muted Gold

    // Panel backgrounds. Blue MUST dominate green — at these brightness levels a desaturated
    // near-black reads as olive/green to the eye, which is what made the banner panel look wrong.
    // Keep B roughly 2x G, and keep them opaque enough that whatever is behind can't tint them.
    private static readonly Color PanelSolid  = new Color(0.014f, 0.022f, 0.052f, 0.975f); // menus, settings
    private static readonly Color PanelVeil   = new Color(0.014f, 0.022f, 0.052f, 0.86f);  // celebration overlay
    private static readonly Color PanelScrim  = new Color(0.010f, 0.016f, 0.055f, 0.97f);  // behind banner text
    private static readonly Color ButtonFill  = new Color(0.030f, 0.052f, 0.125f, 0.50f);  // secondary buttons
    private static readonly Color CardFill    = new Color(0.055f, 0.082f, 0.180f, 1f);     // settings card

    /// <summary>
    /// Real shader-based glow on a TMP label: the SDF material renders the halo, so the glyph
    /// edge itself stays perfectly sharp. Safe to call repeatedly — fontMaterial is a per-label
    /// instance, so re-tinting just updates it.
    /// </summary>
    private static void Neon(TMP_Text t, Color glow, float strength = 0.55f)
    {
        if (t == null) return;
        var mat = t.fontMaterial;
        mat.EnableKeyword(ShaderUtilities.Keyword_Glow);
        mat.SetColor(ShaderUtilities.ID_GlowColor, new Color(glow.r, glow.g, glow.b, strength));
        mat.SetFloat(ShaderUtilities.ID_GlowPower, 0.4f);
        mat.SetFloat(ShaderUtilities.ID_GlowOuter, 0.3f);
        mat.SetFloat(ShaderUtilities.ID_GlowInner, 0.05f);
    }

    private static void NeonColor(TMP_Text t, Color glow, float strength = 0.55f)
    {
        Neon(t, glow, strength);
    }

    /// <summary>Letter-spaced caps for titles — reads as instrument labelling rather than body copy.</summary>
    private static string Spaced(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length * 2);
        for (int i = 0; i < s.Length; i++)
        {
            sb.Append(s[i]);
            if (i < s.Length - 1) sb.Append(' ');
        }
        return sb.ToString();
    }

    // ---------- button helper ----------

    /// <summary>
    /// The game's button style: a flat rectangular panel inside a floating corner-bracket frame.
    /// The fill keeps square corners so it matches the hard angles of the bracket arms.
    /// <paramref name="primary"/> makes it the loud call-to-action (brighter frame + tinted fill).
    /// </summary>
    private Button Button_(string name, Transform parent, Vector2 anchor, Vector2 pos, Vector2 size,
                           string label, int fontSize, Color accent, bool primary, out TMP_Text labelText)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = anchor; rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = pos; rt.sizeDelta = size;

        // Fill — a plain rectangle with hard corners (no sprite means no rounding). Also the
        // raycast and tint target.
        var fillGO = new GameObject("Fill");
        fillGO.transform.SetParent(rt, false);
        var fill = fillGO.AddComponent<Image>();
        fill.raycastTarget = true;
        fill.color = primary ? new Color(accent.r * 0.22f, accent.g * 0.26f, accent.b * 0.38f, 0.5f)
                             : ButtonFill;
        var frt2 = fill.rectTransform;
        frt2.anchorMin = Vector2.zero; frt2.anchorMax = Vector2.one;
        frt2.offsetMin = Vector2.zero; frt2.offsetMax = Vector2.zero;

        // The reticle corners, sitting 8 units OUTSIDE the fill so the frame floats clear of the
        // panel rather than hugging it.
        var lineGO = new GameObject("Brackets");
        lineGO.transform.SetParent(rt, false);
        var line = lineGO.AddComponent<Image>();
        line.sprite = _brackets; line.type = Image.Type.Sliced;
        line.raycastTarget = false;
        line.color = new Color(accent.r, accent.g, accent.b, primary ? 1f : 0.75f);
        var lrt2 = line.rectTransform;
        lrt2.anchorMin = Vector2.zero; lrt2.anchorMax = Vector2.one;
        lrt2.offsetMin = new Vector2(-8, -8); lrt2.offsetMax = new Vector2(8, 8);

        var btn = go.AddComponent<Button>();
        btn.targetGraphic = fill;
        // Every button stamps the time it was pressed. Gameplay polls raw pointer state and fires
        // its ping on pointer-UP — the same instant onClick runs — so this timestamp lets the
        // player reliably discard that press. It doesn't depend on UI raycast timing, which makes
        // it a dependable backstop to the IsOverUI() check.
        btn.onClick.AddListener(() =>
        {
            LastUiPressTime = Time.unscaledTime;
            Haptics.Selection();
            if (Audio != null) Audio.PlayButton();   // every button in the game, one place
        });
        var colors = btn.colors;
        colors.normalColor = Color.white;
        colors.highlightedColor = new Color(1.25f, 1.25f, 1.25f, 1f);
        colors.pressedColor = new Color(1.7f, 1.7f, 1.7f, 1f);
        colors.fadeDuration = 0.07f;
        btn.colors = colors;

        labelText = Text_(name + "Label", rt, new Vector2(0.5f, 0.5f), Vector2.zero,
                          new Vector2(size.x, size.y), fontSize, TextAnchor.MiddleCenter, label);
        // Label sits well above its own glow colour, otherwise same-hue text on a same-hue stroke
        // (red on red especially) turns to mush.
        labelText.color = primary ? new Color(0.95f, 0.99f, 1f, 1f) : Color.Lerp(accent, Color.white, 0.45f);
        Neon(labelText, accent, primary ? 0.7f : 0.45f);
        labelText.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        return btn;
    }

    private GameObject BuildStart()
    {
        var go = new GameObject("StartOverlay");
        go.transform.SetParent(_canvas.transform, false);
        var panel = go.AddComponent<Image>();
        // Nearly opaque: lets the starfield hint through without the maze's exit/player glows
        // reading as stray UI blobs behind the buttons.
        panel.color = PanelSolid;
        panel.raycastTarget = true;   // blocks taps leaking into gameplay behind the menu
        var prt = panel.rectTransform; prt.anchorMin = Vector2.zero; prt.anchorMax = Vector2.one; prt.offsetMin = Vector2.zero; prt.offsetMax = Vector2.zero;
        var root = go.transform as RectTransform;

        // Title: a soft bloom behind letter-spaced caps, echoing the sonar reveal itself.
        var titleGlowGO = new GameObject("TitleGlow");
        titleGlowGO.transform.SetParent(root, false);
        var titleGlow = titleGlowGO.AddComponent<Image>();
        titleGlow.sprite = VisualUtils.RadialGlow();
        titleGlow.raycastTarget = false;
        titleGlow.color = new Color(Accent.r, Accent.g, Accent.b, 0.16f);
        var tgrt = titleGlow.rectTransform;
        tgrt.anchorMin = tgrt.anchorMax = new Vector2(0.5f, 0.5f); tgrt.pivot = new Vector2(0.5f, 0.5f);
        tgrt.anchoredPosition = new Vector2(0, 430); tgrt.sizeDelta = new Vector2(1000, 420);

        var title = Display(Text_("Title", root, new Vector2(0.5f, 0.5f), new Vector2(0, 430), new Vector2(1020, 160), 84, TextAnchor.MiddleCenter, Spaced("SONARFALL")));
        title.color = new Color(0.85f, 0.97f, 1f, 1f);
        Neon(title, Accent, 0.85f);

        _startSub = Text_("Sub", root, new Vector2(0.5f, 0.5f), new Vector2(0, 250), new Vector2(1000, 260), 30, TextAnchor.MiddleCenter, "");
        _startSub.color = new Color(0.62f, 0.78f, 0.92f, 0.85f);

        // The streak line. The game has tracked a consecutive-day streak since forever and spends
        // it on bonus reveals, but the only thing the player could see was an alpha ramp on a glow
        // behind the DAILY button — so nobody knew a streak existed, what it was worth, or that
        // skipping a day would cost them anything. A streak nobody can see cannot motivate anyone.
        // y=205 leaves ~40px of air above CONTINUE (whose top edge is 135). At 178 the line sat
        // 13px off the button and read as part of it rather than as its own status.
        _streakText = Text_("StreakLine", root, new Vector2(0.5f, 0.5f), new Vector2(0, 205),
                            new Vector2(1000, 60), 28, TextAnchor.MiddleCenter, "");
        _streakText.color = GameConfig.StreakColor;

        // Primary action.
        var playBtn = Button_("PlayBtn", root, new Vector2(0.5f, 0.5f), new Vector2(0, 60), new Vector2(560, 150),
                              Spaced("PLAY"), 58, Accent, true, out _playLabel);
        _playBtnRT = playBtn.GetComponent<RectTransform>();
        playBtn.onClick.AddListener(() => { if (OnPlay != null) OnPlay(); });

        // Secondary: the daily ritual, with its own streak flame.
        _dailyBtn = Button_("DailyBtn", root, new Vector2(0.5f, 0.5f), new Vector2(0, -120), new Vector2(500, 116),
                            "DAILY MAZE", 38, Gold, false, out _dailyLabel);
        _dailyBtn.onClick.AddListener(() => { if (OnDaily != null) OnDaily(); });

        var flameGO = new GameObject("DailyFlame");
        flameGO.transform.SetParent(_dailyBtn.transform, false);
        _dailyFlame = flameGO.AddComponent<Image>();
        _dailyFlame.sprite = VisualUtils.RadialGlow();
        _dailyFlame.raycastTarget = false;
        _dailyFlame.color = new Color(GameConfig.StreakColor.r, GameConfig.StreakColor.g, GameConfig.StreakColor.b, 0f);
        var frt = _dailyFlame.rectTransform;
        frt.anchorMin = frt.anchorMax = new Vector2(0f, 0.5f); frt.pivot = new Vector2(0.5f, 0.5f);
        frt.anchoredPosition = new Vector2(58, 0); frt.sizeDelta = new Vector2(120, 120);
        _dailyFlame.transform.SetAsFirstSibling();

        // Tick shown once today's daily is played. Positioned in ShowStart, where the label width
        // is known, so the "DAILY DONE ✓" group stays optically centred.
        var checkGO = new GameObject("DailyCheck");
        checkGO.transform.SetParent(_dailyBtn.transform, false);
        _dailyCheck = checkGO.AddComponent<Image>();
        _dailyCheck.sprite = VisualUtils.Check();
        _dailyCheck.raycastTarget = false;
        _dailyCheck.color = DailyDoneCol;
        var crt2 = _dailyCheck.rectTransform;
        crt2.anchorMin = crt2.anchorMax = new Vector2(0.5f, 0.5f); crt2.pivot = new Vector2(0.5f, 0.5f);
        crt2.sizeDelta = new Vector2(34, 34);
        checkGO.SetActive(false);

        // Level picker. Sits under the daily so the default path down the menu is still
        // PLAY -> DAILY; replaying an old maze is a deliberate detour, not the headline action.
        TMP_Text levelsLbl;
        var levelsBtn = Button_("LevelsBtn", root, new Vector2(0.5f, 0.5f), new Vector2(0, -270), new Vector2(500, 116),
                                Spaced("LEVELS"), 38, Accent, false, out levelsLbl);
        levelsBtn.onClick.AddListener(() => OpenLevelSelect(SaveData.CurrentLevel));

        // Settings gear.
        TMP_Text gearLabel;
        var gearBtn = Button_("GearBtn", root, new Vector2(0.5f, 0.5f), new Vector2(0, -430), new Vector2(110, 110),
                              "", 1, Accent, false, out gearLabel);
        gearBtn.onClick.AddListener(OpenSettings);
        var gearIcon = new GameObject("GearIcon");
        gearIcon.transform.SetParent(gearBtn.transform, false);
        var gi = gearIcon.AddComponent<Image>();
        gi.sprite = VisualUtils.Gear(); gi.raycastTarget = false;
        gi.color = new Color(0.8f, 0.9f, 1f, 0.9f);
        var girt = gi.rectTransform;
        girt.anchorMin = girt.anchorMax = new Vector2(0.5f, 0.5f); girt.pivot = new Vector2(0.5f, 0.5f);
        girt.anchoredPosition = Vector2.zero; girt.sizeDelta = new Vector2(66, 66);

        return go;
    }

    // ---- Fail panel -------------------------------------------------------------------
    // The fail state used to be a banner over a lit maze that auto-restarted after a fixed delay.
    // Two things were wrong with that. The maze rendered straight through the words, so the one
    // piece of information the player wanted ("how close was I?") was the hardest thing on screen
    // to read; and the automatic restart meant the message vanished whether or not they had
    // finished reading it. Now the maze is dimmed behind an opaque slab and nothing happens until
    // the player asks for it.
    private GameObject _failPanel;
    private TMP_Text _failHead, _failSub;
    private Button _failRetryBtn;
    private System.Action _onFailRetry;

    // ---- Level select ------------------------------------------------------------------
    // A snapping wheel rather than a grid of buttons: the ladder is unbounded, so a grid would
    // either need paging or would grow forever, and flicking to level 60 through a wheel is one
    // gesture instead of six page taps.
    private GameObject _levelPanel;
    private ScrollRect _levelScroll;
    private RectTransform _levelContent;
    private readonly List<TMP_Text> _levelRows = new List<TMP_Text>();
    private int _levelSelected = 1;
    private bool _levelSnapping;
    private float _levelSnapT;
    private float _levelSnapFrom, _levelSnapTo;

    private const float LevelRowH   = 132f;   // tall enough to be a comfortable flick target
    private const int   LevelSelectMax = 200; // the sector names cycle well past this

    /// <summary>Fired when the player confirms a level from the picker.</summary>
    public System.Action<int> OnLevelChosen;

    private GameObject BuildLevelSelect()
    {
        var go = new GameObject("LevelSelectOverlay");
        go.transform.SetParent(_canvas.transform, false);
        var panel = go.AddComponent<Image>();
        // Fully opaque, unlike PanelSolid: this sits on top of the main menu, and at PanelSolid's
        // alpha the CONTINUE/DAILY buttons read straight through the wheel. B/G is 2.1, inside the
        // style rule for a flat field.
        panel.color = new Color(0.012f, 0.016f, 0.034f, 1f); panel.raycastTarget = true;
        var prt = panel.rectTransform;
        prt.anchorMin = Vector2.zero; prt.anchorMax = Vector2.one;
        prt.offsetMin = Vector2.zero; prt.offsetMax = Vector2.zero;
        var root = go.transform as RectTransform;

        var title = Display(Text_("LvlTitle", root, new Vector2(0.5f, 0.5f), new Vector2(0, 700),
                                  new Vector2(900, 110), 64, TextAnchor.MiddleCenter, Spaced("SELECT LEVEL")));
        title.color = TitleText;
        Neon(title, Accent, 0.8f);

        // The lit band the wheel snaps into. Drawn under the rows so the numbers sit on top of it.
        var bandGO = new GameObject("LvlBand");
        bandGO.transform.SetParent(root, false);
        var band = bandGO.AddComponent<Image>();
        band.color = new Color(Accent.r, Accent.g, Accent.b, 0.10f);
        band.raycastTarget = false;
        var bandRT = band.rectTransform;
        bandRT.anchorMin = bandRT.anchorMax = new Vector2(0.5f, 0.5f);
        bandRT.pivot = new Vector2(0.5f, 0.5f);
        bandRT.anchoredPosition = new Vector2(0, 40);
        bandRT.sizeDelta = new Vector2(720, LevelRowH);

        var bandFrameGO = new GameObject("LvlBandFrame");
        bandFrameGO.transform.SetParent(root, false);
        var bandFrame = bandFrameGO.AddComponent<Image>();
        bandFrame.sprite = _brackets; bandFrame.type = Image.Type.Sliced; bandFrame.raycastTarget = false;
        bandFrame.color = new Color(Accent.r, Accent.g, Accent.b, 0.85f);
        var bfRT = bandFrame.rectTransform;
        bfRT.anchorMin = bfRT.anchorMax = new Vector2(0.5f, 0.5f); bfRT.pivot = new Vector2(0.5f, 0.5f);
        bfRT.anchoredPosition = new Vector2(0, 40);
        bfRT.sizeDelta = new Vector2(760, LevelRowH + 30f);

        // Viewport clips the wheel to a few rows either side of the band.
        var viewGO = new GameObject("LvlViewport");
        viewGO.transform.SetParent(root, false);
        var viewImg = viewGO.AddComponent<Image>();
        // Invisible but still raycastable — Image hit-tests against its rect, not its alpha, so a
        // fully transparent graphic still catches the drag.
        viewImg.color = new Color(0, 0, 0, 0f);
        // RectMask2D, NOT Mask. Mask builds its stencil from the graphic's ALPHA, so pairing it
        // with a transparent viewport image masks the entire wheel away — the rows were laid out
        // correctly and clipped into nothing. RectMask2D clips to the rectangle and ignores alpha.
        viewGO.AddComponent<RectMask2D>();
        var viewRT = viewImg.rectTransform;
        viewRT.anchorMin = viewRT.anchorMax = new Vector2(0.5f, 0.5f);
        viewRT.pivot = new Vector2(0.5f, 0.5f);
        viewRT.anchoredPosition = new Vector2(0, 40);
        viewRT.sizeDelta = new Vector2(860, LevelRowH * 5f);

        var contentGO = new GameObject("LvlContent");
        contentGO.transform.SetParent(viewRT, false);
        _levelContent = contentGO.AddComponent<RectTransform>();
        _levelContent.anchorMin = new Vector2(0.5f, 1f);
        _levelContent.anchorMax = new Vector2(0.5f, 1f);
        _levelContent.pivot = new Vector2(0.5f, 1f);
        _levelContent.sizeDelta = new Vector2(860, LevelRowH * LevelSelectMax + LevelRowH * 4f);

        _levelScroll = go.AddComponent<ScrollRect>();
        _levelScroll.content = _levelContent;
        _levelScroll.viewport = viewRT;
        _levelScroll.horizontal = false;
        _levelScroll.vertical = true;
        _levelScroll.movementType = ScrollRectMovementType();
        _levelScroll.scrollSensitivity = 40f;
        _levelScroll.decelerationRate = 0.135f;

        // Rows. Two blank rows of padding at each end so level 1 and the last level can both reach
        // the centre band instead of stopping short at the top or bottom of the scroll range.
        for (int i = 0; i < LevelSelectMax; i++)
        {
            int lvl = i + 1;
            var t = Display(Text_("Lvl" + lvl, _levelContent, new Vector2(0.5f, 1f),
                                  new Vector2(0, -(LevelRowH * (i + 2) + LevelRowH * 0.5f)),
                                  new Vector2(820, LevelRowH), 64, TextAnchor.MiddleCenter,
                                  lvl.ToString()));
            t.raycastTarget = false;
            // Midline, not Center. TMP's Center centres the LINE BOX (ascender-to-descender),
            // which for digits sits noticeably above the glyphs you can actually see, so the
            // selected number rode high out of the highlight band. Midline centres on the visual
            // middle of the glyphs themselves, which is what the eye lines up against — and unlike
            // measuring the offset at runtime it stays correct at any font size and at the 1.18x
            // scale the selected row is drawn at.
            t.alignment = TextAlignmentOptions.Midline;
            // Pivot on the row's CENTRE. Text_ sets pivot = anchor, and the anchor here is the
            // content's top edge, which caused two separate misalignments: the glyph sat half a
            // row below the rect origin, and - the one a constant offset can never fix - the
            // per-row localScale (1.18x selected, 0.94x neighbours) scaled about the TOP edge, so
            // each row's number drifted by a different amount and the spacing came out uneven.
            // Pivoting on the centre puts the glyph on the pivot, so scaling no longer moves it.
            var lrt = t.rectTransform;
            lrt.pivot = new Vector2(0.5f, 0.5f);
            lrt.anchoredPosition = new Vector2(0, -(LevelRowH * (i + 2) + LevelRowH * 0.5f));
            _levelRows.Add(t);
        }


        // Viewport spans -290..370, so the caption clears its bottom edge and the buttons clear
        // the caption. Everything below is stacked, not overlapped.
        _levelSubLabel = Text_("LvlSub", root, new Vector2(0.5f, 0.5f), new Vector2(0, -350),
                               new Vector2(900, 60), 32, TextAnchor.MiddleCenter, "");
        _levelSubLabel.color = new Color(0.62f, 0.78f, 0.92f, 0.9f);

        TMP_Text goLbl;
        var goBtn = Button_("LvlGo", root, new Vector2(0.5f, 0.5f), new Vector2(0, -480),
                            new Vector2(520, 140), Spaced("PLAY"), 50, Accent, true, out goLbl);
        goBtn.onClick.AddListener(() =>
        {
            int lvl = _levelSelected;
            CloseLevelSelect();
            if (OnLevelChosen != null) OnLevelChosen(lvl);
        });

        TMP_Text backLbl;
        var backBtn = Button_("LvlBack", root, new Vector2(0.5f, 0.5f), new Vector2(0, -640),
                              new Vector2(420, 110), Spaced("BACK"), 36, Accent, false, out backLbl);
        backBtn.onClick.AddListener(CloseLevelSelect);

        go.SetActive(false);
        return go;
    }

    private TMP_Text _levelSubLabel;

    // Elastic would let the wheel bounce past the padding rows and settle off-centre; clamped keeps
    // every row reachable and the snap arithmetic honest.
    private static ScrollRect.MovementType ScrollRectMovementType() => ScrollRect.MovementType.Clamped;

    public bool LevelSelectOpen => _levelPanel != null && _levelPanel.activeSelf;

    /// <summary>
    /// Highest level the wheel will offer. Everything the player has reached — i.e. every level
    /// they have cleared, plus the one they are currently on. Levels beyond this are not shown at
    /// all rather than shown locked: a wall of greyed-out numbers is just noise on a ladder that
    /// has no end.
    /// </summary>
    private int _levelUnlocked = 1;
    private int _levelOpenedAt = 1;   // reference point for the wheel tick's pitch ramp

    public void OpenLevelSelect(int startAt)
    {
        if (_levelPanel == null) return;
        _levelUnlocked = Mathf.Clamp(SaveData.CurrentLevel, 1, LevelSelectMax);
        _levelSelected = Mathf.Clamp(startAt, 1, _levelUnlocked);
        _levelOpenedAt = _levelSelected;
        // Shrink the scrollable range to the unlocked span, otherwise the wheel keeps flinging
        // into 190 rows of empty space past the player's progress. Height = the offset needed to
        // put the last unlocked row on the band (RowH*(n-1)) plus one viewport (5 rows).
        _levelContent.sizeDelta = new Vector2(_levelContent.sizeDelta.x, LevelRowH * (_levelUnlocked + 4));
        _levelPanel.SetActive(true);
        _levelPanel.transform.SetAsLastSibling();
        _levelSnapping = false;
        // Jump straight to the level they're on, so the common case is zero scrolling.
        _levelContent.anchoredPosition = new Vector2(0, RowOffset(_levelSelected));
        RefreshLevelRows();
        if (Audio != null) Audio.PlayButton();
    }

    public void CloseLevelSelect()
    {
        if (_levelPanel != null) _levelPanel.SetActive(false);
        if (Audio != null) Audio.PlayButton();
    }

    /// <summary>
    /// Content Y that puts <paramref name="level"/> under the centre band.
    ///
    /// Derivation, because an off-by-one here silently parks the whole wheel outside the viewport:
    /// content is top-pivoted against the viewport's top edge, so a row whose local centre is at
    /// -Y draws at (viewportTop - Y + offset). Row i sits at -(RowH*(i+2) + RowH/2) thanks to the
    /// two padding rows, and the band is at the viewport's middle, viewportTop - H/2 with H = 5*RowH.
    /// Solving for offset collapses the constants and leaves exactly RowH * i, i.e. RowH*(level-1).
    /// </summary>
    private float RowOffset(int level) => LevelRowH * (level - 1);

    /// <summary>Which level is currently nearest the band. Inverse of RowOffset.</summary>
    private int NearestRow()
    {
        float y = _levelContent.anchoredPosition.y;
        return Mathf.Clamp(Mathf.RoundToInt(y / LevelRowH) + 1, 1, _levelUnlocked);
    }

    /// <summary>Recolour rows by distance from the band and update the caption.</summary>
    private void RefreshLevelRows()
    {
        for (int i = 0; i < _levelRows.Count; i++)
        {
            int lvl = i + 1;
            int d = Mathf.Abs(lvl - _levelSelected);
            // Locked levels are never drawn, so the wheel visibly ends at the player's progress.
            if (d > 3 || lvl > _levelUnlocked)
            { if (_levelRows[i].enabled) _levelRows[i].enabled = false; continue; }
            _levelRows[i].enabled = true;

            // The centre row is the selection; neighbours fade out fast so the wheel reads as
            // having one active value rather than seven equally-live options.
            float a = d == 0 ? 1f : Mathf.Max(0.10f, 0.42f - (d - 1) * 0.12f);
            float s = d == 0 ? 1.18f : 1f - Mathf.Min(0.18f, d * 0.06f);
            _levelRows[i].color = d == 0 ? new Color(0.9f, 0.98f, 1f, 1f)
                                         : new Color(0.62f, 0.78f, 0.92f, a);
            _levelRows[i].transform.localScale = Vector3.one * s;
        }

        if (_levelSubLabel != null)
        {
            int sector = GameConfig.SectorIndex(_levelSelected) + 1;
            _levelSubLabel.text = "SECTOR " + sector + "  ·  " + GameConfig.SectorName(_levelSelected);
        }
    }

    /// <summary>Wheel physics: track the drag, then ease into the nearest row when it settles.</summary>
    private void TickLevelSelect()
    {
        if (_levelPanel == null || !_levelPanel.activeSelf) return;

        if (_levelSnapping)
        {
            _levelSnapT += Time.unscaledDeltaTime / 0.18f;
            float k = Easing.OutCubic(Mathf.Clamp01(_levelSnapT));
            _levelContent.anchoredPosition = new Vector2(0, Mathf.Lerp(_levelSnapFrom, _levelSnapTo, k));
            if (_levelSnapT >= 1f) _levelSnapping = false;
            return;
        }

        int near = NearestRow();
        if (near != _levelSelected)
        {
            // Pitch tracks how far this notch is from where the wheel opened, so a long flick
            // rises or falls instead of repeating one flat blip.
            float climb = Mathf.Clamp(near - _levelOpenedAt, -6f, 6f);
            _levelSelected = near;
            RefreshLevelRows();
            Haptics.Selection();          // the wheel should feel like it has detents
            if (Audio != null) Audio.PlayWheelTick(climb);
        }

        // Once the fling has died down, pull the nearest row exactly onto the band.
        bool settled = Mathf.Abs(_levelScroll.velocity.y) < 40f && !EchoInput.PointerHeld;
        float target = RowOffset(_levelSelected);
        if (settled && Mathf.Abs(_levelContent.anchoredPosition.y - target) > 0.5f)
        {
            _levelScroll.velocity = Vector2.zero;
            _levelSnapping = true;
            _levelSnapT = 0f;
            _levelSnapFrom = _levelContent.anchoredPosition.y;
            _levelSnapTo = target;
        }
    }


    private GameObject BuildFailPanel()
    {
        var go = new GameObject("FailOverlay");
        go.transform.SetParent(_canvas.transform, false);
        var scrim = go.AddComponent<Image>();
        // Near-opaque on purpose. The revealed maze has already had its moment before this appears;
        // from here on it is background texture, not something to read through.
        scrim.color = new Color(0.010f, 0.014f, 0.032f, 0.90f);
        scrim.raycastTarget = true;         // swallow taps so the maze can't be played behind it
        var srt = scrim.rectTransform;
        srt.anchorMin = Vector2.zero; srt.anchorMax = Vector2.one;
        srt.offsetMin = Vector2.zero; srt.offsetMax = Vector2.zero;
        var root = go.transform as RectTransform;

        // Slab behind the copy, so the headline never sits directly on maze geometry.
        var slabGO = new GameObject("FailSlab");
        slabGO.transform.SetParent(root, false);
        var slab = slabGO.AddComponent<Image>();
        slab.color = PanelSolid; slab.raycastTarget = false;
        var slrt = slab.rectTransform;
        slrt.anchorMin = slrt.anchorMax = new Vector2(0.5f, 0.5f); slrt.pivot = new Vector2(0.5f, 0.5f);
        // Grown and re-centred to hold two actions (RETRY + LEVELS) rather than one.
        slrt.anchoredPosition = new Vector2(0, -30); slrt.sizeDelta = new Vector2(940, 620);

        var frameGO = new GameObject("FailFrame");
        frameGO.transform.SetParent(root, false);
        var frame = frameGO.AddComponent<Image>();
        frame.sprite = _brackets; frame.type = Image.Type.Sliced; frame.raycastTarget = false;
        frame.color = new Color(Danger.r, Danger.g, Danger.b, 0.9f);
        var frt2 = frame.rectTransform;
        frt2.anchorMin = frt2.anchorMax = new Vector2(0.5f, 0.5f); frt2.pivot = new Vector2(0.5f, 0.5f);
        frt2.anchoredPosition = new Vector2(0, -30); frt2.sizeDelta = new Vector2(960, 640);

        _failHead = Display(Text_("FailHead", root, new Vector2(0.5f, 0.5f), new Vector2(0, 190),
                                  new Vector2(900, 110), 74, TextAnchor.MiddleCenter, ""));
        _failHead.color = Danger;
        Neon(_failHead, Danger, 0.8f);

        _failSub = Text_("FailSub", root, new Vector2(0.5f, 0.5f), new Vector2(0, 60),
                         new Vector2(840, 160), 44, TextAnchor.MiddleCenter, "");
        _failSub.color = new Color(0.92f, 0.95f, 1f, 0.96f);
        _failSub.textWrappingMode = TextWrappingModes.Normal;

        TMP_Text retryLbl;
        _failRetryBtn = Button_("FailRetry", root, new Vector2(0.5f, 0.5f), new Vector2(0, -110),
                                new Vector2(480, 132), Spaced("RETRY"), 46, Accent, true, out retryLbl);
        _failRetryBtn.onClick.AddListener(() =>
        {
            // One shot. Without this the player can land three taps on the button while the
            // rebuild coroutine is still running, stacking resets and stranding the screen black.
            if (!_failRetryBtn.interactable) return;
            _failRetryBtn.interactable = false;
            var cb = _onFailRetry; _onFailRetry = null;
            if (cb != null) cb();
        });

        // Jump straight to another level from here. Dying is exactly when a player decides "this
        // one isn't happening, let me go do something else" — routing that through the main menu
        // adds two taps to a moment where the alternative is closing the app.
        //
        // The fail panel is deliberately left ACTIVE underneath: the picker is opaque and drawn
        // last, so BACK simply reveals the fail screen again with RETRY still armed, and the
        // FailRoutine coroutine waiting behind it is never disturbed.
        TMP_Text failLevelsLbl;
        var failLevelsBtn = Button_("FailLevels", root, new Vector2(0.5f, 0.5f), new Vector2(0, -262),
                                    new Vector2(400, 104), Spaced("LEVELS"), 34, Accent, false, out failLevelsLbl);
        failLevelsBtn.onClick.AddListener(() => OpenLevelSelect(SaveData.CurrentLevel));

        go.SetActive(false);
        return go;
    }

    /// <summary>
    /// Show the fail screen and wait. Nothing rebuilds until <paramref name="onRetry"/> fires from
    /// the button, so the player reads the result on their own clock.
    /// </summary>
    public void ShowFailPanel(string headline, string detail, System.Action onRetry)
    {
        if (_failPanel == null) return;
        _onFailRetry = onRetry;
        _failHead.text = Spaced(headline);
        _failSub.text = detail;
        _failRetryBtn.interactable = true;
        _failPanel.SetActive(true);
        _failPanel.transform.SetAsLastSibling();   // above the HUD and any lingering banner
    }

    public void HideFailPanel()
    {
        if (_failPanel != null) _failPanel.SetActive(false);
        _onFailRetry = null;
    }

    public bool FailPanelOpen => _failPanel != null && _failPanel.activeSelf;

    private GameObject BuildCeleb()
    {
        var go = new GameObject("CelebOverlay");
        go.transform.SetParent(_canvas.transform, false);
        var panel = go.AddComponent<Image>();
        // Dark enough that maze glows behind can't sit on top of the headline text, but still
        // translucent so the burst/particles read through.
        panel.color = PanelVeil; panel.raycastTarget = false;
        var prt = panel.rectTransform; prt.anchorMin = Vector2.zero; prt.anchorMax = Vector2.one; prt.offsetMin = Vector2.zero; prt.offsetMax = Vector2.zero;

        // Bloom behind the headline so a clear feels like a burst of light, not a text change.
        var cGlowGO = new GameObject("CTitleGlow");
        cGlowGO.transform.SetParent(go.transform, false);
        _celebGlow = cGlowGO.AddComponent<Image>();
        _celebGlow.sprite = VisualUtils.RadialGlow();
        _celebGlow.raycastTarget = false;
        _celebGlow.color = new Color(Accent.r, Accent.g, Accent.b, 0.16f);
        var cgrt = _celebGlow.rectTransform;
        cgrt.anchorMin = cgrt.anchorMax = new Vector2(0.5f, 0.5f); cgrt.pivot = new Vector2(0.5f, 0.5f);
        cgrt.anchoredPosition = new Vector2(0, 230); cgrt.sizeDelta = new Vector2(1100, 520);

        _celebTitle = Display(Text_("CTitle", go.transform as RectTransform, new Vector2(0.5f, 0.5f), new Vector2(0, 260), new Vector2(980, 160), 68, TextAnchor.MiddleCenter, Spaced("LEVEL CLEAR")));
        _celebTitle.color = TitleText;
        // Same treatment as the praise line. "LEVEL 4 CLEAR" already fills the width once spaced,
        // and the ladder has no ceiling — "LEVEL 100 CLEAR" is two glyphs longer again.
        _celebTitle.enableAutoSizing = true;
        _celebTitle.fontSizeMax = 68f;
        _celebTitle.fontSizeMin = 40f;
        Neon(_celebTitle, Accent, 0.85f);

        // Warm bloom under the praise word.
        var praiseGlowGO = new GameObject("PraiseGlow");
        praiseGlowGO.transform.SetParent(go.transform, false);
        _praiseGlow = praiseGlowGO.AddComponent<Image>();
        _praiseGlow.sprite = VisualUtils.RadialGlow();
        _praiseGlow.raycastTarget = false;
        _praiseGlow.color = new Color(Gold.r, Gold.g, Gold.b, 0f);
        var pgrt = _praiseGlow.rectTransform;
        pgrt.anchorMin = pgrt.anchorMax = new Vector2(0.5f, 0.5f); pgrt.pivot = new Vector2(0.5f, 0.5f);
        pgrt.anchoredPosition = new Vector2(0, 90); pgrt.sizeDelta = new Vector2(900, 420);

        // The star row that used to sit here is gone. Stars are LIVES now — showing a tally of
        // what survived reads as a grade on a test the player didn't sit, and testers said it
        // meant nothing to them. A single word of praise is the reward instead: it lands faster,
        // varies run to run, and doesn't invite the player to feel they underperformed.
        // Auto-sized, because the praise pool is variable-length and Spaced() roughly doubles it:
        // "FULL SPECTRUM" becomes a 25-character string, which at a fixed 82pt ran off both edges
        // of the screen. Short words like "PHEW!" still get the full 82; only the long ones shrink.
        // The box is 940 rather than the screen's 1080 so the settle wobble (peaks at 1.055x) has
        // somewhere to go: 940 * 1.055 = 992, still inside 1080.
        _celebPraise = Display(Text_("CPraise", go.transform as RectTransform, new Vector2(0.5f, 0.5f),
                              new Vector2(0, 90), new Vector2(940, 150), 82, TextAnchor.MiddleCenter, ""));
        _celebPraise.color = Gold;
        _celebPraise.enableAutoSizing = true;
        _celebPraise.fontSizeMax = 82f;
        _celebPraise.fontSizeMin = 44f;
        Neon(_celebPraise, Gold, 0.9f);
        _celebPraise.transform.localScale = Vector3.zero;

        _celebScore = Text_("CScore", go.transform as RectTransform, new Vector2(0.5f, 0.5f), new Vector2(0, -70), new Vector2(1000, 140), 42, TextAnchor.MiddleCenter, "");
        _celebScore.color = new Color(0.9f, 0.97f, 1f, 0.95f);
        Neon(_celebScore, Accent, 0.5f);
        go.SetActive(false);
        return go;
    }

    // ---------- settings ----------

    public void OpenSettings()
    {
        RefreshSettingLabels();
        // "Quit to menu" only makes sense from inside a level — the in-game gear is visible
        // exactly then, so it doubles as the test for whether a level is in flight.
        if (_settingsHomeBtn != null)
            _settingsHomeBtn.gameObject.SetActive(_gearInGame != null && _gearInGame.activeSelf);
        _settingsPanel.SetActive(true);
    }

    public void CloseSettings() => _settingsPanel.SetActive(false);
    public bool SettingsOpen => _settingsPanel != null && _settingsPanel.activeSelf;

    private void RefreshSettingLabels()
    {
        _soundLabel.text = "SOUND        " + (SaveData.SoundOn ? "ON" : "OFF");
        _soundLabel.color = SaveData.SoundOn ? OnState : OffState;
        _hapticsLabel.text = "VIBRATION   " + (SaveData.HapticsOn ? "ON" : "OFF");
        _hapticsLabel.color = SaveData.HapticsOn ? OnState : OffState;
        if (_notifLabel != null)
        {
            // Three states, not two: ON with the OS quietly blocking us used to read as "ON", and
            // a tester staring at that label had no way to know the phone would never show a thing.
            bool on = GameNotifications.Enabled;
            bool blocked = on && !GameNotifications.CanPost;
            _notifLabel.text = "REMINDERS   " + (blocked ? "BLOCKED" : on ? "ON" : "OFF");
            _notifLabel.color = blocked ? new Color(1f, 0.62f, 0.35f, 1f) : on ? OnState : OffState;
        }
    }

    /// <summary>
    /// After the player switches REMINDERS on, wait for the OS to answer. If it never even asked —
    /// permission already refused past the point of prompting, or notifications switched off for
    /// the app in Settings — the only honest response to "turn this on" is to take them to the
    /// switch that is actually off. If a dialog WAS shown and they said no, respect it and do
    /// nothing; bouncing someone into Settings a second after they declined is how apps get
    /// uninstalled.
    /// </summary>
    private System.Collections.IEnumerator SettleReminderPermission(GameNotifications.Request request)
    {
        float waited = 0f;
        while (!request.IsDone && waited < 30f)
        {
            GameNotifications.PollIOS();
            waited += Time.unscaledDeltaTime;
            yield return null;
        }
        RefreshSettingLabels();
        if (GameNotifications.Enabled && !GameNotifications.CanPost && !request.Prompted)
            GameNotifications.OpenSystemSettings();
    }

    private GameObject BuildSettings()
    {
        var go = new GameObject("SettingsPanel");
        go.transform.SetParent(_canvas.transform, false);

        // Scrim: dims whatever is behind (menu or gameplay) enough that it stops competing for
        // attention, without hiding it — the settings read as a card floating above the app.
        var dim = go.AddComponent<Image>();
        dim.color = new Color(0.01f, 0.015f, 0.03f, 0.82f);
        dim.raycastTarget = true;   // also swallows taps that miss the card
        var drt = dim.rectTransform; drt.anchorMin = Vector2.zero; drt.anchorMax = Vector2.one; drt.offsetMin = Vector2.zero; drt.offsetMax = Vector2.zero;

        // The card itself.
        var cardGO = new GameObject("Card");
        cardGO.transform.SetParent(go.transform, false);
        var card = cardGO.AddComponent<Image>();
        card.sprite = _roundRect;
        card.type = Image.Type.Sliced;
        card.color = CardFill;
        card.raycastTarget = true;
        var root = card.rectTransform;
        root.anchorMin = root.anchorMax = new Vector2(0.5f, 0.5f);
        root.pivot = new Vector2(0.5f, 0.5f);
        root.anchoredPosition = Vector2.zero;
        root.sizeDelta = new Vector2(880, 1180);

        var title = Display(Text_("SetTitle", root, new Vector2(0.5f, 0.5f), new Vector2(0, 460), new Vector2(820, 110), 68, TextAnchor.MiddleCenter, "SETTINGS"));
        title.color = new Color(0.6f, 0.9f, 1f, 1f);

        // Rows sit 155 apart, not 140: the bracket halo bleeds 6 units past each button, and at the
        // tighter spacing the two toggles' glows visibly touched.
        var soundBtn = Button_("SoundBtn", root, new Vector2(0.5f, 0.5f), new Vector2(0, 292), new Vector2(700, 120),
                               "", 38, Accent, false, out _soundLabel);
        soundBtn.onClick.AddListener(() =>
        {
            SaveData.SoundOn = !SaveData.SoundOn;
            SaveData.ApplySettings();
            RefreshSettingLabels();
        });

        var hapticBtn = Button_("HapticBtn", root, new Vector2(0.5f, 0.5f), new Vector2(0, 137), new Vector2(700, 120),
                                "", 38, Accent, false, out _hapticsLabel);
        // Created before the click handler so the handler can ask it whether this release is the
        // end of a diagnostic hold. uGUI fires onClick on every pointer-up over the button, hold or
        // not — without this check a tester running the self-test also flipped the setting off.
        var hold = hapticBtn.gameObject.AddComponent<HoldToDiagnose>();
        hapticBtn.onClick.AddListener(() =>
        {
            if (hold.FiredThisPress) return;
            SaveData.HapticsOn = !SaveData.HapticsOn;
            SaveData.ApplySettings();
            RefreshSettingLabels();
            // The platform's success pattern rather than a single tap: distinctive enough that
            // the player can tell "the game buzzed" from "something else on the phone buzzed".
            if (SaveData.HapticsOn) Haptics.Success();
        });

        // Reminders. Third row down at the same 155 spacing. A player who wants the game but not
        // the nudges needs somewhere to say so that isn't the OS settings app — burying that choice
        // is how an app gets its notifications muted wholesale instead of tuned.
        var notifBtn = Button_("NotifBtn", root, new Vector2(0.5f, 0.5f), new Vector2(0, -18), new Vector2(700, 120),
                               "", 38, Accent, false, out _notifLabel);
        var notifHold = notifBtn.gameObject.AddComponent<HoldToDiagnose>();
        notifBtn.onClick.AddListener(() =>
        {
            if (notifHold.FiredThisPress) return;   // the release that ends a long-press is not a tap
            // ON but blocked by the OS: the in-game switch is not the problem, so tapping the row
            // goes straight to the system page instead of flipping a toggle that changes nothing.
            if (GameNotifications.Enabled && !GameNotifications.CanPost)
            {
                GameNotifications.OpenSystemSettings();
                return;
            }
            GameNotifications.Enabled = !GameNotifications.Enabled;
            RefreshSettingLabels();
            // Turning it ON is the moment to ask, if we never got permission (or they said no
            // before). Turning it off just cancels — GameNotifications.Enabled handles that.
            if (GameNotifications.Enabled)
                StartCoroutine(SettleReminderPermission(GameNotifications.BeginPermissionRequest()));
        });

        // Long-press on the reminders row arms a probe that fires 60 seconds after the app is
        // backgrounded. "Do reminders reach a closed app" is otherwise a question that takes until
        // tomorrow morning to answer, and every tester report so far has been a guess.
        notifHold.OnHeld = () =>
        {
            GameNotifications.ScheduleTest(60f);
            ShowBanner("TEST REMINDER ARMED\n<size=55%>Leave the app now — it arrives in about a minute.\n"
                       + GameNotifications.Status + "</size>", Accent, 5f);
            Debug.Log("[Sonarfall] Reminder test armed: " + GameNotifications.Status);
        };

        // A long-press on the vibration row runs the full ladder and prints the platform report.
        //
        // This exists because haptics cannot be verified from one developer handset — whether a
        // buzz fires depends on OS version, the OEM's motor, whether it has amplitude control, and
        // system settings the app cannot see. Any tester can now hold this row, feel six distinct
        // pulses (or not), and send back the single log line that says which of those it was.
        hold.OnHeld = () =>
        {
            Haptics.SelfTest(this);
            ShowBanner("VIBRATION TEST\n<size=55%>" + Haptics.Status + "</size>", Accent, 4f);
            Debug.Log("[Sonarfall] Haptics self-test: " + Haptics.Status);
        };

        // RESET PROGRESS used to live here, behind a confirm step. It is gone entirely.
        //
        // It made sense when a "run" was the unit of play and your save was a score you might want
        // to start over from. Now that the game is a ladder and the level you reached IS the
        // progress, the button's only possible effect is to destroy the thing the player is playing
        // for. RETRY covers "this layout is beating me" and QUIT TO MENU covers "I want out" — no
        // remaining need was being served, so the safest version of this button is no button.

        // Quit to the menu. Lives here rather than on the HUD: it is a deliberate, rare action,
        // and a stray tap on it mid-level would throw away the attempt. Only shown during a level
        // — on the main menu there is nowhere to go home to.
        TMP_Text homeLabel;
        // -173 keeps the 155 row pitch running: 292 / 137 / -18 / -173. It was at -10, which sat
        // directly on top of the REMINDERS row added beside it.
        _settingsHomeBtn = Button_("SettingsHome", root, new Vector2(0.5f, 0.5f), new Vector2(0, -173),
                                   new Vector2(700, 110), Spaced("QUIT TO MENU"), 34, Danger, false, out homeLabel);
        _settingsHomeBtn.onClick.AddListener(() =>
        {
            CloseSettings();
            if (OnHome != null) OnHome();
        });

        TMP_Text closeLabel;
        var closeBtn = Button_("CloseBtn", root, new Vector2(0.5f, 0.5f), new Vector2(0, -460), new Vector2(460, 120),
                               Spaced("CLOSE"), 40, Accent, true, out closeLabel);
        closeBtn.onClick.AddListener(CloseSettings);

        go.SetActive(false);
        return go;
    }

    private Button _settingsHomeBtn;
    private Button _resetBtn;

    /// <summary>
    /// Enable/disable the HUD RETRY button. Held off while a rebuild is in flight so the press
    /// cannot be repeated into the coroutine that is already covering the screen.
    /// </summary>
    public void SetResetInteractable(bool on)
    {
        if (_resetBtn != null) _resetBtn.interactable = on;
    }

    // ---------- in-game gear ----------

    private GameObject BuildInGameGear()
    {
        // Bottom-left of the safe area: far from the thumb's play zone and the top HUD.
        TMP_Text lbl;
        var btn = Button_("GearInGame", _safe, new Vector2(0f, 0f), new Vector2(80, 80), new Vector2(96, 96),
                          "", 1, Accent, false, out lbl);
        btn.onClick.AddListener(OpenSettings);

        var icon = new GameObject("Icon");
        icon.transform.SetParent(btn.transform, false);
        var img = icon.AddComponent<Image>();
        img.sprite = VisualUtils.Gear(); img.raycastTarget = false;
        img.color = new Color(0.8f, 0.9f, 1f, 0.7f);
        var irt2 = img.rectTransform;
        irt2.anchorMin = irt2.anchorMax = new Vector2(0.5f, 0.5f); irt2.pivot = new Vector2(0.5f, 0.5f);
        irt2.anchoredPosition = Vector2.zero; irt2.sizeDelta = new Vector2(58, 58);

        // Two more along the bottom edge. Both are escape hatches players asked for by name:
        // being stuck on a layout you keep dying in, and having no way out of a level at all.
        // Text rather than icons — a glyph for "regenerate this maze" is not a thing anyone reads
        // correctly, and there is room down here.
        TMP_Text resetLbl;
        var resetBtn = Button_("ResetLevelBtn", _safe, new Vector2(0f, 0f), new Vector2(238, 80),
                               new Vector2(190, 96), Spaced("RETRY"), 28, Accent, false, out resetLbl);
        resetBtn.onClick.AddListener(() => { if (OnResetLevel != null) OnResetLevel(); });
        _resetBtn = resetBtn;

        // HOME lives in the settings panel, not out here. Quitting a level is a rare, deliberate
        // action and it sat one thumb-width from the play area; RETRY earns its place on the HUD
        // because it is used mid-level, HOME does not.
        _inGameButtons = new GameObject[] { resetBtn.gameObject };
        return btn.gameObject;
    }

    private GameObject[] _inGameButtons;

    public void ShowInGameGear(bool show)
    {
        if (_gearInGame != null) _gearInGame.SetActive(show);
        if (_inGameButtons == null) return;
        for (int i = 0; i < _inGameButtons.Length; i++)
            if (_inGameButtons[i] != null) _inGameButtons[i].SetActive(show);
    }

    // ---------- teach card ----------

    /// <summary>
    /// One blocking explainer, reused by every moment where the player meets something new: the
    /// tutorial's "here is the exit" step, the first bonus orb, the first decoy.
    ///
    /// Blocking on purpose. Every self-dismissing hint we tried — banners, timed captions — was
    /// read straight past, and players then met the mechanic with no idea what it was ("something
    /// threw me backwards" was a decoy). This stays up until OK is pressed, and GameManager holds
    /// the level frozen the whole time.
    ///
    /// Two layouts, chosen by whether a world target was supplied:
    ///   * no target — card centred over a heavy veil. For things that are hidden anyway.
    ///   * a target  — a marker ring tracks it on screen and the card moves to the OPPOSITE half,
    ///                 so the explanation and the thing it explains are legible together.
    /// </summary>
    private GameObject BuildTeachCard()
    {
        var go = new GameObject("TeachCard");
        go.transform.SetParent(_canvas.transform, false);
        // One CanvasGroup over the whole overlay so the veil and the card fade together. Without
        // it the card scaled up but everything snapped to full opacity on frame one, which is what
        // made an explainer read as "blinking into existence" no matter how nice the scale curve was.
        _teachGroup = go.AddComponent<CanvasGroup>();
        _teachVeil = go.AddComponent<Image>();
        _teachVeil.color = PanelVeil;
        _teachVeil.raycastTarget = true;    // swallows gameplay taps while the card is up
        var vrt = _teachVeil.rectTransform;
        vrt.anchorMin = Vector2.zero; vrt.anchorMax = Vector2.one;
        vrt.offsetMin = Vector2.zero; vrt.offsetMax = Vector2.zero;
        var root = go.transform as RectTransform;

        // ---- the card ----
        var cardGO = new GameObject("Card");
        cardGO.transform.SetParent(root, false);
        _teachCard = cardGO.AddComponent<RectTransform>();
        _teachCard.anchorMin = _teachCard.anchorMax = new Vector2(0.5f, 0.5f);
        _teachCard.pivot = new Vector2(0.5f, 0.5f);
        _teachCard.sizeDelta = new Vector2(900, 560);

        var fillGO = new GameObject("Fill");
        fillGO.transform.SetParent(_teachCard, false);
        _teachFill = fillGO.AddComponent<Image>();
        // CardFill, not PanelSolid: the veil behind this is already near-black, and a card the
        // same value as its own backdrop reads as loose text with brackets floating around it.
        _teachFill.color = CardFill;
        _teachFill.raycastTarget = false;
        var frt = _teachFill.rectTransform;
        frt.anchorMin = Vector2.zero; frt.anchorMax = Vector2.one;
        frt.offsetMin = Vector2.zero; frt.offsetMax = Vector2.zero;

        var frameGO = new GameObject("Frame");
        frameGO.transform.SetParent(_teachCard, false);
        _teachFrame = frameGO.AddComponent<Image>();
        _teachFrame.sprite = _brackets; _teachFrame.type = Image.Type.Sliced;
        _teachFrame.raycastTarget = false;
        var xrt = _teachFrame.rectTransform;
        xrt.anchorMin = Vector2.zero; xrt.anchorMax = Vector2.one;
        xrt.offsetMin = new Vector2(-10, -10); xrt.offsetMax = new Vector2(10, 10);

        // A literal sample of the thing being described — the one place a gameplay colour is
        // allowed on UI chrome, because it IS the gameplay object, just held still.
        var swGO = new GameObject("Swatch");
        swGO.transform.SetParent(_teachCard, false);
        _teachSwatch = swGO.AddComponent<Image>();
        _teachSwatch.sprite = _dotSprite;
        _teachSwatch.raycastTarget = false;
        var swrt = _teachSwatch.rectTransform;
        swrt.anchorMin = swrt.anchorMax = new Vector2(0.5f, 0.5f); swrt.pivot = new Vector2(0.5f, 0.5f);
        swrt.anchoredPosition = new Vector2(0, 178); swrt.sizeDelta = new Vector2(76, 76);

        // Type sizes up hard (54->66 title, 38->48 body). Playtesters were not misreading these
        // cards, they were declining to read them — at 38pt the body scanned as a paragraph of
        // filler rather than a rule of the game. Bigger text with fewer words per line is what
        // makes an explainer feel like something worth stopping for.
        _teachTitle = Display(Text_("TeachTitle", _teachCard, new Vector2(0.5f, 0.5f), new Vector2(0, 88),
                                    new Vector2(880, 96), 66, TextAnchor.MiddleCenter, ""));

        _teachBody = Text_("TeachBody", _teachCard, new Vector2(0.5f, 0.5f), new Vector2(0, -30),
                           new Vector2(TeachBodyW, 200), 48, TextAnchor.MiddleCenter, "");
        _teachBody.color = new Color(0.93f, 0.97f, 1f, 1f);
        _teachBody.textWrappingMode = TextWrappingModes.Normal;   // body copy, unlike every readout
        _teachBody.overflowMode = TextOverflowModes.Overflow;

        _teachOkBtn = Button_("TeachOk", _teachCard, new Vector2(0.5f, 0.5f), Vector2.zero,
                              new Vector2(400, TeachOkH), Spaced("OK"), 42, Accent, true, out _teachOkLabel);
        _teachOkBtn.onClick.AddListener(CloseTeachCard);
        _teachOkRT = _teachOkBtn.transform as RectTransform;

        // ---- marker: a pulsing reticle over a world object ----
        // Built AFTER the card so it draws on top of it. The card is placed in the opposite half
        // of the screen from its target, but on a short maze or a tall card the two can still
        // meet, and the thing being pointed at must never end up behind the words describing it.
        _teachMarker = new GameObject("TeachMarker");
        _teachMarker.transform.SetParent(root, false);
        var mrt = _teachMarker.AddComponent<RectTransform>();
        mrt.anchorMin = mrt.anchorMax = new Vector2(0.5f, 0.5f);
        mrt.pivot = new Vector2(0.5f, 0.5f);
        mrt.sizeDelta = new Vector2(230, 230);

        var ringGO = new GameObject("Ring");
        ringGO.transform.SetParent(mrt, false);
        _teachRing = ringGO.AddComponent<Image>();
        _teachRing.sprite = VisualUtils.HollowRing();
        _teachRing.raycastTarget = false;
        var rrt = _teachRing.rectTransform;
        rrt.anchorMin = Vector2.zero; rrt.anchorMax = Vector2.one;
        rrt.offsetMin = Vector2.zero; rrt.offsetMax = Vector2.zero;

        _teachMarkerLabel = Display(Text_("MarkerLabel", mrt, new Vector2(0.5f, 0.5f), new Vector2(0, 150),
                                          new Vector2(520, 60), 34, TextAnchor.MiddleCenter, ""));

        go.SetActive(false);
        return go;
    }

    // Card metrics. Laid out top-down from these rather than hardcoded positions, because the body
    // is the only variable-height element and a fixed card silently overflowed it into the title
    // and the OK button the moment the copy ran past three lines.
    private const float TeachPad    = 46f;
    private const float TeachWidth  = 950f;   // widened with the type bump so 48pt doesn't over-wrap
    private const float TeachBodyW  = 840f;
    private const float TeachSwatch = 72f;
    private const float TeachTitleH = 90f;    // fits the 66pt title without clipping descenders
    private const float TeachOkH    = 118f;
    private const float TeachGapS   = 26f;   // swatch -> title
    private const float TeachGapT   = 24f;   // title  -> body
    private const float TeachGapB   = 46f;   // body   -> OK

    /// <summary>
    /// Size the card to its content and stack the children down from the top edge. Returns the
    /// card's height so the caller can keep it on screen.
    /// </summary>
    private float LayoutTeachCard(bool hasSwatch)
    {
        _teachBody.ForceMeshUpdate();     // preferredHeight is stale until the mesh is rebuilt
        float bodyH = Mathf.Max(80f, _teachBody.preferredHeight);

        float cardH = TeachPad
                    + (hasSwatch ? TeachSwatch + TeachGapS : 0f)
                    + TeachTitleH + TeachGapT
                    + bodyH + TeachGapB
                    + TeachOkH + TeachPad;
        _teachCard.sizeDelta = new Vector2(TeachWidth, cardH);

        float y = cardH * 0.5f - TeachPad;     // top inner edge, walking downward
        if (hasSwatch)
        {
            _teachSwatch.rectTransform.anchoredPosition = new Vector2(0, y - TeachSwatch * 0.5f);
            y -= TeachSwatch + TeachGapS;
        }
        _teachTitle.rectTransform.anchoredPosition = new Vector2(0, y - TeachTitleH * 0.5f);
        y -= TeachTitleH + TeachGapT;

        _teachBody.rectTransform.sizeDelta = new Vector2(TeachBodyW, bodyH);
        _teachBody.rectTransform.anchoredPosition = new Vector2(0, y - bodyH * 0.5f);
        y -= bodyH + TeachGapB;

        _teachOkRT.anchoredPosition = new Vector2(0, y - TeachOkH * 0.5f);
        return cardH;
    }

    /// <summary>True while an explainer is up — gameplay must treat this as a hard pause.</summary>
    public bool TeachOpen => _teachPanel != null && _teachPanel.activeSelf;

    /// <summary>
    /// Show the explainer and freeze until the player acknowledges it.
    /// <paramref name="accent"/> must be a UI palette colour (it tints chrome); <paramref name="swatch"/>
    /// is the gameplay colour of the thing itself, or clear to hide the sample dot.
    /// Pass a <paramref name="worldTarget"/> to also point at something on screen.
    /// </summary>
    public void ShowTeachCard(string title, string body, Color accent, Color swatch,
                              System.Action onClosed,
                              Camera cam = null, Vector3 worldTarget = default(Vector3),
                              string markerLabel = null)
    {
        if (_teachPanel == null) return;
        HoldLiveBanner();      // a banner already mid-flight would print through the card
        _onTeachClosed = onClosed;
        _teachT = 0f;
        _teachOkShown = int.MinValue;   // force the first countdown digit to be written

        // Take (and clear) whatever ShowTeachCardAtUI staged. Every other caller therefore gets a
        // null target rather than inheriting the previous card's HUD anchor.
        _teachUiTarget = _pendingUiTarget;
        _teachUiTarget2 = _pendingUiTarget2;
        _teachMarkerSize = _pendingUiTarget != null ? _pendingMarkerSize : 230f;
        _pendingUiTarget = null;
        _pendingUiTarget2 = null;
        _pendingMarkerSize = 190f;

        _teachTitle.text = Spaced(title);
        _teachTitle.color = accent;
        Neon(_teachTitle, accent, 0.8f);
        _teachBody.text = body;
        _teachFrame.color = new Color(accent.r, accent.g, accent.b, 0.9f);

        bool hasSwatch = swatch.a > 0.01f;
        _teachSwatch.gameObject.SetActive(hasSwatch);
        if (hasSwatch) _teachSwatch.color = swatch;

        float cardH = LayoutTeachCard(hasSwatch);

        _teachCam = cam;
        _teachWorld = worldTarget;
        bool hasTarget = cam != null || _teachUiTarget != null;
        _teachMarker.SetActive(hasTarget);

        var mrt0 = _teachMarker.GetComponent<RectTransform>();
        mrt0.sizeDelta = new Vector2(_teachMarkerSize, _teachMarkerSize);

        if (hasTarget)
        {
            // A pointed-at object has to stay visible, so the veil only knocks the maze back
            // rather than burying it, and the card takes the half of the screen the target is not
            // in — pushed as far to that edge as it will go without clipping off screen.
            _teachVeil.color = new Color(PanelVeil.r, PanelVeil.g, PanelVeil.b, 0.62f);
            _teachRing.color = accent;
            _teachMarkerLabel.text = markerLabel ?? "";
            _teachMarkerLabel.color = accent;
            Neon(_teachMarkerLabel, accent, 0.75f);

            // Shrink the caption rect to its actual text. This is what makes the edge-clamp in
            // TrackTeachMarker behave: at a fixed 520 wide the label's half-width was 260, so a
            // target at x=458 (the reveal counter) got shunted 190px away from its own reticle
            // just to stay on screen. Sized to content, "REVEALS" is ~160 wide and never needs
            // moving at all.
            _teachMarkerLabel.ForceMeshUpdate();
            float lw = Mathf.Max(80f, _teachMarkerLabel.preferredWidth + 24f);
            _teachMarkerLabel.rectTransform.sizeDelta = new Vector2(lw, 56f);
            TrackTeachMarker();

            // 110 rather than a token gap: pushed to the bottom edge this card lands right on the
            // Android gesture bar / rounded corners, and pushed to the top it fouls the notch.
            float halfCanvas = (_canvas.transform as RectTransform).rect.height * 0.5f;
            float rest = Mathf.Max(0f, halfCanvas - cardH * 0.5f - 110f);
            bool targetHigh = _teachMarker.GetComponent<RectTransform>().anchoredPosition.y > 0f;
            _teachCard.anchoredPosition = new Vector2(0, targetHigh ? -rest : rest);
        }
        else
        {
            _teachVeil.color = new Color(PanelVeil.r, PanelVeil.g, PanelVeil.b, 0.97f);
            _teachCard.anchoredPosition = Vector2.zero;
        }

        // Lock OK until the player has had time to actually read the card.
        if (_teachOkBtn != null)
        {
            _teachOkBtn.interactable = false;
            _teachOkLabel.text = Mathf.CeilToInt(TeachOkLockSeconds).ToString();
            _teachOkLabel.color = new Color(1f, 1f, 1f, 0.45f);
        }

        _teachClosing = false;
        _teachCloseT = 0f;
        if (_teachGroup != null) _teachGroup.alpha = 0f;   // Update fades it up from here
        _teachCard.localScale = Vector3.one * 0.86f;
        _teachPanel.SetActive(true);
        Haptics.Medium();
        if (Audio != null) Audio.PlayTeach();
    }

    private const float TeachInSeconds  = 0.20f;
    private const float TeachOutSeconds = 0.13f;
    private bool  _teachClosing;
    private float _teachCloseT;

    /// <summary>
    /// Begin the dismiss animation. The close callback is deliberately deferred to the END of the
    /// outro (see FinishTeachClose) — the tutorial chains cards back to back, and firing it
    /// immediately would open the next card on top of the one still fading out.
    /// </summary>
    private void CloseTeachCard()
    {
        if (_teachPanel == null || !_teachPanel.activeSelf) return;
        if (_teachClosing) return;               // ignore a second OK press mid-dismiss
        _teachClosing = true;
        _teachCloseT = 0f;
        if (_teachOkBtn != null) _teachOkBtn.interactable = false;
    }

    private void FinishTeachClose()
    {
        _teachClosing = false;
        if (_teachGroup != null) _teachGroup.alpha = 1f;   // reset for the next card
        if (_teachCard != null) _teachCard.localScale = Vector3.one;
        if (_teachPanel != null) _teachPanel.SetActive(false);
        var cb = _onTeachClosed;
        _onTeachClosed = null;      // cleared BEFORE invoking, so a callback that opens another
        if (cb != null) cb();       // card can't have its own callback wiped by this one

        // Release any banner that was held back — but only if the callback didn't immediately
        // open another card, which the tutorial chain does on every step.
        if (!TeachOpen && _pendingBanner != null)
        {
            string m = _pendingBanner; _pendingBanner = null;
            ShowBanner(m, _pendingBannerColor, _pendingBannerHold);
        }
    }

    private string _pendingBanner;
    private Color _pendingBannerColor;
    private float _pendingBannerHold;

    /// <summary>Force the card away without running its callback (bailing out to the menu).</summary>
    public void HideTeachCard()
    {
        _onTeachClosed = null;
        _pendingBanner = null;   // bailing out of the level; a held banner is stale now
        // Hard hide, no outro: this is the "abandon the level" path, and animating a card the
        // player is walking away from just delays the menu. Reset the animation state so the next
        // card doesn't inherit a half-faded group.
        _teachClosing = false;
        _teachCloseT = 0f;
        if (_teachGroup != null) _teachGroup.alpha = 1f;
        if (_teachCard != null) _teachCard.localScale = Vector3.one;
        if (_teachPanel != null) _teachPanel.SetActive(false);
    }

    /// <summary>
    /// Where a HUD element visually sits. For text this is the GLYPH centre, not the rect centre:
    /// a TMP rect is sized for layout and is far bigger than the text in it — the timer's rect
    /// centre is 58px above the actual "34", which is what made the ring swallow the LEVEL
    /// heading. Non-text elements have no such gap, so the rect centre is already right.
    /// </summary>
    private Vector3 UiAnchor(RectTransform rt)
    {
        var tmp = rt.GetComponent<TMP_Text>();
        if (tmp != null && tmp.textInfo != null && tmp.textInfo.characterCount > 0)
            return rt.TransformPoint(tmp.textBounds.center);
        return rt.position;
    }

    /// <summary>Canvas-local centre and half-extent of a HUD element's VISIBLE shape.</summary>
    private Vector2 UiBox(RectTransform rt, RectTransform canvasRT, out Vector2 half)
    {
        var tmp = rt.GetComponent<TMP_Text>();
        bool isText = tmp != null && tmp.textInfo != null && tmp.textInfo.characterCount > 0;
        half = isText ? (Vector2)tmp.textBounds.size * 0.5f : rt.rect.size * 0.5f;

        Vector3 sp = RectTransformUtility.WorldToScreenPoint(null, UiAnchor(rt));
        Vector2 local;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRT, sp, null, out local);
        return local;
    }

    /// <summary>
    /// Keep the marker glued to its target. World targets need re-projecting every frame because
    /// the camera shakes and punch-zooms; UI targets are re-read too, since the safe-area inset
    /// is applied after layout and would otherwise leave the reticle a notch out of place.
    /// </summary>
    private void TrackTeachMarker()
    {
        if (_teachMarker == null) return;

        var canvasRT0 = _canvas.transform as RectTransform;

        Vector3 sp;
        if (_teachUiTarget != null && _teachUiTarget2 != null)
        {
            // Centre of the two shapes' UNION, not the midpoint of their pivots. The reveal
            // readout is a 27x68 digit beside a 40x40 dot sitting 22px lower; averaging the two
            // centres lands 7px above the true centre of the pair, which is visible as the ring
            // riding high. Union it properly and it sits where the eye expects.
            Vector2 hA, hB;
            Vector2 a = UiBox(_teachUiTarget, canvasRT0, out hA);
            Vector2 b = UiBox(_teachUiTarget2, canvasRT0, out hB);
            Vector2 lo = Vector2.Min(a - hA, b - hB);
            Vector2 hi = Vector2.Max(a + hA, b + hB);
            _teachMarker.GetComponent<RectTransform>().anchoredPosition = (lo + hi) * 0.5f;
            UpdateMarkerLabel((lo + hi) * 0.5f, canvasRT0);
            return;
        }

        if (_teachUiTarget != null)
            sp = RectTransformUtility.WorldToScreenPoint(null, UiAnchor(_teachUiTarget));
        else if (_teachCam != null)
            sp = _teachCam.WorldToScreenPoint(_teachWorld);
        else return;

        Vector2 local;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRT0, sp, null, out local);
        _teachMarker.GetComponent<RectTransform>().anchoredPosition = local;
        UpdateMarkerLabel(local, canvasRT0);
    }

    /// <summary>
    /// Keep the caption on screen and clear of what it is labelling.
    ///
    /// Both axes needed fixing. Vertically: the HUD elements live in the top ~100px, so a label
    /// pinned above the ring rendered off-screen entirely. Horizontally: the reveal counter and
    /// the star row sit hard against the right and left edges, and a 520-wide label centred on
    /// them ran off the side — sizing the label to its text is what actually solved that.
    /// </summary>
    private void UpdateMarkerLabel(Vector2 markerLocal, RectTransform canvasRT)
    {
        if (_teachMarkerLabel == null) return;

        float halfH = canvasRT.rect.height * 0.5f;
        float halfW = canvasRT.rect.width * 0.5f;

        // Sits below the ring's own edge, so it scales with the ring rather than being a constant.
        float drop = _teachMarkerSize * 0.5f + 70f;
        bool nearTop = markerLocal.y > halfH - 240f;
        float y = nearTop ? -drop : drop;

        // Slide the label back inside the canvas only if it would genuinely clip.
        float labelHalf = _teachMarkerLabel.rectTransform.rect.width * 0.5f;
        float maxX = Mathf.Max(0f, halfW - labelHalf - 12f);
        float x = Mathf.Clamp(markerLocal.x, -maxX, maxX) - markerLocal.x;

        _teachMarkerLabel.rectTransform.anchoredPosition = new Vector2(x, y);
    }

    /// <summary>
    /// Point the explainer at a piece of the HUD instead of something in the maze. Used by the
    /// tutorial to introduce the clock, the reveal counter and the star row — telling a player
    /// they are on a timer is useless if they don't know which number is the timer.
    /// </summary>
    /// <param name="markerSize">Ring diameter. Text rects are far larger than the glyphs inside
    /// them, so this is sized to what the player actually sees, per target.</param>
    public void ShowTeachCardAtUI(string title, string body, Color accent, Color swatch,
                                  System.Action onClosed, RectTransform uiTarget, string markerLabel,
                                  float markerSize = 190f, RectTransform uiTarget2 = null)
    {
        _pendingUiTarget = uiTarget;   // consumed by ShowTeachCard, so it can't leak to the next card
        _pendingUiTarget2 = uiTarget2;
        _pendingMarkerSize = markerSize;
        ShowTeachCard(title, body, accent, swatch, onClosed, null, default(Vector3), markerLabel);
    }

    private RectTransform _pendingUiTarget, _pendingUiTarget2;
    private float _pendingMarkerSize = 190f;
    private float _teachMarkerSize = 190f;

    // ---------- daily result ----------

    private GameObject BuildDailyResult()
    {
        var go = new GameObject("DailyResult");
        go.transform.SetParent(_canvas.transform, false);
        var dim = go.AddComponent<Image>();
        // Fully opaque: the menu/maze behind must not read through and compete for attention.
        // Uses the shared panel navy — the old 0.02/0.03/0.05 had blue only 1.7x green, which is
        // enough to read olive across a whole screen with nothing else to offset it.
        dim.color = new Color(PanelScrim.r, PanelScrim.g, PanelScrim.b, 1f);
        dim.raycastTarget = true;
        var drt = dim.rectTransform; drt.anchorMin = Vector2.zero; drt.anchorMax = Vector2.one; drt.offsetMin = Vector2.zero; drt.offsetMax = Vector2.zero;
        var root = go.transform as RectTransform;

        var title = Display(Text_("DTitle", root, new Vector2(0.5f, 0.5f), new Vector2(0, 320), new Vector2(1000, 130), 76, TextAnchor.MiddleCenter, "DAILY COMPLETE"));
        title.color = new Color(1f, 0.85f, 0.4f, 1f);

        _dailyResultText = Text_("DBody", root, new Vector2(0.5f, 0.5f), new Vector2(0, 40), new Vector2(1000, 420), 44, TextAnchor.MiddleCenter, "");
        _dailyResultText.color = new Color(0.9f, 0.95f, 1f, 0.95f);

        TMP_Text lbl;
        var okBtn = Button_("DailyOk", root, new Vector2(0.5f, 0.5f), new Vector2(0, -330), new Vector2(460, 130),
                            Spaced("CONTINUE"), 42, Accent, true, out lbl);
        okBtn.onClick.AddListener(() =>
        {
            go.SetActive(false);
            if (OnDailyResultClosed != null) OnDailyResultClosed();
        });

        go.SetActive(false);
        return go;
    }

    public void ShowDailyResult(bool cleared, int score, int dayStreak, bool newBest)
    {
        _dailyResultText.text =
            (cleared ? "You solved today's maze!\n\n" : "Out of time on today's maze.\n\n") +
            "Score   " + score + "\n" +
            "Day streak   " + dayStreak + "\n" +
            (newBest ? "\nNEW DAILY BEST!" : "\nDaily best   " + SaveData.DailyBest) +
            "\n\nA new maze unlocks tomorrow.";
        _dailyResultPanel.SetActive(true);
    }

}
