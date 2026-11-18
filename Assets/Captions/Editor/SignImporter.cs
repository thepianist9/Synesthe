using System.Collections.Generic;
using System.IO;
using System.Linq;
using MetaStarter.Captions;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Recorded signs → the SignDictionary. Reads the landmark JSON written by tools/signs/extract_pose.py (MediaPipe on
/// videos of real signers: PopSign for single signs, your own recordings for whole sentences) and retargets every
/// frame onto the signing avatar: arms from the body landmarks, wrist and fingers from the hand landmarks. The
/// avatar's bones are turned to point where the signer's did, then read back as humanoid muscles, so the signs play
/// on any humanoid character.
///   Meta ▸ Speaker Captions ▸ Import Recorded Signs, or SignImporter.ImportAll() from `unity command eval`.
/// </summary>
public static class SignImporter
{
    const string PosesFolder = "tools/signs/poses";
    const string DictionaryPath = "Assets/Captions/Signing/SignDictionary.asset";
    const string ModelPath = "Assets/Captions/Signing/Avatar/Models/Realistic.fbx";

    [MenuItem("Meta/Synesthe/Import Recorded Signs")]
    public static void ImportMenu() => Debug.Log(ImportAll());

    /// <summary>Where one frame of a SMPL-X pose file puts the avatar's right hand, before and after muscles.</summary>
    public static string DebugSmplx(string path, int frame)
    {
        Rig.DebugLog.Clear();
        Rig.DebugFrame = frame;
        try { using var rig = new Rig(); rig.Retarget(JObject.Parse(File.ReadAllText(path))); }
        finally { Rig.DebugFrame = -1; }
        return Rig.DebugLog.ToString();
    }

    /// <summary>Debug: median/90th-percentile degrees the muscles move each hand from a SMPL-X file's rotations.</summary>
    public static string DebugSmplxHands(string path)
    {
        Rig.HandErr = new();
        List<(int hand, float total, float twist, float forearm)> e;
        try { using var rig = new Rig(); rig.Retarget(JObject.Parse(File.ReadAllText(path))); }
        finally { e = Rig.HandErr; Rig.HandErr = null; }
        string Q(IEnumerable<float> v) { var a = v.OrderBy(x => x).ToArray(); return a.Length == 0 ? "-" : $"{a[a.Length / 2]:F0}/{a[a.Length * 9 / 10]:F0}"; }
        return string.Join("  ", new[] { "right", "left" }.Select((h, i) =>
            $"{h} hand {Q(e.Where(x => x.hand == i).Select(x => x.total))} twist {Q(e.Where(x => x.hand == i).Select(x => x.twist))} forearm {Q(e.Where(x => x.hand == i).Select(x => x.forearm))}"));
    }

    /// <summary>Debug: the avatar's canonical handshape features and one pose file's right-hand features at a frame.</summary>
    public static string DebugHands(string path, int frame, params string[] templates)
    {
        using var rig = new Rig();
        return rig.DebugHands(JObject.Parse(File.ReadAllText(path)), frame, templates);
    }

    /// <summary>Imports every poses/**/*.json into the dictionary (replacing signs of the same gloss). Returns a summary.</summary>
    public static string ImportAll(string folder = PosesFolder)
    {
        // poses/ first (PopSign), then its subfolders (poses/wlasl/, your own recordings): a later file of the same
        // gloss replaces an earlier one, so better sources go in subfolders. poses/picked/ (takes chosen by checking
        // them against other signers, tools/signs/compare_aslcitizen.py) goes last of all.
        var files = Directory.GetFiles(folder, "*.json", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f) is not ("tune.json" or "handshapes.json" or "priors.json" or "one_handed.json" or "one_handed_cache.json" or "contact.json")).ToArray(); // not poses (see below)
        // Files whose signer keeps the other hand down (tools/signs/one_handed.py: one-handed in ASL-LEX, and so in the
        // video): that arm stays at rest, where trackers would lift a hand they can't see.
        var oneHandedPath = Path.Combine(folder, "one_handed.json");
        var oneHanded = File.Exists(oneHandedPath)
            ? new HashSet<string>(JArray.Parse(File.ReadAllText(oneHandedPath)).Select(x => x.ToString().Replace('\\', '/')))
            : new HashSet<string>();
        var picked = Path.Combine(folder, "picked") + Path.DirectorySeparatorChar;
        // EditorPrefs "Signs.SignAvatarsFirst" (prototype only: SignAvatars is research-use): its 3D fits go last of all,
        // so they replace every other source of the same gloss.
        var avatars = Path.Combine(folder, "signavatars") + Path.DirectorySeparatorChar;
        var avatarsFirst = EditorPrefs.GetBool("Signs.SignAvatarsFirst", false);
        System.Array.Sort(files, (a, b) =>
        {
            if (avatarsFirst)
            {
                var av = a.StartsWith(avatars).CompareTo(b.StartsWith(avatars));
                if (av != 0) return av;
            }
            var pick = a.StartsWith(picked).CompareTo(b.StartsWith(picked));
            if (pick != 0) return pick;
            var depth = a.Split(Path.DirectorySeparatorChar).Length.CompareTo(b.Split(Path.DirectorySeparatorChar).Length);
            return depth != 0 ? depth : string.CompareOrdinal(a, b);
        });
        var dict = AssetDatabase.LoadAssetAtPath<SignDictionary>(DictionaryPath);
        // Rebuilt from the pose files each time, so a pose moved out of the folder doesn't linger.
        dict.signs.Clear();
        dict.sentences.Clear();
        var log = new System.Text.StringBuilder();
        var rig = new Rig();
        log.Append(rig.CalibrationReport());
        log.Append(Rig.MeasureSources(files));
        log.AppendLine(FaceGains(files));
        try
        {
            for (var i = 0; i < files.Length; i++)
            {
                if (EditorUtility.DisplayCancelableProgressBar("Importing signs", Path.GetRelativePath(folder, files[i]), (float)i / files.Length)) break;
                var json = JObject.Parse(File.ReadAllText(files[i]));
                // A sign whose hand was hardly ever seen has no handshape: fingerspelling it is clearer.
                if (json.Value<string>("kind") == "sign" && json.Value<float>("handSeen") < MinHandSeen)
                {
                    foreach (var label in json["labels"]) dict.signs.RemoveAll(x => x.gloss == label.ToString().ToUpperInvariant());
                    log.AppendLine($"{Path.GetRelativePath(folder, files[i])}: skipped, hand seen {json.Value<float>("handSeen"):P0}");
                    continue;
                }
                var sign = rig.Retarget(json);
                if (oneHanded.Contains("poses/" + Path.GetRelativePath(folder, files[i]).Replace('\\', '/')))
                {
                    rig.RestOtherArm(sign);
                    log.AppendLine($"{Path.GetRelativePath(folder, files[i])}: one-handed, other arm kept at rest");
                }
                if (json.Value<string>("kind") == "sign") TrimHolds(sign);
                foreach (var label in json["labels"])
                {
                    var s = Copy(sign);
                    if (json.Value<string>("kind") == "sentence") s.english = label.ToString();
                    else s.gloss = label.ToString().ToUpperInvariant();
                    if (s.english.Length > 0) { dict.sentences.RemoveAll(x => x.english == s.english); dict.sentences.Add(s); }
                    else { dict.signs.RemoveAll(x => x.gloss == s.gloss); dict.signs.Add(s); }
                }
                log.AppendLine($"{Path.GetRelativePath(folder, files[i])}: {sign.Frames} frames → {string.Join(", ", json["labels"])}"
                               + (rig.LastSnap.Length > 0 ? $" (held: {rig.LastSnap})" : ""));
                rig.LastSnap = "";
            }
        }
        finally
        {
            rig.Dispose();
            EditorUtility.ClearProgressBar();
        }
        // Pointing signs no dataset has on its own (they point at people, so they're easy to make): procedural.
        rig = new Rig();
        try
        {
            foreach (var (gloss, sign) in rig.Pointing())
            {
                if (dict.signs.Exists(x => x.gloss == gloss && x.credit != Procedural)) continue; // a recording wins
                sign.gloss = gloss;
                dict.signs.RemoveAll(x => x.gloss == gloss);
                dict.signs.Add(sign);
                log.AppendLine($"{gloss}: procedural");
            }
        }
        finally { rig.Dispose(); }
        dict.signs.RemoveAll(x => string.IsNullOrEmpty(x.gloss)); // unplayable leftovers of earlier imports
        // Signs made in the wrong place (found by comparing them with other signers: tools/signs/compare_aslcitizen.py
        // --tune) are moved there whole: poses/tune.json, gloss → [x, y, z] metres (the avatar's right, up, forward).
        // For a tuning run, TuneTry adds copies of signs named GLOSS@n, each moved by a candidate offset.
        var offsets = new Dictionary<string, Vector3>();
        void ReadOffsets(string path, bool copies)
        {
            if (path == null || !File.Exists(path)) return;
            foreach (var (name, v) in JObject.Parse(File.ReadAllText(path)))
            {
                var a = (JArray)v;
                var at = copies ? 1 : 0;
                if (copies)
                {
                    var from = dict.signs.Find(x => x.gloss == a[0].ToString());
                    if (from == null) continue;
                    var copy = Copy(from);
                    copy.gloss = name;
                    dict.signs.RemoveAll(x => x.gloss == name);
                    dict.signs.Add(copy);
                }
                offsets[name] = new Vector3((float)a[at], (float)a[at + 1], (float)a[at + 2]);
            }
        }
        ReadOffsets(Path.Combine(folder, "tune.json"), false);
        ReadOffsets(TuneTry, true);
        // Handshapes from the sign's phonology (ASL-LEX, via tools/signs/handshape_labels.py), formed with the avatar's own
        // hand definitions instead of the tracked fingers: poses/handshapes.json, gloss → [[right, from, to, left], …],
        // from and to as fractions of the sign, left "" to keep that hand as tracked. HandTry adds copies GLOSS@H →
        // [GLOSS, segments] to compare with the sign as it is (moved as GLOSS is).
        var shapes = new Dictionary<string, List<(string right, float from, float to, string left)>>();
        var holdWeight = new Dictionary<string, float>();
        void ReadShapes(string path, bool copies)
        {
            if (path == null || !File.Exists(path)) return;
            foreach (var (name, v) in JObject.Parse(File.ReadAllText(path)))
            {
                var segs = (JArray)(copies ? v[1] : v);
                if (copies)
                {
                    var gloss = v[0].ToString();
                    var from = dict.signs.Find(x => x.gloss == gloss);
                    if (from == null) continue;
                    var copy = Copy(from);
                    copy.gloss = name;
                    dict.signs.RemoveAll(x => x.gloss == name);
                    dict.signs.Add(copy);
                    if (offsets.TryGetValue(gloss, out var o)) offsets[name] = o;
                }
                shapes[name] = segs.Select(x => (x[0].ToString(), (float)x[1], (float)x[2], x.Count() > 3 ? x[3].ToString() : "")).ToList();
                // A fifth number on a segment: the handshape only where the fingers hold still, at most this strongly
                // (see Rig.Handshape); the moves between holds keep the recorded fingers.
                if (segs.Any(x => x.Count() > 4)) holdWeight[name] = segs.Max(x => x.Count() > 4 ? (float)x[4] : 1f);
                else holdWeight.Remove(name);
            }
        }
        ReadShapes(Path.Combine(folder, "handshapes.json"), false);
        ReadShapes(HandTry, true);
        // Linguistic hand rules (tools/signs/sign_priors.py, after SGNify, Forte et al. 2023), for signs without handshapes
        // above: poses/priors.json, gloss → {"hold": one handshape per hand through the sign, "same": the other hand makes
        // the dominant hand's shape}. PriorTry adds copies GLOSS@P → [GLOSS, rules] to compare with the sign as it is.
        var priors = new Dictionary<string, (bool hold, bool same)>();
        void ReadPriors(string path, bool copies)
        {
            if (path == null || !File.Exists(path)) return;
            foreach (var (name, v) in JObject.Parse(File.ReadAllText(path)))
            {
                var r = copies ? v[1] : v;
                if (copies)
                {
                    var gloss = v[0].ToString();
                    var from = dict.signs.Find(x => x.gloss == gloss);
                    if (from == null) continue;
                    var copy = Copy(from);
                    copy.gloss = name;
                    dict.signs.RemoveAll(x => x.gloss == name);
                    dict.signs.Add(copy);
                    if (offsets.TryGetValue(gloss, out var o)) offsets[name] = o;
                }
                priors[name] = (r.Value<bool>("hold"), r.Value<bool>("same"));
            }
        }
        ReadPriors(Path.Combine(folder, "priors.json"), false);
        ReadPriors(PriorTry, true);
        // Signs made touching the face or body (tools/signs/contact_rules.py, from ASL-LEX): poses/contact.json, gloss →
        // where (Chin, Forehead, TorsoTop…); the hand is brought onto the body where it stops short (Rig.Contact).
        // ContactTry adds copies GLOSS@C → [GLOSS, where] to compare with the sign as it is.
        var contacts = new Dictionary<string, string>();
        void ReadContacts(string path, bool copies)
        {
            if (path == null || !File.Exists(path)) return;
            foreach (var (name, v) in JObject.Parse(File.ReadAllText(path)))
            {
                if (copies)
                {
                    var gloss = v[0].ToString();
                    var from = dict.signs.Find(x => x.gloss == gloss);
                    if (from == null) continue;
                    var copy = Copy(from);
                    copy.gloss = name;
                    dict.signs.RemoveAll(x => x.gloss == name);
                    dict.signs.Add(copy);
                    if (offsets.TryGetValue(gloss, out var o)) offsets[name] = o;
                    if (shapes.TryGetValue(gloss, out var sh)) shapes[name] = sh;
                    if (holdWeight.TryGetValue(gloss, out var hw)) holdWeight[name] = hw;
                    if (priors.TryGetValue(gloss, out var pr)) priors[name] = pr;
                }
                contacts[name] = (copies ? v[1] : v).ToString();
            }
        }
        ReadContacts(Path.Combine(folder, "contact.json"), false);
        ReadContacts(ContactTry, true);
        // Muscles span each joint's limits at -1…1. Retargeting matches bone directions with no limits, and noisy tracking
        // asks for more (spread up to ±20, wrists to ±4): past ±1 fingers fold backwards and wrists snap.
        // Motion capture retargeted onto this avatar by bone orientation (3D-LEX, Lex3DImport.ExportRetargeted) is real
        // anatomy: its fingers, thumbs and shoulders go past ±1 (to ~3.7) and play exactly as captured, so it only gets
        // a sanity limit. Clamping it to ±1 bent thumbs by up to 35 degrees.
        foreach (var s in dict.signs) { ClampToLimits(s.muscles, s.credit == Lex3DImport.Credit ? 5f : 1f); SmoothHands(s.muscles); }
        foreach (var s in dict.sentences) { ClampToLimits(s.muscles, s.credit == Lex3DImport.Credit ? 5f : 1f); SmoothHands(s.muscles); }
        // Last, on the finished muscles (clamping and smoothing move hands too): no hand behind the body.
        rig = new Rig();
        try
        {
            int pushed = 0, frames = 0;
            var worst = 0f;
            foreach (var s in dict.signs.Concat(dict.sentences))
            {
                if (s.english.Length == 0 && offsets.TryGetValue(s.gloss, out var offset))
                {
                    rig.Shift(s, offset);
                    log.AppendLine($"{s.gloss}: moved by {100 * offset.x:0}, {100 * offset.y:0}, {100 * offset.z:0} cm");
                }
                var (n, most) = rig.Unclip(s);
                pushed += n;
                frames += s.Frames;
                worst = Mathf.Max(worst, most);
                if (n > 0) log.AppendLine($"{(s.english.Length > 0 ? s.english : s.gloss)}: hands brought in front of the body in {n}/{s.Frames} frames, up to {100 * most:0} cm");
                if (s.english.Length == 0 && s.credit != Procedural) rig.Core(s);
                if (s.english.Length == 0 && contacts.TryGetValue(s.gloss, out var where) && rig.Contact(s) is var closed and > 0f)
                    log.AppendLine($"{s.gloss}: hand brought onto the {where.ToLowerInvariant()} ({100 * closed:0.0} cm)");
                if (s.english.Length == 0 && !shapes.ContainsKey(s.gloss) && priors.TryGetValue(s.gloss, out var rules) && (rules.hold || rules.same))
                {
                    var gaps0 = rig.Gaps(s);
                    log.AppendLine($"{s.gloss}: hand rules{(rules.hold ? " hold" : "")}{(rules.same ? " same" : "")}, {rig.Priors(s, rules.hold, rules.same)}");
                    if (gaps0 != null && rig.Retouch(s, gaps0) is var (retouched0, before0, after0) && retouched0 > 0)
                        log.AppendLine($"{s.gloss}: hands back in touch in {retouched0} frames, gap {1000 * before0:0} → {1000 * after0:0} mm at most");
                }
                if (s.english.Length == 0 && shapes.TryGetValue(s.gloss, out var segs))
                {
                    // New fingers change the hands' size: hands that touched (a fist on a fist) touch again after.
                    var gaps = rig.Gaps(s);
                    log.AppendLine($"{s.gloss}: handshapes {string.Join(", ", segs.Select(x => x.left.Length > 0 ? $"{x.right}/{x.left}" : x.right))}"
                                   + (holdWeight.TryGetValue(s.gloss, out var hw) ? $" (holds only, {hw:0.##})" : "")
                                   + (rig.Handshape(s, segs, holdWeight.TryGetValue(s.gloss, out var hw2) ? hw2 : -1f) ? "" : " (unknown handshape, skipped)"));
                    if (gaps != null && rig.Retouch(s, gaps) is var (retouched, before, after) && retouched > 0)
                        log.AppendLine($"{s.gloss}: hands back in touch in {retouched} frames, gap {1000 * before:0} → {1000 * after:0} mm at most");
                }
            }
            log.AppendLine($"Hands behind the body: {pushed}/{frames} frames brought in front, up to {100 * worst:0} cm");
        }
        finally { rig.Dispose(); }
        dict.Rebuild();
        EditorUtility.SetDirty(dict);
        AssetDatabase.SaveAssets();
        return log + $"Dictionary: {dict.signs.Count} signs, {dict.sentences.Count} sentences";
    }

    // Dictionary signers hold the sign's start and end still for a beat (citation form). Played one after another
    // those holds make the signing halting: keep at most 0.12 s of stillness at either end. Still is the arm moving at
    // under a fifth of its usual speed in this clip (tracking jitter never quite stops it).
    static void TrimHolds(SignDictionary.Sign sign)
    {
        var muscles = sign.muscles;
        var fps = sign.fps;
        var n = muscles.Length / SignPose.Muscles;
        if (n < 6 || fps <= 0) return;
        var speed = new float[n - 1];
        for (var f = 0; f < n - 1; f++)
            for (var m = 0; m < SignPose.Muscles; m++)
            {
                var name = HumanTrait.MuscleName[m];
                if (name.Contains("Arm") || name.Contains("Forearm") || name.Contains("Hand") || name.Contains("Shoulder"))
                    speed[f] = Mathf.Max(speed[f], Mathf.Abs(muscles[(f + 1) * SignPose.Muscles + m] - muscles[f * SignPose.Muscles + m]) * fps);
            }
        var sorted = (float[])speed.Clone();
        System.Array.Sort(sorted);
        var still = 0.2f * sorted[(int)(0.9f * (sorted.Length - 1))];
        var keep = Mathf.CeilToInt(0.12f * fps);
        int a = 0, b = n - 1;
        while (a < n - 1 && speed[a] < still) a++;
        while (b > a && speed[b - 1] < still) b--;
        a = Mathf.Max(0, a - keep);
        b = Mathf.Min(n - 1, b + keep);
        var frames = b - a + 1;
        if (frames < n && frames >= Mathf.Max(6, (int)(0.4f * n))) Slice(sign, a, frames);
    }

    // Keeps frames [start, start + count) of a sign's muscles and face.
    static void Slice(SignDictionary.Sign sign, int start, int count)
    {
        float[] Cut(float[] a, int width)
        {
            if (a.Length < (start + count) * width) return a;
            var o = new float[count * width];
            System.Array.Copy(a, start * width, o, 0, o.Length);
            return o;
        }
        sign.muscles = Cut(sign.muscles, SignPose.Muscles);
        sign.face = Cut(sign.face, SignPose.FaceChannels.Length);
    }

    // MediaPipe's face blendshapes (ARKit names, see extract_pose.py) → the avatar's FACS channels.
    // No eyeBlink: a signer looking down at their hands reads as eyes half shut. SignFace blinks instead.
    static readonly (string from, string[] to)[] FaceMap =
    {
        ("browDownLeft", new[] { "browLowerer_L" }), ("browDownRight", new[] { "browLowerer_R" }),
        ("browInnerUp", new[] { "innerBrowRaiser_L", "innerBrowRaiser_R" }),
        ("browOuterUpLeft", new[] { "outerBrowRaiser_L" }), ("browOuterUpRight", new[] { "outerBrowRaiser_R" }),
        ("cheekPuff", new[] { "cheekPuff_L", "cheekPuff_R" }),
        ("cheekSquintLeft", new[] { "cheekRaiser_L" }), ("cheekSquintRight", new[] { "cheekRaiser_R" }),
        ("eyeSquintLeft", new[] { "lidTightener_L" }), ("eyeSquintRight", new[] { "lidTightener_R" }),
        ("eyeWideLeft", new[] { "upperLidRaiser_L" }), ("eyeWideRight", new[] { "upperLidRaiser_R" }),
        ("jawForward", new[] { "jawThrust" }), ("jawLeft", new[] { "jawSidewaysLeft" }), ("jawRight", new[] { "jawSidewaysRight" }),
        ("jawOpen", new[] { "jawDrop" }),
        ("mouthDimpleLeft", new[] { "dimpler_L" }), ("mouthDimpleRight", new[] { "dimpler_R" }),
        ("mouthFrownLeft", new[] { "lipCornerDepressor_L" }), ("mouthFrownRight", new[] { "lipCornerDepressor_R" }),
        ("mouthFunnel", new[] { "lipFunneler_LB", "lipFunneler_LT", "lipFunneler_RB", "lipFunneler_RT" }),
        ("mouthPucker", new[] { "lipPucker_L", "lipPucker_R" }),
        ("mouthRollLower", new[] { "lipSuck_LB", "lipSuck_RB" }), ("mouthRollUpper", new[] { "lipSuck_LT", "lipSuck_RT" }),
        ("mouthShrugLower", new[] { "chinRaiser_B" }), ("mouthShrugUpper", new[] { "chinRaiser_T" }),
        ("mouthClose", new[] { "lipsToward_LB", "lipsToward_LT", "lipsToward_RB", "lipsToward_RT" }),
        ("mouthLeft", new[] { "mouthLeft" }), ("mouthRight", new[] { "mouthRight" }),
        ("mouthLowerDownLeft", new[] { "lowerLipDepressor_L" }), ("mouthLowerDownRight", new[] { "lowerLipDepressor_R" }),
        ("mouthPressLeft", new[] { "lipPressor_L" }), ("mouthPressRight", new[] { "lipPressor_R" }),
        ("mouthSmileLeft", new[] { "lipCornerPuller_L" }), ("mouthSmileRight", new[] { "lipCornerPuller_R" }),
        ("mouthStretchLeft", new[] { "lipStretcher_L" }), ("mouthStretchRight", new[] { "lipStretcher_R" }),
        ("mouthUpperUpLeft", new[] { "upperLipRaiser_L" }), ("mouthUpperUpRight", new[] { "upperLipRaiser_R" }),
        ("noseSneerLeft", new[] { "noseWrinkler_L" }), ("noseSneerRight", new[] { "noseWrinkler_R" }),
    };

    // Face Landmarker's blendshapes don't share a scale: a full smile scores near 1, but a clear frown or lip stretch
    // rarely passes 0.2, so passed through as they are only the brows, squint, smile and pucker would show. Each one is
    // scaled so that its strong uses across all the recordings (the 90th percentile of each file's peak) reach 0.7,
    // at most 3×, as the finger curves are calibrated to the avatar. Cheek raise, cheek puff and nose wrinkle stay at
    // 0 whatever the face does; SignFace derives those from the smile and lip.
    static readonly Dictionary<string, float> FaceGain = new();

    static string FaceGains(string[] files)
    {
        var peaks = new Dictionary<string, List<float>>();
        foreach (var f in files)
            if (JObject.Parse(File.ReadAllText(f))["face"] is JObject face)
                foreach (var (name, v) in face)
                    if (v is JArray a && a.Count > 0)
                    {
                        if (!peaks.TryGetValue(name, out var l)) peaks[name] = l = new List<float>();
                        l.Add(a.Max(x => (float)x));
                    }
        FaceGain.Clear();
        var report = new System.Text.StringBuilder("Face gains:");
        foreach (var (name, l) in peaks.OrderBy(p => p.Key))
        {
            l.Sort();
            var p90 = l[Mathf.Min(l.Count - 1, (int)(0.9f * l.Count))];
            FaceGain[name] = p90 < 0.02f ? 1f : Mathf.Clamp(0.7f / p90, 1f, 3f);
            report.Append($" {name} {FaceGain[name]:0.0}");
        }
        return report.ToString();
    }

    static float[] Face(JObject json, int n)
    {
        if (json["face"] is not JObject face) return System.Array.Empty<float>();
        var c = SignPose.FaceChannels.Length;
        var o = new float[n * c];
        foreach (var (from, to) in FaceMap)
        {
            if (face[from] is not JArray v || v.Count != n) continue;
            foreach (var ch in to)
            {
                var i = System.Array.IndexOf(SignPose.FaceChannels, ch);
                if (i < 0) continue;
                var gain = FaceGain.TryGetValue(from, out var g) ? g : 1f;
                for (var f = 0; f < n; f++) o[f * c + i] = Mathf.Max(o[f * c + i], Mathf.Clamp01(gain * (float)v[f]));
            }
        }
        return o;
    }

    static void ClampToLimits(float[] muscles, float limit = 1f)
    {
        for (var i = 0; i < muscles.Length; i++) muscles[i] = Mathf.Clamp(muscles[i], -limit, limit);
    }

    // Hands are what tracking gets most wrong frame to frame: clamped, a finger it loses for a frame flips from one limit
    // to the other (in 151 of 190 signs). A 5-frame median drops those flips but keeps real handshape changes sharp,
    // then a 3-frame average evens out what's left. Wrists and forearm twist too, fingers of both hands.
    static void SmoothHands(float[] muscles)
    {
        var frames = muscles.Length / SignPose.Muscles;
        if (frames < 3) return;
        var track = new float[frames];
        var window = new float[5];
        for (var m = 0; m < SignPose.Muscles; m++)
        {
            var name = HumanTrait.MuscleName[m];
            if (!(name.Contains("Hand") || name.Contains("Forearm Twist") || name.Contains("Stretched") || name.Contains("Spread"))) continue;
            if (name.Contains("Forearm Stretch") || name.Contains("Upper")) continue;
            for (var f = 0; f < frames; f++)
            {
                var n = 0;
                for (var k = Mathf.Max(0, f - 2); k <= Mathf.Min(frames - 1, f + 2); k++) window[n++] = muscles[k * SignPose.Muscles + m];
                System.Array.Sort(window, 0, n);
                track[f] = window[n / 2];
            }
            for (var f = 0; f < frames; f++)
            {
                var a = track[Mathf.Max(0, f - 1)];
                var b = track[Mathf.Min(frames - 1, f + 1)];
                muscles[f * SignPose.Muscles + m] = 0.25f * a + 0.5f * track[f] + 0.25f * b;
            }
        }
    }

    /// <summary>How far each finger is pulled towards the clear finger states (0 = as tracked, 1 = fully).</summary>
    public static float FingerSnap = 0.5f;

    /// <summary>How far tracked finger angles are stretched from the tracker's range onto the avatar's handshape range
    /// (0 = as tracked, 1 = fully). See Rig.MeasureSources.</summary>
    public static float FingerRange = 1f;

    /// <summary>How far a held handshape is pulled onto the nearest canonical handshape (0 = off). See Rig.SnapToTemplates.</summary>
    public static float TemplateSnap = 0.5f;

    /// <summary>Matches less sure than this aren't snapped; from SnapFullConfidence on they are snapped fully.</summary>
    public static float SnapMinConfidence = 0.5f, SnapFullConfidence = 0.7f;

    /// <summary>Fingers moving slower than this (degrees per second, mean over the joints) are holding a handshape.</summary>
    public static float HoldSpeed = 150f;

    /// <summary>A hold this far (degrees, root mean square) from a canonical handshape is matched with confidence 0.37.</summary>
    public static float SnapSigma = 30f;

    /// <summary>A finger this far (degrees) from the matched handshape's is pulled onto it with weight 0.37 of the hand's.</summary>
    public static float FingerTolerance = 40f;

    /// <summary>Cost (degrees per unit of log gain) of matching a hold to a handshape with its contrast scaled.</summary>
    public static float GainCost = 20f;

    /// <summary>Handshapes given for holds only (a fifth number in handshapes.json): fingers moving at under this mean
    /// speed (muscle units per second) count as fully held, fading out by twice it.</summary>
    public static float HoldMuscleSpeed = 1f;

    /// <summary>Where a sign's core starts and ends: past the 70% height line, also skip the hands still rising into the
    /// sign or falling out of it faster than this (m/s); 0 = the line alone (with a frame either side). At 0.12 the
    /// wrist no longer drops between signs in sentences (joins dipping over 5 cm: 33 → 0 of 481 in real captions),
    /// and isolated signs score the same against ASL Citizen signers.</summary>
    public static float CoreSpeed = 0.12f;

    /// <summary>A tuning run's candidate contacts (see ImportAll), or null.</summary>
    public static string ContactTry;

    /// <summary>A tuning run's candidate offsets (see ImportAll), or null.</summary>
    public static string TuneTry;

    /// <summary>A tuning run's candidate handshapes (see ImportAll), or null.</summary>
    public static string HandTry;

    /// <summary>A tuning run's candidate linguistic hand rules (see ImportAll), or null.</summary>
    public static string PriorTry;

    const float MinHandSeen = 0.3f;
    const string Procedural = "Procedural pointing sign (Speaker Captions)";

    static SignDictionary.Sign Copy(SignDictionary.Sign s) => new()
    {
        fps = s.fps, muscles = (float[])s.muscles.Clone(), face = (float[])s.face.Clone(), credit = s.credit, gloss = "", english = "",
        coreStart = s.coreStart, coreEnd = s.coreEnd,
    };

    /// <summary>A hidden copy of the avatar, posed frame by frame from landmarks.</summary>
    sealed class Rig : System.IDisposable
    {
        readonly GameObject _go;
        readonly Animator _a;
        readonly SignPose _pose;
        readonly HumanPoseHandler _handler;
        readonly float[] _ready;
        readonly Quaternion _torso; // the avatar's torso frame (fixed: only arms and hands move)
        readonly Dictionary<HumanBodyBones, Vector3> _tipDir = new(); // last bone of a chain: its pointing direction, in its own space

        public Rig()
        {
            _go = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath));
            _go.hideFlags = HideFlags.HideAndDontSave;
            _go.transform.SetPositionAndRotation(new Vector3(200, 0, 0), Quaternion.identity);
            _a = _go.GetComponent<Animator>();
            _pose = new SignPose(_a);
            _handler = new HumanPoseHandler(_a.avatar, _a.transform);
            _ready = Handshapes.Ready();
            _pose.Apply(_ready);
            var ls = Bone(HumanBodyBones.LeftUpperArm).position;
            var rs = Bone(HumanBodyBones.RightUpperArm).position;
            var hips = Bone(HumanBodyBones.Hips).position;
            _torso = Frame(rs - ls, (ls + rs) * 0.5f - hips);
            _hipsY = hips.y;
            _shoulderY = (ls.y + rs.y) * 0.5f;
            // Distal finger bones have no humanoid child: they point on from the bone before them.
            for (var b = HumanBodyBones.Hips; b < HumanBodyBones.LastBone; b++)
            {
                var t = Bone(b);
                if (b.ToString().EndsWith("Distal") && t != null && !b.ToString().Contains("Toe"))
                    _tipDir[b] = t.InverseTransformDirection(t.position - t.parent.position).normalized;
            }
            Calibrate("Right");
            Calibrate("Left");
            MeasureTemplates();
            _pose.Apply(_ready);
        }

        readonly float _hipsY, _shoulderY;

        // A hand's height on the torso: 0 at the hips, 1 at the shoulders.
        float Height(HumanBodyBones hand) => (Bone(hand).position.y - _hipsY) / (_shoulderY - _hipsY);

        Transform Bone(HumanBodyBones b) => _a.GetBoneTransform(b);

        static Quaternion Frame(Vector3 right, Vector3 up)
        {
            var fwd = Vector3.Cross(right, up);
            return Quaternion.LookRotation(fwd, Vector3.Cross(fwd, right));
        }

        public SignDictionary.Sign Retarget(JObject json)
        {
            if (json.Value<string>("format") == "smplx") return RetargetSmplx(json);
            if (json.Value<string>("format") == "muscles") return FromMuscles(json);
            if (json["mhr"] is JObject && UseMhrSkeleton) return RetargetMhr(json);
            var body = json["body"].ToObject<float[][][]>();
            var idx = json["bodyIndex"].ToObject<int[]>();
            int P(int mp) => System.Array.IndexOf(idx, mp);
            var right = json["right"] is JArray r ? r : null;
            var n = body.Length;
            // The other hand only signs when it is seen most of the time (PopSign signers hold their phone in it).
            var leftSeen = 0;
            if (json["left"] is JArray left) foreach (var h in left) if (h is JArray) leftSeen++;
            var twoHanded = leftSeen >= 0.4f * n;
            var muscles = new float[n * SignPose.Muscles];
            var pose = new HumanPose();
            Vector3 lastBend = Vector3.zero, lastBendL = Vector3.zero;
            var source = json.Value<string>("hands") ?? "mediapipe";
            var handR = new (Dictionary<string, float> features, Vector3 thumb)[n];
            var handL = new (Dictionary<string, float> features, Vector3 thumb)[n];
            var heights = new float[n];
            if (LiftFromPicture) Lift(json, body, P);
            if (twoHanded) CloseGap(json, body, P(15), P(16), json["left"] as JArray, right);
            for (var f = 0; f < n; f++)
            {
                Vector3 B(int mp) { var p = body[f][P(mp)]; return new Vector3(p[0], p[1], p[2]); }
                // The signer's torso → the avatar's: leaning, turning and size don't carry over, only the arms do.
                var map = _torso * Quaternion.Inverse(Frame(B(12) - B(11), (B(11) + B(12)) * 0.5f - (B(23) + B(24)) * 0.5f));
                _pose.Apply(_ready);
                Shrug(HumanBodyBones.RightShoulder, HumanBodyBones.RightUpperArm, map * (B(14) - B(12)));
                Arm(HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
                    map * (B(14) - B(12)), map * (B(16) - B(14)), ref lastBend);
                if (twoHanded)
                {
                    Shrug(HumanBodyBones.LeftShoulder, HumanBodyBones.LeftUpperArm, map * (B(13) - B(11)));
                    Arm(HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand,
                        map * (B(13) - B(11)), map * (B(15) - B(13)), ref lastBendL);
                }
                heights[f] = twoHanded ? Mathf.Max(Height(HumanBodyBones.RightHand), Height(HumanBodyBones.LeftHand)) : Height(HumanBodyBones.RightHand);
                var lmR = Landmarks(right, f, map);
                if (lmR != null) Wrist(lmR[9] - lmR[0], lmR[5] - lmR[17]);
                // The other palm too (before, only the right hand turned as the signer's did: the left kept whatever
                // roll the arm gave it, so in two-handed signs the hands met wrong, e.g. HOW's knuckles).
                var lmL = twoHanded ? Landmarks(json["left"] as JArray, f, map) : null;
                if (lmL != null) Wrist(lmL[9] - lmL[0], lmL[5] - lmL[17], true);
                _handler.GetHumanPose(ref pose);
                // Keep the body still (spine, head, legs from the ready pose): only shoulders, arms, hands and fingers sign.
                for (var m = 0; m < SignPose.Muscles; m++)
                {
                    var name = HumanTrait.MuscleName[m];
                    var arm = IsArm(m) || name.StartsWith("Right Shoulder") || name.StartsWith("Left Shoulder");
                    muscles[f * SignPose.Muscles + m] = arm && (twoHanded || !name.StartsWith("Left")) ? pose.muscles[m] : _ready[m];
                }
                // Fingers from their joint angles (shaped over the whole sign below).
                if (lmR != null) handR[f] = Features(lmR, "Right", source);
                if (lmL != null) handL[f] = Features(lmL, "Left", source);
            }
            // Held handshapes onto the nearest canonical ones, then muscles; held through frames where tracking lost the hand.
            LastSnap = SnapToTemplates(handR, "Right", json.Value<float>("fps"));
            if (twoHanded) LastSnap += " / " + SnapToTemplates(handL, "Left", json.Value<float>("fps"));
            foreach (var (hand, side) in new[] { (handR, "Right"), (handL, "Left") })
                for (var f = 0; f < n; f++)
                    if (hand[f].features != null)
                        foreach (var (i, v) in ToMuscles(hand[f].features, hand[f].thumb, side)) muscles[f * SignPose.Muscles + i] = v;
            var sign = new SignDictionary.Sign
            {
                fps = json.Value<float>("fps"), muscles = muscles, face = Face(json, n), credit = json.Value<string>("credit") ?? "",
            };
            TrimLow(sign, heights);
            return sign;
        }

        public static bool LiftFromPicture = true;
        // Pose files with SAM 3D Body's skeleton ("mhr") take its bone rotations (RetargetMhr); off: its points, as MediaPipe's.
        public static bool UseMhrSkeleton = true;
        // ...and its wrists placed where the signer's are against the torso, scaled to the avatar (the avatar's arms are
        // shorter for its shoulders than most signers': the same joint angles alone leave its hands too high).
        public static bool MhrPlaceWrists = true;
        public static System.Text.StringBuilder MhrLog; // for checking: per frame the hands' skin gap (mm) after import
        // Depth from bone lengths too: off, the 3D guess's depth stays. A close camera makes a hand held forward look
        // bigger, so its forearm looks less foreshortened and bone-length depth pulls it back to the chest (TUESDAY,
        // IMPORTANT: wrists 30 cm too far back).
        public static bool LiftDepth = true;
        public static float LiftDepthLimit = 0.1f; // metres the bone-length depth may move a joint from the 3D guess's

        // The body model's 3D is a guess from one camera: in HOW it makes the right upper arm 22 cm and the left 31 cm in
        // the same frame, one forearm straight across and the other straight ahead, though on video both arms make the
        // same shape. What the camera does see well is across and up. With "body2d" (tools/signs/hand_contact.py: the
        // joints in the picture), the arms are rebuilt from it: across and up from the picture (scaled by shoulder
        // width), each bone's depth from its length (one length per bone for both arms over the clip) and only whether
        // it goes towards or away from the camera from the 3D guess. Shoulders and hips keep their depth.
        static void Lift(JObject json, float[][][] body, System.Func<int, int> P)
        {
            if (json["body2d"] is not JArray flatJson || flatJson.Count != body.Length) return;
            var flat = flatJson.ToObject<float[][][]>();
            var n = body.Length;
            float Median(System.Func<int, float> v) { var a = new float[n]; for (var f = 0; f < n; f++) a[f] = v(f); System.Array.Sort(a); return a[n / 2]; }
            Vector3 W(int f, int mp) => new(body[f][P(mp)][0], body[f][P(mp)][1], body[f][P(mp)][2]);
            Vector2 F(int f, int mp) => new(flat[f][P(mp)][0], flat[f][P(mp)][1]);
            var scale = Median(f => (W(f, 11) - W(f, 12)).magnitude) / Mathf.Max(1e-6f, Median(f => (F(f, 11) - F(f, 12)).magnitude));
            var upper = 0.5f * (Median(f => (W(f, 13) - W(f, 11)).magnitude) + Median(f => (W(f, 14) - W(f, 12)).magnitude));
            var fore = Mathf.Max(0.8f * upper, 0.5f * (Median(f => (W(f, 15) - W(f, 13)).magnitude) + Median(f => (W(f, 16) - W(f, 14)).magnitude)));
            for (var f = 0; f < n; f++)
            {
                var world = new Vector3[33];
                foreach (var mp in new[] { 0, 7, 8, 11, 12, 13, 14, 15, 16, 23, 24 }) world[mp] = W(f, mp);
                var mid3 = (world[11] + world[12]) * 0.5f;
                var mid2 = (F(f, 11) + F(f, 12)) * 0.5f;
                Vector3 At(int mp, float z) { var q = (F(f, mp) - mid2) * scale; return new Vector3(mid3.x + q.x, mid3.y + q.y, z); }
                foreach (var mp in new[] { 0, 7, 8, 11, 12, 23, 24 }) Set(mp, At(mp, world[mp].z));
                foreach (var (s, e, w) in new[] { (11, 13, 15), (12, 14, 16) })
                {
                    var sh = At(s, world[s].z);
                    if (!LiftDepth) { Set(e, At(e, world[e].z)); Set(w, At(w, world[w].z)); continue; }
                    var el = Near(Bone(sh, e, upper, world[e].z - world[s].z), world[e].z);
                    Set(e, el);
                    Set(w, Near(Bone(el, w, fore, world[w].z - world[e].z), world[w].z));
                }
                Vector3 Bone(Vector3 from, int to, float length, float towards)
                {
                    var p = At(to, 0f);
                    var across = new Vector2(p.x - from.x, p.y - from.y).magnitude;
                    return new Vector3(p.x, p.y, from.z + Mathf.Sign(towards) * Mathf.Sqrt(Mathf.Max(0f, length * length - across * across)));
                }
                Vector3 Near(Vector3 v, float z) => new(v.x, v.y, Mathf.Clamp(v.z, z - LiftDepthLimit, z + LiftDepthLimit));
                void Set(int mp, Vector3 v) { body[f][P(mp)][0] = v.x; body[f][P(mp)][1] = v.y; body[f][P(mp)][2] = v.z; }
            }
        }

        public static float ContactFull = 0.25f, ContactNone = 0.5f, ContactGap = 0.01f;

        // One camera guesses where each wrist is a few centimetres off, mostly in depth, and the hands are placed at
        // those wrists: in HOW the fists press together on video while the stored hands are 9 cm apart, so the avatar's
        // hands stack instead of meeting. tools/signs/hand_contact.py measures in the picture how close the hands are
        // ("handGap", in hand lengths); where they touch (full at ContactFull, none from ContactNone) both wrists move
        // half way each along the line between the hands' nearest landmarks, until ContactGap apart.
        static void CloseGap(JObject json, float[][][] body, int lw, int rw, JArray left, JArray right)
        {
            if (json["handGap"] is not JArray gaps) return;
            var n = body.Length;
            var move = new Vector3[n];
            for (var f = 0; f < n; f++)
            {
                // A frame or two where a hand dropped out: the nearest frame that saw both.
                float? g = null;
                for (var k = 0; k <= 2 && g == null; k++)
                    foreach (var j in new[] { f - k, f + k })
                        if (g == null && j >= 0 && j < Mathf.Min(n, gaps.Count) && gaps[j].Type == JTokenType.Float) g = (float)gaps[j];
                if (g == null) continue;
                var w = 1f - Mathf.Clamp01((g.Value - ContactFull) / (ContactNone - ContactFull));
                var l = HandPoints(left, f, body[f][lw]);
                var r = HandPoints(right, f, body[f][rw]);
                if (w <= 0f || l == null || r == null) continue;
                var best = float.MaxValue; var v = Vector3.zero;
                foreach (var a in l) foreach (var b in r) if ((b - a).sqrMagnitude < best) { best = (b - a).sqrMagnitude; v = b - a; }
                var d = Mathf.Sqrt(best);
                if (d > ContactGap) move[f] = w * 0.5f * v * (1f - ContactGap / d);
            }
            for (var f = 0; f < n; f++)
            {
                var sum = Vector3.zero; var c = 0;
                for (var k = Mathf.Max(0, f - 2); k <= Mathf.Min(n - 1, f + 2); k++) { sum += move[k]; c++; }
                var m = sum / c;
                for (var i = 0; i < 3; i++) { body[f][lw][i] += m[i]; body[f][rw][i] -= m[i]; }
            }
        }

        // A hand's landmarks in the body's space (metres): its shape from the hand tracker (any units), sized to a
        // 9 cm wrist-to-middle-knuckle, placed at the body model's wrist.
        static Vector3[] HandPoints(JArray hand, int f, float[] wrist)
        {
            if (hand == null || f >= hand.Count || hand[f] is not JArray h || h.Count != 21) return null;
            Vector3 L(int k) => new((float)h[k][0], (float)h[k][1], (float)h[k][2]);
            var size = (L(9) - L(0)).magnitude;
            if (size < 1e-6f) return null;
            var w = new Vector3(wrist[0], wrist[1], wrist[2]);
            var o = new Vector3[21];
            for (var k = 0; k < 21; k++) o[k] = w + (L(k) - L(0)) * (0.09f / size);
            return o;
        }

        // Clips start and end with the hands coming up from and going down to the signer's lap. The app eases into each
        // sign from its ready pose (hand at chest height) and back to it after, so those frames play as a dip: drop the
        // frames at either end where the hands are in the lower part of the torso (keeping one), unless that leaves
        // under half (minKeep). Signs made low have a lower line. Motion capture takes (3D-LEX) start and end with the
        // signer standing still, arms down, often longer than the sign itself: those keep down to a sixth.
        static void TrimLow(SignDictionary.Sign sign, float[] heights, float minKeep = 0.5f)
        {
            var n = heights.Length;
            var peak = float.MinValue;
            foreach (var h in heights) peak = Mathf.Max(peak, h);
            var low = Mathf.Min(0.3f, 0.6f * peak);
            int a = 0, b = n - 1;
            while (a < n && heights[a] < low) a++;
            while (b > a && heights[b] < low) b--;
            a = Mathf.Max(0, a - 1);
            b = Mathf.Min(n - 1, b + 1);
            var keep = b - a + 1;
            if (keep < n && keep >= Mathf.Max(6, Mathf.CeilToInt(n * minKeep))) Slice(sign, a, keep);
        }

        // An arm doesn't swing from a fixed shoulder: raised past about 60° from hanging, the collarbone lifts with it
        // (about a fifth of the extra, up to 12°), and reaching forward brings it forward (up to 8°). Without this the
        // arms look bolted on. Turned before the arm is posed, so the arm still points where the signer's did.
        void Shrug(HumanBodyBones shoulder, HumanBodyBones upper, Vector3 upperDir)
        {
            var s = Bone(shoulder);
            if (s == null || upperDir.sqrMagnitude < 1e-8f) return;
            var up = _torso * Vector3.up;
            var raise = Mathf.Clamp((Vector3.Angle(upperDir, -up) - 60f) * 0.2f, 0f, 12f);
            var forward = 8f * Mathf.Clamp01(Vector3.Dot(upperDir.normalized, _torso * Vector3.forward));
            var dir = Bone(upper).position - s.position;
            var to = Vector3.RotateTowards(dir, up, raise * Mathf.Deg2Rad, 0f);
            to = Vector3.RotateTowards(to, _torso * Vector3.forward, forward * Mathf.Deg2Rad, 0f);
            s.rotation = Quaternion.FromToRotation(dir, to) * s.rotation;
        }

        BodySurface _body;

        // Tracking puts the wrist's depth least reliably, and a wrist a little too far back lands inside the chest or
        // behind the face; fingers pointing back can go in even when the wrist is clear. For every frame, any part of
        // either hand behind the front of the avatar's own body (its mesh: chest, belly, neck, face) brings the wrist
        // forward until it's clear: the arm reaches there, the elbow bent as before, the hand turned as before.
        // Fingertips may touch the body, as in signs made on the chest or chin. Returns the frames moved and the most
        // any moved.
        public (int, float) Unclip(SignDictionary.Sign sign)
        {
            _pose.Apply(_ready);
            _body ??= new BodySurface(_a);
            var fwd = _body.Forward;
            var m = new float[SignPose.Muscles];
            var pose = new HumanPose();
            var moved = 0;
            var most = 0f;
            for (var f = 0; f < sign.Frames; f++)
            {
                System.Array.Copy(sign.muscles, f * SignPose.Muscles, m, 0, SignPose.Muscles);
                var changed = false;
                foreach (var right in new[] { true, false })
                {
                    var (upper, lower, hand) = right
                        ? (HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand)
                        : (HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand);
                    var side = right ? "Right " : "Left ";
                    for (var pass = 0; pass < 4; pass++)
                    {
                        _pose.Apply(m);
                        var behind = _body.Behind(_a, right);
                        if (behind < 0.003f) break;
                        if (pass == 0) most = Mathf.Max(most, behind);
                        changed = true;
                        var shoulder = Bone(upper).position;
                        var handRot = Bone(hand).rotation;
                        var (elbow, forearm) = Reach(upper, lower, hand, Bone(hand).position + fwd * (behind + 0.005f) - shoulder,
                            Bone(lower).position - shoulder);
                        var bend = Vector3.zero;
                        Arm(upper, lower, hand, elbow, forearm, ref bend);
                        Bone(hand).rotation = handRot;
                        _handler.GetHumanPose(ref pose);
                        for (var k = 0; k < SignPose.Muscles; k++)
                            if (IsArm(k) && HumanTrait.MuscleName[k].StartsWith(side)) m[k] = Mathf.Clamp(pose.muscles[k], -1f, 1f);
                    }
                }
                if (!changed) continue;
                moved++;
                System.Array.Copy(m, 0, sign.muscles, f * SignPose.Muscles, SignPose.Muscles);
            }
            _pose.Apply(_ready);
            return (moved, most);
        }

        // Signs made on the face or body (ASL-LEX: contact, at the chin, forehead, mouth, chest…) touch there, but a
        // single camera guesses the wrist's depth a few centimetres off, so the hand often stops short and hovers in
        // front of the chin. Over the sign's core, the moment the dominant hand comes closest to the body's front (its
        // nearest fingertip, knuckle or palm) is the contact; if it's still more than a centimetre away (and within
        // 15 cm: closer is plainly meant to touch), the wrist moves straight back by the gap, fully over the frames
        // within 2 cm of that closest approach and easing in and out over 0.15 s around them, the arm reaching as in
        // Unclip. Returns the gap closed (m), 0 if none.
        public float Contact(SignDictionary.Sign sign)
        {
            var n = sign.Frames;
            if (n < 4) return 0f;
            _pose.Apply(_ready);
            _body ??= new BodySurface(_a);
            var fwd = _body.Forward;
            var m = new float[SignPose.Muscles];
            int a = sign.coreEnd > sign.coreStart ? sign.coreStart : 0, b = sign.coreEnd > sign.coreStart ? sign.coreEnd : n - 1;
            var gap = new float[n];
            for (var f = 0; f < n; f++)
            {
                System.Array.Copy(sign.muscles, f * SignPose.Muscles, m, 0, SignPose.Muscles);
                _pose.Apply(m);
                gap[f] = HandGap();
            }
            var best = a;
            for (var f = a; f <= b; f++) if (gap[f] < gap[best]) best = f;
            var g = gap[best];
            if (float.IsInfinity(g) || g <= 0.01f || g > 0.15f) { _pose.Apply(_ready); return 0f; }
            var push = g - 0.005f;
            var ease = Mathf.Max(1, Mathf.RoundToInt(0.15f * sign.fps));
            var w = new float[n];
            for (var f = 0; f < n; f++) if (f >= a && f <= b && gap[f] <= g + 0.02f) w[f] = 1f;
            var full = (float[])w.Clone();
            for (var f = 0; f < n; f++)
                for (var k = 1; k <= ease && w[f] < 1f; k++)
                    if ((f - k >= 0 && full[f - k] >= 1f) || (f + k < n && full[f + k] >= 1f)) { var u = 1f - k / (float)(ease + 1); w[f] = Mathf.Max(w[f], u * u * (3f - 2f * u)); }
            var pose = new HumanPose();
            for (var f = 0; f < n; f++)
            {
                if (w[f] <= 0f) continue;
                System.Array.Copy(sign.muscles, f * SignPose.Muscles, m, 0, SignPose.Muscles);
                _pose.Apply(m);
                var shoulder = Bone(HumanBodyBones.RightUpperArm).position;
                var handRot = Bone(HumanBodyBones.RightHand).rotation;
                var (elbow, forearm) = Reach(HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
                    Bone(HumanBodyBones.RightHand).position - fwd * (push * w[f]) - shoulder, Bone(HumanBodyBones.RightLowerArm).position - shoulder);
                var bend = Vector3.zero;
                Arm(HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand, elbow, forearm, ref bend);
                Bone(HumanBodyBones.RightHand).rotation = handRot;
                _handler.GetHumanPose(ref pose);
                for (var k = 0; k < SignPose.Muscles; k++)
                    if (IsArm(k) && HumanTrait.MuscleName[k].StartsWith("Right ")) sign.muscles[f * SignPose.Muscles + k] = Mathf.Clamp(pose.muscles[k], -1f, 1f);
            }
            _pose.Apply(_ready);
            return push;
        }

        // How far the right hand's nearest point (fingertips, index and little knuckles, palm) is in front of the body.
        float HandGap()
        {
            var best = float.PositiveInfinity;
            void Try(Vector3 p) => best = Mathf.Min(best, _body.Ahead(p));
            var wrist = Bone(HumanBodyBones.RightHand).position;
            var midKnuckle = Bone(HumanBodyBones.RightMiddleProximal);
            if (midKnuckle != null) Try((wrist + midKnuckle.position) * 0.5f);
            foreach (var finger in new[] { "Thumb", "Index", "Middle", "Ring", "Little" })
            {
                System.Enum.TryParse<HumanBodyBones>("Right" + finger + "Proximal", out var pb);
                System.Enum.TryParse<HumanBodyBones>("Right" + finger + "Intermediate", out var ib);
                System.Enum.TryParse<HumanBodyBones>("Right" + finger + "Distal", out var db);
                var prox = Bone(pb); var mid = Bone(ib); var dist = Bone(db);
                if (prox != null && finger is "Index" or "Little") Try(prox.position);
                if (mid != null && dist != null) Try(dist.position + (dist.position - mid.position) * 0.8f);
            }
            return best;
        }

        // Whether the left arm signs at all in this clip (moves away from the ready pose).
        bool LeftSigns(SignDictionary.Sign sign)
        {
            for (var f = 0; f < sign.Frames; f++)
                for (var k = 0; k < SignPose.Muscles; k++)
                    if (IsArm(k) && HumanTrait.MuscleName[k].StartsWith("Left ") && !IsFinger(HumanTrait.MuscleName[k], "Left")
                        && Mathf.Abs(sign.muscles[f * SignPose.Muscles + k] - _ready[k]) > 0.1f) return true;
            return false;
        }

        // Moves the whole sign by offset (metres, avatar space): each wrist reaches that much further, the elbow
        // bent as before, the hand turned as before. Both hands when both sign, so they still meet where they did.
        public void Shift(SignDictionary.Sign sign, Vector3 offset)
        {
            var both = LeftSigns(sign);
            var m = new float[SignPose.Muscles];
            var pose = new HumanPose();
            for (var f = 0; f < sign.Frames; f++)
            {
                System.Array.Copy(sign.muscles, f * SignPose.Muscles, m, 0, SignPose.Muscles);
                foreach (var right in both ? new[] { true, false } : new[] { true })
                {
                    var (upper, lower, hand) = right
                        ? (HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand)
                        : (HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand);
                    _pose.Apply(m);
                    var shoulder = Bone(upper).position;
                    var handRot = Bone(hand).rotation;
                    var (elbow, forearm) = Reach(upper, lower, hand, Bone(hand).position + offset - shoulder, Bone(lower).position - shoulder);
                    var bend = Vector3.zero;
                    Arm(upper, lower, hand, elbow, forearm, ref bend);
                    Bone(hand).rotation = handRot;
                    _handler.GetHumanPose(ref pose);
                    var side = right ? "Right " : "Left ";
                    for (var k = 0; k < SignPose.Muscles; k++)
                        if (IsArm(k) && HumanTrait.MuscleName[k].StartsWith(side) && !IsFinger(HumanTrait.MuscleName[k], right ? "Right" : "Left"))
                            m[k] = Mathf.Clamp(pose.muscles[k], -1f, 1f);
                }
                System.Array.Copy(m, 0, sign.muscles, f * SignPose.Muscles, SignPose.Muscles);
            }
            _pose.Apply(_ready);
        }

        HandFit _fit;
        readonly Dictionary<string, float[]> _shapes = new();

        // A handshape's finger and thumb muscles (right hand), the thumb fitted on this avatar as for spelling; null if
        // there is no such handshape. Muscle space is mirror-symmetric, so a left hand takes the same values.
        float[] ShapeOf(string name)
        {
            if (_shapes.TryGetValue(name, out var v)) return v;
            _fit ??= new HandFit(_pose);
            var m = (float[])_ready.Clone();
            if (Handshapes.SetFingers(m, "Right", name)) _fit.Thumb(m, Handshapes.SpotOf(name));
            else m = null;
            _pose.Apply(_ready);
            return _shapes[name] = m;
        }

        // Forms the handshapes over the sign: from the frame the hand has risen 30% of the way to its highest to the
        // frame it has dropped back as far, easing in and out over a few frames either side and from one handshape to
        // the next; the tracked fingers stay for the hand coming up and going down. False if a handshape is unknown.
        // The non-dominant (left) arm, hand and fingers at the resting pose in every frame.
        public void RestOtherArm(SignDictionary.Sign sign)
        {
            for (var k = 0; k < SignPose.Muscles; k++)
            {
                var name = HumanTrait.MuscleName[k];
                if (!name.StartsWith("Left ") || name.Contains("Leg") || name.Contains("Foot") || name.Contains("Toes")) continue;
                for (var f = 0; f < sign.Frames; f++) sign.muscles[f * SignPose.Muscles + k] = _ready[k];
            }
        }

        // The skin gap between the hands in each frame (HandsApart's capsules), null without them.
        public float[] Gaps(SignDictionary.Sign sign)
        {
            if (_pose.Apart == null) return null;
            var n = sign.Frames;
            var gaps = new float[n];
            var m = new float[SignPose.Muscles];
            for (var f = 0; f < n; f++)
            {
                System.Array.Copy(sign.muscles, f * SignPose.Muscles, m, 0, SignPose.Muscles);
                _pose.Apply(m);
                gaps[f] = -_pose.Apart.Overlap();
            }
            _pose.Apply(_ready);
            return gaps;
        }

        // Frames whose hands touched (gap under TouchGap) and drifted apart since get the arms brought back in to the old gap.
        const float TouchGap = 0.012f;
        public (int frames, float before, float after) Retouch(SignDictionary.Sign sign, float[] gaps)
        {
            var n = sign.Frames;
            var m = new float[SignPose.Muscles];
            var pose = new HumanPose();
            int count = 0;
            float worstBefore = 0f, worstAfter = 0f;
            for (var f = 0; f < n && f < gaps.Length; f++)
            {
                if (gaps[f] > TouchGap) continue;
                System.Array.Copy(sign.muscles, f * SignPose.Muscles, m, 0, SignPose.Muscles);
                _pose.Apply(m);
                var gap = -_pose.Apart.Overlap();
                var want = Mathf.Max(gaps[f], HandsApart.Margin);
                if (gap < want + 0.003f) continue;
                worstBefore = Mathf.Max(worstBefore, gap);
                _pose.Apart.Touch((gap - want) / Mathf.Max(1e-4f, gap - HandsApart.Margin));
                _pose.Apart.Apply();
                worstAfter = Mathf.Max(worstAfter, -_pose.Apart.Overlap());
                _handler.GetHumanPose(ref pose);
                for (var k = 0; k < SignPose.Muscles; k++)
                {
                    var name = HumanTrait.MuscleName[k];
                    if (IsArm(k) || (name.Contains("Shoulder") && (name.StartsWith("Left") || name.StartsWith("Right"))))
                        sign.muscles[f * SignPose.Muscles + k] = Mathf.Clamp(pose.muscles[k], -1f, 1f);
                }
                count++;
            }
            _pose.Apply(_ready);
            return (count, worstBefore, worstAfter);
        }

        /// <param name="holdOnly">≥ 0: only where the fingers hold still, at most this strongly (fading to nothing as they
        /// move faster than <see cref="HoldMuscleSpeed"/>); &lt; 0: through the whole sign.</param>
        public bool Handshape(SignDictionary.Sign sign, IList<(string right, float from, float to, string left)> segs, float holdOnly = -1f)
        {
            foreach (var (r, _, _, l) in segs)
                if (ShapeOf(r) == null || (l.Length > 0 && ShapeOf(l) == null)) return false;
            var n = sign.Frames;
            if (n < 4) return true;
            var both = LeftSigns(sign); // a left hand that only rests keeps its fingers
            var h = new float[n];
            var m = new float[SignPose.Muscles];
            for (var f = 0; f < n; f++)
            {
                System.Array.Copy(sign.muscles, f * SignPose.Muscles, m, 0, SignPose.Muscles);
                _pose.Apply(m);
                h[f] = Height(HumanBodyBones.RightHand);
            }
            _pose.Apply(_ready);
            var peak = h.Max();
            int a = 0, b = n - 1;
            if (peak - h[0] > 0.1f) while (a < n - 1 && h[a] < h[0] + 0.3f * (peak - h[0])) a++;
            if (peak - h[n - 1] > 0.1f) while (b > a && h[b] < h[n - 1] + 0.3f * (peak - h[n - 1])) b--;
            var ease = Mathf.Max(2, n / 15);
            var fingers = new List<(int right, int left)>();
            for (var k = 0; k < SignPose.Muscles; k++)
                if (IsFinger(HumanTrait.MuscleName[k], "Right")) fingers.Add((k, SignPose.Index("Left " + HumanTrait.MuscleName[k].Substring(6))));
            // Holds: frames where the right hand's fingers move slowly (mean speed over its finger muscles, units/s),
            // smoothed over about 0.1 s so the shape eases in and out.
            float[] still = null;
            if (holdOnly >= 0f)
            {
                var speed = new float[n];
                for (var f = 0; f < n; f++)
                {
                    int p = Mathf.Max(0, f - 1), q = Mathf.Min(n - 1, f + 1);
                    var sum = 0f;
                    foreach (var (r, _) in fingers) sum += Mathf.Abs(sign.muscles[q * SignPose.Muscles + r] - sign.muscles[p * SignPose.Muscles + r]);
                    speed[f] = sum / Mathf.Max(1, fingers.Count) * sign.fps / Mathf.Max(1, q - p);
                }
                var raw = speed.Select(v => Mathf.Clamp01((2f * HoldMuscleSpeed - v) / HoldMuscleSpeed)).ToArray();
                var r3 = Mathf.Max(1, Mathf.RoundToInt(0.05f * sign.fps));
                still = new float[n];
                for (var f = 0; f < n; f++)
                {
                    float sum = 0; var c = 0;
                    for (var g = Mathf.Max(0, f - r3); g <= Mathf.Min(n - 1, f + r3); g++) { sum += raw[g]; c++; }
                    still[f] = holdOnly * sum / c;
                }
            }
            for (var f = 0; f < n; f++)
            {
                var w = Mathf.Clamp01((f - (a - ease)) / (float)ease) * Mathf.Clamp01((b + ease - f) / (float)ease);
                if (still != null) w *= still[f];
                if (w <= 0f) continue;
                var u = b > a ? Mathf.Clamp01((f - a) / (float)(b - a)) : 0.5f;
                var i = 0;
                while (i < segs.Count - 1 && u >= segs[i].to) i++;
                // Into the next handshape over the last and first 6% of two neighbouring ones.
                var j = Mathf.Min(i + 1, segs.Count - 1);
                var k = j == i ? 0f : Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((u - (segs[i].to - 0.06f)) / 0.12f));
                if (i > 0 && u < segs[i].from + 0.06f)
                {
                    j = i; i--;
                    k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((u - (segs[i].to - 0.06f)) / 0.12f));
                }
                float[] ra = ShapeOf(segs[i].right), rb = ShapeOf(segs[j].right);
                float[] la = both && segs[i].left.Length > 0 ? ShapeOf(segs[i].left) : null, lb = both && segs[j].left.Length > 0 ? ShapeOf(segs[j].left) : null;
                var at = f * SignPose.Muscles;
                foreach (var (r, l) in fingers)
                {
                    sign.muscles[at + r] = Mathf.Lerp(sign.muscles[at + r], Mathf.Lerp(ra[r], rb[r], k), w);
                    if (l >= 0 && la != null && lb != null)
                        sign.muscles[at + l] = Mathf.Lerp(sign.muscles[at + l], Mathf.Lerp(la[r], lb[r], k), w);
                }
            }
            return true;
        }

        // Linguistic rules for the fingers (after SGNify). hold: each hand keeps one handshape through the sign, every
        // finger muscle at its median over the sign (where the right hand is raised, as Handshape finds it), eased in
        // and out; same: the other hand makes the dominant hand's shape (held, or frame by frame without hold). A left
        // hand that only rests keeps its fingers. Returns how far the fingers moved, for the log.
        public string Priors(SignDictionary.Sign sign, bool hold, bool same)
        {
            var n = sign.Frames;
            if (n < 4) return "too short";
            var both = LeftSigns(sign);
            if (!hold && !(same && both)) return "nothing to do";
            var h = new float[n];
            var m = new float[SignPose.Muscles];
            for (var f = 0; f < n; f++)
            {
                System.Array.Copy(sign.muscles, f * SignPose.Muscles, m, 0, SignPose.Muscles);
                _pose.Apply(m);
                h[f] = Height(HumanBodyBones.RightHand);
            }
            _pose.Apply(_ready);
            var peak = h.Max();
            int a = 0, b = n - 1;
            if (peak - h[0] > 0.1f) while (a < n - 1 && h[a] < h[0] + 0.3f * (peak - h[0])) a++;
            if (peak - h[n - 1] > 0.1f) while (b > a && h[b] < h[n - 1] + 0.3f * (peak - h[n - 1])) b--;
            var ease = Mathf.Max(2, n / 15);
            var fingers = new List<(int right, int left)>();
            for (var k = 0; k < SignPose.Muscles; k++)
                if (IsFinger(HumanTrait.MuscleName[k], "Right")) fingers.Add((k, SignPose.Index("Left " + HumanTrait.MuscleName[k].Substring(6))));
            float Median(int k)
            {
                var v = new float[b - a + 1];
                for (var f = a; f <= b; f++) v[f - a] = sign.muscles[f * SignPose.Muscles + k];
                System.Array.Sort(v);
                return v[v.Length / 2];
            }
            var heldR = fingers.Select(x => Median(x.right)).ToArray();
            var heldL = fingers.Select(x => x.left >= 0 ? Median(x.left) : 0f).ToArray();
            float moved = 0f;
            var count = 0;
            for (var f = 0; f < n; f++)
            {
                var w = Mathf.Clamp01((f - (a - ease)) / (float)ease) * Mathf.Clamp01((b + ease - f) / (float)ease);
                if (w <= 0f) continue;
                var at = f * SignPose.Muscles;
                for (var i = 0; i < fingers.Count; i++)
                {
                    var (r, l) = fingers[i];
                    var right = sign.muscles[at + r];
                    if (hold)
                    {
                        var to = Mathf.Lerp(right, heldR[i], w);
                        moved += Mathf.Abs(to - right); count++;
                        sign.muscles[at + r] = to;
                    }
                    if (l < 0 || !both) continue;
                    var left = sign.muscles[at + l];
                    var target = same ? (hold ? heldR[i] : right) : heldL[i];
                    var toL = Mathf.Lerp(left, target, w);
                    moved += Mathf.Abs(toL - left); count++;
                    sign.muscles[at + l] = toL;
                }
            }
            return $"fingers moved {moved / Mathf.Max(1, count):0.00} on average{(both ? "" : " (left hand rests)")}";
        }

        // Marks the sign's core (see SignDictionary.Sign.coreStart): the frames with the hands at least 70% of the
        // way from where the clip starts or ends up to their highest. A clip whose hands don't rise into the sign
        // keeps no core. At most 30% is cut at either end.
        public void Core(SignDictionary.Sign sign)
        {
            var n = sign.Frames;
            sign.coreStart = sign.coreEnd = 0;
            if (n < 8) return;
            var both = LeftSigns(sign);
            var m = new float[SignPose.Muscles];
            var h = new float[n];
            for (var f = 0; f < n; f++)
            {
                System.Array.Copy(sign.muscles, f * SignPose.Muscles, m, 0, SignPose.Muscles);
                _pose.Apply(m);
                h[f] = both ? Mathf.Max(Height(HumanBodyBones.RightHand), Height(HumanBodyBones.LeftHand)) : Height(HumanBodyBones.RightHand);
            }
            _pose.Apply(_ready);
            var peak = h.Max();
            int a = 0, b = n - 1;
            var lineA = h[0] + 0.7f * (peak - h[0]);
            var lineB = h[n - 1] + 0.7f * (peak - h[n - 1]);
            if (peak - h[0] > 0.1f) while (a < n - 1 && h[a] < lineA) a++;
            if (peak - h[n - 1] > 0.1f) while (b > a && h[b] < lineB) b--;
            if (CoreSpeed > 0f)
            {
                // Past the line the hands can still be rising into the sign or already falling out of it (a short clip
                // drops 30 cm in two frames): those frames play as a dip between signs, so go on to where that move
                // slows to the sign's own pace.
                var step = CoreSpeed / Mathf.Max(1f, sign.fps);
                var rose = a > 0;
                var fell = b < n - 1;
                if (rose) while (a < b - 1 && h[a + 1] - h[a] > step) a++;
                if (fell) while (b > a + 1 && h[b - 1] - h[b] > step) b--;
                a = Mathf.Min(a, (int)(0.3f * n));
                b = Mathf.Max(b, n - 1 - (int)(0.3f * n));
            }
            else
            {
                a = Mathf.Min(Mathf.Max(0, a - 1), (int)(0.3f * n));
                b = Mathf.Max(Mathf.Min(n - 1, b + 1), n - 1 - (int)(0.3f * n));
            }
            if (a == 0 && b == n - 1) return;
            sign.coreStart = a;
            sign.coreEnd = b;
        }

        // SMPL-X joints (SignAvatars: 3D fits to sign videos) → avatar bones. Body pose index = SMPL-X joint - 1.
        static readonly (HumanBodyBones bone, int joint, int parent)[] SmplxArms =
        {
            (HumanBodyBones.LeftShoulder, 13, -1), (HumanBodyBones.LeftUpperArm, 16, 13), (HumanBodyBones.LeftLowerArm, 18, 16), (HumanBodyBones.LeftHand, 20, 18),
            (HumanBodyBones.RightShoulder, 14, -1), (HumanBodyBones.RightUpperArm, 17, 14), (HumanBodyBones.RightLowerArm, 19, 17), (HumanBodyBones.RightHand, 21, 19),
        };
        // Each hand's 15 joints, in SMPL-X order: index, middle, little (pinky), ring, thumb; three joints each.
        static readonly string[] SmplxFingers = { "Index", "Middle", "Little", "Ring", "Thumb" };
        static readonly string[] Phalanges = { "Proximal", "Intermediate", "Distal" };

        // Debugging the SMPL-X retarget: the avatar's right hand/elbow (cm from its chest: right, up, forward) at DebugFrame,
        // from the joint rotations and again after the round trip through muscles. SignImporter.DebugSmplx(path, frame).
        internal static int DebugFrame = -1;
        // Debug: per frame and hand (0 right, 1 left), degrees the muscles move the hand from the SMPL-X rotations: in all,
        // of which twist about the forearm, and how far they move the forearm. RetargetSmplx fills it when not null.
        internal static List<(int hand, float total, float twist, float forearm)> HandErr;
        internal static readonly System.Text.StringBuilder DebugLog = new();
        string Local(HumanBodyBones b)
        {
            var chest = Bone(HumanBodyBones.UpperChest) ?? Bone(HumanBodyBones.Chest);
            var v = Quaternion.Inverse(_go.transform.rotation) * (Bone(b).position - chest.position) * 100f;
            return $"({v.x:F0}, {v.y:F0}, {v.z:F0})";
        }

        Dictionary<HumanBodyBones, Quaternion> _tpose; // world rotations in the avatar's T-pose (SMPL-X's rest pose)
        Dictionary<HumanBodyBones, Vector3> _tposeDir;

        // The bone an arm, hand or finger bone points at (null: the last of its chain).
        static HumanBodyBones? Child(HumanBodyBones b)
        {
            var n = b.ToString();
            string next = n.EndsWith("Shoulder") ? n[..^8] + "UpperArm" : n.EndsWith("UpperArm") ? n[..^8] + "LowerArm"
                : n.EndsWith("LowerArm") ? n[..^8] + "Hand" : n.EndsWith("Hand") ? n[..^4] + "MiddleProximal"
                : n.EndsWith("Proximal") ? n[..^8] + "Intermediate" : n.EndsWith("Intermediate") ? n[..^12] + "Distal" : null;
            return next != null && System.Enum.TryParse<HumanBodyBones>(next, out var c) ? c : null;
        }

        /// <summary>SAM 3D Body's own skeleton (sam3d_pose.py, "mhr"): every arm, hand and finger bone takes the signer's
        /// rotation directly; no points re-fitted, no handshape snapping. The calibration between the two skeletons is
        /// once per bone: the avatar's T-pose bone turned onto the direction MHR's rest (A-)pose bone points.</summary>
        SignDictionary.Sign RetargetMhr(JObject json)
        {
            if (_tpose == null) CaptureTPose();
            var mhr = (JObject)json["mhr"];
            var names = mhr["bones"].ToObject<string[]>();
            var rot = mhr["rot"].ToObject<float[][][]>();
            var restDir = mhr["restDir"].ToObject<float[][]>();
            var bones = new HumanBodyBones[names.Length];
            var rest = new Quaternion[names.Length];
            for (var i = 0; i < names.Length; i++)
            {
                bones[i] = System.Enum.Parse<HumanBodyBones>(names[i]);
                var d = new Vector3(restDir[i][0], restDir[i][1], restDir[i][2]);
                rest[i] = _tpose.TryGetValue(bones[i], out var tp)
                    ? (_tposeDir.TryGetValue(bones[i], out var td) ? Quaternion.FromToRotation(td, d) * tp : tp)
                    : Quaternion.identity;
            }
            var chest = Bone(HumanBodyBones.UpperChest) != null ? HumanBodyBones.UpperChest : HumanBodyBones.Chest;
            var n = rot.Length;
            var pos = mhr["pos"]?.ToObject<float[][][]>();
            var joints = mhr["joints"]?.ToObject<string[]>();
            if (pos == null || joints == null || pos.Length != n) pos = null;
            var nearFace = mhr["nearFace"]?.ToObject<float[][]>();
            if (nearFace != null && nearFace.Length != n) nearFace = null;
            var touch = mhr["handsTouch"]?.ToObject<float[]>();
            if (touch != null && touch.Length != n) touch = null;
            var muscles = new float[n * SignPose.Muscles];
            var pose = new HumanPose();
            var heights = new float[n];
            for (var f = 0; f < n; f++)
            {
                _pose.Apply(_ready);
                var toWorld = Bone(chest).rotation * Quaternion.Inverse(_tpose[chest]);
                for (var i = 0; i < bones.Length; i++)   // parents before children
                {
                    if (Bone(bones[i]) is not Transform t) continue;
                    var q = rot[f][i];
                    t.rotation = toWorld * new Quaternion(q[0], q[1], q[2], q[3]) * rest[i];
                }
                if (MhrPlaceWrists && pos != null) PlaceWrists(pos[f], joints, toWorld, nearFace?[f]);
                // hands that touch in the video touch here (proportions and placement open small gaps); none pass through
                if (touch != null) _pose.Apart?.Touch(touch[f]);
                _pose.Apart?.Apply();
                if (MhrLog != null && _pose.Apart != null) MhrLog.Append((-_pose.Apart.Overlap() * 1000f).ToString("0")).Append(touch != null ? "/" + touch[f].ToString("0.0") : "").Append(' ');
                heights[f] = Mathf.Max(Height(HumanBodyBones.RightHand), Height(HumanBodyBones.LeftHand));
                _handler.GetHumanPose(ref pose);
                for (var m = 0; m < SignPose.Muscles; m++)
                {
                    var name = HumanTrait.MuscleName[m];
                    var arm = IsArm(m) || (name.Contains("Shoulder") && (name.StartsWith("Left") || name.StartsWith("Right")));
                    muscles[f * SignPose.Muscles + m] = arm ? pose.muscles[m] : _ready[m];
                }
            }
            var sign = new SignDictionary.Sign
            {
                fps = json.Value<float>("fps"), muscles = muscles, face = Face(json, n), credit = json.Value<string>("credit") ?? "",
            };
            TrimLow(sign, heights);
            return sign;
        }

        // Each wrist where the signer's is (SAM's joints, "mhr.pos": from the middle of the shoulders, chest-relative),
        // scaled by torso length (shoulder joints to hip joints) and turned with the chest like the bones. The arm reaches it
        // with its elbow aimed at the signer's elbow, the hand keeping its turn.
        void PlaceWrists(float[][] frame, string[] joints, Quaternion toWorld, float[] nearFace)
        {
            Vector3 J(string name) { var k = System.Array.IndexOf(joints, name); return k < 0 ? Vector3.zero : new Vector3(frame[k][0], frame[k][1], frame[k][2]); }
            var aMid = (Bone(HumanBodyBones.LeftUpperArm).position + Bone(HumanBodyBones.RightUpperArm).position) * 0.5f;
            var aHip = (Bone(HumanBodyBones.LeftUpperLeg).position + Bone(HumanBodyBones.RightUpperLeg).position) * 0.5f;
            var sHip = (J("l_upleg") + J("r_upleg")) * 0.5f;
            if (sHip.sqrMagnitude < 1e-6f) return;
            var scale = (aMid - aHip).magnitude / sHip.magnitude;
            Vector3 At(string name) => aMid + toWorld * (J(name) * scale);
            // A hand at the face goes where the signer's is against their HEAD (eyes, chin): the avatar's head isn't the
            // signer's size or height above the shoulders, so a torso-relative hand misses the chin or forehead.
            var head = HeadMap(J, out var sEyes, out var aEyes, out var headScale);
            Vector3 Wrist(string name, int side)
            {
                var t = At(name);
                var w = nearFace != null && side < nearFace.Length ? nearFace[side] : 0f;
                if (w <= 0f || head == null) return t;
                return Vector3.Lerp(t, aEyes + head.Value * ((J(name) - sEyes) * headScale), w);
            }
            ReachTo(HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand, Wrist("r_wrist", 0), At("r_lowarm"));
            ReachTo(HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand, Wrist("l_wrist", 1), At("l_lowarm"));
        }

        // Signer's head frame (eyes across, neck → eyes up; in "mhr.pos" space) → the avatar's (its eye bones and neck).
        Quaternion? HeadMap(System.Func<string, Vector3> J, out Vector3 sEyes, out Vector3 aEyes, out float scale)
        {
            sEyes = (J("r_eye") + J("l_eye")) * 0.5f;
            aEyes = Vector3.zero; scale = 1f;
            Transform le = Bone(HumanBodyBones.LeftEye), re = Bone(HumanBodyBones.RightEye), neck = Bone(HumanBodyBones.Neck);
            var sUp = sEyes - J("c_neck");
            if (le == null || re == null || neck == null || sUp.sqrMagnitude < 1e-6f || (J("r_eye") - J("l_eye")).sqrMagnitude < 1e-8f) return null;
            aEyes = (le.position + re.position) * 0.5f;
            scale = (aEyes - neck.position).magnitude / sUp.magnitude;
            return Frame(re.position - le.position, aEyes - neck.position) * Quaternion.Inverse(Frame(J("r_eye") - J("l_eye"), sUp));
        }

        // Two-bone reach: the wrist onto target, the elbow on the side of the shoulder→wrist line the pole (the signer's
        // elbow) is on, the hand keeping its turn.
        void ReachTo(HumanBodyBones upperBone, HumanBodyBones lowerBone, HumanBodyBones handBone, Vector3 target, Vector3 pole)
        {
            Transform upper = Bone(upperBone), lower = Bone(lowerBone), hand = Bone(handBone);
            if (upper == null || lower == null || hand == null) return;
            var handRot = hand.rotation;
            Vector3 s = upper.position, e = lower.position, w = hand.position;
            float a = Vector3.Distance(s, e), b = Vector3.Distance(e, w);
            var toT = target - s;
            var d = Mathf.Clamp(toT.magnitude, Mathf.Abs(a - b) + 1e-3f, a + b - 1e-3f);
            var u = toT.normalized;
            var x = (a * a - b * b + d * d) / (2f * d);
            var r = Mathf.Sqrt(Mathf.Max(0f, a * a - x * x));
            var side = Vector3.ProjectOnPlane(pole - s, u);
            if (side.sqrMagnitude < 1e-8f) side = Vector3.ProjectOnPlane(e - s, u);
            var elbow = s + u * x + side.normalized * r;
            upper.rotation = Quaternion.FromToRotation(e - s, elbow - s) * upper.rotation;
            lower.rotation = Quaternion.FromToRotation(hand.position - lower.position, s + u * d - lower.position) * lower.rotation;
            hand.rotation = handRot;
        }

        // The avatar's T-pose, from its import settings (the rest pose SMPL-X rotations are relative to).
        void CaptureTPose()
        {
            var skeleton = ((ModelImporter)AssetImporter.GetAtPath(ModelPath)).humanDescription.skeleton;
            var byName = new Dictionary<string, Quaternion>();
            foreach (var b in skeleton) byName[b.name] = b.rotation;
            foreach (var t in _go.GetComponentsInChildren<Transform>(true))
                if (t != _go.transform && byName.TryGetValue(t.name, out var r)) t.localRotation = r;
            _tpose = new Dictionary<HumanBodyBones, Quaternion>();
            for (var b = HumanBodyBones.Hips; b < HumanBodyBones.LastBone; b++)
                if (Bone(b) is Transform t) _tpose[b] = t.rotation;
            // Each bone's pointing direction in the T-pose (towards its child; the last finger bones point on).
            _tposeDir = new Dictionary<HumanBodyBones, Vector3>();
            for (var b = HumanBodyBones.Hips; b < HumanBodyBones.LastBone; b++)
            {
                if (Bone(b) is not Transform t) continue;
                if (Child(b) is HumanBodyBones c && Bone(c) is Transform ct) _tposeDir[b] = (ct.position - t.position).normalized;
                else if (_tipDir.TryGetValue(b, out var tip)) _tposeDir[b] = t.TransformDirection(tip).normalized;
            }
            _pose.Apply(_ready);
        }

        static Quaternion AxisAngle(JToken pose, int joint)
        {
            var v = new Vector3((float)pose[joint * 3], (float)pose[joint * 3 + 1], (float)pose[joint * 3 + 2]);
            var angle = v.magnitude;
            return angle < 1e-6f ? Quaternion.identity : Quaternion.AngleAxis(angle * Mathf.Rad2Deg, v / angle);
        }

        static readonly Quaternion UprightToCamera = Quaternion.AngleAxis(-180f, Vector3.right);

        // SMPL-X's x is the signer's left, the avatar's x its right: mirror the rotation across x.
        static Quaternion Mirror(Quaternion q) => new(q.x, -q.y, -q.z, q.w);

        SignDictionary.Sign RetargetSmplx(JObject json)
        {
            if (_tpose == null) CaptureTPose();
            var body = (JArray)json["body"];
            JArray right = (JArray)json["right"], left = (JArray)json["left"], root = json["root"] as JArray;
            var n = body.Count;
            var chest = Bone(HumanBodyBones.UpperChest) != null ? HumanBodyBones.UpperChest : HumanBodyBones.Chest;
            var muscles = new float[n * SignPose.Muscles];
            var pose = new HumanPose();
            var q = new Dictionary<int, Quaternion>();
            var heights = new float[n];
            for (var f = 0; f < n; f++)
            {
                _pose.Apply(_ready);
                // Rotations relative to the signer's chest (SMPL-X joint 9): their leaning and turning don't carry over.
                // Rest bones are axis-aligned in SMPL-X, so a chain's product is the bone's rotation from its rest.
                var toWorld = Bone(chest).rotation * Quaternion.Inverse(_tpose[chest]);
                void Set(HumanBodyBones bone, Quaternion smplx)
                {
                    if (Bone(bone) is Transform t) t.rotation = toWorld * Mirror(smplx) * _tpose[bone];
                }
                q.Clear();
                // The arms as the camera saw them, relative to someone standing upright facing it (root = 180° about x):
                // fits lean the spine one way and the root the other (How2Sign's by ~35°), and on the avatar's upright
                // chest arms measured from a leaning one rise to its face. Fits without a root are upright already.
                var upright = root == null ? Quaternion.identity
                    : UprightToCamera * AxisAngle(root[f], 0) * AxisAngle(body[f], 2) * AxisAngle(body[f], 5) * AxisAngle(body[f], 8);
                foreach (var (bone, joint, parent) in SmplxArms)
                {
                    q[joint] = (parent < 0 ? upright : q[parent]) * AxisAngle(body[f], joint - 1);
                    Set(bone, q[joint]);
                }
                heights[f] = Mathf.Max(Height(HumanBodyBones.RightHand), Height(HumanBodyBones.LeftHand));
                foreach (var (hand, wrist, side) in new[] { (right, 21, "Right"), (left, 20, "Left") })
                    for (var finger = 0; finger < 5; finger++)
                    {
                        var chain = q[wrist];
                        for (var k = 0; k < 3; k++)
                        {
                            chain *= AxisAngle(hand[f], finger * 3 + k);
                            if (System.Enum.TryParse<HumanBodyBones>(side + SmplxFingers[finger] + Phalanges[k], out var bone)) Set(bone, chain);
                        }
                    }
                if (DebugFrame == f) DebugLog.Append($"rotations: hand {Local(HumanBodyBones.RightHand)} elbow {Local(HumanBodyBones.RightLowerArm)}\n");
                var before = HandErr == null ? null : new[] { Bone(HumanBodyBones.RightHand).rotation, Bone(HumanBodyBones.LeftHand).rotation,
                    Bone(HumanBodyBones.RightLowerArm).rotation, Bone(HumanBodyBones.LeftLowerArm).rotation };
                _handler.GetHumanPose(ref pose);
                for (var m = 0; m < SignPose.Muscles; m++)
                {
                    var name = HumanTrait.MuscleName[m];
                    var arm = IsArm(m) || (name.Contains("Shoulder") && (name.StartsWith("Left") || name.StartsWith("Right")));
                    muscles[f * SignPose.Muscles + m] = arm ? pose.muscles[m] : _ready[m];
                }
                if (HandErr != null)
                {
                    var check = new float[SignPose.Muscles];
                    System.Array.Copy(muscles, f * SignPose.Muscles, check, 0, check.Length);
                    _pose.Apply(check);
                    for (var i = 0; i < 2; i++)
                    {
                        var hb = i == 0 ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand;
                        var lb = i == 0 ? HumanBodyBones.RightLowerArm : HumanBodyBones.LeftLowerArm;
                        var d = Bone(hb).rotation * Quaternion.Inverse(before[i]); // the hand's world change
                        var axis = (Bone(hb).position - Bone(lb).position).normalized; // along the forearm
                        var p2 = Vector3.Dot(new Vector3(d.x, d.y, d.z), axis) * axis;
                        var twist = new Quaternion(p2.x, p2.y, p2.z, d.w).normalized;
                        HandErr.Add((i, Quaternion.Angle(Quaternion.identity, d), Quaternion.Angle(Quaternion.identity, twist),
                            Quaternion.Angle(Bone(lb).rotation, before[i + 2])));
                    }
                }
                if (DebugFrame == f)
                {
                    var check = new float[SignPose.Muscles];
                    System.Array.Copy(muscles, f * SignPose.Muscles, check, 0, check.Length);
                    _pose.Apply(check);
                    DebugLog.Append($"muscles:   hand {Local(HumanBodyBones.RightHand)} elbow {Local(HumanBodyBones.RightLowerArm)}\n");
                    var gloss = json["labels"][0].ToString().ToUpperInvariant();
                    var stored = AssetDatabase.LoadAssetAtPath<SignDictionary>(DictionaryPath).signs.Find(s => s.gloss == gloss);
                    if (stored != null)
                    {
                        var at = new float[SignPose.Muscles];
                        System.Array.Copy(_ready, at, at.Length);
                        stored.Sample(f / stored.fps, at);
                        _pose.Apply(at);
                        DebugLog.Append($"dictionary: hand {Local(HumanBodyBones.RightHand)} elbow {Local(HumanBodyBones.RightLowerArm)} (t {f / stored.fps:F2} s of {stored.Seconds:F2})\n");
                        foreach (var mn in new[] { "Right Shoulder Down-Up", "Right Arm Down-Up", "Right Arm Front-Back", "Right Forearm Stretch" })
                        {
                            var i = System.Array.IndexOf(HumanTrait.MuscleName, mn);
                            DebugLog.Append($"  {mn}: retarget {muscles[f * SignPose.Muscles + i]:F2} dictionary {at[i]:F2}\n");
                        }
                    }
                }
            }
            var sign = new SignDictionary.Sign { fps = json.Value<float>("fps"), muscles = muscles, credit = json.Value<string>("credit") ?? "" };
            TrimLow(sign, heights);
            return sign;
        }

        static readonly bool[] ArmMuscle = BuildArmMask();
        // Humanoid muscles already (3D-LEX motion capture, read from its FBX by tools/signs/lex3d_export.cs): the arms,
        // shoulders and fingers carry over, the rest of the body stays at the ready pose as for the other sources.
        SignDictionary.Sign FromMuscles(JObject json)
        {
            var src = json["muscles"].ToObject<float[][]>();
            var n = src.Length;
            var muscles = new float[n * SignPose.Muscles];
            var heights = new float[n];
            var m95 = new float[SignPose.Muscles];
            for (var f = 0; f < n; f++)
            {
                for (var m = 0; m < SignPose.Muscles; m++)
                {
                    var name = HumanTrait.MuscleName[m];
                    var arm = IsArm(m) || (name.Contains("Shoulder") && (name.StartsWith("Left") || name.StartsWith("Right")));
                    m95[m] = arm && m < src[f].Length ? src[f][m] : _ready[m];
                }
                System.Array.Copy(m95, 0, muscles, f * SignPose.Muscles, SignPose.Muscles);
                _pose.Apply(m95);
                heights[f] = Mathf.Max(Height(HumanBodyBones.RightHand), Height(HumanBodyBones.LeftHand));
            }
            var sign = new SignDictionary.Sign
            {
                fps = json.Value<float>("fps"), muscles = muscles, face = Face(json, n), credit = json.Value<string>("credit") ?? "",
            };
            TrimLow(sign, heights, 1f / 6f);
            return sign;
        }

        static bool IsArm(int m) => ArmMuscle[m];
        static bool IsFinger(string muscle, string side) => muscle.StartsWith(side + " ") &&
            (muscle.Contains("Thumb") || muscle.Contains("Index") || muscle.Contains("Middle") || muscle.Contains("Ring") || muscle.Contains("Little"));

        static bool[] BuildArmMask()
        {
            var mask = new bool[SignPose.Muscles];
            for (var m = 0; m < mask.Length; m++)
            {
                var name = HumanTrait.MuscleName[m];
                mask[m] = (name.StartsWith("Left ") || name.StartsWith("Right "))
                          && !name.Contains("Leg") && !name.Contains("Foot") && !name.Contains("Toes") && !name.Contains("Shoulder");
            }
            return mask;
        }

        // Two-bone IK: the upper arm and forearm directions that put the wrist at reach from the shoulder, the elbow
        // bent towards pole.
        (Vector3, Vector3) Reach(HumanBodyBones upper, HumanBodyBones lower, HumanBodyBones hand, Vector3 reach, Vector3 pole)
        {
            float a = Vector3.Distance(Bone(upper).position, Bone(lower).position);
            float b = Vector3.Distance(Bone(lower).position, Bone(hand).position);
            var d = Mathf.Clamp(reach.magnitude, Mathf.Abs(a - b) + 1e-3f, a + b - 1e-3f);
            var t = reach.normalized;
            var n = Vector3.ProjectOnPlane(pole, t);
            if (n.sqrMagnitude < 1e-6f) n = Vector3.ProjectOnPlane(Vector3.down, t);
            n.Normalize();
            var x = (a * a - b * b + d * d) / (2 * d);
            var elbow = t * x + n * Mathf.Sqrt(Mathf.Max(0, a * a - x * x));
            return (elbow, t * d - elbow);
        }

        // Upper arm and forearm along the signer's: the bend of the elbow in the same plane as theirs.
        void Arm(HumanBodyBones upper, HumanBodyBones lower, HumanBodyBones hand, Vector3 upperDir, Vector3 lowerDir, ref Vector3 lastBend)
        {
            Transform u = Bone(upper), l = Bone(lower), w = Bone(hand);
            var bend = Vector3.Cross(upperDir, lowerDir);
            // A straight arm has no bend plane: keep the last one.
            if (bend.magnitude < 0.15f * upperDir.magnitude * lowerDir.magnitude && lastBend != Vector3.zero) bend = lastBend;
            lastBend = bend.normalized;
            var curUpper = l.position - u.position;
            var curBend = Vector3.Cross(curUpper, w.position - l.position);
            if (curBend.sqrMagnitude < 1e-8f || bend.sqrMagnitude < 1e-8f)
                u.rotation = Quaternion.FromToRotation(curUpper, upperDir) * u.rotation;
            else
                u.rotation = Quaternion.LookRotation(upperDir, bend) * Quaternion.Inverse(Quaternion.LookRotation(curUpper, curBend)) * u.rotation;
            l.rotation = Quaternion.FromToRotation(w.position - l.position, lowerDir) * l.rotation;
        }

        static Vector3[] Landmarks(JArray hand, int f, Quaternion map)
        {
            if (hand == null || f >= hand.Count || hand[f] is not JArray h || h.Count != 21) return null;
            var lm = new Vector3[21];
            for (var k = 0; k < 21; k++) lm[k] = map * new Vector3((float)h[k][0], (float)h[k][1], (float)h[k][2]);
            return lm;
        }

        static readonly string[] FourFingers = { "Index", "Middle", "Ring", "Little" };

        // A finger's three bends (degrees, as Measure gives them) in the states signers' fingers take: straight, a
        // little bent, closed, bent at the knuckle only, curled tight. Clusters of every right-hand frame in the pose
        // files (tools/signs, 55k fingers).
        static readonly Vector3[] FingerStates =
        {
            new(0, 3, 1), new(39, 22, 14), new(63, 75, 43), new(102, 36, 24), new(29, 141, 60),
        };
        static readonly string[] FiveFingers = { "Thumb", "Index", "Middle", "Ring", "Little" };

        // Hand features both the landmarks and the avatar's own hand are measured by: each finger joint's bend (degrees,
        // signed, across the palm's knuckle line), each finger's sideways angle from the middle finger's metacarpal in
        // the palm's plane, the thumb's two outer bends, and the thumb's metacarpal direction in the hand's frame.
        static Dictionary<string, float> Measure(Vector3[] lm, out Vector3 thumbDir, bool left)
        {
            var o = new Dictionary<string, float>();
            var across = (lm[17] - lm[5]).normalized;
            var normal = Vector3.Cross(lm[5] - lm[0], lm[17] - lm[0]).normalized;
            float Bend(Vector3 u, Vector3 v) => Vector3.SignedAngle(Vector3.ProjectOnPlane(u, across), Vector3.ProjectOnPlane(v, across), across);
            var mid = Vector3.ProjectOnPlane(lm[9] - lm[0], normal);
            for (var k = 0; k < 4; k++)
            {
                int p = 5 + 4 * k;
                // A left hand is a right hand mirrored, which turns its bends the other way: flipped back, so a bent
                // finger reads positive on either hand (the clamps and finger states below assume it).
                var sign = left ? -1f : 1f;
                o[$"{FourFingers[k]} 1 Stretched"] = sign * Bend(lm[p] - lm[0], lm[p + 1] - lm[p]);
                o[$"{FourFingers[k]} 2 Stretched"] = sign * Bend(lm[p + 1] - lm[p], lm[p + 2] - lm[p + 1]);
                o[$"{FourFingers[k]} 3 Stretched"] = sign * Bend(lm[p + 2] - lm[p + 1], lm[p + 3] - lm[p + 2]);
                o[$"{FourFingers[k]} Spread"] = Vector3.SignedAngle(mid, Vector3.ProjectOnPlane(lm[p + 1] - lm[p], normal), normal);
            }
            o["Thumb 2 Stretched"] = Vector3.Angle(lm[2] - lm[1], lm[3] - lm[2]);
            o["Thumb 3 Stretched"] = Vector3.Angle(lm[3] - lm[2], lm[4] - lm[3]);
            thumbDir = Quaternion.Inverse(Quaternion.LookRotation(lm[9] - lm[0], normal)) * (lm[2] - lm[1]).normalized;
            return o;
        }

        // The avatar's hand as 21 landmarks in MediaPipe's order (each finger's tip a bone's length on from its last joint).
        Vector3[] AvatarLandmarks(string side)
        {
            var lm = new Vector3[21];
            lm[0] = Bone(side == "Right" ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand).position;
            for (var k = 0; k < 5; k++)
            {
                var p = 1 + 4 * k;
                Transform Get(string ph) => Bone(System.Enum.Parse<HumanBodyBones>(side + FiveFingers[k] + ph));
                Transform a = Get("Proximal"), b = Get("Intermediate"), c = Get("Distal");
                lm[p] = a.position; lm[p + 1] = b.position; lm[p + 2] = c.position;
                lm[p + 3] = c.position + c.TransformDirection(_tipDir[System.Enum.Parse<HumanBodyBones>(side + FiveFingers[k] + "Distal")])
                            * Vector3.Distance(b.position, c.position) * 0.85f;
            }
            return lm;
        }

        // Each muscle's feature on this avatar at muscle values -1…1 (the rest of the hand as in the ready pose), and
        // for the thumb's base, which two muscles turn together, its direction over a grid of both.
        const int Steps = 17;
        static float StepValue(int i) => -1f + 2f * i / (Steps - 1);
        readonly Dictionary<string, float[]> _curve = new();
        readonly Dictionary<string, List<(float spread, float stretch, Vector3 dir)>> _thumbGrid = new();

        void Calibrate(string side)
        {
            var m = (float[])_ready.Clone();
            foreach (var name in Measure(AvatarLandmarks(side), out _, side == "Left").Keys)
            {
                var i = SignPose.Index($"{side} {name}");
                if (i < 0) continue;
                var curve = new float[Steps];
                for (var s = 0; s < Steps; s++)
                {
                    System.Array.Copy(_ready, m, m.Length);
                    m[i] = StepValue(s);
                    _pose.Apply(m);
                    curve[s] = Measure(AvatarLandmarks(side), out _, side == "Left")[name];
                }
                _curve[$"{side} {name}"] = curve;
            }
            int sp = SignPose.Index($"{side} Thumb Spread"), st = SignPose.Index($"{side} Thumb 1 Stretched");
            var grid = _thumbGrid[side] = new List<(float, float, Vector3)>();
            for (var a = 0; a < Steps; a++)
            for (var b = 0; b < Steps; b++)
            {
                System.Array.Copy(_ready, m, m.Length);
                m[sp] = StepValue(a);
                m[st] = StepValue(b);
                _pose.Apply(m);
                Measure(AvatarLandmarks(side), out var dir, side == "Left");
                grid.Add((m[sp], m[st], dir));
            }
            _pose.Apply(_ready);
        }

        /// <summary>For the import log: each calibrated feature's range on the avatar (muscle -1 → 1).</summary>
        public string CalibrationReport()
        {
            var sb = new System.Text.StringBuilder("Finger calibration (degrees at muscle -1 / 0 / 1):\n");
            foreach (var (name, c) in _curve)
                if (name.StartsWith("Right")) sb.AppendLine($"  {name}: {c[0]:F0} / {c[Steps / 2]:F0} / {c[Steps - 1]:F0}");
            return sb.ToString();
        }

        // The muscle value at which the avatar's curve reaches v (the nearest, past its ends).
        static float Invert(float[] curve, float v)
        {
            float best = float.MaxValue, at = 0f;
            for (var i = 0; i + 1 < curve.Length; i++)
            {
                float a = curve[i], b = curve[i + 1];
                var t = Mathf.Abs(b - a) < 1e-4f ? 0f : Mathf.Clamp01((v - a) / (b - a));
                var err = Mathf.Abs(Mathf.Lerp(a, b, t) - v);
                if (err < best - 1e-3f) { best = err; at = Mathf.Lerp(StepValue(i), StepValue(i + 1), t); }
            }
            return at;
        }

        // ── Range calibration ──────────────────────────────────────────────────────────────────────────────────────
        // A tracker reads a fist as a loose fist and a flat hand as a slightly bent one (MediaPipe learned finger depth
        // from synthetic hands; WiLoR less so), so the same angles on the avatar give a lazy claw. Each feature's
        // range over every pose file of a tracker (2nd–98th percentile, per side) is stretched onto the range the
        // avatar's own canonical handshapes span: the tracker's tightest fist becomes the avatar's fist.
        static readonly Dictionary<string, (float lo, float hi)> SourceRange = new();

        /// <summary>Measures every tracked hand in the pose files, per tracker ("hands": mediapipe/wilor) and side.</summary>
        public static string MeasureSources(IEnumerable<string> files)
        {
            SourceRange.Clear();
            var all = new Dictionary<string, List<float>>();
            foreach (var file in files)
            {
                var json = JObject.Parse(File.ReadAllText(file));
                if (json.Value<string>("format") == "smplx" || json["body"] == null) continue;
                var src = json.Value<string>("hands") ?? "mediapipe";
                foreach (var (key, side) in new[] { ("right", "Right"), ("left", "Left") })
                {
                    if (json[key] is not JArray hand) continue;
                    for (var f = 0; f < hand.Count; f++)
                    {
                        var lm = Landmarks(hand, f, Quaternion.identity);
                        if (lm == null) continue;
                        foreach (var (name, v) in Measure(lm, out _, side == "Left"))
                        {
                            // Spread is only measured where it means something (see ToMuscles); a joint read past
                            // what a hand can do is a tracking flip, not range.
                            if (name.EndsWith("Spread") || v < -60f || v > 150f) continue;
                            var k = $"{src} {side} {name}";
                            if (!all.TryGetValue(k, out var list)) all[k] = list = new List<float>();
                            list.Add(v);
                        }
                    }
                }
            }
            var sb = new System.Text.StringBuilder("Tracked finger ranges (2nd–98th percentile, degrees):\n");
            foreach (var (k, list) in all.OrderBy(x => x.Key))
            {
                if (list.Count < 200) continue;
                list.Sort();
                var r = (list[(int)(0.02f * (list.Count - 1))], list[(int)(0.98f * (list.Count - 1))]);
                SourceRange[k] = r;
                if (k.Contains(" Right ")) sb.AppendLine($"  {k}: {r.Item1:F0} … {r.Item2:F0}");
            }
            return sb.ToString();
        }

        // ── Canonical handshapes, measured on this avatar ─────────────────────────────────────────────────────────
        readonly List<(string name, Dictionary<string, float> right, Vector3 rightThumb, Dictionary<string, float> left, Vector3 leftThumb)> _templates = new();
        readonly Dictionary<string, (float lo, float hi)> _templateRange = new();

        void MeasureTemplates()
        {
            var fingers = new List<(int right, int left)>();
            for (var k = 0; k < SignPose.Muscles; k++)
                if (IsFinger(HumanTrait.MuscleName[k], "Right")) fingers.Add((k, SignPose.Index("Left " + HumanTrait.MuscleName[k].Substring(6))));
            foreach (var name in Handshapes.Names)
            {
                var m = ShapeOf(name);
                if (m == null) continue;
                var both = (float[])m.Clone();
                foreach (var (r, l) in fingers) if (l >= 0) both[l] = m[r];
                _pose.Apply(both);
                var right = Measure(AvatarLandmarks("Right"), out var rt, false);
                var left = Measure(AvatarLandmarks("Left"), out var lt, true);
                _templates.Add((name, right, rt, left, lt));
            }
            _pose.Apply(_ready);
            foreach (var t in _templates)
            foreach (var (side, fs) in new[] { ("Right", t.right), ("Left", t.left) })
            foreach (var (name, v) in fs)
            {
                var k = $"{side} {name}";
                _templateRange[k] = _templateRange.TryGetValue(k, out var r) ? (Mathf.Min(r.lo, v), Mathf.Max(r.hi, v)) : (v, v);
            }
        }

        public string DebugHands(JObject json, int frame, string[] names)
        {
            var sb = new System.Text.StringBuilder();
            void Row(string label, Dictionary<string, float> fs, Vector3 thumb) =>
                sb.AppendLine($"{label,-10} " + string.Join(" ", fs.OrderBy(x => x.Key).Select(x => $"{x.Key.Replace(" Stretched", "").Replace("Spread", "Sp")}={x.Value:F0}")) + $" thumb={thumb.x:F2},{thumb.y:F2},{thumb.z:F2}");
            foreach (var side in new[] { "Right", "Left" })
            {
                foreach (var t in _templates) if (names.Contains(t.name)) Row($"{side[0]} {t.name}", side == "Right" ? t.right : t.left, side == "Right" ? t.rightThumb : t.leftThumb);
                var lm = Landmarks(json[side.ToLowerInvariant()] as JArray, frame, Quaternion.identity);
                if (lm == null) continue;
                Row($"{side[0]} raw", Measure(lm, out var th, side == "Left"), th);
                var (f, t2) = Features(lm, side, json.Value<string>("hands") ?? "mediapipe");
                Row($"{side[0]} mapped", f, t2);
            }
            return sb.ToString();
        }

        /// <summary>For the import log: the last sign's held handshapes (name and confidence), per hand.</summary>
        public string LastSnap = "";

        // A hand's features from its landmarks: measured as on the avatar, its range stretched onto the avatar's, the
        // last joints kept in step and each finger pulled a little towards the clear finger states.
        (Dictionary<string, float>, Vector3) Features(Vector3[] lm, string side, string source)
        {
            var features = Measure(lm, out var thumbDir, side == "Left");
            if (FingerRange > 0f)
                foreach (var name in features.Keys.ToList())
                {
                    if (!SourceRange.TryGetValue($"{source} {side} {name}", out var s) || !_templateRange.TryGetValue($"{side} {name}", out var t)) continue;
                    if (Mathf.Abs(s.hi - s.lo) < 5f || Mathf.Abs(t.hi - t.lo) < 5f) continue;
                    var u = (features[name] - s.lo) / (s.hi - s.lo);
                    // A little past either end, as the percentiles leave the most extreme 2% out.
                    var mapped = Mathf.LerpUnclamped(t.lo, t.hi, Mathf.Clamp(u, -0.1f, 1.1f));
                    features[name] = Mathf.Lerp(features[name], mapped, FingerRange);
                }
            Tidy(features);
            return (features, thumbDir);
        }

        // ── Template snap ─────────────────────────────────────────────────────────────────────────────────────────
        // Signs hold one handshape and move between them. Where the fingers are still (a hold), the hold's median hand
        // is matched to the nearest canonical handshape; each finger is then pulled onto it by how well the whole hand
        // matches and how near that finger already is, so a finger the tracker read clearly otherwise keeps its own
        // shape. Easing in and out over a few frames; the moves between holds stay as tracked.
        string SnapToTemplates((Dictionary<string, float> features, Vector3 thumb)[] hand, string side, float fps)
        {
            var n = hand.Length;
            // Held through frames where tracking lost the hand (frames before it is first seen stay empty).
            for (var f = 1; f < n; f++) if (hand[f].features == null && hand[f - 1].features != null) hand[f] = hand[f - 1];
            var first = System.Array.FindIndex(hand, h => h.features != null);
            if (TemplateSnap <= 0f || first < 0 || _templates.Count == 0) return "";
            if (fps <= 0f) fps = 30f;
            var flex = FourFingers.SelectMany(x => new[] { $"{x} 1 Stretched", $"{x} 2 Stretched", $"{x} 3 Stretched" }).ToArray();
            // Finger speed (degrees per second, mean over the flexion joints), as the lowest of 3 frames around.
            var raw = new float[n];
            for (var f = first; f < n; f++)
            {
                int a = Mathf.Max(first, f - 1), b = Mathf.Min(n - 1, f + 1);
                if (b == a) continue;
                raw[f] = flex.Average(k => Mathf.Abs(hand[b].features[k] - hand[a].features[k])) / (b - a) * fps;
            }
            var hold = new bool[n];
            for (var f = first; f < n; f++)
                hold[f] = Mathf.Min(raw[f], Mathf.Min(raw[Mathf.Max(first, f - 1)], raw[Mathf.Min(n - 1, f + 1)])) < HoldSpeed;
            var minLen = Mathf.Max(3, Mathf.RoundToInt(0.1f * fps));
            var segs = new List<(int a, int b, int template, float c, float gain, float offset)>();
            for (var f = first; f < n;)
            {
                if (!hold[f]) { f++; continue; }
                var g = f;
                while (g + 1 < n && hold[g + 1]) g++;
                if (g - f + 1 >= minLen)
                {
                    var (t, c, gain, offset) = Nearest(hand, f, g, side, flex);
                    if (t >= 0) segs.Add((f, g, t, c, gain, offset));
                }
                f = g + 1;
            }
            if (segs.Count == 0) return "no hold";
            var ramp = Mathf.Max(2, Mathf.RoundToInt(0.12f * fps));
            for (var f = first; f < n; f++)
            {
                // The nearest hold (inside it, full; within the ramp of it, easing off).
                var best = (w: 0f, s: -1);
                for (var i = 0; i < segs.Count; i++)
                {
                    var d = f < segs[i].a ? segs[i].a - f : f > segs[i].b ? f - segs[i].b : 0;
                    var w = Mathf.SmoothStep(0f, 1f, 1f - d / (float)(ramp + 1));
                    if (w > best.w) best = (w, i);
                }
                if (best.s < 0) continue;
                var seg = segs[best.s];
                var tpl = _templates[seg.template];
                var tf = side == "Right" ? tpl.right : tpl.left;
                var tt = side == "Right" ? tpl.rightThumb : tpl.leftThumb;
                var src = hand[f].features;
                var o = new Dictionary<string, float>(src);
                // Only a confident match is pulled on: a wrong handshape held crisply reads worse than the tracked one.
                var k = TemplateSnap * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(SnapMinConfidence, SnapFullConfidence, seg.c)) * best.w;
                foreach (var finger in FourFingers)
                {
                    var names = new[] { $"{finger} 1 Stretched", $"{finger} 2 Stretched", $"{finger} 3 Stretched", $"{finger} Spread" };
                    // How far this finger is from the handshape as the tracker shows it (its contrast squashed by gain).
                    float Off(string j) => src[j] - Squash(tf[j], seg.gain, seg.offset);
                    var df = Mathf.Sqrt((Sq(Off(names[0])) + Sq(Off(names[1])) + Sq(Off(names[2]))) / 3f);
                    var w = k * Mathf.Exp(-Sq(df / FingerTolerance));
                    foreach (var name in names) o[name] = Mathf.Lerp(src[name], tf[name], w);
                }
                var dt = Mathf.Sqrt((Sq(src["Thumb 2 Stretched"] - tf["Thumb 2 Stretched"]) + Sq(src["Thumb 3 Stretched"] - tf["Thumb 3 Stretched"]) + Sq(Vector3.Angle(hand[f].thumb, tt))) / 3f);
                var wt = k * Mathf.Exp(-Sq(dt / FingerTolerance));
                foreach (var name in new[] { "Thumb 2 Stretched", "Thumb 3 Stretched" }) o[name] = Mathf.Lerp(src[name], tf[name], wt);
                hand[f] = (o, Vector3.Slerp(hand[f].thumb, tt, wt));
            }
            return string.Join(", ", segs.Select(s => $"{_templates[s.template].name} {s.c:0.00}×{s.gain:0.0}"));
        }

        static float Sq(float x) => x * x;

        // A template joint angle as a lax tracker shows it: drawn towards a relaxed hand's bend by gain, then shifted.
        // (Squashed towards 0 instead, a loose flat hand and a fist are both "even" and match each other.)
        const float Relaxed = 35f;
        static float Squash(float t, float gain, float offset) => Relaxed + gain * (t - Relaxed) + offset;

        // The canonical handshape nearest a hold's median hand: how sure (1 = the same, 0.37 = SnapSigma away), and the
        // gain and offset that best turn the template's joint angles into the tracked ones. Trackers squash a handshape's
        // contrast (H read as index and middle a little bent, ring and little half closed: closer to O in plain degrees
        // than to H), so the pattern across the fingers is matched, the template drawn towards a relaxed hand by a gain
        // (0.5–1.3), with a cost for a gain away from 1 or an offset. Spread only counts for fingers open in both, as a curled finger has no spread to see.
        (int, float, float, float) Nearest((Dictionary<string, float> features, Vector3 thumb)[] hand, int a, int b, string side, string[] flex)
        {
            var median = new Dictionary<string, float>();
            foreach (var key in hand[a].features.Keys)
            {
                var v = new List<float>();
                for (var f = a; f <= b; f++) v.Add(hand[f].features[key]);
                v.Sort();
                median[key] = v[v.Count / 2];
            }
            var thumb = Vector3.zero;
            for (var f = a; f <= b; f++) thumb += hand[f].thumb;
            var best = (t: -1, d: float.MaxValue, gain: 1f, offset: 0f);
            for (var t = 0; t < _templates.Count; t++)
            {
                var tf = side == "Right" ? _templates[t].right : _templates[t].left;
                var tt = side == "Right" ? _templates[t].rightThumb : _templates[t].leftThumb;
                float mx = flex.Average(k => median[k]), mt = flex.Average(k => tf[k]);
                float cov = flex.Sum(k => (median[k] - mx) * (tf[k] - mt)), var_ = flex.Sum(k => Sq(tf[k] - mt));
                var gain = Mathf.Clamp(var_ > 1f ? cov / var_ : 1f, 0.5f, 1.3f);
                var offset = mx - Squash(mt, gain, 0f);
                var d2 = flex.Average(k => Sq(median[k] - Squash(tf[k], gain, offset)))
                         + Sq(GainCost * Mathf.Log(gain)) + Sq(0.5f * offset)
                         + 0.25f * (Sq(median["Thumb 2 Stretched"] - tf["Thumb 2 Stretched"]) + Sq(median["Thumb 3 Stretched"] - tf["Thumb 3 Stretched"]))
                         + 0.5f * Sq(Vector3.Angle(thumb, tt));
                var open = FourFingers.Where(x => Mathf.Abs(median[$"{x} 1 Stretched"]) < 45f && Mathf.Abs(tf[$"{x} 1 Stretched"]) < 45f).ToArray();
                if (open.Length > 0) d2 += 0.5f * open.Average(x => Sq(median[$"{x} Spread"] - tf[$"{x} Spread"]));
                if (d2 < best.d) best = (t, d2, gain, offset);
            }
            return best.t < 0 ? (-1, 0f, 1f, 0f) : (best.t, Mathf.Exp(-best.d / Sq(SnapSigma)), best.gain, best.offset);
        }

        // A hand's finger and thumb muscles from its features: the muscle value that gives the avatar the same. Angles
        // don't depend on how the avatar's finger bones are oriented or on its joint limits, which a fixed scale did
        // (a 90° bend isn't muscle -1 on every finger).
        List<(int, float)> ToMuscles(Dictionary<string, float> features, Vector3 thumbDir, string side)
        {
            var o = new List<(int, float)>();
            foreach (var (name, v) in features)
                if (_curve.TryGetValue($"{side} {name}", out var curve))
                {
                    var m = Invert(curve, v);
                    // A curled finger's spread can't be seen (its first bone points into the palm, so its angle in
                    // the palm's plane is noise): it lies against its neighbours instead.
                    if (name.EndsWith("Spread"))
                        m = Mathf.Lerp(m, -0.4f, Mathf.Clamp01((Mathf.Abs(features[name.Replace("Spread", "1 Stretched")]) - 40f) / 30f));
                    o.Add((SignPose.Index($"{side} {name}"), m));
                }
            var best = (spread: 0f, stretch: 0f, angle: float.MaxValue);
            foreach (var (spread, stretch, dir) in _thumbGrid[side])
            {
                var angle = Vector3.Angle(dir, thumbDir);
                if (angle < best.angle) best = (spread, stretch, angle);
            }
            o.Add((SignPose.Index($"{side} Thumb Spread"), best.spread));
            o.Add((SignPose.Index($"{side} Thumb 1 Stretched"), best.stretch));
            return o;
        }

        static void Tidy(Dictionary<string, float> features)
        {
            // A finger's last joint can't bend on its own: it follows the middle joint (about 0.7 of its bend) and
            // neither bends backwards. Tracking noise that breaks this reads as a broken finger, so pull it halfway in.
            foreach (var finger in FourFingers)
            {
                string j2 = $"{finger} 2 Stretched", j3 = $"{finger} 3 Stretched";
                var b2 = Mathf.Max(features[j2], -5f);
                features[j2] = b2;
                features[j3] = Mathf.Max(Mathf.Lerp(features[j3], 0.7f * b2, 0.5f), -5f);
                // Signers' fingers take a few clear states; tracking blurs them into in-between shapes that read as a
                // lazy hand. Pull the finger part way towards the states near it, weighted by how near (so it never
                // jumps from one to the next).
                if (FingerSnap <= 0f) continue;
                var v = new Vector3(features[$"{finger} 1 Stretched"], features[j2], features[j3]);
                var sum = Vector3.zero;
                var weight = 0f;
                foreach (var state in FingerStates)
                {
                    var k = Mathf.Exp(-(v - state).sqrMagnitude / (2f * 20f * 20f));
                    sum += k * state;
                    weight += k;
                }
                if (weight < 1e-6f) continue;
                v = Vector3.Lerp(v, sum / weight, FingerSnap);
                (features[$"{finger} 1 Stretched"], features[j2], features[j3]) = (v.x, v.y, v.z);
            }
        }

        // Turns the right hand so its fingers run along dir and its index knuckle is towards side of its little one.
        void Wrist(Vector3 dir, Vector3 side, bool left = false)
        {
            var w = Bone(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
            var curDir = Bone(left ? HumanBodyBones.LeftMiddleProximal : HumanBodyBones.RightMiddleProximal).position - w.position;
            var curSide = Bone(left ? HumanBodyBones.LeftIndexProximal : HumanBodyBones.RightIndexProximal).position
                          - Bone(left ? HumanBodyBones.LeftLittleProximal : HumanBodyBones.RightLittleProximal).position;
            w.rotation = Quaternion.LookRotation(dir, Vector3.Cross(dir, side))
                         * Quaternion.Inverse(Quaternion.LookRotation(curDir, Vector3.Cross(curDir, curSide))) * w.rotation;
        }

        // Where the right wrist goes, from the right shoulder in avatar space (x right, y up, z towards the viewer;
        // metres), at the start and end of each pointing sign; where the fingers point (thumb side up) and its handshape.
        public IEnumerable<(string, SignDictionary.Sign)> Pointing()
        {
            var you = (new Vector3(0.02f, -0.22f, 0.35f), new Vector3(0.02f, -0.15f, 0.48f));
            var me = (new Vector3(-0.06f, -0.14f, 0.3f), new Vector3(-0.08f, -0.12f, 0.24f));
            var there = (new Vector3(0.06f, -0.22f, 0.28f), new Vector3(0.1f, -0.2f, 0.34f));
            yield return ("YOU", Make(you, new Vector3(0, 0.05f, 1f), '1'));
            yield return ("ME", Make(me, new Vector3(-0.5f, 0.1f, -1f), '1'));
            yield return ("I", Make(me, new Vector3(-0.5f, 0.1f, -1f), '1'));
            // YOUR: a flat hand, fingers up and palm towards the person, pushed a little towards them.
            var your = (new Vector3(0f, -0.2f, 0.26f), new Vector3(0f, -0.18f, 0.4f));
            yield return ("YOUR", Make(your, new Vector3(0, 1f, 0.3f), 'B', palmOut: true));
            yield return ("THERE", Make(there, new Vector3(0.25f, 0.05f, 1f), '1'));
            foreach (var g in new[] { "HE", "SHE", "IT" }) yield return (g, Make(there, new Vector3(0.25f, 0.05f, 1f), '1'));
        }

        SignDictionary.Sign Make((Vector3 from, Vector3 to) wrist, Vector3 fingers, char shape, bool palmOut = false, int frames = 18)
        {
            var hand = Handshapes.Spelling(shape, _ready);
            var muscles = new float[frames * SignPose.Muscles];
            var pose = new HumanPose();
            var bend = Vector3.zero;
            for (var f = 0; f < frames; f++)
            {
                // Out and back a little: a point, held.
                var k = Mathf.Sin(Mathf.Clamp01(f / (frames * 0.5f)) * Mathf.PI * 0.5f);
                _pose.Apply(_ready);
                var (u, l) = Reach(HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
                    Vector3.Lerp(wrist.from, wrist.to, k), new Vector3(0.4f, -1f, -0.3f));
                Arm(HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand, u, l, ref bend);
                // Palm out (towards the viewer), fingers up: index knuckle towards the body's middle; else thumb side up.
                Wrist(fingers, palmOut ? Vector3.left : Vector3.up);
                _handler.GetHumanPose(ref pose);
                for (var m = 0; m < SignPose.Muscles; m++)
                {
                    var name = HumanTrait.MuscleName[m];
                    muscles[f * SignPose.Muscles + m] = IsFinger(name, "Right") ? hand[m]
                        : IsArm(m) && name.StartsWith("Right") ? pose.muscles[m] : _ready[m];
                }
            }
            return new SignDictionary.Sign { fps = 30, muscles = muscles, credit = Procedural };
        }

        public void Dispose()
        {
            _handler.Dispose();
            Object.DestroyImmediate(_go);
        }
    }
}
