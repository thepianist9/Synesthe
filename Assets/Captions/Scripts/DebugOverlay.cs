using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using Week1Bench;

namespace MetaStarter.Captions
{
    /// <summary>
    /// In-headset debug view, to see why a caption went where it did:
    ///   on each face  — the detected face box, the lip outline from the face mesh (white outer, yellow inner),
    ///                   and a panel with the talking score, mouth movement, lip sync, distance and tied voice,
    ///                   plus a 3 s graph: lip gap (person colour) against loudness (white). In sync = talking.
    ///   heads-up      — mic level and speech detector, words of the current sentence coloured by the face they went
    ///                   to (grey = not decided yet, | = turn change), voice → face links, and the last decisions.
    /// Created by CaptionDirector the first time the debug view is turned on; disabled (everything hidden) when it's off.
    /// </summary>
    public class DebugOverlay : MonoBehaviour
    {
        public CaptionDirector director;
        public PersonTracker tracker;
        public MicStream mic;
        public Transform head;
        public TMP_FontAsset font;
        public Material lineMaterial; // URP Unlit, transparent

        const float RefDistance = 1.2f;
        const float GraphSeconds = 3f;
        const float GraphW = 0.2f, GraphH = 0.035f;

        class FaceView
        {
            public Transform Root;
            public TextMeshPro Label;
            public Transform Bg;
            public LineRenderer Box, OuterLip, InnerLip, LipGraph, LoudGraph, GraphFrame;
        }

        readonly Dictionary<int, FaceView> _views = new();
        readonly Dictionary<(Color, int), Material> _materials = new();
        readonly List<(float t, float v)> _samples = new();
        TextMeshPro _hud;
        Transform _hudBg;
        float _fontScale = 10f;
        float _nextText;
        Vector3 _hudPos;
        Vector3 _hudVel;

        void Start()
        {
            (_hud, _hudBg) = MakePanel("Debug HUD", transform, 0.5f, 0.016f);
            _hud.alignment = TextAlignmentOptions.TopLeft;
            _hudPos = HudTarget();
        }

        void OnEnable()
        {
            if (_hud != null) _hud.transform.parent.gameObject.SetActive(true);
        }

        // Off: hide the HUD and drop the face views (they're made again, fresh, when it's back on).
        void OnDisable()
        {
            if (_hud != null) _hud.transform.parent.gameObject.SetActive(false);
            foreach (var v in _views.Values)
            {
                Destroy(v.Root.gameObject); Destroy(v.Box.gameObject); Destroy(v.OuterLip.gameObject); Destroy(v.InnerLip.gameObject);
            }
            _views.Clear();
        }

        void LateUpdate()
        {
            if (head == null || tracker == null) return;
            var refreshText = Time.time >= _nextText;
            if (refreshText) _nextText = Time.time + 0.1f;

            // Heads-up panel: lazy-follows the head so it can be read without chasing it.
            _hudPos = Vector3.SmoothDamp(_hudPos, HudTarget(), ref _hudVel, 0.4f);
            _hud.transform.parent.position = _hudPos;
            _hud.transform.parent.rotation = Quaternion.LookRotation(_hudPos - head.position, Vector3.up);
            if (refreshText) SetPanelText(_hud, _hudBg, HudText(), 0.5f);

            foreach (var p in tracker.People)
            {
                if (!_views.TryGetValue(p.Id, out var v)) _views[p.Id] = v = MakeFaceView(p);
                UpdateFaceView(p, v, refreshText);
            }
            foreach (var id in new List<int>(_views.Keys))
            {
                if (tracker.People.Exists(p => p.Id == id)) continue;
                var v = _views[id];
                Destroy(v.Root.gameObject); Destroy(v.Box.gameObject); Destroy(v.OuterLip.gameObject); Destroy(v.InnerLip.gameObject);
                _views.Remove(id);
            }
        }

        Vector3 HudTarget()
        {
            var fwd = head.forward; fwd.y = 0;
            fwd = fwd.sqrMagnitude > 1e-6f ? fwd.normalized : Vector3.forward;
            var right = Vector3.Cross(Vector3.up, fwd);
            return head.position + fwd * 1.1f + Vector3.up * 0.3f - right * 0.05f;
        }

        // ---------------- per face ----------------

        FaceView MakeFaceView(TrackedPerson p)
        {
            var v = new FaceView { Root = new GameObject($"Debug {p.Label}").transform };
            v.Root.SetParent(transform, false);
            (v.Label, v.Bg) = MakePanel("Scores", v.Root, 0.24f, 0.013f);
            v.Label.alignment = TextAlignmentOptions.TopLeft;
            v.Box = MakeLine("Face box", null, p.Color, 0.0025f, true);
            v.OuterLip = MakeLine("Outer lip", null, Color.white, 0.0012f, true);
            v.InnerLip = MakeLine("Inner lip", null, new Color(1f, 0.85f, 0.2f), 0.0012f, true);
            v.GraphFrame = MakeLine("Graph frame", v.Root, new Color(1, 1, 1, 0.25f), 0.0006f, true);
            v.LoudGraph = MakeLine("Loudness", v.Root, new Color(1, 1, 1, 0.85f), 0.0012f, false);
            v.LipGraph = MakeLine("Lip gap", v.Root, p.Color, 0.0018f, false);
            return v;
        }

        void UpdateFaceView(TrackedPerson p, FaceView v, bool refreshText)
        {
            var visible = Time.time - p.LastSeen < 0.6f;
            var speaking = director.CurrentSpeaker == p;

            // Face box and lips, where the camera saw them.
            SetLoop(v.Box, visible ? p.BoxWorld : null);
            v.Box.widthMultiplier = speaking ? 0.006f : 0.0025f;
            SetLoop(v.OuterLip, visible ? p.OuterLipWorld : null);
            SetLoop(v.InnerLip, visible ? p.InnerLipWorld : null);

            // Panel below the face, facing you, sized for its distance.
            var toFace = p.HeadPosition - head.position;
            var scale = Mathf.Clamp(toFace.magnitude / RefDistance, 0.6f, 3f);
            v.Root.position = p.HeadPosition + Vector3.down * 0.15f * scale;
            v.Root.rotation = Quaternion.LookRotation(v.Root.position - head.position, Vector3.up);
            v.Root.localScale = Vector3.one * scale;

            if (refreshText)
            {
                var talk = director.DebugTalking(p, out var mouth, out var sync);
                var voice = director.VoiceOf(p);
                var hex = ColorUtility.ToHtmlStringRGB(p.Color);
                var sb = new StringBuilder();
                sb.Append($"<color=#{hex}><b>{p.Label}</b></color>");
                if (speaking) sb.Append($"  <color=#{hex}>● SPEAKING</color>");
                if (!visible) sb.Append("  <color=#8A8A8A>(not seen)</color>");
                sb.Append($"\n{p.DistanceMeters:F1} m · {(voice != null ? voice.Label : "no voice yet")} · lips {(p.MouthOpen >= 0 ? "mesh" : "proxy")}");
                sb.Append($"\ntalk <b>{Fmt(talk)}</b>  mouth {Fmt(mouth)}  sync {(float.IsNaN(sync) ? "—" : sync.ToString("+0.00;-0.00"))}");
                SetPanelText(v.Label, v.Bg, sb.ToString(), 0.24f, extraBottom: GraphH + 0.015f);
            }
            UpdateGraph(p, v);
        }

        // Last 3 s: lip gap (person colour) over loudness (white). A talker's line rises and falls with the white one.
        void UpdateGraph(TrackedPerson p, FaceView v)
        {
            var top = -v.Label.rectTransform.sizeDelta.y - 0.006f;
            var x0 = -GraphW / 2;
            v.GraphFrame.positionCount = 4;
            v.GraphFrame.SetPositions(new[] { new Vector3(x0, top, 0), new Vector3(-x0, top, 0), new Vector3(-x0, top - GraphH, 0), new Vector3(x0, top - GraphH, 0) });

            var now = Time.time;
            p.OpenSamples(now - GraphSeconds, now, _samples);
            v.LipGraph.positionCount = _samples.Count;
            for (var i = 0; i < _samples.Count; i++)
            {
                var x = x0 + (_samples[i].t - (now - GraphSeconds)) / GraphSeconds * GraphW;
                v.LipGraph.SetPosition(i, new Vector3(x, top - GraphH + Mathf.Clamp01(_samples[i].v / 0.3f) * GraphH, -0.001f));
            }
            const int n = 60;
            var env = director.Envelope;
            var pts = new List<Vector3>(n);
            for (var i = 0; i < n; i++)
            {
                var t = now - GraphSeconds + GraphSeconds * i / (n - 1);
                var db = env.At(t);
                if (float.IsNaN(db)) continue;
                pts.Add(new Vector3(x0 + GraphW * i / (n - 1), top - GraphH + Mathf.Clamp01((db + 65f) / 50f) * GraphH, -0.0005f));
            }
            v.LoudGraph.positionCount = pts.Count;
            v.LoudGraph.SetPositions(pts.ToArray());
        }

        // ---------------- heads-up ----------------

        string HudText()
        {
            var sb = new StringBuilder();
            var db = mic != null && mic.Level > 0 ? 20f * Mathf.Log10(mic.Level) : -120f;
            var bars = Mathf.RoundToInt(Mathf.Clamp01((db + 70f) / 60f) * 12);
            sb.Append($"<b>MIC</b> <color=#7CFC9A>{new string('|', bars)}</color><color=#444444>{new string('|', 12 - bars)}</color> {db:F0} dB  ");
            sb.Append(director.VadSpeech ? "<color=#7CFC9A>● speech</color>" : "<color=#666666>○ quiet</color>");
            sb.Append($"   faces {tracker.DetectionsPerSecond:F0}/s · lips {(tracker.LipLandmarks ? $"mesh {tracker.LandmarkMs:F0} ms/face" : "proxy")}");

            var now = director.DebugUtterance();
            var who = director.CurrentSpeaker;
            sb.Append("\n<b>NOW</b> ");
            sb.Append(now.Length > 0 ? $"→ {(who != null ? $"<color=#{ColorUtility.ToHtmlStringRGB(who.Color)}>{who.Label}</color>" : "unsure")}:  {now}" : "<color=#666666>—</color>");

            sb.Append("\n<b>VOICES</b> ");
            var voices = director.Voices;
            if (voices == null) sb.Append("<color=#666666>off</color>");
            else if (voices.Voices.Count == 0) sb.Append("<color=#666666>none yet</color>");
            else
                foreach (var v in voices.Voices)
                {
                    var id = v.BoundPerson();
                    var p = id >= 0 ? tracker.People.Find(x => x.Id == id) : null;
                    var target = id < 0 ? "?" : p != null ? $"<color=#{ColorUtility.ToHtmlStringRGB(p.Color)}>P{id}</color>" : $"P{id} (gone)";
                    sb.Append($"V{v.Id}→{target} <size=75%>({v.Count})</size>   ");
                }

            foreach (var (time, text) in director.RecentNotes)
            {
                var t = text.Length > 140 ? text.Substring(0, 140) + "…" : text;
                sb.Append($"\n<size=80%><color=#9AA0AA>{Time.time - time,3:F0}s</color> {t}</size>");
            }
            return sb.ToString();
        }

        // ---------------- helpers ----------------

        static string Fmt(float v) => v < 0 ? "—" : v.ToString("0.00");

        (TextMeshPro, Transform) MakePanel(string name, Transform parent, float width, float lineHeight)
        {
            var root = new GameObject(name).transform;
            root.SetParent(parent, false);
            var bg = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Destroy(bg.GetComponent<Collider>());
            bg.name = "Background";
            bg.transform.SetParent(root, false);
            bg.GetComponent<MeshRenderer>().sharedMaterial = MaterialFor(new Color(0.05f, 0.06f, 0.08f, 0.72f), Background);
            var go = new GameObject("Text");
            go.transform.SetParent(root, false);
            var t = go.AddComponent<TextMeshPro>();
            if (font != null) t.font = font;
            t.color = new Color(0.92f, 0.93f, 0.96f);
            t.textWrappingMode = TextWrappingModes.Normal;
            t.overflowMode = TextOverflowModes.Overflow;
            t.richText = true;
            t.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            t.fontSize = 1f;
            var h = t.GetPreferredValues("Ag", 100f, 100f).y;
            _fontScale = h > 1e-5f ? 1f / h : 10f;
            t.fontSize = lineHeight * _fontScale;
            t.rectTransform.pivot = new Vector2(0.5f, 1f);
            t.rectTransform.sizeDelta = new Vector2(width, 0.05f);
            return (t, bg.transform);
        }

        // Text hangs down from the panel origin; the background covers it (plus room for the graph under it).
        static void SetPanelText(TextMeshPro t, Transform bg, string text, float width, float extraBottom = 0f)
        {
            t.text = text;
            var h = t.GetPreferredValues(text, width, 10f).y;
            t.rectTransform.sizeDelta = new Vector2(width, h);
            const float pad = 0.008f;
            bg.localScale = new Vector3(width + 2 * pad, h + extraBottom + 2 * pad, 1f);
            bg.localPosition = new Vector3(0, -(h + extraBottom) / 2, 0.002f);
        }

        LineRenderer MakeLine(string name, Transform parent, Color color, float width, bool loop)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent != null ? parent : transform, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = parent == null;
            lr.loop = loop;
            lr.widthMultiplier = width;
            lr.numCapVertices = 2;
            lr.positionCount = 0;
            lr.sharedMaterial = MaterialFor(color);
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            return lr;
        }

        static void SetLoop(LineRenderer lr, Vector3[] pts)
        {
            if (pts == null) { lr.positionCount = 0; return; }
            lr.positionCount = pts.Length;
            lr.SetPositions(pts);
        }

        // Draw order: glass bubbles (Transparent−10) < panel backgrounds < text (Transparent) < lines.
        const int Background = (int)UnityEngine.Rendering.RenderQueue.Transparent - 5;
        const int Lines = (int)UnityEngine.Rendering.RenderQueue.Transparent + 5;

        Material MaterialFor(Color c, int queue = Lines)
        {
            if (_materials.TryGetValue((c, queue), out var m)) return m;
            m = new Material(lineMaterial) { color = c, renderQueue = queue };
            _materials[(c, queue)] = m;
            return m;
        }

        void OnDestroy()
        {
            foreach (var m in _materials.Values) Destroy(m);
        }
    }
}
