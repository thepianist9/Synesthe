using System;
using System.Collections.Generic;
using System.IO;
using Meta.XR;
using TMPro;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace MetaStarter.Captions
{
    /// <summary>
    /// Sounds you teach (Settings › Sounds): the knock at your front door, the washer's tune, the oven timer. On the
    /// setup panel: point at where the sound comes from and pinch (a pin goes there, on the wall the depth sensor sees,
    /// else 2 m along the ray), pick what it is, then make the sound three times. Each take keeps the fingerprint of
    /// its loudest window (<see cref="SoundEvents.Taught"/>: no model is trained); the quietest one is kept as the
    /// room, so a window more like the room than the sound never counts.
    /// When it's heard again, the pin shows where it came from: its symbol in a circle with rings spreading from it.
    /// When the pin is out of view, the edge of the view glows on its side and an arrow with its symbol points to it.
    /// The info pill says what and where ("behind you"). Pins are kept with spatial anchors, so they stay on the
    /// door from one day to the next; where an anchor can't be saved (the simulator), by position.
    /// </summary>
    public partial class ModeMenu
    {
        const int TakesNeeded = 3;
        const float TakeSeconds = 4f, TakeTail = 1.2f, TakeDrain = 0.7f;  // listen, then the windows still on their way
        const float PinLinger = 7f, PinSize = 0.1f, CueAngle = 32f;
        const string SoundsFile = "taught_sounds.json";

        static readonly (string name, string glyph)[] SoundNames =
        {
            ("Front door", ""), ("Doorbell", ""), ("Oven timer", Glyphs.Timer), ("Washer", ""),
        };

        // What the pill and the pin say when it's heard: what happened, not just the name.
        static readonly Dictionary<string, string> SoundHeard = new()
        {
            ["Front door"] = "Knock at the front door", ["Doorbell"] = "Doorbell ringing",
            ["Oven timer"] = "Oven timer going off", ["Washer"] = "Washer finished",
        };

        static string HeardPhrase(string name)
        {
            var i = name.LastIndexOf(' ');
            var b = i > 0 && int.TryParse(name[(i + 1)..], out _) ? name[..i] : name; // "Front door 2"
            return SoundHeard.TryGetValue(b, out var s) ? (b == name ? s : $"{s} ({name[(i + 1)..]})") : name;
        }

        [Serializable] sealed class SoundEntry
        {
            public string id, name, glyph, anchor;
            public Vector3 pos;
            public float[] prints;  // the takes' fingerprints, one after the other
            public float[] room;
        }
        [Serializable] sealed class SoundFile { public List<SoundEntry> sounds = new(); }

        sealed class SoundPin
        {
            public SoundEntry E;
            public Transform Root, Anchor;
            public Material Disc;
            public Material[] Rings;
            public TextMeshPro Icon, Name;
            public float Shown, FiredAt = -99f, FiredSince = -99f;
            public Vector3 Position => Anchor != null ? Anchor.position : E.pos;
        }

        readonly List<SoundPin> _pins = new();
        SoundEvents.Learned[] _learned = Array.Empty<SoundEvents.Learned>();
        bool _soundsOpen;
        SoundPin _teachPin;
        readonly List<float[]> _takes = new();
        float[] _room;
        float _roomRms = float.MaxValue;
        float _takeStart = -1f;
        string _takeNote;
        Texture2D _discTex, _ringTex;
        [Tooltip("Off: a taught sound or your name shows only the notification pill (no marker in the room, no arrow or glow at the edge, no \"behind you\").")]
        public bool alertMarkers = false;
        Transform _cue, _cueArrow, _cueGlow;
        Material _cueGlowMat, _cueDiscMat;
        TextMeshPro _cueIcon, _cueChevron, _cueLabel;
        float _cueAlpha;
        SoundPin _cuePin;
        bool _soundHoverWas;
        readonly List<(float rms, float[] print, float time)> _takeWins = new();
        float _takeLevel, _nextTakeAt = -1f;

        // The info pill for a taught sound also says where it is.
        string ToastWhere()
        {
            if (!alertMarkers) return "";
            if (_toastShown.Kind == "name") return _toastShown.Source != null && _callPin != null ? " · " + Where(_callPin.Position) : "";
            if (!_toastShown.Kind.StartsWith(SoundEvents.TaughtPrefix, StringComparison.Ordinal)) return "";
            var pin = _pins.Find(p => p.E.id == _toastShown.Source);
            return pin != null ? " · " + Where(pin.Position) : "";
        }

        static string SoundsPath => Path.Combine(Application.persistentDataPath, SoundsFile);

        // ---------------- building ----------------

        void BuildSounds()
        {
            if (!Application.isPlaying) return;
            _discTex = RoundTexture("Pin disc", r => 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.9f, 1f, r)));
            _ringTex = RoundTexture("Pin ring", r => Mathf.Clamp01(1f - Mathf.Abs(r - 0.9f) / 0.08f));
            BuildCue();
            LoadSounds();
        }

        static Texture2D RoundTexture(string name, Func<float, float> alpha)
        {
            const int n = 128;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, name = name };
            var px = new Color32[n * n];
            for (var y = 0; y < n; y++)
                for (var x = 0; x < n; x++)
                {
                    float u = (x + 0.5f) / n * 2f - 1f, v = (y + 0.5f) / n * 2f - 1f;
                    px[y * n + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(alpha(Mathf.Sqrt(u * u + v * v))) * 255));
                }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return tex;
        }

        Material SoundMaterial(Texture tex, int queue)
        {
            var m = new Material(panelMaterial) { name = tex.name, renderQueue = queue };
            m.SetTexture(BaseMapId, tex);
            m.SetColor(BaseColorId, new Color(1, 1, 1, 0));
            return m;
        }

        Transform SoundQuad(string name, Transform parent, Material m, int order)
        {
            var q = Quad(name, parent, Vector2.zero, 0);
            q.localPosition = Vector3.zero;
            var r = q.GetComponent<MeshRenderer>();
            r.sharedMaterial = m;
            r.sortingOrder = order;
            return q;
        }

        TextMeshPro SoundText(Transform parent, string text, float size, bool icon, int order, int queue = 0)
        {
            var go = new GameObject(icon ? "Icon" : "Text");
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<TextMeshPro>();
            if (icon) t.font = iconFont;
            else if (font != null) t.font = font;
            t.text = text;
            t.fontSize = size * (icon ? _iconScale : _fontScale);
            t.alignment = TextAlignmentOptions.Center;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.rectTransform.sizeDelta = new Vector2(size * 12f, size * 1.6f);
            var r = t.GetComponent<MeshRenderer>();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.sortingOrder = order;
            if (queue > 0) t.fontMaterial.renderQueue = queue;
            t.color = new Color(1, 1, 1, 0);
            return t;
        }

        SoundPin MakePin(SoundEntry e)
        {
            var root = new GameObject($"Sound pin: {e.name}").transform;
            root.SetParent(transform, false);
            root.position = e.pos;
            var pin = new SoundPin { E = e, Root = root, Rings = new Material[3] };
            for (var i = 0; i < pin.Rings.Length; i++)
            {
                pin.Rings[i] = SoundMaterial(_ringTex, 3000);
                SoundQuad("Ring", root, pin.Rings[i], 20);
            }
            pin.Disc = SoundMaterial(_discTex, 3000);
            SoundQuad("Disc", root, pin.Disc, 21).localScale = Vector3.one;
            pin.Icon = SoundText(root, e.glyph, 0.55f, true, 22);
            pin.Icon.transform.localPosition = new Vector3(0, 0, -0.01f);
            pin.Name = SoundText(root, e.name, 0.32f, false, 22);
            pin.Name.transform.localPosition = new Vector3(0, -0.78f, -0.01f);
            root.gameObject.SetActive(false);
            return pin;
        }

        // The cue for a sound out of view, held in front of the eyes like the alert glow: a soft glow on the edge it
        // came from, and an arrow there (a circle with the sound's symbol, a chevron pointing on round to it).
        void BuildCue()
        {
            const int n = 128;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, name = "Sound edge glow" };
            var px = new Color32[n * n];
            for (var y = 0; y < n; y++)
                for (var x = 0; x < n; x++)
                {
                    float u = (x + 0.5f) / n, v = (y + 0.5f) / n * 2f - 1f;
                    var a = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.55f, 1f, u)) * Mathf.Exp(-v * v * 2.2f);
                    px[y * n + x] = new Color32(255, 255, 255, (byte)(a * 255));
                }
            tex.SetPixels32(px);
            tex.Apply(false, true);

            _cue = new GameObject("Sound direction").transform;
            _cue.SetParent(head, false);
            _cue.localPosition = new Vector3(0, 0, 0.3f);
            _cueGlowMat = SoundMaterial(tex, 3500);
            _cueGlow = SoundQuad("Edge glow", _cue, _cueGlowMat, 100);
            _cueGlow.localScale = new Vector3(0.62f, 0.62f, 1f);
            _cueArrow = new GameObject("Arrow").transform;
            _cueArrow.SetParent(_cue, false);
            _cueDiscMat = SoundMaterial(_discTex, 3500);
            SoundQuad("Disc", _cueArrow, _cueDiscMat, 101).localScale = Vector3.one * 0.046f;
            _cueIcon = SoundText(_cueArrow, Glyphs.Hearing, 0.025f, true, 102, 3501);
            _cueLabel = SoundText(_cueArrow, "", 0.011f, false, 102, 3501);
            _cueLabel.transform.localPosition = new Vector3(0, -0.034f, -0.001f);
            _cueLabel.outlineWidth = 0.25f;
            _cueLabel.outlineColor = new Color32(0, 0, 0, 200);
            _cueIcon.transform.localPosition = new Vector3(0, 0, -0.001f);
            _cueChevron = SoundText(_cueArrow, Glyphs.ChevronRight, 0.036f, true, 102, 3501);
            _cue.gameObject.SetActive(false);
        }

        // ---------------- the panel: Settings › Sounds ----------------

        void OpenSounds()
        {
            _onboarding = false;
            _soundsOpen = true;
            _menu.Open = false;
            if (_quick != null) _quick.Open = false;
            _closeAt = -1f;
            _dock.Open = false;
            Place(_setup, FrontOf(_setup));
            _setup.Returning = false;
            _setup.Root.gameObject.SetActive(true);
            _setup.Open = true;
            Enter(Stage.SoundList);
        }

        void CloseSounds()
        {
            CancelTeach(false);
            director.hearOnly = false;
            _soundsOpen = false;
            Open(); // back to the modes
        }

        static bool SoundStage(Stage s) => s is Stage.HearIntro or Stage.HearRun or Stage.HearResult or Stage.SoundList or Stage.SoundPlace or Stage.SoundName or Stage.SoundRecord or Stage.SoundSaved
            or Stage.NameListen or Stage.NameConfirm;

        void EnterSounds(Stage stage)
        {
            director.hearOnly = stage == Stage.NameListen; // transcribe (for the menu only) just while it listens for your name
            switch (stage)
            {
                case Stage.NameListen: case Stage.NameConfirm:
                    EnterName(stage);
                    break;
                case Stage.SoundList:
                {
                    var name = MyName.Length > 0 ? $"Name: {MyName}" : "Your name";
                    var nameLine = MyName.Length > 0
                        ? $"\nWhen someone says <b>{MyName}</b>, you'll see who called."
                        : "\nAdd your name to see when someone calls you.";
                    if (_pins.Count == 0)
                    {
                        Say(0, 0, "Teach it your sounds",
                            "A knock at your door, the washer finishing, the oven timer: put a pin where it comes from and " +
                            "make the sound three times. When it's heard again, " +
                            (alertMarkers ? "the pin ripples, and if it's out of view an arrow at the edge of your view points to it." : "a notification pops up.") + nameLine, Glyphs.Hearing);
                        SetButtons(("Done", CloseSounds, false), ("Hearing check", () => StartHearCheck(false), false), (name, StartNameListen, false),
                            ("Teach a sound", StartTeach, true));
                    }
                    else
                    {
                        var list = new System.Text.StringBuilder();
                        foreach (var p in _pins) list.Append(Bullet($"<b>{p.E.name}</b> · {Where(p.Position)}")).Append('\n');
                        Say(0, 0, "Your sounds", list + "Alarms and sirens are always on." + nameLine, Glyphs.Hearing);
                        SetButtons(("Hearing check", () => StartHearCheck(false), false), (name, StartNameListen, false), ("Forget all", ForgetSounds, false),
                            ("Teach a sound", StartTeach, true));
                    }
                    break;
                }
                case Stage.SoundPlace:
                    Say(1, 3, "Where does it come from?",
                        "Point at the door, the washer or the oven: the blue pin follows your hand. Pinch to put it there.\n" +
                        "<color=#FFFFFF80>No hands? Look at the spot and hold still.</color>", Glyphs.Point);
                    SetButtons(("Cancel", () => CancelTeach(true), false));
                    break;
                case Stage.SoundName:
                    Say(2, 3, "What is it?", "Its name and symbol show on the pin and in the alert.", Glyphs.Hearing);
                    var b = new (string, Action, bool)[SoundNames.Length];
                    for (var i = 0; i < SoundNames.Length; i++)
                    {
                        var (name, glyph) = SoundNames[i];
                        b[i] = (name, () => PickSoundName(name, glyph), false);
                    }
                    SetButtons(b);
                    break;
                case Stage.SoundRecord:
                    Say(3, 3, "Make the sound", RecordBody(), _teachPin.E.glyph);
                    if (_nextTakeAt > 0f) SetButtons(("Cancel", () => CancelTeach(true), false));
                    else SetButtons(("Cancel", () => CancelTeach(true), false),
                        (_takeNote != null ? "Try again" : _takes.Count == 0 ? "Start" : "Next", StartTake, true));
                    break;
                case Stage.SoundSaved:
                    Say(0, 0, $"{_pins[^1].E.name} learned",
                        (alertMarkers ? $"Try it: make the sound once more. Its pin ripples and you'll see “{HeardPhrase(_pins[^1].E.name)}”.\n" +
                        "When it's out of view, the edge of your view glows on its side, with an arrow to it." : $"Try it: make the sound once more. You'll see “{HeardPhrase(_pins[^1].E.name)}”."), Glyphs.Check, done: true);
                    SetButtons(("Teach another", StartTeach, false), ("Done", () => Enter(Stage.SoundList), true));
                    break;
            }
        }

        string TakeDots => $"Take {Mathf.Min(_takes.Count + 1, TakesNeeded)} of {TakesNeeded}  {new string('•', _takes.Count)}<color=#FFFFFF40>{new string('•', TakesNeeded - _takes.Count)}</color>";

        string RecordBody() =>
            _takeNote != null ? $"<color=#FF9F0A>{_takeNote}</color>\n{TakeDots}"
            : _nextTakeAt > 0f ? $"<color=#30D158>Got it.</color> Once more, the same way: get ready.\n{TakeDots}"
            : _takes.Count == 0
                ? "Press Start, then make the sound once, the way it usually sounds (knock the way a visitor would). " +
                  "You have 4 seconds; keep the room quiet."
                : $"Again, the same way. {TakeDots}";

        // A rough loudness bar for the panel, from one window's level.
        static string LevelBar(float rms)
        {
            var k = Mathf.Clamp01(Mathf.Log10(Mathf.Max(rms, 1e-5f) / 0.0005f) / Mathf.Log10(40f));
            var n = Mathf.RoundToInt(k * 10f);
            var c = rms >= SoundEvents.TaughtMinRms ? "#30D158" : "#FFFFFF";
            return $"<size=150%><b><color={c}>{new string('|', n)}</color><color=#FFFFFF30>{new string('|', 10 - n)}</color></b></size>";
        }

        void BackSounds()
        {
            if (_stage == Stage.SoundList) CloseSounds();
            else if (_stage is Stage.NameListen or Stage.NameConfirm) Enter(Stage.SoundList);
            else CancelTeach(true);
        }

        void StartTeach()
        {
            CancelTeach(false);
            _takes.Clear();
            _room = null;
            _roomRms = float.MaxValue;
            _takeNote = null;
            _teachPin = MakePin(new SoundEntry { id = Guid.NewGuid().ToString("N").Substring(0, 8), name = "", glyph = Glyphs.Hearing,
                pos = head.position + Flat(head.forward) * 2f });
            _teachPin.Name.text = "Pinch to put it here";
            Enter(Stage.SoundPlace);
        }

        void CancelTeach(bool toList)
        {
            if (director.Sounds != null) director.Sounds.Capturing = false;
            _takeStart = -1f;
            _nextTakeAt = -1f;
            if (_teachPin != null) { Destroy(_teachPin.Root.gameObject); _teachPin = null; }
            if (toList) Enter(Stage.SoundList);
        }

        // Point(): while placing, the ray ends on the room (depth) or 2 m out, and the pin follows it.
        bool SoundSpotUnder(Ray ray, ref Vector3 end)
        {
            if (!_setup.Open || _stage != Stage.SoundPlace || _teachPin == null) return false;
            var p = ray.GetPoint(2f);
            var depth = tracker != null ? tracker.depthRaycast : null;
            if (depth != null && depth.isActiveAndEnabled && EnvironmentRaycastManager.IsSupported &&
                depth.Raycast(ray, out var hit, 6f) && hit.status == EnvironmentRaycastHitStatus.Hit)
                p = hit.point - ray.direction * 0.05f; // just in front of the wall
            end = p;
            _teachPin.E.pos = p;
            return true;
        }

        void PlaceSound(Vector3 at)
        {
            _teachPin.E.pos = at;
            UiFeedback.Play(UiSound.Drop, at);
            Debug.Log($"[Captions] Sound pin placed {Where(at)} ({Vector3.Distance(head.position, at):F1} m)");
            Enter(Stage.SoundName);
        }

        void PickSoundName(string name, string glyph)
        {
            var n = 1;
            var unique = name;
            while (_pins.Exists(p => p.E.name == unique)) unique = $"{name} {++n}";
            _teachPin.E.name = unique;
            _teachPin.E.glyph = glyph;
            _teachPin.Icon.text = glyph;
            _teachPin.Name.text = unique;
            _teachPin.Root.name = $"Sound pin: {unique}";
            UiFeedback.Play(UiSound.Confirm, _setup.Root.position);
            Enter(Stage.SoundRecord);
        }

        void StartTake()
        {
            var s = director.Sounds;
            if (s == null) { _takeNote = "Sound recognition is still starting. Try again in a moment."; Enter(Stage.SoundRecord); return; }
            while (s.Captured.TryDequeue(out _)) { }
            s.Capturing = true;
            _takeStart = Time.time;
            _takeNote = null;
            _nextTakeAt = -1f;
            _takeWins.Clear();
            _takeLevel = 0f;
            _titleText.T.text = "Make the sound now";
            SetButtons(("Cancel", () => CancelTeach(true), false));
            UiFeedback.Play(UiSound.Pick, _setup.Root.position);
        }

        void UpdateTake()
        {
            if (_stage != Stage.SoundRecord) return;
            // After a good take the next one starts by itself, so you only press Start once.
            if (_nextTakeAt > 0f)
            {
                var wait = _nextTakeAt - Time.time;
                if (wait <= 0f) { StartTake(); return; }
                _titleText.T.text = $"Next one in {Mathf.CeilToInt(wait)}…";
                if (_teachPin != null) _teachPin.Name.text = $"Again in {Mathf.CeilToInt(wait)}…";
                return;
            }
            if (_takeStart < 0f) return;
            var s = director.Sounds;
            var t = Time.time - _takeStart;
            while (s.Captured.TryDequeue(out var cw)) { _takeWins.Add(cw); _takeLevel = Mathf.Max(_takeLevel, cw.rms); }
            if (t < TakeSeconds)
            {
                var left = Mathf.CeilToInt(TakeSeconds - t);
                _titleText.T.text = $"Make the sound now · {left}";
                _bodyText.T.text = $"{LevelBar(_takeLevel)}\n<color=#FFFFFF80>{TakeDots}</color>";
                if (_teachPin != null) _teachPin.Name.text = $"Make the sound now · {left}";
                return;
            }
            if (t < TakeSeconds + TakeTail)
            {
                _titleText.T.text = "Listening…";
                _bodyText.T.text = $"{LevelBar(_takeLevel)}\n<color=#FFFFFF80>{TakeDots}</color>";
                if (_teachPin != null) _teachPin.Name.text = "Listening…";
                return;
            }
            s.Capturing = false;
            if (t < TakeSeconds + TakeTail + TakeDrain) return;
            _takeStart = -1f;
            while (s.Captured.TryDequeue(out var cw)) _takeWins.Add(cw);
            if (_teachPin != null) _teachPin.Name.text = _teachPin.E.name;

            (float rms, float[] print, float time) loud = default, quiet = default;
            var n = 0;
            foreach (var w in _takeWins)
            {
                if (n == 0 || w.rms > loud.rms) loud = w;
                if (n == 0 || w.rms < quiet.rms) quiet = w;
                n++;
            }
            _takeWins.Clear();
            Debug.Log($"[Captions] Sound take: {n} windows, loudest {loud.rms:F4}, quietest {quiet.rms:F4}");
            if (n == 0) _takeNote = "Nothing came through from the microphone. Check it's on, then try again.";
            else if (loud.rms < SoundEvents.TaughtMinRms || n > 1 && loud.rms < 3f * quiet.rms) _takeNote = "Didn't hear it. Make it a bit louder or come closer, then try again.";
            else
            {
                var sims = _takes.ConvertAll(p => SoundEvents.Similarity(p, loud.print).ToString("F2"));
                Debug.Log($"[Captions] Sound take {_takes.Count + 1}: {n} windows, loudest {loud.rms:F4}, quietest {quiet.rms:F4}" +
                          (sims.Count > 0 ? $", like the earlier takes {string.Join(" ", sims)}" : ""));
                _takes.Add(loud.print);
                if (n > 1 && quiet.rms < loud.rms * 0.5f && quiet.rms < _roomRms) { _room = quiet.print; _roomRms = quiet.rms; }
            }
            if (_takes.Count >= TakesNeeded) { FinishTeach(); return; }
            UiFeedback.Play(_takeNote == null ? UiSound.Confirm : UiSound.Back, _setup.Root.position);
            if (_takeNote == null) _nextTakeAt = Time.time + 2.5f;
            Enter(Stage.SoundRecord);
        }

        void FinishTeach()
        {
            var pin = _teachPin;
            _teachPin = null;
            var dims = _takes[0].Length;
            pin.E.prints = new float[dims * _takes.Count];
            for (var i = 0; i < _takes.Count; i++) Array.Copy(_takes[i], 0, pin.E.prints, i * dims, dims);
            pin.E.room = _room;
            _pins.Add(pin);
            AnchorPin(pin);
            SaveSounds();
            PushTaught();
            pin.FiredSince = pin.FiredAt = Time.time; // ripple once: this is where it'll show
            UiFeedback.Play(UiSound.Confirm, _setup.Root.position);
            Debug.Log($"[Captions] Sound learned: {pin.E.name} ({_takes.Count} takes{(_room != null ? ", room kept" : "")})");
            Enter(Stage.SoundSaved);
        }

        void ForgetSounds()
        {
            foreach (var p in _pins)
            {
                if (p.Anchor != null && p.Anchor.TryGetComponent<OVRSpatialAnchor>(out var a)) EraseAnchor(a);
                Destroy(p.Root.gameObject);
            }
            _pins.Clear();
            SaveSounds();
            PushTaught();
            Debug.Log("[Captions] Taught sounds forgotten");
            Enter(Stage.SoundList);
        }

        static async void EraseAnchor(OVRSpatialAnchor a)
        {
            try { await a.EraseAnchorAsync(); }
            catch (Exception e) { Debug.LogWarning($"[Captions] Sound pin anchor not erased: {e.Message}"); }
            if (a != null) Destroy(a.gameObject);
        }

        // ---------------- keeping them ----------------

        void PushTaught()
        {
            var list = new List<SoundEvents.Learned>();
            foreach (var p in _pins)
            {
                if (p.E.prints == null || p.E.prints.Length == 0) continue;
                const int dims = 527;
                var k = p.E.prints.Length / dims;
                var ex = new float[k][];
                for (var i = 0; i < k; i++) { ex[i] = new float[dims]; Array.Copy(p.E.prints, i * dims, ex[i], 0, dims); }
                list.Add(new SoundEvents.Learned { Id = p.E.id, Name = HeardPhrase(p.E.name), Glyph = p.E.glyph, Examples = ex,
                    Room = p.E.room is { Length: dims } ? p.E.room : null });
            }
            _learned = list.ToArray();
            if (director.Sounds != null) director.Sounds.Taught = _learned;
        }

        void SaveSounds()
        {
            try
            {
                var f = new SoundFile();
                foreach (var p in _pins) { p.E.pos = p.Position; f.sounds.Add(p.E); }
                File.WriteAllText(SoundsPath, JsonUtility.ToJson(f));
            }
            catch (Exception e) { Debug.LogWarning($"[Captions] Taught sounds not saved: {e.Message}"); }
        }

        void LoadSounds()
        {
            try
            {
                if (!File.Exists(SoundsPath)) return;
                var f = JsonUtility.FromJson<SoundFile>(File.ReadAllText(SoundsPath));
                foreach (var e in f.sounds) _pins.Add(MakePin(e));
                PushTaught();
                Debug.Log($"[Captions] Taught sounds: {string.Join(", ", _pins.ConvertAll(p => p.E.name))}");
                LoadAnchors();
            }
            catch (Exception e) { Debug.LogWarning($"[Captions] Taught sounds not loaded: {e.Message}"); }
        }

        async void AnchorPin(SoundPin pin)
        {
            try
            {
                var go = new GameObject($"Sound anchor: {pin.E.name}");
                go.transform.SetPositionAndRotation(pin.E.pos, Quaternion.identity);
                var a = go.AddComponent<OVRSpatialAnchor>();
                pin.Anchor = go.transform;
                if (!await a.WhenLocalizedAsync()) { Debug.Log("[Captions] Sound pin kept by position (no anchor)"); return; }
                var r = await a.SaveAnchorAsync();
                if (!r.Success) { Debug.Log($"[Captions] Sound pin anchor not saved ({r.Status}); kept by position"); return; }
                pin.E.anchor = a.Uuid.ToString();
                SaveSounds();
            }
            catch (Exception e) { Debug.LogWarning($"[Captions] Sound pin anchor: {e.Message}"); }
        }

        async void LoadAnchors()
        {
            try
            {
                var ids = new List<Guid>();
                foreach (var p in _pins) if (Guid.TryParse(p.E.anchor, out var g)) ids.Add(g);
                if (ids.Count == 0) return;
                var found = new List<OVRSpatialAnchor.UnboundAnchor>();
                var r = await OVRSpatialAnchor.LoadUnboundAnchorsAsync(ids, found);
                if (!r.Success) { Debug.Log($"[Captions] Sound pin anchors not loaded ({r.Status}); pins by position"); return; }
                foreach (var u in r.Value)
                {
                    var pin = _pins.Find(p => p.E.anchor == u.Uuid.ToString());
                    if (pin == null || !await u.LocalizeAsync()) continue;
                    var go = new GameObject($"Sound anchor: {pin.E.name}");
                    var a = go.AddComponent<OVRSpatialAnchor>();
                    u.BindTo(a);
                    pin.Anchor = go.transform;
                }
            }
            catch (Exception e) { Debug.LogWarning($"[Captions] Sound pin anchors: {e.Message}"); }
        }

        // ---------------- when one is heard ----------------

        void FireSound(SoundEvents.Alert a)
        {
            var pin = _pins.Find(p => p.E.id == a.Source);
            if (pin == null) return;
            if (Time.time - pin.FiredAt > 3f) pin.FiredSince = Time.time;
            pin.FiredAt = Time.time;
            _cuePin = pin;
            Debug.Log($"[Captions] Taught sound heard: {pin.E.name} ({a.Prob:F2}), {Where(pin.Position)}");
        }

        /// <summary>"ahead", "on your left", "behind you" (and a few metres away).</summary>
        string Where(Vector3 at)
        {
            if (head == null) return "";
            var to = at - head.position;
            var angle = Vector3.SignedAngle(Flat(head.forward), Flat(to), Vector3.up);
            var a = Mathf.Abs(angle);
            var side = a < 35f ? "ahead" : a < 125f ? (angle > 0 ? "on your right" : "on your left") : "behind you";
            var d = Flat(to).magnitude;
            return d > 2.5f ? $"{side}, {d:F0} m" : side;
        }

        void UpdateSounds()
        {
            if (_discTex == null) return;
            HookName();
            UpdateNameListen();
            UpdateCaller();
            var s = director.Sounds;
            if (s != null && s.Taught != _learned) s.Taught = _learned;
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb != null && kb.kKey.wasPressedThisFrame && _pins.Count > 0) // editor: K = as if the last taught sound were heard
            {
                var p = _pins[^1];
                OnAlert(new SoundEvents.Alert { Kind = SoundEvents.TaughtPrefix + p.E.id, Name = HeardPhrase(p.E.name), Glyph = p.E.glyph, Advice = "",
                    Source = p.E.id, Level = SoundEvents.Priority.Info, Prob = 1f, Time = Time.time });
            }
#endif
            if (_setup.Open && _stage == Stage.SoundRecord) UpdateTake();
            UpdateHearCheck();
            if (_checking && !(_setup.Open && _stage == Stage.HearRun)) StopHearCheck(); // panel closed mid-check

            SoundPin cue = null;
            foreach (var p in _pins) if (UpdatePin(p) && (cue == null || p.FiredAt > cue.FiredAt)) cue = p;
            if (_teachPin != null) UpdatePin(_teachPin);
            if (_callPin != null && UpdatePin(_callPin) && (cue == null || _callPin.FiredAt > cue.FiredAt)) cue = _callPin;
            UpdateCue(cue);
        }

        // The pin: shown while it sounds (and a while after), while it's being taught, and in the sounds list.
        // Returns whether it sounds now and is out of view (it wants the cue).
        bool UpdatePin(SoundPin p)
        {
            var now = Time.time;
            var firing = now - p.FiredAt < PinLinger;
            var teaching = p == _teachPin;
            var listing = _setup.Open && _soundsOpen && _stage == Stage.SoundList && _pins.Contains(p);
            var want = (firing && alertMarkers) || teaching ? 1f : listing ? 0.55f : 0f;
            p.Shown = Mathf.MoveTowards(p.Shown, want, Time.deltaTime * 3f);
            var on = p.Shown > 0.005f;
            if (p.Root.gameObject.activeSelf != on) p.Root.gameObject.SetActive(on);
            if (!on) return false;

            var pos = p.Position;
            var dist = Vector3.Distance(head.position, pos);
            p.Root.SetPositionAndRotation(pos, Quaternion.LookRotation(pos - head.position, Vector3.up));
            var size = PinSize * Mathf.Max(1f, dist / 1.2f); // about the same size in view from further off
            var color = SysBlue;
            var alpha = p.Shown;
            // A beat each 1.2 s while it sounds, three rings spreading out from it; placing: a slow breath.
            var beat = 0f;
            for (var i = 0; i < p.Rings.Length; i++)
            {
                var ring = p.Root.GetChild(i);
                var k = Mathf.Repeat((now - p.FiredSince) / 1.8f + i / 3f, 1f);
                var ringOn = firing || (teaching && _stage == Stage.SoundRecord && _takeStart >= 0f);
                ring.localScale = Vector3.one * size * (1f + 2.6f * k);
                p.Rings[i].SetColor(BaseColorId, WithAlpha(color, ringOn ? 0.55f * (1f - k) * (1f - k) * alpha : 0f));
                if (i == 0) beat = Mathf.Exp(-Mathf.Repeat(now - p.FiredSince, 1.2f) * 6f);
            }
            var pulse = firing ? 1f + 0.12f * beat : teaching ? 1f + 0.05f * Mathf.Sin(now * 3f) : 1f;
            p.Root.GetChild(p.Rings.Length).localScale = Vector3.one * size * pulse;
            p.Disc.SetColor(BaseColorId, WithAlpha(color, 0.92f * alpha));
            p.Icon.transform.localScale = p.Name.transform.localScale = Vector3.one * size * pulse;
            p.Icon.transform.localPosition = new Vector3(0, 0, -0.002f);
            p.Name.transform.localPosition = new Vector3(0, -size * 0.95f, -0.002f);
            p.Icon.color = WithAlpha(Primary, alpha);
            if (p != _teachPin && p != _callPin) p.Name.text = firing ? HeardPhrase(p.E.name) : p.E.name;
            p.Name.color = WithAlpha(Primary, alpha * (p.Name.text.Length > 0 ? 1f : 0f));
            if (!firing || !alertMarkers) return false;
            return Vector3.Angle(head.forward, pos - head.position) > CueAngle;
        }

        void UpdateCue(SoundPin p)
        {
            if (_cue == null) return;
            if (p != null) _cuePin = p;
            _cueAlpha = Mathf.MoveTowards(_cueAlpha, p != null ? 1f : 0f, Time.deltaTime * 3f);
            var on = _cueAlpha > 0.005f && _cuePin != null;
            if (_cue.gameObject.activeSelf != on) _cue.gameObject.SetActive(on);
            if (!on) return;
            // Which way round: its direction in the view; behind you, straight to the nearer side.
            var local = head.InverseTransformPoint(_cuePin.Position);
            var v = new Vector2(local.x, local.y);
            if (local.z < 0f) v = new Vector2(local.x >= 0f ? 1f : -1f, Mathf.Clamp(local.y, -0.3f, 0.3f));
            if (v.sqrMagnitude < 1e-6f) v = Vector2.right;
            v.Normalize();
            var angle = Mathf.Atan2(v.y, v.x) * Mathf.Rad2Deg;
            var t = Time.time - _cuePin.FiredSince;
            var glow = 0.6f + 0.3f * Mathf.Cos(t * Mathf.PI * 2f / 1.2f);
            _cueGlow.localRotation = Quaternion.Euler(0, 0, angle);
            _cueGlowMat.SetColor(BaseColorId, WithAlpha(SysBlue, glow * _cueAlpha));
            // The arrow sits inside the edge (an ellipse: the view is wider than tall), its chevron pointing on out.
            var at = new Vector2(v.x * 0.15f, v.y * 0.1f);
            _cueArrow.localPosition = new Vector3(at.x, at.y, -0.001f);
            _cueArrow.localRotation = Quaternion.identity;
            _cueIcon.text = _cuePin.E.glyph;
            _cueLabel.text = _cuePin == _callPin ? _callPin.Name.text : _cuePin.E.name;
            _cueLabel.color = WithAlpha(Primary, _cueAlpha);
            var nudge = 0.004f * Mathf.Sin(t * Mathf.PI * 2f / 1.2f);
            _cueChevron.transform.localPosition = new Vector3(v.x * (0.034f + nudge), v.y * (0.034f + nudge), -0.001f);
            _cueChevron.transform.localRotation = Quaternion.Euler(0, 0, angle);
            _cueDiscMat.SetColor(BaseColorId, WithAlpha(SysBlue, 0.95f * _cueAlpha));
            _cueIcon.color = WithAlpha(Primary, _cueAlpha);
            _cueChevron.color = WithAlpha(SysBlue, _cueAlpha);
        }
    }
}
