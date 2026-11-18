using TMPro;
using UnityEngine;

namespace MetaStarter.Captions
{
    /// <summary>
    /// Launch animation, before the start screen. First a handful of glass bubbles pop up around the wearer, each
    /// growing just long enough to say hello in another language before it shrinks back and fades. Then an empty glass
    /// orb pops in at the centre and stretches into a pill; the name appears letter by letter, each letter rising in a
    /// colour of its own before it settles to white (letters seen as colours: synesthesia), the tagline follows and a
    /// glint sweeps the glass. Then the pill shrinks back into the orb, which pops, and <see cref="Play"/>'s callback
    /// opens the menu.
    /// <see cref="Pose"/> draws any moment of it, for the Editor preview.
    /// </summary>
    public class SplashLogo : MonoBehaviour
    {
        public Transform head;
        public Material glassMaterial;   // MetaStarter/LiquidGlassBubble
        public Material imageMaterial;   // URP Unlit, transparent
        public TMP_FontAsset font;
        public string title = ModeMenu.AppName;
        public string tagline = "Live captions & ASL signing";
        /// <summary>What the intro bubbles say, one each (Latin, Cyrillic and Greek: the scripts Inter covers).</summary>
        public string[] greetings = { "Hola", "Bonjour", "Привет", "Hallo", "Γειά σου", "Ciao", "Olá" };

        /// <summary>The intro bubbles, then the logo (which starts as the last bubbles fade).</summary>
        public const float IntroDuration = 2.3f, LogoDuration = 3.3f, Duration = IntroDuration + LogoDuration;
        const float Distance = 0.9f, Size = 1.7f; // Size: the whole splash, scaled up from the pill below
        // Pill height and orb radius (m); the pill's width fits the name. Both are centred on the splash.
        const float PillH = 0.11f, OrbR = 0.046f;

        static readonly int BodyId = Shader.PropertyToID("_Body"), BodyCenterId = Shader.PropertyToID("_BodyCenter");
        static readonly int OrbId = Shader.PropertyToID("_Orb"), QuadId = Shader.PropertyToID("_Quad");
        static readonly int GrowId = Shader.PropertyToID("_Grow"), AlphaId = Shader.PropertyToID("_Alpha");
        static readonly int TintId = Shader.PropertyToID("_Tint"), LightId = Shader.PropertyToID("_LightDir");
        static readonly int BlurId = Shader.PropertyToID("_BlurRadius"), StrengthId = Shader.PropertyToID("_PtStrength");
        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor"), BaseMapId = Shader.PropertyToID("_BaseMap");

        // Intro bubbles: where each sits (m, in front of the logo's spot; +z is further away), started one after another.
        static readonly Vector3[] BubbleSpots =
        {
            new(-0.42f, 0.16f, 0.25f), new(0.30f, 0.22f, 0.15f), new(-0.12f, -0.12f, 0.05f), new(0.48f, -0.08f, 0.35f),
            new(-0.55f, -0.18f, 0.45f), new(0.10f, 0.30f, 0.5f), new(0.22f, -0.24f, 0f),
        };
        const float BubbleStagger = 0.13f, BubbleLife = 1.75f, BubbleOrbR = 0.04f, BubbleH = 0.09f, BubbleLine = 0.045f;

        struct Bubble { public Transform t; public MeshRenderer glass; public TextMeshPro text; public float w, start; public Vector3 spot, drift, bob; public Color tint; public float phase, freq, roll; }
        Bubble[] _bubbles;
        Transform _intro;

        Transform _root, _orb;
        MeshRenderer _glass, _halo;
        float _pillW;
        Color[] _hues;
        readonly System.Text.StringBuilder _sb = new();
        TextMeshPro _title, _tagline;
        MaterialPropertyBlock _mpb;
        System.Action _done;
        float _t = -1f, _wait;

        /// <summary>Run the animation once, then call <paramref name="done"/>.</summary>
        public void Play(System.Action done)
        {
            if (_root == null) Build();
            _done = done;
            _t = 0f;
            _wait = 0.5f; // the headset's pose settles in the first moments after launch
            _root.gameObject.SetActive(false);
            _intro.gameObject.SetActive(false);
        }

        void Update()
        {
            if (_t < 0f) return;
            if (_wait > 0f)
            {
                _wait -= Time.deltaTime;
                if (_wait > 0f) return;
                Place(head);
            }
            _t += Time.deltaTime;
            Pose(_t);
            if (_t < Duration) return;
            _t = -1f;
            Destroy(_root.gameObject);
            Destroy(_intro.gameObject);
            _root = null;
            _done?.Invoke();
        }

        /// <summary>Stand the splash in front of <paramref name="viewer"/>, at eye height.</summary>
        public void Place(Transform viewer)
        {
            var fwd = viewer.forward;
            fwd.y = 0;
            fwd = fwd.sqrMagnitude < 1e-4f ? Vector3.forward : fwd.normalized;
            var pos = viewer.position + fwd * Distance;
            var rot = Quaternion.LookRotation(pos - viewer.position, Vector3.up);
            _root.SetPositionAndRotation(pos, rot);
            _root.gameObject.SetActive(true);
            _intro.SetPositionAndRotation(pos, rot);
            _intro.gameObject.SetActive(true);
            // Each bubble turns to face the wearer (who stands Distance behind the intro's origin).
            foreach (var b in _bubbles)
                Face(b, b.spot, 0f);
        }

        public void Build()
        {
            _mpb = new MaterialPropertyBlock();
            _root = new GameObject("Splash").transform;
            _root.SetParent(transform, false);
            _orb = new GameObject("Orb").transform;
            _orb.SetParent(_root, false);

            _halo = Quad("Halo", _orb, imageMaterial, 0);
            _glass = Quad("Glass", _orb, glassMaterial, 1);
            SetImage(_halo, HaloTexture(), Color.clear);

            _title = Text("Title", _root, title, 0.05f, new Vector2(0, 0.012f));
            _tagline = Text("Tagline", _root, tagline, 0.017f, new Vector2(0, -0.03f));
            var widest = 0f;
            foreach (var t in new[] { _title, _tagline })
            {
                t.alignment = TextAlignmentOptions.Midline;
                t.rectTransform.pivot = new Vector2(0.5f, 0.5f);
                t.rectTransform.sizeDelta = new Vector2(0.6f, t.rectTransform.sizeDelta.y);
                widest = Mathf.Max(widest, t.GetPreferredValues(t.text, 100f, 100f).x);
            }
            _pillW = Mathf.Max(0.24f, widest + 0.09f);
            // Each letter's own colour, round the colour wheel.
            _hues = new Color[title.Length];
            for (var i = 0; i < _hues.Length; i++) _hues[i] = Color.HSVToRGB((0.02f + (float)i / Mathf.Max(1, _hues.Length)) % 1f, 0.6f, 1f);
            BuildIntro();
        }

        void BuildIntro()
        {
            _intro = new GameObject("Splash intro").transform;
            _intro.SetParent(transform, false);
            var n = Mathf.Min(greetings?.Length ?? 0, BubbleSpots.Length);
            _bubbles = new Bubble[n];
            for (var i = 0; i < n; i++)
            {
                var t = new GameObject("Hello " + greetings[i]).transform;
                t.SetParent(_intro, false);
                t.localPosition = BubbleSpots[i];
                var glass = Quad("Glass", t, glassMaterial, 0);
                var text = Text("Text", t, greetings[i], BubbleLine, Vector2.zero);
                text.alignment = TextAlignmentOptions.Midline;
                text.rectTransform.pivot = new Vector2(0.5f, 0.5f);
                text.rectTransform.sizeDelta = new Vector2(0.4f, BubbleLine * 1.5f);
                var w = text.GetPreferredValues(text.text, 100f, 100f).x + BubbleH * 0.9f;
                var tint = Color.HSVToRGB((0.08f + i * 0.13f) % 1f, 0.45f, 0.22f);
                tint.a = 0.4f;
                // Each drifts its own way: a direction, speed, bob and sway of its own (fixed per bubble, so every launch looks the same).
                var rnd = new System.Random(7919 * (i + 1));
                float R(float a, float b) => a + (b - a) * (float)rnd.NextDouble();
                var ang = R(0f, 2 * Mathf.PI);
                var drift = new Vector3(Mathf.Cos(ang), Mathf.Sin(ang) * 0.8f, R(-0.5f, 0.5f)) * R(0.03f, 0.07f);
                _bubbles[i] = new Bubble
                {
                    t = t, glass = glass, text = text, w = w, start = i * BubbleStagger, spot = BubbleSpots[i], tint = tint,
                    drift = drift, bob = new Vector3(R(0.004f, 0.01f), R(0.006f, 0.014f), 0f),
                    phase = R(0f, 2 * Mathf.PI), freq = R(2.2f, 4.2f), roll = R(-6f, 6f) * (i % 2 == 0 ? 1 : -1),
                };
            }
        }

        MeshRenderer Quad(string name, Transform parent, Material material, int layer)
        {
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            var col = q.GetComponent<Collider>();
            if (Application.isPlaying) Destroy(col); else DestroyImmediate(col);
            q.name = name;
            q.transform.SetParent(parent, false);
            q.transform.localPosition = new Vector3(0, 0, -0.003f * layer);
            var r = q.GetComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.sortingOrder = layer;
            return r;
        }

        TextMeshPro Text(string name, Transform parent, string text, float lineHeight, Vector2 pos)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(pos.x, pos.y, -0.008f);
            var t = go.AddComponent<TextMeshPro>();
            if (font != null) t.font = font;
            t.fontSize = 1f;
            var h = t.GetPreferredValues("Ag", 100f, 100f).y;
            t.fontSize = lineHeight * (h > 1e-5f ? 1f / h : 10f);
            t.text = text;
            t.alignment = TextAlignmentOptions.MidlineLeft;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.rectTransform.pivot = new Vector2(0, 0.5f);
            t.rectTransform.sizeDelta = new Vector2(0.3f, lineHeight * 1.5f);
            t.color = new Color(1, 1, 1, 0);
            var r = t.GetComponent<MeshRenderer>();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.sortingOrder = 5;
            return t;
        }

        void SetImage(MeshRenderer r, Texture tex, Color color)
        {
            r.GetPropertyBlock(_mpb);
            if (tex != null) _mpb.SetTexture(BaseMapId, tex);
            _mpb.SetColor(BaseColorId, color);
            r.SetPropertyBlock(_mpb);
        }

        // A soft round glow, made once.
        static Texture2D _haloTex;
        static Texture2D HaloTexture()
        {
            if (_haloTex != null) return _haloTex;
            const int n = 128;
            _haloTex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, name = "Splash halo" };
            var px = new Color32[n * n];
            for (var y = 0; y < n; y++)
            for (var x = 0; x < n; x++)
            {
                var d = new Vector2(x - n / 2f + 0.5f, y - n / 2f + 0.5f).magnitude / (n / 2f);
                var a = Mathf.Pow(Mathf.Clamp01(1 - d), 2.2f);
                px[y * n + x] = new Color32(255, 255, 255, (byte)(a * 255));
            }
            _haloTex.SetPixels32(px);
            _haloTex.Apply();
            return _haloTex;
        }

        // ---------------- the animation ----------------

        static float Ramp(float t, float a, float b) => Mathf.Clamp01((t - a) / (b - a));
        static float EaseOut(float x) => 1 - Mathf.Pow(1 - x, 3);
        static float EaseInOut(float x) => x < 0.5f ? 4 * x * x * x : 1 - Mathf.Pow(-2 * x + 2, 3) / 2;
        static float EaseOutBack(float x) { const float c = 1.6f; return 1 + (c + 1) * Mathf.Pow(x - 1, 3) + c * Mathf.Pow(x - 1, 2); }

        /// <summary>Draw the animation at <paramref name="t"/> seconds (0 to <see cref="Duration"/>).</summary>
        public void Pose(float t)
        {
            PoseIntro(t);
            PoseLogo(t - IntroDuration);
        }

        // Each bubble: an orb pops in, grows into a pill around its greeting, holds it a moment, shrinks back to the
        // orb and fades, drifting up a little the whole time.
        void PoseIntro(float t)
        {
            var any = false;
            foreach (var b in _bubbles)
            {
                var u = t - b.start;
                var live = u > 0f && u < BubbleLife;
                b.t.gameObject.SetActive(live);
                if (!live) continue;
                any = true;
                var appear = Ramp(u, 0f, 0.3f);
                var grow = EaseInOut(Ramp(u, 0.22f, 0.6f)) * (1 - EaseInOut(Ramp(u, 1.15f, 1.45f)));
                var fade = EaseInOut(Ramp(u, 1.4f, BubbleLife));
                var alpha = EaseOut(appear) * (1 - fade);
                // Own drift (easing off as it goes), plus a bob and a slight sway that are out of step with the others.
                var k = u / BubbleLife;
                var w = u * b.freq + b.phase;
                var pos = b.spot + b.drift * (k * (2 - k)) + new Vector3(b.bob.x * Mathf.Sin(w * 0.7f), b.bob.y * Mathf.Sin(w), 0f);
                b.t.localPosition = pos;
                Face(b, pos, b.roll * Mathf.Sin(w * 0.5f));
                b.t.localScale = Vector3.one * Mathf.LerpUnclamped(0.3f, 1f, EaseOutBack(appear)) * (1 - 0.35f * fade);

                var half = new Vector2(Mathf.Lerp(BubbleOrbR * 0.45f, b.w / 2, grow), Mathf.Lerp(BubbleOrbR * 0.45f, BubbleH / 2, grow));
                var size = new Vector2(Mathf.Max(BubbleOrbR, half.x), Mathf.Max(BubbleOrbR, half.y)) * 2 + Vector2.one * 0.028f;
                b.glass.transform.localScale = new Vector3(size.x, size.y, 1);
                var light = b.t.rotation * new Vector3(-0.8f + 1.6f * Ramp(u, 0.2f, 1.4f), 0.9f, -0.6f);
                b.glass.GetPropertyBlock(_mpb);
                _mpb.SetVector(BodyId, new Vector4(b.w, BubbleH, BubbleH / 2, 0.008f));
                _mpb.SetVector(BodyCenterId, Vector4.zero);
                _mpb.SetVector(OrbId, new Vector4(BubbleOrbR, 0.02f, 0, 0));
                _mpb.SetVector(QuadId, new Vector4(0, 0, size.x, size.y));
                _mpb.SetFloat(GrowId, grow);
                _mpb.SetFloat(AlphaId, alpha);
                _mpb.SetColor(TintId, b.tint);
                _mpb.SetFloat(BlurId, 0.03f);
                _mpb.SetFloat(StrengthId, 0.97f);
                _mpb.SetVector(LightId, light);
                b.glass.SetPropertyBlock(_mpb);

                // The greeting: in once the pill has room for it, out before it shrinks.
                var say = EaseOut(Ramp(u, 0.45f, 0.65f)) * (1 - EaseInOut(Ramp(u, 1.0f, 1.2f)));
                b.text.color = new Color(1, 1, 1, say);
                b.text.characterSpacing = Mathf.Lerp(8f, 0f, EaseOut(Ramp(u, 0.45f, 0.75f)));
            }
            _intro.gameObject.SetActive(any);
        }

        // Turn a bubble to face the wearer (Distance behind the intro's origin), tilted by roll degrees.
        static void Face(Bubble b, Vector3 pos, float roll) =>
            b.t.localRotation = Quaternion.LookRotation(pos - new Vector3(0, 0, -Distance), Vector3.up) * Quaternion.Euler(0, 0, roll);

        void PoseLogo(float t)
        {
            // 0.00–0.40  an empty glass orb pops in at the centre (slight overshoot), a soft glow breathing out
            // 0.35–1.00  it stretches into the pill
            // 0.70–1.55  the name appears letter by letter, each rising in its own colour, then settling to white
            // 1.40–1.90  the tagline fades in; 1.50–2.40 a light glint sweeps across the glass
            // 2.50–2.85  the words fade; 2.70–3.05 the pill shrinks back into the orb; 3.00–3.30 the orb swells and pops
            var appear = Ramp(t, 0f, 0.4f);
            var grow = EaseInOut(Ramp(t, 0.35f, 1.0f)) * (1 - EaseInOut(Ramp(t, 2.7f, 3.05f)));
            var pop = Ramp(t, 3.0f, LogoDuration);
            var alpha = EaseOut(appear) * (1 - pop * pop);
            var words = 1 - EaseInOut(Ramp(t, 2.5f, 2.85f));

            _root.localScale = Vector3.one * Size;
            _orb.localPosition = Vector3.zero;
            _orb.localScale = Vector3.one * Mathf.LerpUnclamped(0.35f, 1f, EaseOutBack(appear)) * (1 + 0.45f * EaseOut(pop));

            // Glass: orb → pill, both centred.
            var half = new Vector2(Mathf.Lerp(OrbR * 0.45f, _pillW / 2, grow), Mathf.Lerp(OrbR * 0.45f, PillH / 2, grow));
            var size = new Vector2(Mathf.Max(OrbR, half.x), Mathf.Max(OrbR, half.y)) * 2 + Vector2.one * 0.028f;
            _glass.transform.localScale = new Vector3(size.x, size.y, 1);
            var sweep = EaseInOut(Ramp(t, 1.5f, 2.4f));
            var light = _root.rotation * new Vector3(Mathf.Lerp(-1.2f, 1.2f, sweep), 0.9f, -0.6f);
            _glass.GetPropertyBlock(_mpb);
            _mpb.SetVector(BodyId, new Vector4(_pillW, PillH, PillH / 2, 0.01f));
            _mpb.SetVector(BodyCenterId, Vector4.zero);
            _mpb.SetVector(OrbId, new Vector4(OrbR, 0.03f, 0, 0));
            _mpb.SetVector(QuadId, new Vector4(0, 0, size.x, size.y));
            _mpb.SetFloat(GrowId, grow);
            _mpb.SetFloat(AlphaId, alpha);
            _mpb.SetColor(TintId, new Color(0.15f, 0.16f, 0.18f, 0.4f));
            _mpb.SetFloat(BlurId, 0.03f);
            _mpb.SetFloat(StrengthId, 0.97f);
            _mpb.SetVector(LightId, light);
            _glass.SetPropertyBlock(_mpb);

            // The glow behind the orb drifts round the colour wheel; it breathes as the orb lands and flares as it pops.
            var pulse = Mathf.Sin(Mathf.PI * Ramp(t, 0.05f, 1.0f)) + Mathf.Sin(Mathf.PI * pop);
            var glow = Color.HSVToRGB(Mathf.Repeat(0.02f + t * 0.3f, 1f), 0.6f, 1f);
            glow.a = EaseOut(appear) * (1 - pop) * (0.12f + 0.4f * pulse) * (1 - grow); // none while the pill is open (it would show through the glass)
            SetImage(_halo, null, glow);
            _halo.transform.localScale = Vector3.one * OrbR * (3.6f + 1.4f * pulse);

            // The name, letter by letter: each fades in rising a little, in its own colour, then turns white.
            _sb.Clear();
            var n = title.Length;
            for (var i = 0; i < n; i++)
            {
                var s0 = 0.7f + i * (0.6f / Mathf.Max(1, n - 1));
                var a = EaseOut(Ramp(t, s0, s0 + 0.28f));
                var c = Color.Lerp(_hues[i], Color.white, EaseInOut(Ramp(t, s0 + 0.3f, s0 + 0.9f)));
                c.a = a * words;
                _sb.Append("<voffset=").Append(((1 - a) * -0.3f).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))
                   .Append("em><color=#").Append(ColorUtility.ToHtmlStringRGBA(c)).Append('>').Append(title[i]).Append("</color></voffset>");
            }
            _title.color = Color.white;
            _title.text = _sb.ToString();
            var tag = EaseOut(Ramp(t, 1.4f, 1.9f));
            _tagline.color = new Color(1, 1, 1, 0.75f * tag * words);
            _tagline.transform.localPosition = new Vector3(0, -0.03f - 0.006f * (1 - tag), -0.008f);
        }
    }
}
