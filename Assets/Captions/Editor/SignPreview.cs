using System.Collections.Generic;
using System.IO;
using System.Linq;
using MetaStarter.Captions;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Renders the signing avatar to a PNG without entering Play mode, to check handshapes and arm poses by eye.
///   SignPreview.Render("out.png", "A")          — a fingerspelled letter (or digit) in signing position
///   SignPreview.Render("out.png", "", "48=-0.5") — rest pose with raw humanoid muscle overrides (index=value;…)
///   SignPreview.RenderSign("out.png", "NOT", 0.5f) — a dictionary sign (or sentence, by its English) at 0–1 through it
/// Call from `unity command eval`. Close-up of the hand with handCloseUp.
/// </summary>
public static class SignPreview
{
    const string ModelPath = "Assets/Captions/Signing/Avatar/Models/Realistic.fbx";

    public static string RenderSign(string path, string gloss, float progress, bool handCloseUp = false)
    {
        var dict = AssetDatabase.LoadAssetAtPath<SignDictionary>("Assets/Captions/Signing/SignDictionary.asset");
        var sign = dict.sentences.Find(s => s.english == gloss) ?? dict.signs.Find(s => s.gloss == gloss.ToUpperInvariant());
        if (sign == null) return $"no sign {gloss}";
        var m = Handshapes.Ready();
        sign.Sample(progress * sign.Seconds, m);
        return RenderMuscles(path, m, handCloseUp);
    }

    static string RenderMuscles(string path, float[] m, bool handCloseUp, float[] face = null)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < m.Length; i++) sb.Append(i).Append('=').Append(m[i].ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(';');
        return Render(path, "", sb.ToString(), handCloseUp, model: _frameModel, face: face);
    }

    static string _frameModel; // RenderFrames' -signModel (a prefab or model path; default the Realistic avatar)

    /// <summary>Every dictionary sign at three points through it, as &lt;out&gt;/&lt;GLOSS&gt;_&lt;n&gt;.png, then &lt;out&gt;/done.txt.
    /// For batch mode: Unity -executeMethod SignPreview.RenderAll -signPreviewOut &lt;folder&gt; [-signImport, to re-import first]</summary>
    public static void RenderAll()
    {
        var args = System.Environment.GetCommandLineArgs();
        var i = System.Array.IndexOf(args, "-signPreviewOut");
        var outDir = i >= 0 && i + 1 < args.Length ? args[i + 1] : "Temp/SignPreview";
        Directory.CreateDirectory(outDir);
        if (System.Array.IndexOf(args, "-signImport") >= 0) File.WriteAllText(Path.Combine(outDir, "import.txt"), SignImporter.ImportAll());
        var dict = AssetDatabase.LoadAssetAtPath<SignDictionary>("Assets/Captions/Signing/SignDictionary.asset");
        foreach (var s in dict.signs)
            for (var n = 0; n < 3; n++)
                RenderSign(Path.Combine(outDir, $"{s.gloss.Replace('/', '-')}_{n}.png"), s.gloss, 0.25f + 0.25f * n);
        File.WriteAllText(Path.Combine(outDir, "done.txt"), dict.signs.Count.ToString());
    }

    /// <summary>Signs played through as frames, &lt;out&gt;/&lt;GLOSS&gt;/f000.png…, then &lt;out&gt;/done.txt; ffmpeg turns them into video.
    /// For batch mode: Unity -executeMethod SignPreview.RenderFrames -signPreviewOut &lt;folder&gt; -signGlosses "WAIT|SICK|How are you?|I+WANT+DRINK" [-signFps 15] [-signModel Assets/…/Aera.prefab] [-signCloseUp] [-signImport]
    /// (signs by gloss, sentences by their English, A+B+C: signs one after another as SignAvatar plays a sentence;
    /// -signNoCore plays each whole clip instead). Import options: -signSnap 0.5 (SignImporter.FingerSnap),
    /// -signTuneTry &lt;json&gt; (SignImporter.TuneTry), -signHandTry &lt;json&gt; (SignImporter.HandTry), -signPriorTry &lt;json&gt; (SignImporter.PriorTry).</summary>
    public static void RenderFrames() => RenderFrames(System.Environment.GetCommandLineArgs());

    /// <summary>The same from the open Editor, e.g. unity command eval: SignPreview.RenderFrames(new[]{"-signPreviewOut", dir, "-signGlosses", "CAT|DOG", "-signFps", "25"}).</summary>
    public static void RenderFrames(string[] args)
    {
        string Arg(string name, string fallback) { var i = System.Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
        var outDir = Arg("-signPreviewOut", "Temp/SignPreview");
        var fps = float.Parse(Arg("-signFps", "15"), System.Globalization.CultureInfo.InvariantCulture);
        SignImporter.FingerSnap = float.Parse(Arg("-signSnap", SignImporter.FingerSnap.ToString(System.Globalization.CultureInfo.InvariantCulture)), System.Globalization.CultureInfo.InvariantCulture);
        SignImporter.TuneTry = Arg("-signTuneTry", null);
        SignImporter.HandTry = Arg("-signHandTry", null);
        SignImporter.PriorTry = Arg("-signPriorTry", null);
        // -signFingerTry <json>: {"V": [[base, tip] x4 (index, middle, ring, little)], ...} (Handshapes.FingerTry)
        Handshapes.FingerTry.Clear();
        var fingerTry = Arg("-signFingerTry", null);
        if (fingerTry != null)
            foreach (var kv in Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(fingerTry)))
                Handshapes.FingerTry[kv.Key] = ((Newtonsoft.Json.Linq.JArray)kv.Value)
                    .Select(f => ((float)f[0], (float)f[1])).ToArray();
        var bg = Arg("-signBg", null);
        BgColor = bg == null ? null : (Color?)new Color(
            float.Parse(bg.Split(',')[0], System.Globalization.CultureInfo.InvariantCulture),
            float.Parse(bg.Split(',')[1], System.Globalization.CultureInfo.InvariantCulture),
            float.Parse(bg.Split(',')[2], System.Globalization.CultureInfo.InvariantCulture));
        Size = int.Parse(Arg("-signSize", "768"));
        var noCore = System.Array.IndexOf(args, "-signNoCore") >= 0;
        _frameModel = Arg("-signModel", null);
        Directory.CreateDirectory(outDir);
        if (System.Array.IndexOf(args, "-signImport") >= 0) File.WriteAllText(Path.Combine(outDir, "import.txt"), SignImporter.ImportAll());
        var dict = AssetDatabase.LoadAssetAtPath<SignDictionary>("Assets/Captions/Signing/SignDictionary.asset");
        var glosses = Arg("-signGlosses", "").Split('|', System.StringSplitOptions.RemoveEmptyEntries);
        // "*": every sign and sentence. -signMeasureOnly: no images, only behind.txt (quick).
        if (glosses.Length == 1 && glosses[0] == "*")
            glosses = dict.signs.Select(s => s.gloss).Concat(dict.sentences.Select(s => s.english)).ToArray();
        MeasureOnly = System.Array.IndexOf(args, "-signMeasureOnly") >= 0;
        for (var g = 0; g < glosses.Length; g++)
        {
            // In A+B+C, fs-DAVID is fingerspelled, as SignAvatar spells a word it has no sign for.
            var parts = glosses[g].Split('+').Select(p => p.Trim()).ToList();
            var spell = parts.Select(p => p.StartsWith("fs-") ? p.Substring(3).ToUpperInvariant() : null).ToList();
            string spoken = null;
            if (glosses[g].StartsWith("text:"))
            {
                // An English sentence, resolved as SignAvatar.Final does: recorded clauses whole, the rest in gloss order.
                spoken = glosses[g].Substring(5).Trim();
                (parts, spell) = FromText(dict, spoken);
            }
            var sequence = spoken != null ? parts.Select(p => p == null ? null : (SignDictionary.Sign)null).ToList()
                : glosses[g].Contains('+')
                ? parts.Select(p => p.StartsWith("fs-") ? null : dict.signs.Find(s => s.gloss == p.ToUpperInvariant())).ToList()
                : new List<SignDictionary.Sign> { dict.sentences.Find(s => s.english == glosses[g]) ?? dict.signs.Find(s => s.gloss == glosses[g].ToUpperInvariant()) };
            if (spoken != null) sequence = parts.Select(p => p == null ? null : (dict.sentences.Find(x => x.english == p) ?? dict.signs.Find(x => x.gloss == p))).ToList();
            if (sequence.Where((s, k) => s == null && spell[k] == null).Any() || sequence.All(s => s == null)) continue;
            var sign = sequence.First(s => s != null);
            var dir = Path.Combine(outDir, string.Concat(glosses[g].Split(Path.GetInvalidFileNameChars())).Replace(' ', '_'));
            Directory.CreateDirectory(dir);
            var closeUp = System.Array.IndexOf(args, "-signCloseUp") >= 0;
            ScoreCam = System.Array.IndexOf(args, "-signScoreCam") >= 0;
            // As SignAvatar plays it: from rest up to the ready pose, the sign, hands in front (neutral) 1.2 s, then rest,
            // the pose following each target as SignAvatar's spring does.
            var dt = 1f / fps;
            var rest = Handshapes.Rest();
            var ready = Handshapes.Ready();
            var neutral = Handshapes.Neutral();
            var now = (float[])rest.Clone();
            var target = new float[now.Length];
            var vel = new float[now.Length];
            var frame = 0;
            // The face as SignAvatar builds it: the recorded expression, affect, question brows, negation, blinks.
            // Seeded by name, not list position, so a sign moves the same in every render and renders compare fairly.
            var seed = glosses[g].Split('@')[0].Aggregate(17, (h, c) => h * 31 + c);
            var face = new SignFace(seed);
            var body = new SignBody(seed);
            var english = spoken ?? sign.english ?? "";
            PreviewSomber = SignFace.SomberText(english);
            var label = english.Length > 0 ? english : sign.gloss;
            var negated = english.Length > 0 ? SignFace.NegativeEnglish(english) : SignFace.Negates(sign.gloss, null);
            var kind = english.Length > 0 ? Gloss.FromText(english).Kind : Gloss.Kind.Statement;
            float raise = 0, lower = 0;
            var behindFrames = 0; // frames a hand was behind the body (see BodySurface), and the most it was
            var behindMost = 0f;
            void Step(System.Action<float[]> aim, bool playing, float at, bool started = false)
            {
                var faceK = 1f - Mathf.Exp(-dt * 8f);
                raise = Mathf.Lerp(raise, playing && kind == Gloss.Kind.YesNoQuestion ? 0.8f : 0f, faceK);
                lower = Mathf.Lerp(lower, playing && kind == Gloss.Kind.WhQuestion ? 0.7f : 0f, faceK);
                face.Tick(dt, playing ? sign : null, at, playing ? label : null, playing && negated, started, raise, lower);
                aim(target);
                face.AddHead(target);
                body.Add(target, dt, playing, raise, lower, ready);
                SignPose.Follow(now, target, vel, dt);
                RenderMuscles(Path.Combine(dir, $"f{frame++:000}.png"), now, closeUp, face.Now);
                if (LastBehind > 0.01f) behindFrames++;
                behindMost = Mathf.Max(behindMost, LastBehind);
            }
            for (var t = 0f; t < 0.4f; t += dt) Step(m => System.Array.Copy(ready, m, m.Length), false, 0);
            for (var i = 0; i < sequence.Count; i++)
            {
                if (sequence[i] == null)
                {
                    // SignAvatar's spelling: lettersPerSecond, each letter's own motion (J, Z), then its pause.
                    var word = spell[i];
                    for (var t = 0f; t < word.Length / LettersPerSecond; t += dt)
                    {
                        var pos = t * LettersPerSecond;
                        var c = word[Mathf.Min((int)pos, word.Length - 1)];
                        Step(m => { System.Array.Copy(LetterPose(c), m, m.Length); Handshapes.Motion(c, pos - (int)pos, m); }, false, 0);
                    }
                    for (var t = 0f; t < 0.18f; t += dt) Step(m => System.Array.Copy(ready, m, m.Length), false, 0);
                    continue;
                }
                sign = sequence[i];
                var (from, to) = noCore ? (0f, sign.Seconds) : sign.Span(i > 0);
                var clip = sign;
                // SignAvatar's pace: signingRate (0.85), long citation signs tightened inside a sentence.
                var rate = SignAvatar.RateFor(to - from, 0.85f, sequence.Count > 1 && !dict.sentences.Contains(sign));
                for (var t = 0f; t <= (to - from) / rate; t += dt)
                {
                    var at = from + t * rate;
                    Step(m => { System.Array.Copy(ready, m, m.Length); clip.Sample(at, m); }, true, at, t == 0f);
                }
                // SignAvatar's pause between signs: the last pose held.
                if (i < sequence.Count - 1)
                    for (var t = 0f; t < 0.18f; t += dt) Step(m => { System.Array.Copy(ready, m, m.Length); clip.Sample(to, m); }, true, to);
            }
            for (var t = 0f; t < 1.2f; t += dt) Step(m => System.Array.Copy(neutral, m, m.Length), false, 0);
            for (var t = 0f; t < 0.8f; t += dt) Step(m => System.Array.Copy(rest, m, m.Length), false, 0);
            File.AppendAllText(Path.Combine(outDir, "progress.txt"), $"{g + 1}/{glosses.Length} {glosses[g]}\n");
            File.AppendAllText(Path.Combine(outDir, "behind.txt"), $"{glosses[g]}\t{behindFrames}/{frame} frames over 1 cm, most {100 * behindMost:0.0} cm\n");
        }
        File.WriteAllText(Path.Combine(outDir, "done.txt"), glosses.Length.ToString());
    }

    /// <summary>Which way a muscle moves this rig: the ready pose with each of -signProbe "Spine Front-Back|…" at +0.6
    /// and -0.6, as &lt;out&gt;/&lt;muscle&gt;_plus.png and _minus.png, then &lt;out&gt;/done.txt.</summary>
    public static void RenderProbe()
    {
        var args = System.Environment.GetCommandLineArgs();
        string Arg(string name, string fallback) { var i = System.Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
        var outDir = Arg("-signPreviewOut", "Temp/SignPreview");
        Directory.CreateDirectory(outDir);
        foreach (var muscle in Arg("-signProbe", "").Split('|', System.StringSplitOptions.RemoveEmptyEntries))
            foreach (var (tag, v) in new[] { ("plus", 0.6f), ("minus", -0.6f) })
            {
                var m = Handshapes.Ready();
                m[Handshapes.MuscleIndex(muscle)] += v;
                RenderMuscles(Path.Combine(outDir, $"{muscle.Replace(' ', '_')}_{tag}.png"), m, false);
            }
        File.WriteAllText(Path.Combine(outDir, "done.txt"), "probe");
    }

    /// <summary>Every handshape (<see cref="Handshapes.Names"/>) close up, the thumb fitted as for spelling, with the hand
    /// turned three ways, as &lt;out&gt;/&lt;name&gt;_&lt;k&gt;.png, then &lt;out&gt;/done.txt. tools/signs/handshape_labels.py reads
    /// them to know each handshape as hand tracking sees it.</summary>
    public static void RenderHandshapes()
    {
        var args = System.Environment.GetCommandLineArgs();
        var i = System.Array.IndexOf(args, "-signPreviewOut");
        var outDir = i >= 0 && i + 1 < args.Length ? args[i + 1] : "Temp/SignPreview";
        Directory.CreateDirectory(outDir);
        var go = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath));
        go.hideFlags = HideFlags.HideAndDontSave;
        try
        {
            var pose = new SignPose(go.GetComponent<Animator>());
            var fit = new HandFit(pose);
            var ready = fit.Ready(Handshapes.Ready());
            var twist = Handshapes.MuscleIndex("Right Forearm Twist In-Out");
            var bend = Handshapes.MuscleIndex("Right Hand Down-Up");
            var names = Handshapes.Names.ToList();
            foreach (var name in names)
            {
                var m = (float[])ready.Clone();
                Handshapes.SetFingers(m, "Right", name);
                fit.Thumb(m, Handshapes.SpotOf(name));
                var turns = new[] { (0f, 0f), (0.35f, 0f), (-0.35f, 0.3f) };
                for (var k = 0; k < turns.Length; k++)
                {
                    var v = (float[])m.Clone();
                    v[twist] = Mathf.Clamp(v[twist] + turns[k].Item1, -1f, 1f);
                    v[bend] = Mathf.Clamp(v[bend] + turns[k].Item2, -1f, 1f);
                    RenderMuscles(Path.Combine(outDir, $"{name}_{k}.png"), v, true);
                }
            }
            File.WriteAllText(Path.Combine(outDir, "done.txt"), string.Join(" ", names));
        }
        finally { Object.DestroyImmediate(go); }
    }

    /// <summary>English text → what SignAvatar plays: per item a recorded sentence's English or a sign's gloss
    /// (spell null), or null with the letters to fingerspell.</summary>
    static (List<string> items, List<string> spell) FromText(SignDictionary dict, string text)
    {
        var items = new List<string>();
        var spell = new List<string>();
        foreach (var (part, recorded) in SignAvatar.Clauses(dict, text))
        {
            if (recorded != null) { items.Add(recorded.english); spell.Add(null); continue; }
            if (dict.TryMatch(part, out var whole)) { items.Add(whole.english); spell.Add(null); continue; }
            var sentence = Gloss.FromText(part);
            foreach (var w in sentence.Signs)
            {
                SignDictionary.Sign found = null;
                if (!sentence.Names.Contains(w))
                    foreach (var f in Gloss.Forms(w)) if (dict.TryGet(f, out found)) break;
                if (found != null) { if (items.Count == 0 || items[^1] != found.gloss) { items.Add(found.gloss); spell.Add(null); } }
                else if (sentence.Names.Contains(w) || w.Length <= 5) { items.Add(null); spell.Add(w); }
            }
        }
        return (items, spell);
    }

    static bool PreviewSomber; // the sentence being rendered is SignFace.SomberText: no resting smile

    const float LettersPerSecond = 4.5f; // SignAvatar.lettersPerSecond's default
    static readonly Dictionary<char, float[]> _letterPoses = new();

    /// <summary>A fingerspelled letter's pose as SignAvatar forms it: the ready pose, thumb fitted (HandFit).</summary>
    static float[] LetterPose(char c)
    {
        if (_letterPoses.TryGetValue(c, out var m)) return m;
        var go = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath));
        go.hideFlags = HideFlags.HideAndDontSave;
        try
        {
            go.transform.SetPositionAndRotation(new Vector3(100, 0, 0), Quaternion.identity);
            var fit = new HandFit(new SignPose(go.GetComponent<Animator>()));
            m = Handshapes.Spelling(c, fit.Ready(Handshapes.Ready()));
            fit.Thumb(m, Handshapes.SpotOf(c));
        }
        finally { Object.DestroyImmediate(go); }
        return _letterPoses[c] = m;
    }

    static BodySurface _surface;

    /// <summary>After <see cref="Render"/>: how far (m) the furthest-back part of either hand was behind the body's front.</summary>
    public static float LastBehind;

    static bool MeasureOnly;
    /// <summary>Trailer renders: -signBg r,g,b (e.g. 0,1,0 to key the avatar out) and -signSize pixels.</summary>
    public static Color? BgColor; public static int Size = 768;

    /// <param name="face">Face weights (<see cref="SignPose.FaceChannels"/>): set, the face is also shown close up, top right.</param>
    /// Webcam-like framing with the whole head in view and no face inset (a second face confuses MediaPipe): for scoring.
    public static bool ScoreCam;

    public static string Render(string path, string letter, string muscles = "", bool handCloseUp = false, bool question = false, string model = null,
        float[] face = null)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(model ?? ModelPath);
        var go = Object.Instantiate(prefab);
        go.hideFlags = HideFlags.HideAndDontSave;
        var lightGo = new GameObject("Preview light") { hideFlags = HideFlags.HideAndDontSave };
        var camGo = new GameObject("Preview camera") { hideFlags = HideFlags.HideAndDontSave };
        // 4x MSAA as on Quest (Mobile_RPAsset), so alpha-to-coverage hair looks as it will in the headset.
        var rt = new RenderTexture(Size, Size, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
        try
        {
            go.transform.SetPositionAndRotation(new Vector3(100, 0, 0), Quaternion.identity);
            var pose = new SignPose(go.GetComponent<Animator>());
            var fit = new HandFit(pose); // as SignAvatar does: palm towards the viewer, thumb fitted to each letter
            var m = Handshapes.Rest();
            if (!string.IsNullOrEmpty(letter))
            {
                m = Handshapes.Spelling(letter[0], fit.Ready(Handshapes.Ready()));
                fit.Thumb(m, Handshapes.SpotOf(letter[0]));
            }
            foreach (var kv in muscles.Split(';'))
            {
                var p = kv.Split('=');
                if (p.Length == 2) m[int.Parse(p[0])] = float.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture);
            }
            pose.Apply(Handshapes.Ready());
            _surface ??= new BodySurface(go.GetComponent<Animator>());
            pose.Apply(m);
            var bodySurface = _surface.For(go.GetComponent<Animator>());
            LastBehind = Mathf.Max(bodySurface.Behind(go.GetComponent<Animator>(), true), bodySurface.Behind(go.GetComponent<Animator>(), false));
            if (MeasureOnly) return "";
            if (face != null)
            {
                var warm = (float[])face.Clone();
                SignFace.Warm(warm, (Object.FindAnyObjectByType<SignAvatar>()?.friendliness ?? 0.8f) * (PreviewSomber ? 0f : 1f));
                pose.SetFace(warm);
            }
            else pose.SetQuestionFace(question ? 1f : 0f, 0f);

            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.2f;
            lightGo.transform.rotation = Quaternion.Euler(30, 160, 0);

            var anim = go.GetComponent<Animator>();
            var head = anim.GetBoneTransform(HumanBodyBones.Head).position;
            // Close-up: the middle of the hand (between the wrist and the middle finger's knuckle, a bit further out).
            var wrist = anim.GetBoneTransform(HumanBodyBones.RightHand).position;
            var hand = wrist + (anim.GetBoneTransform(HumanBodyBones.RightMiddleProximal).position - wrist) * 1.6f;
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = BgColor ?? new Color(0.35f, 0.37f, 0.4f);
            cam.fieldOfView = handCloseUp ? 24f : ScoreCam ? 38f : 32f;
            // Scoring camera: framed like a signer's webcam (ASL Citizen), from well above the head to the waist, so
            // signs at the forehead or above stay in frame for MediaPipe.
            var look = handCloseUp ? hand : head + Vector3.down * (ScoreCam ? 0.2f : 0.3f);
            // The avatar faces +z: the camera stands in front of it (where the viewer will be).
            camGo.transform.position = look + go.transform.forward * (handCloseUp ? 0.6f : 1.6f);
            camGo.transform.LookAt(look);
            cam.targetTexture = rt;
            cam.Render();
            if (face != null && !handCloseUp && !ScoreCam)
            {
                // The face close up in the top right corner (expressions are a few pixels at full size).
                cam.rect = new Rect(0.66f, 0.66f, 0.33f, 0.33f);
                cam.fieldOfView = 14f;
                var eyes = head + Vector3.up * 0.07f;
                camGo.transform.position = eyes + go.transform.forward * 0.9f;
                camGo.transform.LookAt(eyes);
                cam.Render();
                cam.rect = new Rect(0, 0, 1, 1);
            }

            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            RenderTexture.active = null;
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            return $"forward {go.transform.forward} head {head - go.transform.position} hand {hand - go.transform.position}";
        }
        finally
        {
            rt.Release();
            Object.DestroyImmediate(go);
            Object.DestroyImmediate(lightGo);
            Object.DestroyImmediate(camGo);
        }
    }
}
