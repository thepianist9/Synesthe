using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Makes the Movement SDK's stylised "Lina" (head, hair and hands; the skeleton has a full body) a Unity Humanoid:
/// her bone names (LeftArmUpper, RightHandIndexProximal, …) aren't ones Unity's automatic mapping knows.
/// </summary>
public static class LinaSetup
{
    public const string ModelPath = "Assets/Captions/Signing/Lina/Models/Lina.fbx";

    static readonly Dictionary<string, string> Bones = new()
    {
        ["Hips"] = "Hips", ["Spine"] = "SpineLower", ["Chest"] = "SpineMiddle", ["UpperChest"] = "SpineUpper",
        ["Neck"] = "Neck", ["Head"] = "Head", ["LeftEye"] = "LeftEye", ["RightEye"] = "RightEye",
    };

    [MenuItem("Meta/Synesthe/Set Up Lina Avatar")]
    public static string Setup()
    {
        foreach (var side in new[] { "Left", "Right" })
        {
            Bones[$"{side}UpperLeg"] = $"{side}LegUpper";
            Bones[$"{side}LowerLeg"] = $"{side}LegLower";
            Bones[$"{side}Foot"] = $"{side}FootAnkle";
            Bones[$"{side}Toes"] = $"{side}FootBall";
            Bones[$"{side}Shoulder"] = $"{side}Shoulder";
            Bones[$"{side}UpperArm"] = $"{side}ArmUpper";
            Bones[$"{side}LowerArm"] = $"{side}ArmLower";
            Bones[$"{side}Hand"] = $"{side}HandWrist";
            foreach (var (unity, lina) in new[] { ("Thumb", "Thumb"), ("Index", "Index"), ("Middle", "Middle"), ("Ring", "Ring"), ("Little", "Pinky") })
            {
                // Lina's thumb chain starts at the trapezium (metacarpal-like); Unity's thumb has 3 bones.
                var names = unity == "Thumb" ? new[] { "Trapezium", "Meta", "Proximal" } : new[] { "Proximal", "Intermediate", "Distal" };
                var parts = new[] { "Proximal", "Intermediate", "Distal" };
                for (var i = 0; i < 3; i++) Bones[$"{side} {unity} {parts[i]}"] = $"{side}Hand{lina}{names[i]}";
            }
        }

        var importer = (ModelImporter)AssetImporter.GetAtPath(ModelPath);
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
        var skeleton = new List<SkeletonBone>();
        foreach (var t in model.GetComponentsInChildren<Transform>(true))
            skeleton.Add(new SkeletonBone { name = t.name, position = t.localPosition, rotation = t.localRotation, scale = t.localScale });

        var human = new List<HumanBone>();
        foreach (var (unityName, boneName) in Bones)
        {
            var humanName = System.Array.Find(HumanTrait.BoneName, n => n.Replace(" ", "") == unityName.Replace(" ", ""));
            if (humanName == null) return $"unknown humanoid bone {unityName}";
            var hb = new HumanBone { humanName = humanName, boneName = boneName };
            hb.limit.useDefaultValues = true;
            human.Add(hb);
        }

        var desc = importer.humanDescription;
        desc.human = human.ToArray();
        desc.skeleton = skeleton.ToArray();
        importer.animationType = ModelImporterAnimationType.Human;
        importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        importer.humanDescription = desc;
        // The sample's materials (its scene assigned them; the model's own names differ).
        foreach (var (inModel, mat) in new[] { ("eye_mat", "LinaEye"), ("head_mat", "LinaHead"), ("mouth_mat", "LinaMouth"),
                     ("handsSkin_MAT", "LinaHand"), ("brows_MAT", "LinaHair"), ("lashes", "LinaHair") })
        {
            var m = AssetDatabase.LoadAssetAtPath<Material>($"Assets/Captions/Signing/Lina/Materials/{mat}.mat");
            if (m != null) importer.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), inModel), m);
        }
        importer.SaveAndReimport();

        foreach (var o in AssetDatabase.LoadAllAssetsAtPath(ModelPath))
            if (o is Avatar a) return $"avatar valid={a.isValid} human={a.isHuman}";
        return "no avatar";
    }
}
