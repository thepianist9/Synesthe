using TMPro;
using UnityEngine;

namespace MetaStarter.Captions
{
    /// <summary>
    /// Liquid-glass caption bubble (shader: MetaStarter/LiquidGlassBubble).
    ///   • Idle: a small glass orb floats beside the person (gentle bob).
    ///   • Speech: the panel grows out of the orb with a springy, liquid motion (smooth neck between them),
    ///     text fades in once it is mostly open; the orb pulses while words are still coming.
    ///   • No new words for <see cref="collapseAfterSeconds"/> (1.5 s): the panel melts back into the orb, so the
    ///     next words open a fresh bubble (and a new speaker is obvious).
    /// Readability: while open the bubble stays where it opened (it only re-anchors if the person moves away),
    /// the panel never shrinks and changes size smoothly, and words the recognizer may still revise are dimmed.
    /// Faces the viewer and keeps a constant apparent size. <see cref="Target"/> is where the orb sits.
    /// Text motion (<see cref="animateText"/>): new words fade in and settle upwards a few millimetres, left to right;
    /// words that were read again (after a language switch) or revised glow softly and fade back to white, so the eye notices the update.
    /// Short and calm on purpose — no bouncing or flashing, which would make reading harder.
    /// </summary>
    public class CaptionBubble : MonoBehaviour
    {
        const float ReferenceDistance = 1.2f;
        const float MaxTextWidth = 0.32f, MinBodyWidth = 0.11f, PadX = 0.024f, PadY = 0.018f;
        const float BodyLineHeight = 0.024f, NameLineHeight = 0.013f;
        const float OrbRadius = 0.014f;
        const int DefaultLines = 4;
        // Reading pace: the roll-up moves the text up at most one line every ReadSeconds, so a fast talker's words
        // don't scroll away before they're read. While behind, the bubble holds up to ExtraLines more lines
        // (and doesn't melt); past that it jumps to stay close to the speech.
        const float ReadSeconds = 2.5f;
        const int ExtraLines = 2;
        /// <summary>Lines a caption shows before it rolls up (the Caption lines setting, 3–6).</summary>
        public static int Lines = DefaultLines;
        // An open bubble stays put while you read it. It moves only when the person has really moved: further than
        // RelockMeters (sideways; up and down counts half, so nods and sitting up don't move it) for RelockSeconds,
        // or twice as far at once (they walked off). Then it glides over in RelockGlide seconds.
        const float RelockMeters = 0.25f, RelockSeconds = 0.8f, RelockGlide = 0.5f;

        public Transform viewer;
        /// <summary>Where the orb sits (world).</summary>
        public Vector3 Target;
        /// <summary>+1: panel grows to the right of the orb, -1: to the left.</summary>
        public float Side = 1f;
        /// <summary>Panel centred over the orb, which hangs from the middle of its lower edge (like the app logo's
        /// bubble), text centred. For notifications.</summary>
        public bool centered;
        /// <summary>Show the idle orb when there is no text (false for status/fallback bubbles).</summary>
        public bool showIdleOrb = true;
        [Tooltip("Melt back into the orb after this long without new words.")]
        public float collapseAfterSeconds = 1.5f;
        /// <summary>Plain subtitle: just the text (no glass, no orb, no name), centred on <see cref="Target"/>. Used
        /// when nobody can be tied to the words — a bubble floating away from the real speaker looks like a bug.</summary>
        public bool plain;
        /// <summary>Hidden without losing its state (signing mode shows the avatar instead of bubbles).</summary>
        public bool Suppressed;
        /// <summary>Follows <see cref="Target"/> closely instead of staying where it opened: for a strip that belongs
        /// to the view (the wearer's own words), not to a person in the room.</summary>
        public bool headLocked;
        /// <summary>0–1: grows the bubble slightly (a glass button under the pointer).</summary>
        public float Highlight;
        [Tooltip("Animate words as they arrive and highlight corrected words. Off = plain text (reduced motion).")]
        public static bool animateText = true;
        /// <summary>Caption size (×), the start screen's Text Size slider: scales every bubble, glass and text alike.</summary>
        public static float TextScale = 1f;
        /// <summary>This bubble's own size (×) and line limit, and how opaque its text is: the wearer's own-speech strip
        /// is smaller, one line and quieter than other people's captions.</summary>
        public float sizeScale = 1f;
        /// <summary>0: use <see cref="Lines"/>.</summary>
        public int maxLines;
        int LineLimit => maxLines > 0 ? maxLines : Mathf.Clamp(Lines, 1, 8);
        // Roll-up state: the start of the top line (by its text, since the caller's rolling text is trimmed at the
        // front), when it became the top line, and whether lines are waiting to scroll off.
        string _topProbe, _fullText;
        // Smooth roll-up: the lines that just left the top stay drawn above the text ("ghost") while everything slides
        // up by their height over ScrollSeconds and they fade out; then they're dropped (positions match exactly).
        const float ScrollSeconds = 0.45f;
        string _ghost = "", _shownMarked = "";
        float _ghostHeight, _scrollStart = -999f;
        float ScrollT => Mathf.Clamp01((Time.time - _scrollStart) / ScrollSeconds);
        bool Scrolling => _ghost.Length > 0;
        int _fullUnstable;
        float _topSince;
        bool _behind;
        [Range(0f, 1f)] public float textOpacity = 1f;
        /// <summary>Roll-up slides the lines up (and fades the one leaving) instead of jumping.</summary>
        public bool smoothScroll = true;
        const float RevealSeconds = 0.32f, RevealRise = 0.0045f, StaggerPerChar = 0.012f, MaxStagger = 0.35f;
        const float CorrectionGlowSeconds = 1.2f;
        static readonly Color CorrectionGlow = new(0.55f, 0.9f, 1f);

        // Per character of the shown text: when it appeared, and when it was last corrected (Time.time).
        string _shownPlain = "";
        bool _animating;
        float[] _born = System.Array.Empty<float>(), _fixed = System.Array.Empty<float>();

        TextMeshPro _body, _name;
        Color _speakerColor;

        /// <summary>Recolour the speaker's name; <paramref name="hue"/> (they're tagged) also tints the glass in their colour,
        /// so whose bubble it is shows at a glance.</summary>
        public void SetSpeakerColor(Color c, bool hue = false)
        {
            if (_name == null) return;
            if (hue && !_hued) _flashAt = Time.time; // just tagged or recognised: show it
            _hued = hue;
            if (c == _speakerColor) return;
            _speakerColor = c;
            var a = _name.alpha;
            _name.color = Color.Lerp(c, Color.white, 0.35f);
            _name.alpha = a;
        }

        // A tagged person's glass: their colour, a bit lighter, strong enough to see over the room but light enough
        // for white text to stay readable on it. Fades in and out with the tag.
        const float HueStrength = 0.3f;
        // The moment someone is tagged or recognised, their glass glows in their colour (and the bubble shows, even
        // closed, as their orb) for FlashSeconds, two soft pulses, then settles to the usual tint: you see they're known.
        const float FlashSeconds = 1.6f, FlashStrength = 0.55f;
        float _flashAt = -99f;
        float Flash => Mathf.Clamp01(1f - (Time.time - _flashAt) / FlashSeconds);
        bool _hued;
        float _hue;
        Color _baseTint = new(1, 1, 1, 0.1f);
        MeshRenderer _shape;
        Transform _content;
        MaterialPropertyBlock _mpb;
        Vector2 _bodySize = new(0.14f, 0.06f);   // shown (smoothed)
        Vector2 _bodyTarget = new(0.14f, 0.06f); // from the text layout
        Vector2 _textSize, _nameSize;
        Vector3 _anchor, _vel;
        bool _locked;
        float _movedFor;   // how long the person has been further than RelockMeters from the anchor
        float _lastUpdate = -999f;
        bool _open, _speaking, _placed;
        float _grow, _growVel;     // spring state
        float _presence = 1f;      // fades the whole thing (orb included) in/out
        float _phase; // orbs bob out of step

        void Awake() => _phase = Random.value * 6.28f; // Unity's Random can't be used in field initialisers

        static float _bodyFontScale, _nameFontScale;
        static readonly int BodyId = Shader.PropertyToID("_Body");
        static readonly int BodyCenterId = Shader.PropertyToID("_BodyCenter");
        static readonly int OrbId = Shader.PropertyToID("_Orb");
        static readonly int QuadId = Shader.PropertyToID("_Quad");
        static readonly int GrowId = Shader.PropertyToID("_Grow");
        static readonly int AlphaId = Shader.PropertyToID("_Alpha");
        static readonly int TintId = Shader.PropertyToID("_Tint");

        public static CaptionBubble Create(string name, Transform parent, Transform viewer, Material glassMaterial,
            TMP_FontAsset bodyFont, TMP_FontAsset nameFont, Color speakerColor)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var b = go.AddComponent<CaptionBubble>();
            b.viewer = viewer;
            b.Build(glassMaterial, bodyFont, nameFont, speakerColor);
            return b;
        }

        void Build(Material glassMaterial, TMP_FontAsset bodyFont, TMP_FontAsset nameFont, Color speakerColor)
        {
            _mpb = new MaterialPropertyBlock();
            _content = new GameObject("Content").transform;
            _content.SetParent(transform, false);

            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            if (Application.isPlaying) Destroy(q.GetComponent<Collider>()); else DestroyImmediate(q.GetComponent<Collider>());
            q.name = "Glass";
            q.transform.SetParent(transform, false);
            _shape = q.GetComponent<MeshRenderer>();
            _shape.sharedMaterial = glassMaterial;
            if (glassMaterial != null && glassMaterial.HasProperty(TintId)) _baseTint = glassMaterial.GetColor(TintId);
            _shape.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _shape.enabled = false; // LateUpdate shows it once there is something to show

            _body = MakeText("Caption", bodyFont, Color.white, TextAlignmentOptions.TopLeft);
            _speakerColor = speakerColor;
            _name = MakeText("Speaker", nameFont != null ? nameFont : bodyFont, Color.Lerp(speakerColor, Color.white, 0.35f), TextAlignmentOptions.BottomLeft);
            // White text over bright passthrough: a soft shadow keeps it legible.
            _body.fontSharedMaterial = new Material(_body.fontSharedMaterial);
            _body.fontSharedMaterial.EnableKeyword("UNDERLAY_ON");
            _body.fontSharedMaterial.SetColor("_UnderlayColor", new Color(0, 0, 0, 0.4f));
            _body.fontSharedMaterial.SetFloat("_UnderlayOffsetY", -0.5f);
            _body.fontSharedMaterial.SetFloat("_UnderlaySoftness", 0.5f);

            if (_bodyFontScale <= 0) _bodyFontScale = MeasureFontScale(_body);
            if (_nameFontScale <= 0) _nameFontScale = MeasureFontScale(_name);
            _body.fontSize = BodyLineHeight * _bodyFontScale;
            _name.fontSize = NameLineHeight * _nameFontScale;
            SetTextAlpha(0);
        }

        TextMeshPro MakeText(string name, TMP_FontAsset font, Color color, TextAlignmentOptions align)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_content, false);
            var t = go.AddComponent<TextMeshPro>();
            if (font != null) t.font = font;
            ComplexText.Attach(t);
            t.color = color;
            t.alignment = align;
            t.textWrappingMode = TextWrappingModes.Normal;
            t.overflowMode = TextOverflowModes.Overflow;
            t.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return t;
        }

        static float MeasureFontScale(TextMeshPro t)
        {
            t.fontSize = 1f;
            var h = t.GetPreferredValues("Ag", 100f, 100f).y;
            return h > 1e-5f ? 1f / h : 10f;
        }

        /// <summary>Open (or update) the bubble. <paramref name="final"/> = sentence finished; false = still talking.
        /// <paramref name="unstableWords"/>: how many trailing words may still change (shown dimmed).
        /// <paramref name="refresh"/>: restart the collapse timer (new words). A final that only confirms words
        /// already shown passes false, so the bubble still melts once the person has stopped talking.</summary>
        public void Show(string speaker, string text, bool final, int unstableWords = 0, bool refresh = true)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (!_open && !refresh) return; // already melted: don't re-open just to confirm old words
            var reopening = !_open || _grow < 0.05f;
            _name.text = plain ? "" : $"<size=110%>●</size> {speaker.ToUpperInvariant()}";
            Layout(text.Replace('<', ' '), final ? 0 : unstableWords, reopening);
            _speaking = !final;
            if (refresh) _lastUpdate = Time.time;
            _open = true;
        }

        public void Hide() { _open = false; _speaking = false; }

        /// <summary>The person is still talking (between sentences too): keep the open panel from melting.</summary>
        public void KeepOpen() { if (_open) _lastUpdate = Time.time; }
        public bool IsShowing => _open;
        /// <summary>The text shown (whole, before any roll-up).</summary>
        public string Text => _fullText ?? "";

        void Layout(string text, int unstableWords, bool reopening)
        {
            // Wrap at the full width; once more than the line limit, drop whole lines from the top (TV roll-up),
            // at most one every ReadSeconds. No "…" prefix: it would change the wrapping and make every line jump.
            _fullText = text;
            _fullUnstable = unstableWords;
            _body.text = text;
            _body.rectTransform.sizeDelta = new Vector2(MaxTextWidth, 10f);
            _body.ForceMeshUpdate();
            var info = _body.textInfo;
            var n = info.lineCount;
            var limit = LineLimit;
            var top = Mathf.Max(0, n - limit);
            var now = Time.time;
            var at = reopening || string.IsNullOrEmpty(_topProbe) ? -1 : text.IndexOf(_topProbe, System.StringComparison.Ordinal);
            var oldTop = -1;
            if (at >= 0)
            {
                var cur = 0;
                while (cur + 1 < n && info.lineInfo[cur + 1].firstCharacterIndex <= at) cur++;
                oldTop = cur;
                if (cur < top)
                {
                    // Behind the speech: one line up once the top one has had its time, and never more than
                    // ExtraLines over the limit.
                    if (now - _topSince >= ReadSeconds) cur++;
                    top = Mathf.Max(cur, n - limit - ExtraLines);
                }
                else top = cur; // (fewer lines than the limit after a correction: don't bring back lines already gone)
            }
            _behind = n - top > limit;
            // Lines leaving the top now: keep them as the ghost of a slide (a slide already running keeps its ghost).
            var full = text;
            if (n > 0 && top > 0) text = text.Substring(Mathf.Min(text.Length, info.lineInfo[top].firstCharacterIndex)).TrimStart();
            if (reopening || !smoothScroll) _ghost = "";
            else if (oldTop >= 0 && top > oldTop)
            {
                var fromOld = full.Substring(Mathf.Min(full.Length, info.lineInfo[oldTop].firstCharacterIndex)).TrimStart();
                _ghost = fromOld.EndsWith(text, System.StringComparison.Ordinal) ? fromOld.Substring(0, fromOld.Length - text.Length) : "";
                _scrollStart = now;
            }
            var probe = text.Substring(0, Mathf.Min(24, text.Length));
            if (probe != _topProbe) { _topProbe = probe; _topSince = now; }
            var size = _body.GetPreferredValues(text, MaxTextWidth, 10f);
            var nameSize = _name.GetPreferredValues(_name.text, 1f, 1f);
            var w = Mathf.Max(MinBodyWidth, Mathf.Max(Mathf.Min(size.x, MaxTextWidth), nameSize.x) + PadX * 2);
            var target = new Vector2(w, size.y + nameSize.y + 0.004f + PadY * 2);
            // While open the panel only grows: a box that shrinks and grows with every word is hard to read.
            _bodyTarget = reopening ? target : Vector2.Max(_bodyTarget, target);
            // Jump to the new size: a panel still easing bigger leaves the new line hanging outside the glass.
            _bodySize = _bodyTarget;
            // Draw the text at the width it was measured and wrapped at: a box even slightly narrower lets TextMeshPro
            // wrap one more line than measured, and that line hangs below the panel.
            _textSize = new Vector2(MaxTextWidth, size.y);
            _nameSize = nameSize;

            // Words the recognizer may still revise are dimmed (same layout: alpha doesn't change metrics).
            var cut = text.Length;
            for (var u = 0; u < unstableWords && cut > 0; u++)
            {
                cut = text.LastIndexOf(' ', Mathf.Max(0, cut - 2));
                if (cut < 0) { cut = 0; break; }
            }
            _shownMarked = cut < text.Length ? text.Substring(0, cut) + "<alpha=#8C>" + text.Substring(cut) : text;
            _body.text = _ghost + _shownMarked;
            _ghostHeight = 0f;
            if (Scrolling)
            {
                // Height of the ghost lines: from the first line's top to the top of the first line of the shown text.
                _body.ForceMeshUpdate();
                var li = _body.textInfo;
                var g = 0;
                while (g < li.lineCount && li.lineInfo[g].firstCharacterIndex < _ghost.Length) g++;
                _ghostHeight = g < li.lineCount ? li.lineInfo[0].ascender - li.lineInfo[g].ascender : 0f;
                if (_ghostHeight <= 0f) { _ghost = ""; _body.text = _shownMarked; }
            }
            TrackChanges(text, reopening);
            PlaceText();
        }

        void PlaceText()
        {
            var c = BodyCenter();
            var w = _bodySize.x;
            var top = c.y + _bodySize.y / 2 - PadY;
            _name.rectTransform.sizeDelta = _nameSize;
            _name.transform.localPosition = new Vector3(centered ? c.x : c.x - w / 2 + PadX + _nameSize.x / 2, top - _nameSize.y / 2, -0.002f);
            // While sliding, the ghost lines sit above the text and the whole block rises by their height.
            var ghost = Scrolling ? _ghostHeight : 0f;
            var t = ScrollT;
            var rise = ghost * (1f - (1f - t) * (1f - t) * (1f - t)); // ease-out cubic
            _body.rectTransform.sizeDelta = new Vector2(_textSize.x, _textSize.y + ghost);
            // Left-aligned text starts at the panel's left padding; plain (centred) text is centred on the panel.
            var x = plain || centered ? c.x : c.x - w / 2 + PadX + _textSize.x / 2;
            _body.transform.localPosition = new Vector3(x, top - _nameSize.y - 0.004f - (_textSize.y + ghost) / 2 - ghost + rise, -0.002f);
        }

        // Panel sits above the orb and to its side, so the orb hangs off its lower corner (like a liquid drop).
        // The orb nests in the panel's rounded lower corner so the smooth-min union forms a neck between them.
        Vector2 BodyCenter() => plain
            ? new Vector2(0, _bodySize.y / 2)
            : centered ? new Vector2(0, _bodySize.y / 2 + OrbRadius * 0.55f)
            : new(Side * (_bodySize.x / 2 - OrbRadius * 0.15f), _bodySize.y / 2 + OrbRadius * 0.55f);

        /// <summary>Centre the panel and its text over the orb (see <see cref="centered"/>).</summary>
        public void MakeCentered()
        {
            centered = true;
            _body.alignment = TextAlignmentOptions.Top;
        }

        /// <summary>Switch to plain subtitle text (see <see cref="plain"/>).</summary>
        public void MakePlain()
        {
            plain = true;
            showIdleOrb = false;
            _body.alignment = TextAlignmentOptions.Top;
            // Glass panel behind it (no orb, no name) for contrast; a slightly stronger shadow on top.
            _body.fontSharedMaterial.SetColor("_UnderlayColor", new Color(0, 0, 0, 0.6f));
            _body.fontSharedMaterial.SetFloat("_UnderlaySoftness", 0.5f);
        }

        // Which characters are new or corrected compared with the text shown before. The roll-up can drop whole lines
        // from the top, so first find where the old text continues in the new one.
        void TrackChanges(string text, bool reopening)
        {
            var now = Time.time;
            var born = new float[text.Length];
            var fixd = new float[text.Length];
            for (var i = 0; i < text.Length; i++) { born[i] = now + Mathf.Min(i * StaggerPerChar * 0.5f, MaxStagger); fixd[i] = -999f; }

            if (!reopening && _shownPlain.Length > 0 && text.Length > 0)
            {
                var probe = text.Substring(0, Mathf.Min(16, text.Length));
                var k = _shownPlain.StartsWith(probe) ? 0 : _shownPlain.IndexOf(probe, System.StringComparison.Ordinal);
                if (k >= 0)
                {
                    var old = _shownPlain.Substring(k);
                    var pre = 0;
                    while (pre < old.Length && pre < text.Length && old[pre] == text[pre]) pre++;
                    for (var i = 0; i < pre; i++) { born[i] = _born[i + k]; fixd[i] = _fixed[i + k]; }
                    if (pre == old.Length)
                    {
                        // Words added at the end: reveal left to right.
                        for (var i = pre; i < text.Length; i++) born[i] = now + Mathf.Min((i - pre) * StaggerPerChar, MaxStagger);
                    }
                    else
                    {
                        // Corrected (read again after a language switch, or the live model revising itself): keep the unchanged tail still,
                        // glow the changed middle.
                        var suf = 0;
                        while (suf < old.Length - pre && suf < text.Length - pre &&
                               old[old.Length - 1 - suf] == text[text.Length - 1 - suf]) suf++;
                        for (var i = 0; i < suf; i++)
                        {
                            born[text.Length - 1 - i] = _born[k + old.Length - 1 - i];
                            fixd[text.Length - 1 - i] = _fixed[k + old.Length - 1 - i];
                        }
                        for (var i = pre; i < text.Length - suf; i++)
                        {
                            born[i] = now - RevealSeconds * 0.5f; // already half visible: a correction shouldn't vanish first
                            fixd[i] = now;
                        }
                    }
                }
            }
            _shownPlain = text;
            _born = born;
            _fixed = fixd;
        }

        // Applied after TextMeshPro builds the mesh: per-character alpha, rise and glow.
        void AnimateText()
        {
            if (_body.alpha <= 0.001f) return;
            var now = Time.time;
            if (Scrolling && ScrollT >= 1f)
            {
                // Slide done: drop the ghost; the shown text now sits exactly where the slide left it.
                _ghost = ""; _body.text = _shownMarked; PlaceText(); _animating = true;
            }
            else if (Scrolling) PlaceText();
            var off = _ghost.Length;
            var active = Scrolling;
            for (var i = 0; animateText && i < _born.Length && !active; i++)
                active = now - _born[i] < RevealSeconds || now - _fixed[i] < CorrectionGlowSeconds;
            if (!active)
            {
                // One clean rebuild after the last animated frame, so nothing stays half-faded or tinted.
                if (_animating) { _animating = false; _body.ForceMeshUpdate(); }
                return;
            }
            _animating = true;

            _body.ForceMeshUpdate();
            var info = _body.textInfo;
            var ghostAlpha = 1f - Mathf.Clamp01(ScrollT * 1.6f); // leaving lines are gone before they reach the name
            for (var i = 0; i < info.characterCount && i - off < _born.Length; i++)
            {
                var ch = info.characterInfo[i];
                if (!ch.isVisible) continue;
                var k = i - off;
                var t = k < 0 || !animateText ? 1f : Mathf.Clamp01((now - _born[k]) / RevealSeconds);
                var reveal = 1f - (1f - t) * (1f - t) * (1f - t); // ease-out cubic
                var ease = reveal * (k < 0 ? ghostAlpha : 1f);
                var glow = k < 0 || !animateText ? 0f : Mathf.Clamp01(1f - (now - _fixed[k]) / CorrectionGlowSeconds);
                glow *= glow;
                var mesh = info.meshInfo[ch.materialReferenceIndex];
                var v = ch.vertexIndex;
                var rise = new Vector3(0, -(1f - reveal) * RevealRise, 0);
                for (var j = 0; j < 4; j++)
                {
                    mesh.vertices[v + j] += rise;
                    var c = mesh.colors32[v + j];
                    Color cc = c;
                    if (glow > 0) cc = Color.Lerp(cc, new Color(CorrectionGlow.r, CorrectionGlow.g, CorrectionGlow.b, cc.a), glow);
                    cc.a *= ease;
                    mesh.colors32[v + j] = cc;
                }
            }
            _body.UpdateVertexData(TMP_VertexDataUpdateFlags.Colors32 | TMP_VertexDataUpdateFlags.Vertices);
        }

        void SetTextAlpha(float a)
        {
            _body.alpha = a * textOpacity;
            _name.alpha = a * textOpacity;
        }

        void LateUpdate()
        {
            var dt = Mathf.Min(Time.deltaTime, 0.05f);
            if (Suppressed)
            {
                if (_shape.enabled) { _shape.enabled = false; SetTextAlpha(0); }
                return;
            }
            if (_open && _behind && Time.time - _topSince >= ReadSeconds) Layout(_fullText, _fullUnstable, false); // next line up
            // Behind on reading: one more line's worth of time, then it melts anyway (it used to wait for every line).
            if (_open && Time.time - _lastUpdate > collapseAfterSeconds + (_behind ? ReadSeconds : 0f)) { _open = false; _speaking = false; }

            // Springy grow: slightly under-damped so the panel overshoots a touch, like a liquid settling.
            var target = _open ? 1f : 0f;
            _growVel += (target - _grow) * 170f * dt;
            _growVel *= Mathf.Exp(-13f * dt);
            _grow = Mathf.Clamp(_grow + _growVel * dt, 0f, 1.06f);

            var wantPresence = _open || showIdleOrb || Flash > 0f ? 1f : 0f;
            _presence = Mathf.MoveTowards(_presence, wantPresence, dt / 0.3f);
            if (_presence <= 0.001f && !_open)
            {
                if (_shape.enabled) { _shape.enabled = false; SetTextAlpha(0); }
                return;
            }
            _shape.enabled = true;

            // Panel size eases to the layout size (it only ever grows while open).
            if ((_bodySize - _bodyTarget).sqrMagnitude > 1e-8f)
            {
                _bodySize = Vector2.Lerp(_bodySize, _bodyTarget, 1f - Mathf.Exp(-14f * dt));
                PlaceText();
            }

            if (viewer != null)
            {
                // Open bubbles stay where they opened; they re-anchor only if the person really moved.
                var scaleNow = Mathf.Clamp((Target - viewer.position).magnitude / ReferenceDistance, 0.6f, 3f);
                if (_open && _grow > 0.3f && !headLocked)
                {
                    var off = Target - _anchor;
                    var moved = new Vector2(new Vector2(off.x, off.z).magnitude, off.y * 0.5f).magnitude / (RelockMeters * scaleNow);
                    _movedFor = moved > 1f ? _movedFor + dt : 0f;
                    if (!_locked || moved > 2f || _movedFor > RelockSeconds) { _anchor = Target; _movedFor = 0f; }
                    _locked = true;
                }
                else _locked = false;
                var bob = Vector3.up * Mathf.Sin(Time.time * 1.7f + _phase) * 0.004f * (1f - Mathf.Clamp01(_grow));
                var goal = (_locked ? _anchor : Target) + bob;
                transform.position = _placed ? Vector3.SmoothDamp(transform.position, goal, ref _vel, _locked ? RelockGlide : headLocked ? 0.1f : 0.18f, Mathf.Infinity, dt) : goal;
                _placed = true;
                var toBubble = transform.position - viewer.position;
                if (toBubble.sqrMagnitude > 1e-4f)
                {
                    transform.rotation = Quaternion.LookRotation(toBubble, Vector3.up);
                    transform.localScale = Vector3.one * Mathf.Clamp(toBubble.magnitude / ReferenceDistance, 0.6f, 3f) * TextScale * sizeScale * (1f + 0.06f * Highlight);
                }
            }

            UpdateShape();
            SetTextAlpha(Mathf.SmoothStep(0, 1, Mathf.InverseLerp(0.85f, 1f, _grow)) * _presence); // only once the glass is (nearly) open
            AnimateText();
        }

        void UpdateShape()
        {
            var c = BodyCenter();
            var pulse = _speaking ? 1f + 0.1f * Mathf.Sin(Time.time * 9f) : 1f;
            var orbR = plain ? 0f : OrbRadius * pulse; // plain subtitle: glass panel only
            var half = _bodySize * 0.5f;
            var min = new Vector2(Mathf.Min(-orbR, c.x - half.x), Mathf.Min(-orbR, c.y - half.y)) - Vector2.one * 0.012f;
            var max = new Vector2(Mathf.Max(orbR, c.x + half.x), Mathf.Max(orbR, c.y + half.y)) + Vector2.one * 0.012f;
            var size = max - min;
            var center = (max + min) * 0.5f;
            _shape.transform.localPosition = new Vector3(center.x, center.y, 0.001f);
            _shape.transform.localScale = new Vector3(size.x, size.y, 1);

            _shape.GetPropertyBlock(_mpb);
            _mpb.SetVector(BodyId, new Vector4(_bodySize.x, _bodySize.y, Mathf.Min(0.016f, _bodySize.y * 0.22f), 0.006f)); // tailored corners, a shallow rim
            _mpb.SetVector(BodyCenterId, new Vector4(c.x, c.y, 0, 0));
            _mpb.SetVector(OrbId, new Vector4(orbR, plain ? 0f : 0.012f, 0, 0));
            _mpb.SetVector(QuadId, new Vector4(center.x, center.y, size.x, size.y));
            _mpb.SetFloat(GrowId, _grow);
            _mpb.SetFloat(AlphaId, _presence);
            _hue = Mathf.MoveTowards(_hue, _hued && !plain ? 1f : 0f, Time.deltaTime * 2f);
            var tint = Color.Lerp(_speakerColor, Color.white, 0.2f);
            tint.a = HueStrength;
            var glow = Color.Lerp(_baseTint, tint, _hue);
            var f = Flash;
            if (f > 0f && !plain)
            {
                var beat = f * (0.6f + 0.4f * Mathf.Cos((1f - f) * FlashSeconds * Mathf.PI * 2f / (FlashSeconds / 2f)));
                var bright = Color.Lerp(_speakerColor, Color.white, 0.1f);
                bright.a = Mathf.Max(glow.a, HueStrength + FlashStrength * beat);
                glow = Color.Lerp(glow, bright, beat);
            }
            _mpb.SetColor(TintId, glow);
            _shape.SetPropertyBlock(_mpb);
        }
    }
}
