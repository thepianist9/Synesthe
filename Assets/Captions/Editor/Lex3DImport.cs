using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>3D-LEX v1.0 (Ranum et al., LREC-COLING 2024; CC BY 4.0, osf.io/cv276): 1000 ASL signs captured with
/// Vicon (body) and StretchSense gloves (fingers). Its FBX animations, copied into <see cref="Folder"/>, import as
/// Humanoid; <see cref="Export"/> samples each one's muscles into a pose file (format "muscles") for SignImporter.
/// The FBXs are only a source: delete the folder afterwards, the pose files are what the app keeps.</summary>
public class Lex3DImport : AssetPostprocessor
{
    public const string Folder = "Assets/Lex3DTmp";
    public const string Credit = "3D-LEX v1.0 (Ranum et al. 2024), CC BY 4.0";

    void OnPreprocessModel()
    {
        if (!assetPath.StartsWith(Folder + "/")) return;
        var m = (ModelImporter)assetImporter;
        m.animationType = ModelImporterAnimationType.Human;
        m.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        m.materialImportMode = ModelImporterMaterialImportMode.None;
        m.importBlendShapes = false;
        m.importCameras = false;
        m.importLights = false;
        m.resampleCurves = true;
        m.animationCompression = ModelImporterAnimationCompression.Off;
    }

    /// <summary>Writes outDir/GLOSS.json per FBX in the folder, labelled GLOSS+suffix; returns a report.</summary>
    public static string Export(string outDir, string suffix = "", float fps = 30f)
    {
        Directory.CreateDirectory(outDir);
        var log = new System.Text.StringBuilder();
        int ok = 0, bad = 0;
        foreach (var path in Directory.GetFiles(Folder, "*.fbx").OrderBy(p => p))
        {
            var gloss = Path.GetFileNameWithoutExtension(path).ToUpperInvariant();
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            var avatar = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Avatar>().FirstOrDefault();
            var clip = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>().FirstOrDefault(c => !c.name.StartsWith("__preview__"));
            if (model == null || avatar == null || !avatar.isHuman || clip == null)
            {
                log.AppendLine($"{gloss}: skipped (avatar {(avatar == null ? "none" : avatar.isHuman ? "human" : "not human")}, clip {(clip != null)})");
                bad++;
                continue;
            }
            {
                // A humanoid clip holds muscle curves ("Left Arm Down-Up", fingers as "LeftHand.Index.1 Stretched"),
                // read straight from it: no rig to sample, and no bind pose in between.
                var curves = new AnimationCurve[HumanTrait.MuscleCount];
                foreach (var b in AnimationUtility.GetCurveBindings(clip))
                {
                    var i = System.Array.IndexOf(HumanTrait.MuscleName, MuscleName(b.propertyName));
                    if (i >= 0) curves[i] = AnimationUtility.GetEditorCurve(clip, b);
                }
                var found = curves.Count(c => c != null);
                if (found < 60) { log.AppendLine($"{gloss}: skipped, {found} muscle curves"); bad++; continue; }
                // Some takes are empty (HELLO: two keys, nothing moves): no arm or finger muscle travels.
                var travel = 0f;
                foreach (var c in curves)
                    if (c != null && c.length > 1) travel = Mathf.Max(travel, c.keys.Max(k => k.value) - c.keys.Min(k => k.value));
                if (travel < 0.05f) { log.AppendLine($"{gloss}: skipped, still (muscles move at most {travel:0.000})"); bad++; continue; }
                var n = Mathf.Max(1, Mathf.RoundToInt(clip.length * fps) + 1);
                var frames = new JArray();
                for (var f = 0; f < n; f++)
                {
                    var t = Mathf.Min(f / fps, clip.length);
                    frames.Add(new JArray(curves.Select(c => c == null ? 0f : (float)System.Math.Round(c.Evaluate(t), 4))));
                }
                var json = new JObject
                {
                    ["kind"] = "sign", ["format"] = "muscles", ["fps"] = fps, ["handSeen"] = 1f,
                    ["labels"] = new JArray(gloss + suffix), ["credit"] = Credit, ["source"] = "3dlex/" + Path.GetFileName(path),
                    ["muscles"] = frames,
                };
                File.WriteAllText(Path.Combine(outDir, gloss + ".json"), json.ToString(Newtonsoft.Json.Formatting.None));
                ok++;
                if (ok <= 3) log.AppendLine($"{gloss}: {n} frames, {clip.length:0.00} s at {clip.frameRate} fps, {found} muscle curves");
            }
        }
        return $"3D-LEX export: {ok} signs, {bad} skipped\n" + log;
    }

    // ---- Retargeting by orientation (ExportRetargeted) ----
    // Copying muscle values from the 3D-LEX rig onto our avatar is off by a constant 8-9 degrees per arm segment and
    // 21 degrees at the palm (measured against the raw 3D-LEX skeleton), because a muscle value of 0 means each
    // avatar's own T-pose and the two T-poses differ (our avatar's palms are tilted). So instead: drive the 3D-LEX rig
    // with its muscles (exact: under 0.5 degrees), give our avatar's shoulder, arm and finger bones the same orientation
    // in the body frame (from the shoulders and spine), and read our avatar's own muscles back.
    static readonly HumanBodyBones[] Chain = BuildChain();

    static HumanBodyBones[] BuildChain()
    {
        var list = new System.Collections.Generic.List<HumanBodyBones>();
        foreach (var s in new[] { "Left", "Right" })
        {
            foreach (var b in new[] { "Shoulder", "UpperArm", "LowerArm", "Hand" }) list.Add(Parse(s + b));
            foreach (var f in new[] { "Thumb", "Index", "Middle", "Ring", "Little" })
                foreach (var j in new[] { "Proximal", "Intermediate", "Distal" }) list.Add(Parse(s + f + j));
        }
        return list.ToArray(); // parents before children
    }

    static HumanBodyBones Parse(string n) => (HumanBodyBones)System.Enum.Parse(typeof(HumanBodyBones), n);

    static Quaternion BodyFrame(Animator a)
    {
        var right = a.GetBoneTransform(HumanBodyBones.RightUpperArm).position - a.GetBoneTransform(HumanBodyBones.LeftUpperArm).position;
        var up = a.GetBoneTransform(HumanBodyBones.Neck).position - a.GetBoneTransform(HumanBodyBones.Hips).position;
        return Quaternion.LookRotation(Vector3.Cross(right, up), up);
    }

    // Where a bone points (to its child joint) and which way its hand's palm faces, as a rotation in the body frame.
    static Quaternion Aim(Animator a, HumanBodyBones b, Quaternion body)
    {
        var t = a.GetBoneTransform(b);
        var name = b.ToString();
        var side = name.StartsWith("Left") ? "Left" : "Right";
        Vector3 dir;
        var child = Next(b);
        var c = child.HasValue ? a.GetBoneTransform(child.Value) : null;
        if (c != null) dir = c.position - t.position;
        else if (t.childCount > 0) dir = t.GetChild(0).position - t.position;               // finger tip end bone
        else dir = t.position - t.parent.position;                                          // straight on
        var hand = a.GetBoneTransform(Parse(side + "Hand")).position;
        var palm = Vector3.Cross(a.GetBoneTransform(Parse(side + "IndexProximal")).position - hand,
                                 a.GetBoneTransform(Parse(side + "LittleProximal")).position - hand);
        if (side == "Left") palm = -palm;                                                   // same anatomical side
        var inv = Quaternion.Inverse(body);
        return Quaternion.LookRotation(inv * dir, inv * palm);
    }

    static HumanBodyBones? Next(HumanBodyBones b)
    {
        var n = b.ToString();
        var side = n.StartsWith("Left") ? "Left" : "Right";
        if (n.EndsWith("Shoulder")) return Parse(side + "UpperArm");
        if (n.EndsWith("UpperArm")) return Parse(side + "LowerArm");
        if (n.EndsWith("LowerArm")) return Parse(side + "Hand");
        if (n.EndsWith("Hand")) return Parse(side + "MiddleProximal");
        if (n.EndsWith("Proximal")) return Parse(n.Replace("Proximal", "Intermediate"));
        if (n.EndsWith("Intermediate")) return Parse(n.Replace("Intermediate", "Distal"));
        return null;
    }

    /// <summary>Like <see cref="Export"/>, but retargeted onto <paramref name="target"/> by bone orientation; the
    /// muscles written are the target avatar's own.</summary>
    public static string ExportRetargeted(string outDir, string suffix, GameObject targetModel, float fps = 30f, string only = null)
    {
        Directory.CreateDirectory(outDir);
        var log = new System.Text.StringBuilder();
        int ok = 0, bad = 0;
        var R = Object.Instantiate(targetModel);
        var rA = R.GetComponent<Animator>();
        var rh = new HumanPoseHandler(rA.avatar, R.transform);
        var keep = only == null ? null : new System.Collections.Generic.HashSet<string>(only.Split(' ', '|'));
        try
        {
            foreach (var path in Directory.GetFiles(Folder, "*.fbx").OrderBy(p => p))
            {
                var gloss = Path.GetFileNameWithoutExtension(path).ToUpperInvariant();
                if (keep != null && !keep.Contains(gloss)) continue;
                var avatar = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Avatar>().FirstOrDefault();
                var clip = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>().FirstOrDefault(c => !c.name.StartsWith("__preview__"));
                if (avatar == null || !avatar.isHuman || clip == null) { log.AppendLine($"{gloss}: skipped (no humanoid clip)"); bad++; continue; }
                var curves = new AnimationCurve[HumanTrait.MuscleCount];
                foreach (var b in AnimationUtility.GetCurveBindings(clip))
                {
                    var i = System.Array.IndexOf(HumanTrait.MuscleName, MuscleName(b.propertyName));
                    if (i >= 0) curves[i] = AnimationUtility.GetEditorCurve(clip, b);
                }
                var travel = 0f;
                foreach (var c in curves)
                    if (c != null && c.length > 1) travel = Mathf.Max(travel, c.keys.Max(k => k.value) - c.keys.Min(k => k.value));
                if (travel < 0.05f) { log.AppendLine($"{gloss}: skipped, still"); bad++; continue; }
                var H = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(path));
                var hA = H.GetComponent<Animator>();
                if (hA.avatar == null) hA.avatar = avatar;
                var hh = new HumanPoseHandler(avatar, H.transform);
                try
                {
                    // Calibration: both rigs in their T-pose (all muscles 0); per bone, the turn that takes the 3D-LEX
                    // bone's aim (direction + palm) to ours, applied to our bone's rotation.
                    var hp = new HumanPose(); var rp = new HumanPose();
                    hh.GetHumanPose(ref hp); rh.GetHumanPose(ref rp);
                    hp.muscles = new float[HumanTrait.MuscleCount]; rp.muscles = new float[HumanTrait.MuscleCount];
                    hh.SetHumanPose(ref hp); rh.SetHumanPose(ref rp);
                    var bh = BodyFrame(hA); var br = BodyFrame(rA);
                    var offset = new System.Collections.Generic.Dictionary<HumanBodyBones, Quaternion>();
                    foreach (var b in Chain)
                    {
                        var ht = hA.GetBoneTransform(b); var rt = rA.GetBoneTransform(b);
                        if (ht == null || rt == null) continue;
                        // our bone's body-frame rotation = 3D-LEX bone's body-frame rotation * offset, where the offset makes
                        // both aims agree in the T-pose
                        var hRot = Quaternion.Inverse(bh) * ht.rotation;
                        var rRot = Quaternion.Inverse(br) * rt.rotation;
                        var turn = Aim(hA, b, bh) * Quaternion.Inverse(Aim(rA, b, br));              // ours → theirs
                        offset[b] = Quaternion.Inverse(hRot) * (turn * rRot);
                    }
                    var n = Mathf.Max(1, Mathf.RoundToInt(clip.length * fps) + 1);
                    var frames = new JArray();
                    float most = 0f;
                    for (var f = 0; f < n; f++)
                    {
                        var t = Mathf.Min(f / fps, clip.length);
                        var m = curves.Select(c => c == null ? 0f : c.Evaluate(t)).ToArray();
                        hp.muscles = m; hh.SetHumanPose(ref hp);
                        rp.muscles = m; rh.SetHumanPose(ref rp);                                   // body, head, legs
                        bh = BodyFrame(hA); br = BodyFrame(rA);
                        foreach (var b in Chain)
                        {
                            if (!offset.TryGetValue(b, out var off)) continue;
                            var ht = hA.GetBoneTransform(b);
                            rA.GetBoneTransform(b).rotation = br * (Quaternion.Inverse(bh) * ht.rotation) * off;
                        }
                        rh.GetHumanPose(ref rp);
                        foreach (var v in rp.muscles) most = Mathf.Max(most, Mathf.Abs(v));
                        frames.Add(new JArray(rp.muscles.Select(v => (float)System.Math.Round(v, 4))));
                    }
                    var json = new JObject
                    {
                        ["kind"] = "sign", ["format"] = "muscles", ["fps"] = fps, ["handSeen"] = 1f,
                        ["labels"] = new JArray(gloss + suffix), ["credit"] = Credit, ["source"] = "3dlex/" + Path.GetFileName(path),
                        ["retarget"] = "orientation", ["muscles"] = frames,
                    };
                    File.WriteAllText(Path.Combine(outDir, gloss + ".json"), json.ToString(Newtonsoft.Json.Formatting.None));
                    ok++;
                    if (ok <= 3) log.AppendLine($"{gloss}: {n} frames, largest muscle {most:0.00}");
                }
                finally { hh.Dispose(); Object.DestroyImmediate(H); }
            }
        }
        finally { rh.Dispose(); Object.DestroyImmediate(R); }
        return $"3D-LEX export (orientation retarget): {ok} signs, {bad} skipped\n" + log;
    }

    // Clip curve name → HumanTrait.MuscleName: "RightHand.Thumb.1 Stretched" → "Right Thumb 1 Stretched".
    static string MuscleName(string property)
    {
        foreach (var side in new[] { "Left", "Right" })
            if (property.StartsWith(side + "Hand.")) return side + " " + property.Substring(side.Length + 5).Replace('.', ' ');
        return property;
    }
}
