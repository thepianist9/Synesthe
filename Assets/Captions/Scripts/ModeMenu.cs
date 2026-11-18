using System.Collections.Generic;
using TMPro;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace MetaStarter.Captions
{
    /// <summary>
    /// Start screen: one glass window with a tab bar beside it (ModeMenu.Settings.cs: Modes, Microphone, Settings), after
    /// the visionOS dashboard look. Modes: the two modes (speech bubbles, avatar signing) as tiles, a symbol top left,
    /// a toggle top right (on for the active one, <see cref="CaptionDirector.mode"/>) and the name along the bottom;
    /// under them the caption text-size slider. A grab bar under the window moves it (pinch and drag); turn away from
    /// it and it glides back in front of you. Buttons give a small press and a click.
    /// Shown at every launch in front of the viewer; point with a hand (pinch) or controller (trigger) to choose. With
    /// neither (hands not tracked, e.g. in a dim room, and no controller) it works by gaze: look at a button and hold
    /// your look while the ring around the dot fills (<see cref="DwellSeconds"/>). The
    /// choice is remembered and preselected underneath, so captions already run while the panel is up. Afterwards a
    /// round glass button at the bottom right of the view shows the mode; point at it and pinch (or look at it) to bring
    /// the panel back. It travels with your head but lazily (<see cref="PlaceDock"/>), and shrinks to a faint dot until
    /// you look or point at it. In a conversation with hands tracked there is no button in view at all: pinch and hold
    /// with either hand, pointing at nothing, or double-tap the thumb on the index finger (<see cref="UpdatePinchMenu"/>). Not in the signing modes, where the
    /// hands sign (pinch-like handshapes too) and would open it by accident.
    /// Choosing a mode starts the conversation setup (ModeMenu.Setup.cs); captions wait until it is done.
    /// Editor: B = bubbles, S = signing, Enter = start the conversation, M = microphone tab.
    /// </summary>
    public partial class ModeMenu : MonoBehaviour
    {
        public CaptionDirector director;
        [UnityEngine.Serialization.FormerlySerializedAs("micMenu")]
        public MicSettings micSettings;
        public Transform head;
        public OVRHand leftHand, rightHand;
        public Transform rightController, leftController;
        public Material bubbleMaterial;   // MetaStarter/LiquidGlassBubble
        public Material panelMaterial;    // URP Unlit, transparent: icons, lines, pointer ray
        public TMP_FontAsset font;
        public TMP_FontAsset iconFont;   // filled icons (Material Symbols Rounded), drawn as text so they stay sharp
        public PersonTracker tracker;
        public SignAvatar signAvatar;
        /// <summary>The app's name, shown on the splash and the start screen. A constant, so an old value saved in
        /// the scene can't bring back a previous name.</summary>
        public const string AppName = "Synesthe";
        [Tooltip("Show the start screen at launch (off = start in the remembered mode).")]
        public bool showAtStart = true;
        [Tooltip("Play the logo animation before the start screen.")]
        public bool showSplash = true;
        public Texture2D logo;

        const string PrefKey = "captions.mode", TextKey = "captions.textScale";
        const float Distance = 0.7f, MenuDrop = 0.1f;           // panel: about arm's length, a little below the eyes (m)
        const float W = 0.74f, H = 0.33f, Pad = 0.026f;    // panel size and margin (m)
        const float DockSize = 0.05f, DockSlack = 40f;      // mode button: diameter (m); how far you can turn before it follows (°),
                                                          // more than its own offset (26° right, 28° down) so you can look at it
        const float MinText = 0.8f, MaxText = 1.6f;          // caption size range (×)
        const float DwellSeconds = 1.2f, ChipDwellSeconds = 2f; // gaze: how long to look at a button to press it

        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor"), BaseMapId = Shader.PropertyToID("_BaseMap");
        static readonly int BodyId = Shader.PropertyToID("_Body"), BodyCenterId = Shader.PropertyToID("_BodyCenter");
        static readonly int OrbId = Shader.PropertyToID("_Orb"), QuadId = Shader.PropertyToID("_Quad");
        static readonly int GrowId = Shader.PropertyToID("_Grow"), AlphaId = Shader.PropertyToID("_Alpha");
        static readonly int TintId = Shader.PropertyToID("_Tint"), FrostId = Shader.PropertyToID("_Frost");
        static readonly int BlurId = Shader.PropertyToID("_BlurRadius"), RefractionId = Shader.PropertyToID("_Refraction");
        static readonly int StrengthId = Shader.PropertyToID("_PtStrength"), HoverId = Shader.PropertyToID("_Hover");
        static readonly int ClearId = Shader.PropertyToID("_Clear"), ClearDimId = Shader.PropertyToID("_ClearDim");
        static readonly int GlassFinishId = Shader.PropertyToID("_Glass"), AdaptId = Shader.PropertyToID("_Adapt");

        // Look and sizes from Apple's iOS & iPadOS 27 UI Kit (Figma), dark appearance: Liquid Glass panels, system
        // fills for controls, label colours for text, the system accents. A press shows in cream.
        // Size: 1 pt ≈ 0.72 mm at the panel's 0.7 m, so a Large button (50 pt) is 36 mm.
        const float Pt = 0.00072f;
        const float LargeBtn = 50 * Pt, MediumBtn = 34 * Pt, SmallBtn = 28 * Pt, CloseBtn = 44 * Pt;
        const float ToggleW = 64 * Pt, ToggleH = 28 * Pt, KnobW = 38 * Pt, KnobH = 24 * Pt; // the iOS 26+ switch
        // Fills (dark): Primary #787880 36%, Secondary 32%, Tertiary #767680 24%, Quaternary 18%.
        static readonly Color FillPrimary = Rgba(0x787880, 0.36f), FillSecondary = Rgba(0x787880, 0.32f),
            FillTertiary = Rgba(0x767680, 0.24f), FillQuaternary = Rgba(0x767680, 0.18f);
        static readonly Color SegmentOn = Rgba(0xFFFFFF, 0.27f); // a segmented control's selected segment
        // Accents (dark).
        static readonly Color SysRed = Hex(0xFF4245), SysOrange = Hex(0xFF9230), SysGreen = Hex(0x30D158), SysGray = Hex(0x8E8E93);
        // Backgrounds (dark): elevated secondary #2C2C2E for a sheet over a panel.
        static readonly Color SheetFill = Rgba(0x2C2C2E, 0.96f);
        static readonly Color PanelTint = new(0f, 0f, 0f, 0f);
        static readonly Color CardTint = FillQuaternary, CardActive = FillPrimary;
        static readonly Color Circle = FillSecondary, Track = FillTertiary, Filled = Hex(0xFFFFFF);
        // Meta UI Set button roles: primary (filled accent), secondary (clear glass), borderless (no fill until hovered).
        static readonly Color Borderless = new(1f, 1f, 1f, 0f);
        const float PanelDim = 0.7f; // marks a panel background: frosted, dark glass (clear glass read milky behind text)
        // A panel's glass: the room behind blurred, then the kit's dark Liquid Glass fill (#1A1A1A).
        static readonly Color PanelSmoke = Rgba(0x1A1A1A, 0.94f);
        static readonly Color PanelFrost = Rgba(0x1C1C1E, 0.92f); // the same without the passthrough camera (secondary background)
        static readonly Color TileInk = Hex(0x1A1A1A); // a symbol on a light tile (the glass button label colour)
        // Labels (dark): Primary #FFF, Secondary #EBEBF5 70%, Tertiary #EBEBF5 30%.
        static readonly Color Primary = Hex(0xFFFFFF), Secondary = Rgba(0xEBEBF5, 0.7f), Tertiary = Rgba(0xEBEBF5, 0.3f);
        static readonly Color Button = FillSecondary, Accent = SysOrange;
        static Color Hex(int rgb) => Rgba(rgb, 1f);
        static Color Rgba(int rgb, float a) => new(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, a);
        // Each mode's own colour: its tile's symbol, its tile while chosen, its setup and the mode button.
        // Bubbles in the system orange; subtitles cream, signing light grey, sign to speech grey.
        static readonly Color BubblesColor = SysOrange, SigningColor = Hex(0xD9DADF),
            SpeechColor = Hex(0xADACA7), SubtitlesColor = Hex(0xFCF8D8);

        static Color ModeColor(CaptionDirector.CaptionMode m) => m switch
        {
            CaptionDirector.CaptionMode.Signing => SigningColor,
            CaptionDirector.CaptionMode.SignToSpeech => SpeechColor,
            CaptionDirector.CaptionMode.Subtitles => SubtitlesColor,
            _ => BubblesColor,
        };

        static Color WithAlpha(Color c, float a) => new(c.r, c.g, c.b, a);

        // Everything on the panel, so it can fade as one.
        sealed class Glass
        {
            public MeshRenderer R; public Vector2 Size; public float Radius, Bevel; public Color Tint; public float Hover, Press, Held; public bool Hidden; public float Dim = 0.16f;
            public bool Glide; public Vector2 To;   // Glide: slides to To instead of jumping (toggle knobs, page dots)
            public Vector2 Grow;                    // extra size when pointed at (the grab bar widens from a hairline)
            public Color Shown; public float Vis; public bool Started; // what's on screen: tint and visibility ease to the new ones
        }
        sealed class Label { public TextMeshPro T; public Color C; public bool Hidden; public float Vis; public bool Started; }
        // "Getting ready · Loading speech ▬▬▭": a small capsule under a start panel while the models load, then "Ready".
        sealed class ReadyPill { public Glass Track, Fill; public Label Text; public float X0, Bar, Y, Shown, ReadyAt = -1f; public bool Short; public Panel Panel; }
        ReadyPill _menuPill, _setupPill;
        // A capsule slider (Control Center style: no knob, the fill is the value), along x or (Vertical) up y.
        // A0..A1: where the value runs along its axis (local to Root); Cross: the other coordinate of its middle.
        sealed class Slider
        {
            public Collider Hit; public Glass Track, Fill; public Transform Root; public bool Vertical;
            public float A0, A1, Cross;
            public System.Func<float> Get; public System.Action<float> Set; public System.Action Done; // values 0..1
        }
        readonly List<Slider> _sliders = new();
        Slider _dragSlider, _textSlider;
        sealed class Panel
        {
            public Transform Root;
            public readonly List<Glass> Glass = new();
            public readonly List<Label> Labels = new();
            public bool Open;
            public float Fade;
            public float Yaw, Dist, Drop;  // where it opens: turned from your view (°), how far, how low (m)
            public float Away;             // how long you've been turned away from it
            public bool Returning;
            public Vector3 Vel;
            public float Dim;              // fades a little while it's being moved
            public float Swap = 1f;        // 0 → 1 after its content changes (a new step or tab): the text fades in
        }
        Panel _menu, _setup, _dock, _building; // _building: the panel Add* calls add to (and _group: the tab)
        readonly Dictionary<Collider, System.Action> _clicks = new();
        readonly Dictionary<Collider, Glass> _hovers = new();
        readonly Dictionary<Collider, Panel> _owner = new();
        readonly Dictionary<Collider, Panel> _grabs = new(); // grab bars
        readonly HashSet<Collider> _backings = new();        // each panel's whole face: the ray stops on it

        Transform _root;
        Glass _bubblesCard, _signingCard, _speechCard, _subtitlesCard, _fill, _track;
        Toggle _bubblesToggle, _signingToggle, _speechToggle, _subtitlesToggle;
        Label _hint, _dockText;
        Label _dockIcon;
        Glass _dockGlass;
        Vector3 _pointAt;       // where the ray meets the hovered button, for its glow
        Glass _pointGlass;
        float _iconScale;
        Collider _slider, _dockHit;
        float _handsSeenAt = -999f, _dockWake;
        OVRHand _rayHand;             // the hand whose ray points this frame (null: controller or gaze)
        OVRInput.Controller _rayController, _buzzing; // the controller whose ray points (None: a hand or gaze); one vibrating
        float _buzzUntil;
        // Pointing forgiveness, in metres on the panel: a ray this close to a button is on it; once on a button, the ray
        // has to leave it by this much to let go (more while a pinch is closing, which swings the ray a little).
        const float NearMiss = 0.008f, LeaveMargin = 0.006f, PinchMargin = 0.02f;
        // The ray's look: it starts a little out from the fingers, bends (gently) onto the button it snapped to, and
        // reaches this far when it points at nothing. Its tip glides after the hand instead of shaking with it.
        const float RayGap = 0.035f, FreeLength = 0.6f;
        // Meta Interaction SDK style: a short glowing ray out of the hand that fades out, pointing at the cursor on the
        // panel; the cursor (the spot) shows where it lands. Not a line all the way to the target.
        const float ShortRay = 0.12f;
        const int RayPoints = 20;
        Vector3 _tip;
        bool _tipValid;
        float _rayAlpha, _glintAge = 1f;
        MaterialPropertyBlock _rayMpb;
        readonly Gradient _rayGradient = new();
        readonly GradientColorKey[] _rayColors = new GradientColorKey[2];
        readonly GradientAlphaKey[] _rayAlphas = new GradientAlphaKey[3];
        static readonly int GlintId = Shader.PropertyToID("_Glint");
        object _pointTarget;          // what that ray (or gaze) is on: a button, a face, the avatar
        float _pinchHeld;
        bool _pinchFired;
        // The held pinch that opened the menu presses nothing in it: letting go makes the pinch flicker (a "new" pinch
        // on whatever the lowered hand points at, usually Back to menu). Presses wait until it's been let go a moment.
        bool _holdOpened;
        float _letGoAt = -1f;
        const float LetGoSeconds = 0.3f;
        const float PinchHoldSeconds = 2f;   // a held pinch (either hand) opens the menu; a quick one is still a click
        OVRHand _menuHand;                   // the hand holding that pinch
        // Thumb double-tap (microgesture: thumb tip tapped on the side of the index finger, twice) also opens it.
        readonly float[] _tapAt = { -9f, -9f };
        readonly bool[] _tapWas = new bool[2];
        const float DoubleTapSeconds = 0.6f;
        bool _handsLostHint;
        const float PinchRingAfter = 0.35f;  // the ring shows once a pinch is clearly being held, not for clicks
        LineRenderer _pinchRing;
        float _sliderX0, _sliderX1, _sliderY;
        float _dockYaw, _dockPitch, _dockStill, _lastYaw, _lastPitch;
        bool _dockPlaced, _dockHover;
        Panel _moving;          // a panel being carried by its grab bar
        bool _carryStrip;       // the subtitle line being moved (Subtitles mode: pinch it and drag)
        float _stripDist;
        float _moveDist;
        Vector3 _moveOffset;
        Collider _hoverWas;     // for the hover sound
        int _sliderStep = -1;   // for the slider's ticks
        LineRenderer _ray, _ring;
        object _gazeOn; // what the gaze dwell is timing: a button, a face or the avatar
        float _dwell;
        bool _gaze;
        Transform _cursor;
        RayCursor _spot;
        MaterialPropertyBlock _mpb;
        Color _frost;
        float _fontScale, _closeAt = -1f;
        bool _pinchWas;
        bool _dragging => _dragSlider != null;
        bool _resumeOnClose; // the menu paused a conversation: closing it carries on
        int _summarisedSaid; // captions in the running conversation when the menu last asked for its summary

        void Awake()
        {
            director.live = !showAtStart; // with the start screen, captions wait for the setup
            director.mode = (CaptionDirector.CaptionMode)PlayerPrefs.GetInt(PrefKey, (int)CaptionDirector.CaptionMode.Bubbles);
            CaptionBubble.TextScale = Mathf.Clamp(PlayerPrefs.GetFloat(TextKey, 1f), MinText, MaxText);
            if (director != null && PlayerPrefs.HasKey(HoldKey))
                director.SetCaptionHold(Mathf.Clamp(PlayerPrefs.GetFloat(HoldKey), MinHold, MaxHold));
            if (PlayerPrefs.HasKey(LinesKey)) CaptionBubble.Lines = Mathf.Clamp(PlayerPrefs.GetInt(LinesKey), MinLines, MaxLines);
        }

        void Start()
        {
            Build();
            // Sign to speech reads the wearer's hands (made here: it needs the same head, hands and look as the menu).
            if (GetComponent<SignReader>() == null)
            {
                var reader = gameObject.AddComponent<SignReader>();
                reader.director = director;
                reader.signAvatar = signAvatar;
                reader.head = head;
                reader.leftHand = leftHand;
                reader.rightHand = rightHand;
                reader.bubbleMaterial = bubbleMaterial;
                reader.font = font;
                reader.handMaterial = panelMaterial;
            }
            micSettings.Changed += MicsChanged;
            micSettings.Offered += OfferMic;
            micSettings.Message += TellMic;
            StartConversationLog();
            BuildPointer();
            if (showAtStart && showSplash)
            {
                var splash = gameObject.AddComponent<SplashLogo>();
                splash.head = head;
                splash.glassMaterial = bubbleMaterial;
                splash.imageMaterial = panelMaterial;
                splash.font = font;
                splash.title = AppName;
                splash.Play(() => { if (_startedFromLaunch) ShowDock(); else if (Onboarded && !director.welcomeNow) Open(); else OpenWelcome(); }); // -e start 1 already began one
            }
            else if (showAtStart) { if (Onboarded && !director.welcomeNow) Open(); else OpenWelcome(); }
            else ShowDock();
        }

        // ---------------- building ----------------

        /// <summary>Create the panel (hidden). Also used by the Editor preview, outside Play mode.</summary>
        public void Build()
        {
            _mpb ??= new MaterialPropertyBlock();
            _frost = bubbleMaterial.GetColor(FrostId);
            _fontScale = MeasureFontScale(font, "Ag");
            _iconScale = MeasureFontScale(iconFont, Glyphs.Close);
            _root = new GameObject("Mode panel").transform;
            _root.SetParent(transform, false);
            _building = _menu = new Panel { Root = _root, Dist = Distance, Drop = MenuDrop };
            _group = null;

            AddGlass(_root, Vector2.zero, new Vector2(W, H), 0.048f, PanelTint, 0, bevel: 0.008f).Dim = PanelDim;
            AddBacking(_root, new Vector2(W, H));
            float left = -W / 2 + Pad, right = W / 2 - Pad, top = H / 2;
            _titleAt = new Vector2(left + 0.004f, top - 0.04f);
            _title = AddLabel(_root, "", _titleAt, 0.026f, Primary, TextAlignmentOptions.MidlineLeft);
            BuildTabBar();

            // Modes: a 2 × 2 grid of wide tiles; the whole tile is the button.
            _group = _modesTab = new Group();
            const float gap = 0.012f, rowGap = 0.01f;
            var tw = (right - left - gap) / 2;
            var tileTop = top - 0.07f;
            var tileH = 0.164f;                  // both rows together, as tall as the one row of four was
            var rh = (tileH - rowGap) / 2;
            var row2 = tileTop - rh - rowGap;
            ModeTile(left, tileTop, tw, rh, Glyphs.Captions, "Subtitles", "Just the words, like a video",
                CaptionDirector.CaptionMode.Subtitles, out _subtitlesCard, out _subtitlesToggle);
            ModeTile(right - tw, tileTop, tw, rh, Glyphs.Bubbles, "Speech bubbles", "Captions by each person",
                CaptionDirector.CaptionMode.Bubbles, out _bubblesCard, out _bubblesToggle);
            ModeTile(left, row2, tw, rh, Glyphs.Signing, "Avatar signing", "ASL by an avatar you place",
                CaptionDirector.CaptionMode.Signing, out _signingCard, out _signingToggle);
            ModeTile(right - tw, row2, tw, rh, Glyphs.SignToSpeech, "Sign to speech", "Your signing, said aloud",
                CaptionDirector.CaptionMode.SignToSpeech, out _speechCard, out _speechToggle);

            // Text size: a thick capsule like a brightness slider, filled in white with the text-size symbol in it.
            const float sh = 0.046f;
            var rowY = tileTop - tileH - 0.014f - sh / 2;
            _sliderX0 = left + sh / 2;
            _sliderX1 = right - sh / 2;
            _sliderY = rowY;
            _track = AddGlass(_root, new Vector2((left + right) / 2, rowY), new Vector2(right - left, sh), sh / 2, Track, 1, bevel: 0.004f);
            _fill = AddGlass(_root, Vector2.zero, Vector2.one * sh, sh / 2, Filled, 2, bevel: 0.002f);
            _fill.Dim = 0f;
            AddIcon(_root, Glyphs.TextSize, new Vector2(left + 0.024f, rowY), 0.02f, TileInk, 3);
            AddLabel(_root, "A", new Vector2(right - 0.026f, rowY), 0.022f, Secondary, TextAlignmentOptions.Midline, 0.03f, layer: 3);
            _slider = AddHit(_root, new Vector2((left + right) / 2, rowY), new Vector2(right - left, sh + 0.006f), null, _track);
            _textSlider = AddSlider(_slider, _track, _fill, _root, false, _sliderX0, _sliderX1, _sliderY,
                () => Mathf.InverseLerp(MinText, MaxText, CaptionBubble.TextScale), v => SetTextScale(Mathf.Lerp(MinText, MaxText, v)),
                SaveTextScale);

            _hint = AddLabel(_root, GazeHint, new Vector2(0, -H / 2 + 0.014f), 0.0125f,
                Tertiary, TextAlignmentOptions.Midline);

            _menuPill = AddReadyPill(_root, 0.12f, -H / 2 + 0.014f);

            BuildMicTab(left, right, top);
            BuildTopicTab(left, right, top);
            BuildLanguagePicker(left, right, top);
            BuildHistoryTab(left, right, top);
            _group = null;
            AddGrabBar(_root, W, H, Close);
            ShowTab(Tab.Modes);

            Refresh();
            _root.gameObject.SetActive(false);
            BuildSetup();
            BuildDock();
            BuildQuick();
            BuildAsk();
            BuildAlerts();
            BuildSounds();
        }

        // A wide tile of the 2 × 2 grid: the mode's symbol on the left, its name and a line about it, the toggle right.
        void ModeTile(float x, float top, float w, float h, string glyph, string name, string detail,
            CaptionDirector.CaptionMode mode, out Glass card, out Toggle toggle)
        {
            var c = new Vector2(x + w / 2, top - h / 2);
            card = AddGlass(_root, c, new Vector2(w, h), 0.03f, CardTint, 1, bevel: 0.006f);
            var ip = new Vector2(x + 0.016f + 0.023f, c.y);
            AddGlass(_root, ip, Vector2.one * 0.046f, 0.023f, WithAlpha(ModeColor(mode), 0.8f), 2, bevel: 0.003f);
            AddIcon(_root, glyph, ip, 0.026f, Primary, 3);
            toggle = AddToggle(new Vector2(x + w - 0.016f - ToggleW / 2, c.y));
            var tx = ip.x + 0.023f + 0.014f;
            var tw = x + w - 0.016f - ToggleW - 0.01f - tx;
            AddLabel(_root, name, new Vector2(tx, c.y + 0.011f), 0.019f, Primary, TextAlignmentOptions.MidlineLeft, tw);
            AddLabel(_root, detail, new Vector2(tx, c.y - 0.013f), 0.0115f, Secondary, TextAlignmentOptions.MidlineLeft, tw);
            AddHit(_root, c, new Vector2(w, h), () => Choose(mode), card);
        }

        // A line of text and a short bar ending at x (the start screen's hint row; the welcome's step line, right side,
        // in short form: just the step).
        ReadyPill AddReadyPill(Transform root, float x, float y, bool shortText = false)
        {
            const float pw = 0.24f, bar = 0.05f, bh = 0.005f, gap = 0.012f;
            var p = new ReadyPill { X0 = x - bar, Bar = bar, Y = y, Short = shortText, Panel = _building };
            p.Text = AddLabel(root, "", new Vector2(x - pw, y), 0.0125f, Secondary, TextAlignmentOptions.MidlineRight, pw - bar - gap);
            p.Text.T.rectTransform.pivot = new Vector2(0, 0.5f);
            p.Text.T.textWrappingMode = TextWrappingModes.NoWrap;
            p.Track = AddGlass(root, new Vector2(p.X0 + bar / 2, y), new Vector2(bar, bh), bh / 2, Track, 1, bevel: 0.001f, round: true);
            p.Track.Dim = 0f;
            p.Fill = AddGlass(root, new Vector2(p.X0 + bh / 2, y), new Vector2(bh, bh), bh / 2, Filled, 2, bevel: 0.001f, round: true);
            p.Fill.Dim = 0f;
            SetPill(p, director == null || director.Starting);
            return p;
        }

        static void SetPill(ReadyPill p, bool show) => p.Track.Hidden = p.Fill.Hidden = p.Text.Hidden = !show;

        // While the models load: the step and a bar that creeps on between steps (so a long one doesn't look stuck),
        // then "Models ready" for a moment, and it fades. Hidden at once if the startup failed (the error bubble says why).
        void UpdateReadyPill(ReadyPill p)
        {
            if (p == null || director == null) return;
            var loading = director.Starting && !director.StartupFailed;
            if (!loading && p.ReadyAt < 0f && (p.Panel == null || p.Panel.Fade >= 0.99f)) p.ReadyAt = Time.time;
            // "Models ready" for 2 s once the panel is fully in, also when the loading finished behind the splash.
            var show = loading || (!director.StartupFailed && (p.ReadyAt < 0f || Time.time - p.ReadyAt < 2f));
            SetPill(p, show);
            if (!show) return;
            var creep = Mathf.Min(0.1f, (Time.time - director.StartupStepAt) * 0.02f);
            var goal = loading ? Mathf.Min(0.97f, director.StartupProgress + creep) : 1f;
            p.Shown = Mathf.MoveTowards(p.Shown, Mathf.Max(p.Shown, goal), Time.deltaTime * (loading ? 0.6f : 2f));
            var text = loading ? (p.Short ? director.StartupStep : "Getting ready · " + director.StartupStep)
                : director.StartupStep.Replace("Ready", "Models ready");
            if (p.Text.T.text != text) p.Text.T.text = text;
            p.Text.C = loading ? Secondary : Primary;
            var bh = p.Track.Size.y;
            var len = Mathf.Lerp(bh, p.Bar, Mathf.SmoothStep(0f, 1f, p.Shown));
            p.Fill.Size = new Vector2(len, bh);
            var z = p.Fill.R.transform.localPosition.z;
            p.Fill.R.transform.localPosition = new Vector3(p.X0 + len / 2, p.Y, z);
            p.Fill.Tint = loading ? Filled : SysGreen;
        }

        // The window controls under a panel, as on visionOS: a grab bar (pinch it and drag to move the panel) and,
        // when given, a small round close button to its left.
        // Under every panel, one capsule of the same dark glass as the panels: the close button (a round button like the
        // menu's others) at its left end, the grab line filling the rest. Pointing at the line lights the whole capsule;
        // pinch there and the panel follows your hand.
        void AddGrabBar(Transform root, float panelW, float panelH, System.Action close = null)
        {
            const float h = CloseBtn * 0.85f, inner = SmallBtn, pad = 0.012f;
            const float w = 60 * Pt; // the kit's grabber: 60 × 4 pt     // the grab line
            var lead = close != null ? h : 0f;                       // room for the close button
            var cw = lead + w + 2 * pad;
            var y = -panelH / 2 - 0.01f - h / 2;
            var capsule = AddGlass(root, new Vector2(0, y), new Vector2(cw, h), h / 2, PanelTint, 0, bevel: 0.003f, round: true);
            capsule.Dim = PanelDim;
            var lineAt = new Vector2(-cw / 2 + lead + pad + w / 2, y);
            var line = AddGlass(root, lineAt, new Vector2(w, 4 * Pt), 2 * Pt, Tertiary, 1, bevel: 0.001f, round: true);
            line.Dim = 0f;
            _grabs[AddHit(root, lineAt, new Vector2(w + 2 * pad, h + 0.012f), null, capsule)] = _building;
            if (close == null) return;
            var at = new Vector2(-cw / 2 + h / 2, y);
            var dot = AddGlass(root, at, Vector2.one * inner, inner / 2, Circle, 1, bevel: 0.003f, round: true);
            AddIcon(root, Glyphs.Close, at, 0.013f, Primary, 2);
            AddHit(root, at, new Vector2(h + 0.006f, h + 0.012f), close, dot);
        }

        // The mode button: a round glass button with the mode's icon and a word under it.
        void BuildDock()
        {
            var root = new GameObject("Mode button").transform;
            root.SetParent(transform, false);
            _building = _dock = new Panel { Root = root };
            var button = _dockGlass = AddGlass(root, Vector2.zero, Vector2.one * DockSize, DockSize / 2, PanelTint, 0, bevel: 0.004f);
            button.Dim = 0.32f;
            _dockIcon = AddIcon(root, Glyphs.Bubbles, Vector2.zero, DockSize * 0.5f, Primary, 2);
            _dockText = AddLabel(root, "", new Vector2(0, -DockSize / 2 - 0.013f), 0.012f, Primary, TextAlignmentOptions.Midline, 0.14f);
            // During a conversation it opens the conversation menu (save, past conversations, back to the menu).
            _dockHit = AddHit(root, Vector2.zero, Vector2.one * (DockSize + 0.012f), () => { if (director.live || _paused) OpenQuick(); else Open(); }, button);
            root.gameObject.SetActive(false);
        }


        // The house style is tailored rather than bubbly: anything bigger than a dot or a toggle gets half the corner it
        // asks for (pills become soft rectangles, round icon buttons rounded squares), and every rim is shallower.
        const float Tailored = 0.5f, Flatter = 0.6f, KeepRoundBelow = 0.03f;

        Glass AddGlass(Transform parent, Vector2 pos, Vector2 size, float radius, Color tint, int layer, float bevel = 0.0025f,
            bool round = false) // round: a true circle or capsule (the conversation menu's toggles), not tailored
        {
            if (!round && Mathf.Min(size.x, size.y) > KeepRoundBelow) radius *= Tailored;
            bevel *= Flatter;
            var q = Quad("Glass", parent, pos, layer);
            var r = q.GetComponent<MeshRenderer>();
            r.sharedMaterial = bubbleMaterial;
            var g = new Glass { R = r, Size = size, Radius = radius, Bevel = bevel, Tint = tint };
            _building.Glass.Add(g);
            _group?.Glass.Add(g);
            return g;
        }

        // A symbol from the icon font: sharp at any size.
        Label AddIcon(Transform parent, string glyph, Vector2 pos, float size, Color color, int layer)
        {
            var l = AddLabel(parent, glyph, pos, size, color, TextAlignmentOptions.Center, size * 1.5f, layer);
            l.T.font = iconFont;
            l.T.fontSize = size * _iconScale;
            l.T.textWrappingMode = TextWrappingModes.NoWrap;
            l.T.rectTransform.sizeDelta = new Vector2(size * 1.5f, size * 1.5f);
            return l;
        }

        Label AddLabel(Transform parent, string text, Vector2 pos, float lineHeight, Color color, TextAlignmentOptions align,
            float width = 0.3f, int layer = 2)
        {
            var go = new GameObject("Text");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(pos.x, pos.y, -0.003f * layer);
            var t = go.AddComponent<TextMeshPro>();
            if (font != null) t.font = font;
            ComplexText.Attach(t);
            t.text = text;
            t.fontSize = lineHeight * _fontScale;
            t.alignment = align;
            t.textWrappingMode = TextWrappingModes.Normal;
            t.overflowMode = TextOverflowModes.Overflow;
            t.rectTransform.sizeDelta = new Vector2(width, lineHeight * 1.5f);
            t.rectTransform.pivot = align == TextAlignmentOptions.MidlineLeft ? new Vector2(0, 0.5f)
                : align == TextAlignmentOptions.TopLeft ? new Vector2(0, 1) : new Vector2(0.5f, 0.5f);
            var r = t.GetComponent<MeshRenderer>();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.sortingOrder = 10 + layer;
            var l = new Label { T = t, C = color };
            _building.Labels.Add(l);
            _group?.Labels.Add(l);
            return l;
        }

        // The panel's face, solid to the ray: pointing at a gap between buttons ends the ray (and the dot) on the
        // glass, instead of the ray passing through to the room behind. Behind the buttons' own colliders.
        void AddBacking(Transform root, Vector2 size)
        {
            var go = new GameObject("Backing");
            go.transform.SetParent(root, false);
            go.transform.localPosition = new Vector3(0, 0, -0.001f);
            var c = go.AddComponent<BoxCollider>();
            c.size = new Vector3(size.x, size.y, 0.002f);
            _owner[c] = _building;
            _backings.Add(c);
        }

        Collider AddHit(Transform parent, Vector2 pos, Vector2 size, System.Action click, Glass hover)
        {
            var go = new GameObject("Hit");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(pos.x, pos.y, -0.01f);
            var c = go.AddComponent<BoxCollider>();
            c.size = new Vector3(size.x, size.y, 0.01f);
            if (click != null) _clicks[c] = click;
            if (hover != null) _hovers[c] = hover;
            _owner[c] = _building;
            _group?.Hits.Add(c);
            return c;
        }

        static Transform Quad(string name, Transform parent, Vector2 pos, int layer)
        {
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            var col = q.GetComponent<Collider>();
            if (Application.isPlaying) Destroy(col); else DestroyImmediate(col);
            q.name = name;
            q.transform.SetParent(parent, false);
            // Nearer layers sit a little in front, and draw later (transparent, no depth writes).
            q.transform.localPosition = new Vector3(pos.x, pos.y, -0.003f * layer);
            var r = q.GetComponent<MeshRenderer>();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.sortingOrder = layer;
            return q.transform;
        }

        static float MeasureFontScale(TMP_FontAsset f, string sample)
        {
            var go = new GameObject("Measure");
            var t = go.AddComponent<TextMeshPro>();
            if (f != null) t.font = f;
            t.fontSize = 1f;
            var h = t.GetPreferredValues(sample, 100f, 100f).y;
            if (Application.isPlaying) Destroy(go); else DestroyImmediate(go);
            return h > 1e-5f ? 1f / h : 10f;
        }

        // ---------------- state ----------------

        // In the menu no mode is on: a tile lights only for the conversation running in it, or for a moment after you
        // choose it (on the way to its setup).
        void Refresh()
        {
            var m = director.live || _closeAt > 0 ? director.mode : (CaptionDirector.CaptionMode)(-1);
            SetToggle(_bubblesToggle, m == CaptionDirector.CaptionMode.Bubbles);
            SetToggle(_signingToggle, m == CaptionDirector.CaptionMode.Signing);
            SetToggle(_speechToggle, m == CaptionDirector.CaptionMode.SignToSpeech);
            SetToggle(_subtitlesToggle, m == CaptionDirector.CaptionMode.Subtitles);
            _subtitlesCard.Tint = m == CaptionDirector.CaptionMode.Subtitles ? WithAlpha(SubtitlesColor, 0.22f) : CardTint;
            _bubblesCard.Tint = m == CaptionDirector.CaptionMode.Bubbles ? WithAlpha(BubblesColor, 0.22f) : CardTint;
            _signingCard.Tint = m == CaptionDirector.CaptionMode.Signing ? WithAlpha(SigningColor, 0.22f) : CardTint;
            _speechCard.Tint = m == CaptionDirector.CaptionMode.SignToSpeech ? WithAlpha(SpeechColor, 0.22f) : CardTint;
        }

        // Where a panel opens, and where it comes back to: in front of you (turned by its Yaw), a little low.
        Vector3 FrontOf(Panel p) =>
            head.position + Quaternion.AngleAxis(p.Yaw, Vector3.up) * Flat(head.forward) * p.Dist + Vector3.down * p.Drop;

        void Place(Panel p, Vector3 pos) => p.Root.SetPositionAndRotation(pos, Quaternion.LookRotation(pos - head.position, Vector3.up));

        void Open()
        {
            // A conversation pauses while the menu is up (nothing is transcribed or tracked) and goes on when it closes.
            if (director.live) { director.live = false; director.StartFresh(); _resumeOnClose = true; }
            // Out of the mode: summarise what's been said so far now (made again, whole, when the conversation ends).
            if (_log != null && _log.Saving && _log.Said > _summarisedSaid && _summarizer != null)
            {
                _summarisedSaid = _log.Said;
                _summarizer.Request(_log.CurrentId);
            }
            if (_paused) { _paused = false; _resumeOnClose = true; } // paused from the conversation menu: closing carries on
            _menu.Open = true;
            _setup.Open = false;
            if (_quick != null) _quick.Open = false;
            _closeAt = -1f;
            Place(_menu, FrontOf(_menu)); // stays put while open: steady to point at
            _menu.Returning = false;
            _root.gameObject.SetActive(true);
            _dock.Open = false;
            ShowTab(Tab.Modes);
            Refresh();
        }

        /// <summary>Editor preview: open at once, fully faded in, in front of <paramref name="viewer"/>.</summary>
        public void OpenForPreview(Transform viewer, bool micTab = false, bool settingsTab = false)
        {
            if (micTab) ShowTab(Tab.Mic);
            if (settingsTab) ShowTab(Tab.Topic);
            var pos = viewer.position + viewer.forward * Distance;
            _root.SetPositionAndRotation(pos, Quaternion.LookRotation(pos - viewer.position, Vector3.up));
            _root.gameObject.SetActive(true);
            Refresh();
            _menu.Fade = _menu.Swap = 1f; // a still picture: skip the animations
            Apply();
        }

        void Close()
        {
            _pickForQuick = false;
            if (_resumeOnClose) { _resumeOnClose = false; director.StartFresh(); director.live = true; }
            _menu.Open = false;
            _dragSlider = null;
            UiFeedback.Play(UiSound.Back, _root.position);
            ShowDock();
        }

        void Choose(CaptionDirector.CaptionMode mode)
        {
            var same = director.mode == mode;
            director.mode = mode;
            PlayerPrefs.SetInt(PrefKey, (int)mode);
            PlayerPrefs.Save();
            Debug.Log($"[Captions] Mode: {mode}");
            Refresh();
            UiFeedback.Play(UiSound.Confirm, _root.position);
            _closeAt = Time.time + (same ? 0.15f : 0.6f); // a moment to see the change, then on to the setup
        }

        Slider AddSlider(Collider hit, Glass track, Glass fill, Transform root, bool vertical, float a0, float a1, float cross,
            System.Func<float> get, System.Action<float> set, System.Action done)
        {
            var s = new Slider { Hit = hit, Track = track, Fill = fill, Root = root, Vertical = vertical, A0 = a0, A1 = a1, Cross = cross,
                Get = get, Set = set, Done = done };
            _sliders.Add(s);
            return s;
        }

        Slider SliderOf(Collider c) => c == null ? null : _sliders.Find(s => s.Hit == c);
        Slider _gazeSliderUsed;

        static float SliderValue(Slider s, Vector3 world)
        {
            var p = s.Root.InverseTransformPoint(world);
            return Mathf.InverseLerp(s.A0, s.A1, s.Vertical ? p.y : p.x);
        }

        // The fill grows from the start end to the value; it is never shorter than the capsule is thick.
        static void LayoutSlider(Slider s)
        {
            var thick = s.Vertical ? s.Track.Size.x : s.Track.Size.y;
            var len = Mathf.Lerp(s.A0, s.A1, Mathf.Clamp01(s.Get())) - s.A0 + thick;
            var z = s.Fill.R.transform.localPosition.z;
            var mid = s.A0 - thick / 2 + len / 2;
            s.Fill.Size = s.Vertical ? new Vector2(thick, len) : new Vector2(len, thick);
            s.Fill.R.transform.localPosition = s.Vertical ? new Vector3(s.Cross, mid, z) : new Vector3(mid, s.Cross, z);
        }

        void SaveTextScale()
        {
            PlayerPrefs.SetFloat(TextKey, CaptionBubble.TextScale);
            PlayerPrefs.Save();
        }

        void SetTextScale(float s)
        {
            CaptionBubble.TextScale = Mathf.Clamp(s, MinText, MaxText);
            // A tick per tenth of the range, so you can feel the size change.
            var step = Mathf.RoundToInt(Mathf.InverseLerp(MinText, MaxText, CaptionBubble.TextScale) * 10);
            if (_sliderStep >= 0 && step != _sliderStep) UiFeedback.Play(UiSound.Tick, _fill.R.transform.position);
            _sliderStep = step;
        }

        void ShowDock()
        {
            var signing = director.mode == CaptionDirector.CaptionMode.Signing;
            var speech = director.mode == CaptionDirector.CaptionMode.SignToSpeech;
            _dockFallback = director.TrackingFallback;
            _dockIcon.T.text = signing ? Glyphs.Signing : speech ? Glyphs.SignToSpeech
                : director.Subtitles || director.TrackingFallback ? Glyphs.Captions : Glyphs.Bubbles;
            // Always the round button (a capsule with a status line read as a switch): the mode's icon, and under it
            // the mode's name in a conversation, "Set up" before one.
            var inConversation = director.live || _paused;
            _dockGlass.Size = Vector2.one * DockSize;
            _dockGlass.Radius = DockSize / 2 * Tailored;
            _dockGlass.Tint = PanelTint;
            _dockGlass.Dim = 0.32f;
            At(_dockIcon, Vector2.zero);
            _dockIcon.T.fontSize = DockSize * 0.5f * _fontScale;
            var t = _dockText.T;
            t.alignment = TextAlignmentOptions.Midline;
            t.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            t.rectTransform.sizeDelta = new Vector2(0.14f, 0.018f);
            t.fontSize = 0.012f * _fontScale;
            At(_dockText, new Vector2(0, -DockSize / 2 - 0.013f));
            ((BoxCollider)_dockHit).size = new Vector3(DockSize + 0.012f, DockSize + 0.012f, 0.01f);
            _dockText.T.text = !inConversation ? "Set up" : signing && signAvatar != null && signAvatar.only != null ? $"Signing · {signAvatar.only.Label}"
                : signing ? "Signing" : speech ? "Sign to speech" : director.Subtitles ? "Subtitles"
                : director.TrackingFallback ? "Subtitles · no tracking" : "Bubbles";
            _dock.Open = true;
            _dock.Root.gameObject.SetActive(true);
        }

        // ---------------- per frame ----------------

        bool _startedFromLaunch; // -e start 1: the splash then leaves the conversation running instead of opening the menu
        bool _dockFallback; // the dock shows the tracking-loss subtitles (CaptionDirector.TrackingFallback)

        void Update()
        {
            if (head == null || _root == null) return;
            // Tracking lost or back mid-conversation: relabel the dock (only while it's the thing showing).
            if (director.TrackingFallback != _dockFallback && _dock.Root.gameObject.activeSelf && !_root.gameObject.activeSelf && !_ask.Open)
                ShowDock();
            if (director.startNow && _setup != null)
            {
                director.startNow = false; _startedFromLaunch = true;
                if (!string.IsNullOrEmpty(director.tagExtra))
                {
                    TagModePref = director.tagExtra switch { "auto" => TagMode.Automatic, "both" => TagMode.Both, _ => TagMode.ByHand };
                    tracker?.SetAutoTag(!SigningMode && !SpeechMode && !director.Subtitles && AutoTagPref);
                    Debug.Log($"[Captions] Launch: {TagModeLabel}");
                }
                StartConversation();
            }
            if (!string.IsNullOrEmpty(director.nameExtra) && tracker != null && _stage == Stage.Conversation)
                foreach (var p in tracker.People)
                {
                    if (!p.Visible) continue;
                    p.Tag = director.nameExtra; // as if pointed at and named (an auto tag is replaced, as by renaming)
                    p.Color = Tags[0].Item2;
                    if (!_tagged.Contains(p)) _tagged.Add(p);
                    Debug.Log($"[Captions] Tagged Person {p.Id} as {p.Tag} (launch option)");
                    director.nameExtra = null;
                    break;
                }
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb != null && kb.bKey.wasPressedThisFrame) Choose(CaptionDirector.CaptionMode.Bubbles);
            if (kb != null && kb.sKey.wasPressedThisFrame) Choose(CaptionDirector.CaptionMode.Signing);
            if (kb != null && kb.tKey.wasPressedThisFrame) Choose(CaptionDirector.CaptionMode.SignToSpeech);
            if (kb != null && kb.enterKey.wasPressedThisFrame) StartConversation();
            if (kb != null && kb.mKey.wasPressedThisFrame) { if (_menu.Open && _tab == Tab.Mic) Close(); else OpenMic(); }
            if (kb != null && _menu.Open && _tab == Tab.Mic && _note == null)
                for (var i = 0; i < Mathf.Min(9, micSettings.Options.Count); i++)
                    if (kb[(Key)((int)Key.Digit1 + i)].wasPressedThisFrame) PickMic(micSettings.Options[i]);
#endif
            if (_closeAt > 0 && Time.time >= _closeAt) { _closeAt = -1f; _menu.Open = false; BeginSetup(); }
            if ((leftHand != null && leftHand.IsTracked) || (rightHand != null && rightHand.IsTracked)) _handsSeenAt = Time.time;
            foreach (var panel in new[] { _menu, _setup, _dock, _quick, _ask })
            {
                // In a conversation with hands tracked the held pinch stands in for the dock: no capsule in view, even
                // while saving, translating or paused (the conversation menu shows those). It's back only when no hands
                // have been seen for 30 s, as the gaze button to the menu.
                var open = panel.Open && !(panel == _dock && PinchMode) && !_alertUp; // an alert card has the view to itself
                panel.Fade = Mathf.MoveTowards(panel.Fade, open ? 1f : 0f, Time.deltaTime * 5f);
                if (panel.Fade <= 0f && panel.Root.gameObject.activeSelf) panel.Root.gameObject.SetActive(false);
            }
            if (_menu.Open || _setup.Open || _quick.Open || _ask.Open) _dock.Open = false;
            if (_ask.Open && (_menu.Open || _setup.Open || _quick.Open || !director.live)) _ask.Open = false; // a menu took over
            if (_ask.Open && Time.time > _askUntil) { _ask.Open = false; ShowDock(); } // no answer: it goes (asked again next run)
            KeepInView(_ask);
            KeepInView(_menu);
            KeepInView(_setup);
            KeepInView(_quick);
            PlaceDock();
            // Faces are looked for in a conversation and in the setup steps that tag or show people; not in the menu,
            // the welcome or while it learns your voice.
            director.facesWanted = _setup.Open && _stage < Stage.Welcome && _stage != Stage.YourVoice && _stage != Stage.SignIntro;
            if (tracker != null) tracker.lookingForFaces = _setup.Open && _stage is Stage.TagOne or Stage.TagMany or Stage.TagLive or Stage.TagName;
            Point();
            UpdatePinchMenu();
            if (_root.gameObject.activeSelf)
            {
                if (_tab == Tab.Mic && _note == null) UpdateMicTab();
                if (_tab == Tab.Topic && _picking == null && _languageText != null && Time.frameCount % 30 == 0) UpdateLanguageLabels();
                if (_tab == Tab.History) UpdateHistory();
                UpdateSummaryPulse();
                Apply();
            }
            if (_quick.Root.gameObject.activeSelf) { UpdateQuick(); ApplyPanel(_quick); }
            if (_ask.Root.gameObject.activeSelf) ApplyPanel(_ask);
            if (_setup.Root.gameObject.activeSelf) { UpdateSetup(); UpdateReadyPill(_setupPill); ApplyPanel(_setup); }
            if (_dock.Root.gameObject.activeSelf) WakeDock();
            UpdateFaces();
            UpdateAlerts();
            UpdateSounds();
        }

        // The mode button sits at the bottom right of the view, out of the way of faces and captions. It goes where your head goes,
        // lazily: turn up to DockSlack degrees (to look at it) and it stays; turn further and it comes along at the
        // edge; keep still and it glides back to its spot. Never while you're pointing or looking at it.
        void PlaceDock()
        {
            var fwd = Flat(head.forward);
            var yaw = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
            // Pitch too (limited to ±40°): looking down at people mustn't lift the button into the middle of the view.
            var pitch = Mathf.Clamp(-Mathf.Asin(Mathf.Clamp(head.forward.y, -1f, 1f)) * Mathf.Rad2Deg, -40f, 40f);
            if (!_dockPlaced || !_dock.Root.gameObject.activeSelf) { _dockYaw = yaw; _dockPitch = pitch; _dockPlaced = true; }
            var diff = Mathf.DeltaAngle(_dockYaw, yaw);
            if (Mathf.Abs(diff) > DockSlack) _dockYaw += diff - Mathf.Sign(diff) * DockSlack;
            var dp = pitch - _dockPitch;
            if (Mathf.Abs(dp) > DockSlack) _dockPitch += dp - Mathf.Sign(dp) * DockSlack;
            var turning = (Mathf.Abs(Mathf.DeltaAngle(_lastYaw, yaw)) + Mathf.Abs(pitch - _lastPitch)) / Mathf.Max(Time.deltaTime, 1e-3f) > 20f;
            _lastYaw = yaw;
            _lastPitch = pitch;
            // No gliding back while you point at it or look at it (it would slide away from your eyes).
            var lookingAt = _dock.Root.gameObject.activeSelf && Vector3.Angle(head.forward, _dock.Root.position - head.position) < 20f;
            _dockStill = turning || _dockHover || lookingAt ? 0f : _dockStill + Time.deltaTime;
            if (_dockStill > 0.6f)
            {
                var k = 1f - Mathf.Exp(-2.5f * Time.deltaTime);
                _dockYaw = Mathf.LerpAngle(_dockYaw, yaw, k);
                _dockPitch = Mathf.Lerp(_dockPitch, pitch, k);
            }
            // Bottom right of the view: ~26° right of and ~28° below where you look.
            var dir = Quaternion.Euler(0, _dockYaw, 0) * Quaternion.Euler(_dockPitch, 0, 0) * Quaternion.Euler(0, 26f, 0) * Quaternion.Euler(28f, 0, 0);
            var pos = head.position + dir * Vector3.forward * 0.85f;
            Place(_dock, pos);
        }

        // Any mode, live, hands tracked lately (not just this frame: hands drop out of tracking when lowered).
        bool PinchMode => (director.live || _paused) && Time.time - _handsSeenAt < 30f;

        // The mode button is a faint dot (smaller, no icon or word) until you look at it or point at it.
        void WakeDock()
        {
            ApplyPanel(_dock);
            var look = Vector3.Angle(head.forward, _dock.Root.position - head.position) < 14f;
            _dockWake = Mathf.MoveTowards(_dockWake, look || _dockHover || !director.live ? 1f : 0f, Time.deltaTime * 4f);
            var wake = Mathf.SmoothStep(0f, 1f, _dockWake);
            _dock.Root.localScale *= Mathf.Lerp(0.45f, 1f, wake);
            _dockIcon.Hidden = _dockText.Hidden = wake < 0.5f;
        }


        // Every mode: pinch and hold with either hand for 2 s and the conversation menu opens, wherever you look; a ring
        // round the pinch fills meanwhile. Or double-tap the thumb on the side of the index finger (Meta's thumb-tap
        // microgesture, the shortcut gesture Meta's hand guidelines suggest). Not when that hand is pressing something
        // (its ray on a button or a face), and not during Meta's own palm-up pinch. The welcome's pinch step uses the
        // same gestures to practise them.
        void UpdatePinchMenu()
        {
            var tutorial = _onboarding && _setup.Open && _stage == Stage.Pinch && !_pinchTried;
            var allowed = (tutorial || (PinchMode && !_menu.Open && !_setup.Open && !_quick.Open)) && !_dragging && !_carrying && _moving == null && !_carryStrip &&
                          !(_alertUp && _shown.Level == SoundEvents.Priority.Danger); // an alarm first: "I'm safe" carries on

            // Thumb double-tap, per hand.
            for (var i = 0; i < 2; i++)
            {
                var hand = i == 0 ? leftHand : rightHand;
                var tap = hand != null && hand.IsTracked && hand.GetMicrogestureType() == OVRHand.MicrogestureType.ThumbTap;
                var edge = tap && !_tapWas[i];
                _tapWas[i] = tap;
                if (!edge) continue;
                if (!allowed || !MenuHandFree(hand)) { _tapAt[i] = -9f; continue; }
                if (Time.time - _tapAt[i] > DoubleTapSeconds) { _tapAt[i] = Time.time; continue; }
                _tapAt[0] = _tapAt[1] = -9f;
                _pinchHeld = 0f;
                DrawPinchRing(_menuHand, 0f);
                OpenMenuByGesture(hand, "thumb double-tap");
                return;
            }

            // Held pinch: keep the hand already holding; otherwise whichever hand starts pinching (left first).
            if (!(allowed && MenuHandFree(_menuHand) && _menuHand.GetFingerIsPinching(OVRHand.HandFinger.Index)))
            {
                var next = allowed ? PinchingFree(leftHand) ?? PinchingFree(rightHand) : null;
                if (next == null || next != _menuHand || _pinchFired)
                {
                    DrawPinchRing(_menuHand, 0f);
                    _pinchHeld = 0f;
                    _pinchFired = false;
                }
                _menuHand = next;
                if (next == null) return;
            }
            var h = _menuHand;
            _pinchHeld += Time.deltaTime;
            DrawPinchRing(h, _pinchFired ? 0f : Mathf.InverseLerp(PinchRingAfter, PinchHoldSeconds, _pinchHeld));
            if (_pinchFired || _pinchHeld < PinchHoldSeconds) return;
            _pinchFired = true;
            _holdOpened = true;
            _letGoAt = -1f;
            DrawPinchRing(h, 0f);
            OpenMenuByGesture(h, "held pinch");
        }

        bool MenuHandFree(OVRHand h) => h != null && h.IsTracked && !h.IsSystemGestureInProgress && !(h == _rayHand && _pointTarget != null);
        OVRHand PinchingFree(OVRHand h) => MenuHandFree(h) && h.GetFingerIsPinching(OVRHand.HandFinger.Index) ? h : null;

        void OpenMenuByGesture(OVRHand h, string how)
        {
            if (TryPinchTutorial()) return;
            Debug.Log($"[Captions] Conversation menu opened by a {how} ({(h == leftHand ? "left" : "right")} hand)");
            OpenQuick();
            UiFeedback.Play(UiSound.Confirm, _quick.Root.position);
        }

        // An arc just in front of the pinching fingers, facing you, filling clockwise.
        void DrawPinchRing(OVRHand h, float progress)
        {
            if (_pinchRing == null) return;
            _pinchRing.enabled = progress > 0.01f && h != null && h.IsPointerPoseValid;
            if (!_pinchRing.enabled) return;
            var centre = h.PointerPose.position + h.PointerPose.forward * 0.04f;
            var toViewer = (head.position - centre).normalized;
            var right = Vector3.Cross(Vector3.up, toViewer).normalized;
            var up = Vector3.Cross(toViewer, right);
            const float r = 0.018f;
            var n = _pinchRing.positionCount;
            for (var i = 0; i < n; i++)
            {
                var a = Mathf.PI / 2 - progress * 2 * Mathf.PI * i / (n - 1);
                _pinchRing.SetPosition(i, centre + (right * Mathf.Cos(a) + up * Mathf.Sin(a)) * r);
            }
        }


        // A panel you've turned away from (over 60° for a second) glides back in front of you.
        void KeepInView(Panel p)
        {
            if (!p.Open || p == _moving) { p.Away = 0f; p.Returning = false; return; }
            var off = Vector3.Angle(Flat(p.Root.position - head.position), Flat(head.forward));
            p.Away = off > 60f ? p.Away + Time.deltaTime : 0f;
            if (p.Away > 1f) p.Returning = true;
            if (!p.Returning) return;
            var goal = FrontOf(p);
            Place(p, Vector3.SmoothDamp(p.Root.position, goal, ref p.Vel, 0.3f));
            if ((p.Root.position - goal).sqrMagnitude < 4e-4f) p.Returning = false;
        }

        void Apply()
        {
            // The slider's fill follows the caption size.
            LayoutSlider(_textSlider);
            UpdateReadyPill(_menuPill);

            // Only while pointing by gaze, and not while the "Getting ready" line has the row.
            var pill = _menuPill != null && !_menuPill.Track.Hidden;
            _hint.C.a = Mathf.MoveTowards(_hint.C.a, _gaze && !pill ? 0.65f : 0f, Time.deltaTime * (pill ? 8f : 3f));
            UpdateHandsLostHint();
            ApplyPanel(_menu);
        }

        // The line under the panel while it's chosen by gaze: if hands were tracked a moment ago and have dropped out
        // (dim light, hands out of view), say so, since that's why the pinch stopped working.
        const string GazeHint = "Look at a button and hold your look to choose";
        const string HandsLostHint = "Hands not seen: hold them up in front of you, or turn on more light";
        void UpdateHandsLostHint()
        {
            var lost = _gaze && Time.time - _handsSeenAt > 1.5f && Time.time - _handsSeenAt < 60f;
            if (lost == _handsLostHint) return;
            _handsLostHint = lost;
            _hint.T.text = lost ? HandsLostHint : GazeHint;
        }

        void ApplyPanel(Panel panel)
        {
            var ease = Mathf.SmoothStep(0, 1, panel.Fade);
            // Opening settles in with a slight overshoot; closing just shrinks away.
            panel.Root.localScale = Vector3.one * Mathf.LerpUnclamped(0.96f, 1f, panel.Open ? EaseOutBack(panel.Fade) : ease);
            panel.Swap = Mathf.MoveTowards(panel.Swap, 1f, Time.deltaTime * 4f);
            float dt = Time.deltaTime, follow = 1f - Mathf.Exp(-16f * dt);
            // While it's carried, the panel thins a little so the room shows (and it reads as "in motion").
            panel.Dim = Mathf.MoveTowards(panel.Dim, panel == _moving ? 1f : 0f, Time.deltaTime * 5f);
            var alpha = ease * (1f - 0.3f * panel.Dim);
            foreach (var g in panel.Glass)
            {
                var pad = 0.024f;
                g.Press = Mathf.MoveTowards(g.Press, 0f, Time.deltaTime * 4f);
                if (!g.Started) { g.Started = true; g.Shown = g.Tint; g.Vis = g.Hidden ? 0f : 1f; }
                g.Shown = Color.Lerp(g.Shown, g.Tint, follow);
                g.Vis = Mathf.MoveTowards(g.Vis, g.Hidden ? 0f : 1f, dt * 7f);
                if (g.Glide)
                {
                    var p = g.R.transform.localPosition;
                    At(g, Vector2.Lerp(new Vector2(p.x, p.y), g.To, follow));
                }
                // Hover: grows a little and brightens. Held (pinching on it): sinks in for as long as the pinch lasts.
                // Press (the moment it lands): a quick dip and spring back.
                var grown = g.Grow == Vector2.zero ? g.Size : g.Size + g.Grow * Mathf.SmoothStep(0f, 1f, g.Hover);
                var size = grown * (1f + 0.025f * g.Hover - 0.03f * g.Held - 0.045f * Mathf.Sin(g.Press * Mathf.PI));
                g.R.transform.localScale = new Vector3(size.x + pad, size.y + pad, 1);
                var lit = g.Shown.a > 0.5f ? Color.Lerp(g.Shown, Color.white, 0.4f) : new Color(1, 1, 1, Mathf.Min(1f, g.Shown.a + 0.12f));
                var tint = Color.Lerp(Color.Lerp(g.Shown, lit, g.Hover), new Color(1, 1, 1, Mathf.Max(lit.a, 0.5f)), 0.5f * Mathf.Max(g.Press, 0.6f * g.Held));
                g.R.GetPropertyBlock(_mpb);
                _mpb.SetVector(BodyId, new Vector4(size.x, size.y, Mathf.Min(g.Radius, Mathf.Min(size.x, size.y) / 2), g.Bevel));
                _mpb.SetVector(BodyCenterId, Vector4.zero);
                _mpb.SetVector(OrbId, Vector4.zero);
                _mpb.SetVector(QuadId, new Vector4(0, 0, size.x + pad, size.y + pad));
                _mpb.SetFloat(GrowId, 1f);
                // Borderless buttons (UI Set): no glass at all until you point at them.
                var shown = g.Vis * (g.Tint == Borderless ? Mathf.Max(g.Hover, g.Press) : 1f);
                _mpb.SetFloat(AlphaId, shown * alpha);
                // A soft glow inside the button under where you point (visionOS hover).
                var glow = Vector4.zero;
                if (g == _pointGlass)
                {
                    var lp = g.R.transform.InverseTransformPoint(_pointAt);
                    glow = new Vector4(lp.x * (size.x + pad), lp.y * (size.y + pad), Mathf.Clamp(Mathf.Min(size.x, size.y) * 0.9f, 0.012f, 0.04f), g.Hover);
                }
                _mpb.SetVector(HoverId, glow);
                var frosted = g.Dim >= PanelDim;     // a panel background: blurred and dark; buttons stay clear glass
                if (frosted) tint = PanelSmoke;
                _mpb.SetFloat(ClearId, frosted ? 0f : 1f);
                // Panels and the rows on them are matte: little white sheen or veil over the smoke, no lifting over a
                // dark room. Buttons and switches keep the full glass finish.
                var matte = frosted || g.Tint == CardTint || g.Tint == CardActive;
                _mpb.SetFloat(GlassFinishId, matte ? 0.3f : 1f);
                _mpb.SetFloat(AdaptId, matte ? 0f : 0.7f);
                _mpb.SetFloat(ClearDimId, g.Dim);
                _mpb.SetColor(TintId, tint);
                _mpb.SetFloat(BlurId, frosted ? 0.045f : 0.03f); // wide: the room behind reads as soft shapes and colour
                _mpb.SetFloat(RefractionId, 0.012f);
                _mpb.SetFloat(StrengthId, 0.97f);    // nearly opaque, so the sharp room doesn't show through the blur
                // Without the passthrough camera (Editor) the glass is a flat frost: brighten it the same way.
                _mpb.SetColor(FrostId, frosted ? PanelFrost : Color.Lerp(_frost, new Color(tint.r, tint.g, tint.b, Mathf.Max(_frost.a, tint.a)), tint.a));
                g.R.SetPropertyBlock(_mpb);
            }
            var swap = Mathf.SmoothStep(0, 1, panel.Swap);
            foreach (var l in panel.Labels)
            {
                if (!l.Started) { l.Started = true; l.Vis = l.Hidden ? 0f : 1f; }
                l.Vis = Mathf.MoveTowards(l.Vis, l.Hidden ? 0f : 1f, dt * 7f);
                var c = l.C;
                c.a *= l.Vis * alpha * swap;
                l.T.color = c;
            }
        }

        // 0 → 1 with a small overshoot past 1 near the end.
        static float EaseOutBack(float t)
        {
            const float c1 = 0.6f, c3 = c1 + 1f; // a hint of settle, not a bounce
            t = Mathf.Clamp01(t) - 1f;
            return 1f + c3 * t * t * t + c1 * t * t;
        }

        void Point()
        {
            var ray = PointerRay(out var pinch, out var strength);
            // No hand and no controller: point with the head (gaze) and press by looking a moment (dwell).
            _gaze = !ray.HasValue;
            if (_gaze) ray = new Ray(head.position, head.forward);
            var click = pinch && !_pinchWas;
            _pinchWas = pinch;
            if (_holdOpened)
            {
                // Only a real pinch of the hand that held it counts as still holding: a relaxed hand often reads above
                // 0.3 strength, and pinches of the other hand are clicks, so either used to keep every click blocked
                // (2026-11-09: "clicking on icons but nothing happens").
                var held = _menuHand != null && _menuHand.IsTracked && _menuHand.GetFingerIsPinching(OVRHand.HandFinger.Index);
                if (held || pinch && _rayHand == _menuHand) _letGoAt = -1f;
                else if (_letGoAt < 0f) _letGoAt = Time.time;
                if (_letGoAt >= 0f && Time.time - _letGoAt >= LetGoSeconds) _holdOpened = false;
                else click = false;
            }
            if (_dragging && !pinch)
            {
                _dragSlider.Done?.Invoke();
                _dragSlider = null;
            }

            StopBuzz();
            Collider hit = null;
            Panel on = null;   // the open panel the ray meets, on a button or between them
            var end = Vector3.zero;
            if (Physics.Raycast(ray.Value, out var info, 3f))
            {
                end = info.point;
                if (_owner.TryGetValue(info.collider, out var panel) && panel.Open)
                {
                    on = panel;
                    hit = Pressable(info.collider) ? info.collider : NearestButton(panel, end);
                }
            }
            // Keep the button you're on until the ray clearly leaves it, so its edge doesn't flicker and closing a pinch
            // (which swings the ray) still presses what you were pointing at.
            var pinching = !_gaze && strength > 0.3f;
            if (_hoverWas is BoxCollider prev && prev != hit && (hit == null || pinching) && !_dragging && !_carrying && _moving == null &&
                Usable(prev) && _owner.TryGetValue(prev, out var was) && was.Open &&
                new Plane(prev.transform.forward, prev.transform.position).Raycast(ray.Value, out var pd) &&
                OutsideBy(prev, ray.Value.GetPoint(pd)) < (pinching ? PinchMargin : LeaveMargin))
            {
                hit = prev;
                on = was;
            }
            // The ray ends on the button's glass (its collider stands a little in front of it), and on the button even
            // when it snapped to it from just outside: the ray bends onto it, so you see it took.
            if (hit is BoxCollider hb && new Plane(hb.transform.forward, hb.transform.position + hb.transform.forward * 0.005f).Raycast(ray.Value, out var bd))
            {
                var lp = hb.transform.InverseTransformPoint(ray.Value.GetPoint(bd)) - hb.center;
                var inset = Mathf.Min(0.004f, hb.size.x / 4, hb.size.y / 4);
                lp.x = Mathf.Clamp(lp.x, -hb.size.x / 2 + inset, hb.size.x / 2 - inset);
                lp.y = Mathf.Clamp(lp.y, -hb.size.y / 2 + inset, hb.size.y / 2 - inset);
                end = hb.transform.TransformPoint(lp + hb.center);
            }
            // In the setup the ray can also land on a face (to tag it) or on the avatar (to move it); not through a panel.
            object target = hit;
            TrackedPerson face = null;
            var avatar = false;
            if (!_dragging && !_carrying && _moving == null)
            {
                // A face nearer than the panel the ray meets wins: the setup panel glides under the person just named,
                // and with people close by (or faces on a screen) it ends up behind the next face, which then couldn't
                // be tagged (2026-11-09: only the first person could be named Friend).
                var faceEnd = end;
                var f = FaceUnder(ray.Value, ref faceEnd);
                var nearer = f != null && on != null &&
                             Vector3.Distance(ray.Value.origin, faceEnd) < Vector3.Distance(ray.Value.origin, end) - 0.05f;
                if (f != null && (hit == null && on == null || nearer))
                {
                    face = f; target = f; end = faceEnd; hit = null; on = null;
                }
                else if (hit == null && on == null && AvatarUnder(ray.Value, ref end)) { target = _anchor; avatar = true; }
                else if (hit == null && on == null && SoundSpotUnder(ray.Value, ref end)) target = _teachPin.Root;
                else if (hit == null && on == null && StripUnder(ray.Value, ref end)) target = director.SubtitleStrip;
            }
            _faceHover = face;
            _pointTarget = target;
            _avatarHover = avatar || _carrying;
            foreach (var (c, g) in _hovers)
            {
                var lit = c == hit || (_dragging && c == _dragSlider.Hit) || (_moving != null && _grabs.TryGetValue(c, out var gp) && gp == _moving);
                g.Hover = Mathf.MoveTowards(g.Hover, lit ? 1 : 0, Time.deltaTime * 6f);
                g.Held = Mathf.MoveTowards(g.Held, lit && !_gaze ? strength : 0, Time.deltaTime * 10f);
            }
            _dockHover = hit == _dockHit;
            _pointGlass = hit != null && _hovers.TryGetValue(hit, out var pointed) ? pointed : null;
            _pointAt = end;
            if (target != null && target != (object)_hoverWas && !_dragging && !_carrying && _moving == null &&
                !(_teachPin != null && target == (object)_teachPin.Root && _soundHoverWas))
            {
                UiFeedback.Play(UiSound.Hover, end);
                Buzz(0.15f, 0.03f);
            }
            _hoverWas = target as Collider;
            _soundHoverWas = _teachPin != null && target == (object)_teachPin.Root;

            var progress = 0f;
            if (_gaze)
            {
                // New buttons can appear right where you were looking: none presses until your look leaves them.
                if (_gazeHold && hit == null) _gazeHold = false;
                if (_gazeHold) { _gazeOn = target; _dwell = -1f; }
                else if (target != _gazeOn) { _gazeOn = target; _dwell = 0f; }
                else if (target != null && _dwell >= 0f) _dwell += Time.deltaTime;
                var need = hit == _dockHit ? ChipDwellSeconds : DwellSeconds;
                var gazeSlider = SliderOf(hit);
                if (gazeSlider != null)
                {
                    // Slider by gaze: after a short look, the value follows where you look along it.
                    if (_dwell > 0.4f) { gazeSlider.Set(SliderValue(gazeSlider, end)); _gazeSliderUsed = gazeSlider; }
                    progress = Mathf.Clamp01(_dwell / 0.4f);
                }
                else if (target != null && _dwell >= need) { click = true; _dwell = -1f; } // once; look away to press again
                else if (target != null && _dwell >= 0f) progress = _dwell / need;
            }
            else _gazeOn = null;

            // A gaze-moved slider keeps its value once the look leaves it.
            if (_gazeSliderUsed != null && SliderOf(hit) != _gazeSliderUsed) { _gazeSliderUsed.Done?.Invoke(); _gazeSliderUsed = null; }
            // Sliders: pinch on one and drag along it (the ray may leave the slider while dragging).
            if (click && SliderOf(hit) is { } pressed) { _dragSlider = pressed; UiFeedback.Play(UiSound.Press, end); }
            if (_dragging && ray.HasValue && new Plane(_dragSlider.Root.forward, _dragSlider.Root.position).Raycast(ray.Value, out var d))
            {
                end = ray.Value.GetPoint(d);
                _dragSlider.Set(SliderValue(_dragSlider, end));
            }

            // Moving a panel by its grab bar: it rides the ray at the same distance, facing you.
            if (click && hit != null && _grabs.TryGetValue(hit, out var grabbed))
            {
                _moving = grabbed;
                _moveDist = Vector3.Distance(ray.Value.origin, end);
                _moveOffset = grabbed.Root.position - end;
                UiFeedback.Play(UiSound.Pick, end);
                click = false;
            }
            if (_moving != null)
            {
                if (!pinch) { UiFeedback.Play(UiSound.Drop, _moving.Root.position); _moving = null; }
                else
                {
                    end = ray.Value.GetPoint(_moveDist);
                    Place(_moving, end + _moveOffset);
                }
            }

            // Moving the avatar: pick it up, carry it along the ray, put it down.
            if (click && avatar) { Pick(ray.Value, end); click = false; }

            // Moving the subtitle line: pinch it and drag it where you want it (onto the radio); it stays when let go.
            if (click && target != null && target == (object)director.SubtitleStrip)
            {
                _carryStrip = true;
                _stripDist = Vector3.Distance(ray.Value.origin, end);
                _stripOffset = director.SubtitleStrip.position - end;
                UiFeedback.Play(UiSound.Pick, end);
                click = false;
            }
            if (_carryStrip)
            {
                end = ray.Value.GetPoint(_stripDist);
                director.PinSubtitles(end + _stripOffset, pinch);
                if (!pinch) { _carryStrip = false; UiFeedback.Play(UiSound.Drop, end); }
            }
            if (_carrying) end = Carry(ray.Value, pinch, ref progress);

            var show = _menu.Open || _setup.Open || _quick.Open || _ask.Open || hit != null || target != null || _carryStrip;
            var busy = _dragging || _carrying || _moving != null || _carryStrip;
            var landed = target != null || busy || on != null; // the ray ends on something (a panel stops it)
            var to = landed ? end : ray.Value.GetPoint(_gaze ? Distance : FreeLength);
            // The tip glides after the hand (it shakes, and a pinch tugs it); a big jump (a new target, the ray just
            // shown) is taken at once.
            if (!_tipValid || (to - _tip).sqrMagnitude > 0.12f * 0.12f || busy) _tip = to;
            else _tip = Vector3.Lerp(_tip, to, 1f - Mathf.Exp(-28f * Time.deltaTime));
            _tipValid = show;
            // The spot where the ray lands: a dot on the panel, with a ring when it's on something to press, closing in
            // blue as you pinch.
            if (show && (landed || _gaze)) _spot.Show(_tip, head.position, target != null || busy, _gaze ? 0f : busy ? 1f : strength);
            else _spot.Hide();
            // Only while it meets a panel, button or face (or something is held): pointing at nothing shows no ray.
            DrawRay(ray.Value, show && !_gaze && landed, landed, target != null || busy, strength); // gaze: just the dot, no line
            DrawRing(show && (_gaze || _carrying) ? progress : 0f);

            if (!click || target == null) return;
            _spot.Ripple();
            _glintAge = 0f;
            Buzz(0.5f, 0.05f);
            if (face != null) { UiFeedback.Play(UiSound.Press, end); PickFace(face); }
            else if (_teachPin != null && target == (object)_teachPin.Root) PlaceSound(end);
            else if (hit != null && _clicks.TryGetValue(hit, out var action))
            {
                if (_hovers.TryGetValue(hit, out var g)) g.Press = 1f;
                UiFeedback.Play(UiSound.Press, end);
                action();
            }
        }

        // The ray: a hairline of light in a soft halo (PointerRay shader). Out of the hand it fades in; on a panel it
        // runs bright up to the tip, on a button brighter still, and turns blue as the pinch closes; pointing at nothing
        // it fades out again short of the room. A press sends a glint down it to the button.
        Vector3 _stripOffset;

        // The ray on the subtitle line (Subtitles mode, words showing, pointing with a hand or controller).
        bool StripUnder(Ray ray, ref Vector3 end)
        {
            if (_gaze || director == null || !director.SubtitlesGrabbable) return false;
            // Facing you, at the line (or where it would be with no words on it).
            var at = director.StripAnchor;
            var face = Quaternion.LookRotation(Flat(at - head.position).sqrMagnitude > 1e-4f ? Flat(at - head.position) : Flat(head.forward));
            if (!new Plane(face * Vector3.back, at).Raycast(ray, out var d) || d < 0.2f) return false;
            var p = ray.GetPoint(d);
            var lp = Quaternion.Inverse(face) * (p - at); // the line grows upwards from its anchor
            var scale = Mathf.Max(1f, Vector3.Distance(head.position, at) / 1.2f); // a line across the room is bigger
            if (Mathf.Abs(lp.x) > 0.24f * scale || lp.y < -0.05f * scale || lp.y > 0.15f * scale) return false;
            end = p;
            director.HintStrip();
            return true;
        }

        void DrawRay(Ray ray, bool on, bool landed, bool onTarget, float strength)
        {
            _rayAlpha = Mathf.MoveTowards(_rayAlpha, on ? 1f : 0f, Time.deltaTime * (on ? 8f : 5f));
            _ray.enabled = _rayAlpha > 0.01f;
            if (!_ray.enabled) return;
            var p0 = ray.origin + ray.direction * RayGap;
            // Toward the cursor (it may have snapped onto a button just off the ray), stopping short of it.
            var toTip = _tip - p0;
            var dir = toTip.sqrMagnitude > 1e-6f ? toTip.normalized : ray.direction;
            var len = Mathf.Min(ShortRay, Mathf.Max(0f, toTip.magnitude - 0.03f));
            for (var i = 0; i < RayPoints; i++)
                _ray.SetPosition(i, p0 + dir * (len * i / (RayPoints - 1f)));
            _ray.widthMultiplier = 1f + 0.35f * strength;
            var tint = Color.Lerp(onTarget ? new Color(1f, 0.98f, 0.92f) : Color.white, PinchBlue, strength);
            var a = _rayAlpha * (landed ? (onTarget ? 1f : 0.8f) : 0.6f);
            // Bright just out of the hand, fading to nothing at its far end.
            _rayColors[0] = new GradientColorKey(tint, 0f);
            _rayColors[1] = new GradientColorKey(Color.white, 1f);
            _rayAlphas[0] = new GradientAlphaKey(a * 0.6f, 0f);
            _rayAlphas[1] = new GradientAlphaKey(a, 0.2f);
            _rayAlphas[2] = new GradientAlphaKey(0f, 1f);
            _rayGradient.SetKeys(_rayColors, _rayAlphas);
            _ray.colorGradient = _rayGradient;
            _glintAge += Time.deltaTime;
            var g = Mathf.Clamp01(_glintAge / 0.22f);
            _ray.GetPropertyBlock(_rayMpb);
            _rayMpb.SetVector(GlintId, new Vector4(Mathf.Lerp(0.1f, 1.05f, 1f - (1f - g) * (1f - g)), 0.12f, g < 1f ? 1f - g : 0f, 0));
            _ray.SetPropertyBlock(_rayMpb);
        }

        // The dwell ring: an arc around the gaze dot that fills as you hold your look.
        void DrawRing(float progress)
        {
            _ring.enabled = progress > 0.01f;
            if (!_ring.enabled) return;
            var n = _ring.positionCount;
            var toViewer = (head.position - _cursor.position).normalized;
            var right = Vector3.Cross(Vector3.up, toViewer).normalized;
            var up = Vector3.Cross(toViewer, right);
            const float r = 0.012f;
            for (var i = 0; i < n; i++)
            {
                var a = Mathf.PI / 2 - progress * 2 * Mathf.PI * i / (n - 1); // clockwise from the top
                _ring.SetPosition(i, _cursor.position + toViewer * 0.004f + (right * Mathf.Cos(a) + up * Mathf.Sin(a)) * r);
            }
        }

        void BuildPointer()
        {
            var go = new GameObject("Mode menu ray");
            go.transform.SetParent(transform, false);
            _ray = go.AddComponent<LineRenderer>();
            _ray.useWorldSpace = true;
            _ray.positionCount = RayPoints;
            // Includes the halo: the bright core is about a third of this (≈ 1 mm at the hand, 2.5 mm at the tip).
            _ray.widthCurve = new AnimationCurve(new Keyframe(0, 0.006f), new Keyframe(1, 0.0025f)); // a short beam, tapering away from the hand
            _ray.textureMode = LineTextureMode.Stretch;
            _ray.alignment = LineAlignment.View;
            _ray.numCapVertices = 0;
            _ray.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _ray.receiveShadows = false;
            _rayMpb = new MaterialPropertyBlock();
            var rayMaterial = Resources.Load<Material>("PointerRay");
            if (rayMaterial != null) _ray.sharedMaterial = rayMaterial;
            else
            {
                // (Without its material: a plain line.)
                _ray.sharedMaterial = panelMaterial;
                _ray.widthCurve = new AnimationCurve(new Keyframe(0, 0.0015f), new Keyframe(1, 0.003f));
                _ray.GetPropertyBlock(_rayMpb);
                _rayMpb.SetColor(BaseColorId, new Color(1, 1, 1, 0.6f));
                _rayMpb.SetTexture(BaseMapId, Texture2D.whiteTexture);
                _ray.SetPropertyBlock(_rayMpb);
            }
            _ray.enabled = false;

            var ringGo = new GameObject("Mode menu dwell ring");
            ringGo.transform.SetParent(transform, false);
            _ring = ringGo.AddComponent<LineRenderer>();
            _ring.useWorldSpace = true;
            _ring.positionCount = 33;
            _ring.widthMultiplier = 0.0025f;
            _ring.numCapVertices = 3;
            _ring.sharedMaterial = panelMaterial;
            _ring.GetPropertyBlock(_mpb);
            _mpb.SetColor(BaseColorId, PinchBlue);
            _mpb.SetTexture(BaseMapId, Texture2D.whiteTexture);
            _ring.SetPropertyBlock(_mpb);
            _ring.enabled = false;

            var pinchGo = new GameObject("Pinch hold ring");
            pinchGo.transform.SetParent(transform, false);
            _pinchRing = pinchGo.AddComponent<LineRenderer>();
            _pinchRing.useWorldSpace = true;
            _pinchRing.positionCount = 41;
            _pinchRing.widthMultiplier = 0.003f;
            _pinchRing.numCapVertices = 3;
            _pinchRing.sharedMaterial = panelMaterial;
            _pinchRing.SetPropertyBlock(_mpb); // the same colour as the dwell ring
            _pinchRing.enabled = false;

            _spot = RayCursor.Create("Mode menu cursor", transform, panelMaterial);
            _cursor = _spot.transform;
        }

        static readonly Color PinchBlue = Hex(0xFCF8D8); // (cream now) the ray, rings and ripple while pinching

        bool Usable(Collider c) => c.enabled && c.gameObject.activeInHierarchy && Pressable(c);
        bool Pressable(Collider c) => _clicks.ContainsKey(c) || SliderOf(c) != null || (!_gaze && _grabs.ContainsKey(c));

        // The ray met the panel between buttons: the button whose edge is nearest, if it's within a near miss.
        Collider NearestButton(Panel panel, Vector3 point)
        {
            Collider best = null;
            var bestOut = NearMiss;
            foreach (var (c, p) in _owner)
            {
                if (p != panel || c is not BoxCollider box || _backings.Contains(c) || !Usable(c)) continue;
                var o = OutsideBy(box, point);
                if (o < bestOut) { bestOut = o; best = c; }
            }
            return best;
        }

        // How far a point on the panel is outside a button's box, in its plane (0: inside).
        static float OutsideBy(BoxCollider c, Vector3 world)
        {
            var p = c.transform.InverseTransformPoint(world) - c.center;
            var s = c.transform.lossyScale;
            return new Vector2(Mathf.Max(Mathf.Abs(p.x) - c.size.x / 2, 0) * s.x, Mathf.Max(Mathf.Abs(p.y) - c.size.y / 2, 0) * s.y).magnitude;
        }

        bool OnPanel(Ray r) => Physics.Raycast(r, out var i, 3f) && _owner.TryGetValue(i.collider, out var p) && p.Open;

        // A tick in the hand holding the controller that points (a hover, a press). Hands and gaze: the sounds only.
        void Buzz(float amplitude, float seconds)
        {
            if (_rayController == OVRInput.Controller.None) return;
            if (_buzzing != OVRInput.Controller.None && _buzzing != _rayController) OVRInput.SetControllerVibration(0, 0, _buzzing);
            OVRInput.SetControllerVibration(1f, amplitude, _rayController);
            _buzzing = _rayController;
            _buzzUntil = Time.time + seconds;
        }

        void StopBuzz()
        {
            if (_buzzing == OVRInput.Controller.None || Time.time < _buzzUntil) return;
            OVRInput.SetControllerVibration(0, 0, _buzzing);
            _buzzing = OVRInput.Controller.None;
        }

        /// <summary>The pointing ray of a tracked hand (system pointer pose; pinch = select) or, failing that, a
        /// controller (trigger or A/X = select); strength: how far the pinch (or trigger) is in, 0 to 1. With two, the one
        /// pointing at a panel wins, then the one that pointed last (always while it's pinching), then the right.</summary>
        Ray? PointerRay(out bool pinch, out float strength)
        {
            pinch = false;
            strength = 0f;
            var lastHand = _rayHand;
            var lastController = _rayController;
            var keep = _pinchWas ? 3 : 1; // the hand already pointing: never swapped mid-pinch
            _rayHand = null;
            _rayController = OVRInput.Controller.None;
            Ray? best = null;
            var bestScore = -1;
            foreach (var hand in new[] { rightHand, leftHand })
            {
                if (hand == null || !hand.IsTracked || !hand.IsPointerPoseValid) continue;
                var r = new Ray(hand.PointerPose.position, hand.PointerPose.forward);
                var score = (OnPanel(r) ? 2 : 0) + (hand == lastHand ? keep : 0);
                if (score <= bestScore) continue;
                bestScore = score;
                best = r;
                _rayHand = hand;
                pinch = hand.GetFingerIsPinching(OVRHand.HandFinger.Index);
                strength = pinch ? 1f : hand.GetFingerPinchStrength(OVRHand.HandFinger.Index);
            }
            if (best.HasValue) return best;
            foreach (var (t, c) in new[] { (rightController, OVRInput.Controller.RTouch), (leftController, OVRInput.Controller.LTouch) })
            {
                if (t == null || !OVRInput.IsControllerConnected(c)) continue;
                var r = new Ray(t.position, t.forward);
                var score = (OnPanel(r) ? 2 : 0) + (c == lastController ? keep : 0);
                if (score <= bestScore) continue;
                bestScore = score;
                best = r;
                _rayController = c;
                pinch = OVRInput.Get(OVRInput.Button.PrimaryIndexTrigger, c) || OVRInput.Get(OVRInput.Button.One, c);
                strength = pinch ? 1f : OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, c);
            }
            return best;
        }

        static Vector3 Flat(Vector3 v) { v.y = 0; return v.sqrMagnitude < 1e-4f ? Vector3.forward : v.normalized; }
    }
}
