using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Meta.XR;
using Unity.InferenceEngine;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR.Management;
using Week1Bench;

/// <summary>
/// One-click (or batch-mode) setup for the Week 1 bench:
///   Meta ▸ Week 1 Bench ▸ Configure Project + Build Scene
///   Meta ▸ Week 1 Bench ▸ Build APK
/// Batch: unity run &lt;project&gt; -- -executeMethod Week1ProjectSetup.SetupAll
/// </summary>
public static class Week1ProjectSetup
{
    const string ScenePath = "Assets/Week1/Week1Bench.unity";
    const string PackageId = "com.metastarter.week1bench";
    const string CoreRoot = "Packages/com.meta.xr.sdk.core";

    [MenuItem("Meta/Week 1 Bench/Configure Project + Build Scene")]
    public static void SetupAll()
    {
        ConfigurePlugins();
        ConfigurePlayer();
        ConfigureXR();
        ConfigureMetaProject();
        BuildScene();
        RunMetaFixAll();
        AssetDatabase.SaveAssets();
        Debug.Log("[Week1Setup] Done. Open Meta ▸ Tools ▸ Project Setup Tool to review remaining recommendations.");
    }

    // ---------------- native plugins ----------------

    public static void ConfigurePlugins()
    {
        AssetDatabase.Refresh();
        foreach (var path in AssetDatabase.FindAssets("", new[] { "Assets/Plugins/SherpaOnnx" })
                     .Select(AssetDatabase.GUIDToAssetPath).Distinct())
        {
            if (AssetImporter.GetAtPath(path) is not PluginImporter imp) continue;
            imp.ClearSettings();
            imp.SetCompatibleWithAnyPlatform(false);
            if (path.EndsWith(".dll"))
            {
                imp.SetCompatibleWithAnyPlatform(true);
            }
            else if (path.EndsWith(".so"))
            {
                imp.SetCompatibleWithPlatform(BuildTarget.Android, true);
                imp.SetPlatformData(BuildTarget.Android, "CPU", "ARM64");
            }
            else if (path.EndsWith(".dylib"))
            {
                imp.SetCompatibleWithEditor(true);
                imp.SetEditorData("OS", "OSX");
                imp.SetEditorData("CPU", "ARM64");
                imp.SetCompatibleWithPlatform(BuildTarget.StandaloneOSX, true);
            }
            imp.SaveAndReimport();
        }
    }

    // ---------------- player / Android ----------------

    public static void ConfigurePlayer()
    {
        if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
            EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);

        PlayerSettings.companyName = "Meta Starter Team";
        PlayerSettings.productName = "Week1 Bench";
        PlayerSettings.SetApplicationIdentifier(UnityEditor.Build.NamedBuildTarget.Android, PackageId);
        PlayerSettings.SetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel32;
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { UnityEngine.Rendering.GraphicsDeviceType.Vulkan });
        PlayerSettings.colorSpace = ColorSpace.Linear;
        PlayerSettings.enableFrameTimingStats = true;
        PlayerSettings.Android.forceInternetPermission = false;

        // Meta SDK + Input System together: "Both".
        var ps = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset").FirstOrDefault();
        if (ps != null)
        {
            var so = new SerializedObject(ps);
            var prop = so.FindProperty("activeInputHandler");
            if (prop != null) { prop.intValue = 2; so.ApplyModifiedPropertiesWithoutUndo(); }
        }
    }

    // ---------------- XR plug-in management ----------------

    public static void ConfigureXR()
    {
        if (!EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.k_SettingsKey, out XRGeneralSettingsPerBuildTarget perTarget) || perTarget == null)
        {
            Directory.CreateDirectory("Assets/XR");
            perTarget = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
            AssetDatabase.CreateAsset(perTarget, "Assets/XR/XRGeneralSettingsPerBuildTarget.asset");
            EditorBuildSettings.AddConfigObject(XRGeneralSettings.k_SettingsKey, perTarget, true);
        }
        if (!perTarget.HasSettingsForBuildTarget(BuildTargetGroup.Android))
            perTarget.CreateDefaultSettingsForBuildTarget(BuildTargetGroup.Android);
        if (!perTarget.HasManagerSettingsForBuildTarget(BuildTargetGroup.Android))
            perTarget.CreateDefaultManagerSettingsForBuildTarget(BuildTargetGroup.Android);

        var settings = perTarget.SettingsForBuildTarget(BuildTargetGroup.Android);
        settings.InitManagerOnStart = true;
        var ok = XRPackageMetadataStore.AssignLoader(settings.AssignedSettings, "Unity.XR.Oculus.OculusLoader", BuildTargetGroup.Android);
        Debug.Log($"[Week1Setup] Oculus XR loader assigned for Android: {ok}");
        EditorUtility.SetDirty(perTarget);
    }

    /// <summary>
    /// Play mode on a Mac runs through the Meta XR Simulator, which needs the OpenXR loader on the
    /// Standalone target (the Oculus XR plugin is Windows-only in the Editor). Android stays on Oculus.
    /// </summary>
    [MenuItem("Meta/Week 1 Bench/Configure Simulator (Play mode)")]
    public static void ConfigureSimulatorPlayMode()
    {
        if (!EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.k_SettingsKey, out XRGeneralSettingsPerBuildTarget perTarget) || perTarget == null)
        {
            ConfigureXR();
            EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.k_SettingsKey, out perTarget);
        }
        if (!perTarget.HasSettingsForBuildTarget(BuildTargetGroup.Standalone))
            perTarget.CreateDefaultSettingsForBuildTarget(BuildTargetGroup.Standalone);
        if (!perTarget.HasManagerSettingsForBuildTarget(BuildTargetGroup.Standalone))
            perTarget.CreateDefaultManagerSettingsForBuildTarget(BuildTargetGroup.Standalone);
        var settings = perTarget.SettingsForBuildTarget(BuildTargetGroup.Standalone);
        settings.InitManagerOnStart = true;
        XRPackageMetadataStore.RemoveLoader(settings.AssignedSettings, "Unity.XR.Oculus.OculusLoader", BuildTargetGroup.Standalone);
        var ok = XRPackageMetadataStore.AssignLoader(settings.AssignedSettings, "UnityEngine.XR.OpenXR.OpenXRLoader", BuildTargetGroup.Standalone);

        var featureSet = UnityEditor.XR.OpenXR.Features.OpenXRFeatureSetManager.GetFeatureSetWithId(BuildTargetGroup.Standalone, "com.meta.openxr.featureset.metaxr");
        if (featureSet != null)
        {
            featureSet.isEnabled = true;
            UnityEditor.XR.OpenXR.Features.OpenXRFeatureSetManager.SetFeaturesFromEnabledFeatureSets(BuildTargetGroup.Standalone);
        }
        var openXr = UnityEngine.XR.OpenXR.OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Standalone);
        var enabled = new System.Collections.Generic.List<string>();
        foreach (var f in openXr.GetFeatures())
        {
            if (f.GetType().Name == "MetaXRFeature") f.enabled = true;
            if (f.enabled) enabled.Add(f.GetType().Name);
        }
        EditorUtility.SetDirty(openXr);
        EditorUtility.SetDirty(perTarget);
        AssetDatabase.SaveAssets();
        Debug.Log($"[Week1Setup] Standalone OpenXR loader assigned: {ok}; feature set found: {featureSet != null}; enabled features: {string.Join(", ", enabled)}");
    }

    // ---------------- Meta project config ----------------

    public static void ConfigureMetaProject()
    {
        var cfg = OVRProjectConfig.CachedProjectConfig;
        cfg.handTrackingSupport = OVRProjectConfig.HandTrackingSupport.ControllersAndHands;
        cfg.handTrackingFrequency = OVRProjectConfig.HandTrackingFrequency.HIGH;
        cfg.insightPassthroughSupport = OVRProjectConfig.FeatureSupport.Required;
        cfg.isPassthroughCameraAccessEnabled = true;
        OVRProjectConfig.CommitProjectConfig(cfg);
    }

    static void RunMetaFixAll()
    {
        // FixTasks(group, filter, logMessages, blocking, onCompleted, allowHiddenGroupTasks) is internal.
        try
        {
            var method = typeof(OVRProjectSetup).GetMethod("FixTasks", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            var logEnum = typeof(OVRProjectSetup).GetNestedType("LogMessages", BindingFlags.NonPublic | BindingFlags.Public);
            if (method == null || logEnum == null) { Debug.LogWarning("[Week1Setup] OVRProjectSetup.FixTasks not found; run Fix All by hand."); return; }
            var args = method.GetParameters().Select(p => p.HasDefaultValue ? p.DefaultValue : null).ToArray();
            args[0] = BuildTargetGroup.Android;
            args[2] = Enum.ToObject(logEnum, 2); // Changed
            args[3] = true;                     // blocking
            method.Invoke(null, args);
            Debug.Log("[Week1Setup] Meta Project Setup Tool: Fix All requested (blocking).");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Week1Setup] Could not run Meta Fix All automatically: {e.Message}");
        }
    }

    // ---------------- scene ----------------

    /// <summary>OVRCameraRig with passthrough underlay, clear background, and mic + camera permission requests.</summary>
    public static GameObject CreatePassthroughRig(Scene scene, out Transform centerEye)
    {
        var rigPrefab = AssetDatabase.LoadAssetAtPath<GameObject>($"{CoreRoot}/Prefabs/OVRCameraRig.prefab");
        if (rigPrefab == null) throw new Exception("Meta XR Core prefabs not found — are packages resolved?");
        var rig = (GameObject)PrefabUtility.InstantiatePrefab(rigPrefab, scene);
        rig.name = "OVRCameraRig";
        var manager = rig.GetComponent<OVRManager>();
        manager.isInsightPassthroughEnabled = true;
        var mso = new SerializedObject(manager);
        SetBool(mso, "requestRecordAudioPermissionOnStartup", true);
        SetBool(mso, "requestPassthroughCameraAccessPermissionOnStartup", true);
        SetBool(mso, "requestScenePermissionOnStartup", true); // depth sensor (EnvironmentRaycastManager)
        mso.ApplyModifiedPropertiesWithoutUndo();
        rig.AddComponent<OVRPassthroughLayer>();

        centerEye = rig.transform.Find("TrackingSpace/CenterEyeAnchor");
        var cam = centerEye.GetComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0, 0, 0, 0);
        return rig;
    }

    public static void BuildScene()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        var handPrefab = AssetDatabase.LoadAssetAtPath<GameObject>($"{CoreRoot}/Prefabs/OVRHandPrefab.prefab");
        if (handPrefab == null) throw new Exception("Meta XR Core prefabs not found — are packages resolved?");
        var rig = CreatePassthroughRig(scene, out var centerEye);

        OVRHand MakeHand(string anchor, bool left)
        {
            var parent = rig.transform.Find($"TrackingSpace/{anchor}");
            var go = (GameObject)PrefabUtility.InstantiatePrefab(handPrefab, parent);
            go.name = left ? "LeftHand" : "RightHand";
            var hand = go.GetComponent<OVRHand>();
            var skel = go.GetComponent<OVRSkeleton>();
            var mesh = go.GetComponent<OVRMesh>();
            SetEnum(hand, "HandType", left ? "HandLeft" : "HandRight");
            // OVRHand.OnValidate re-syncs these, so set them through the SDK's own setters afterwards.
            var skelType = left ? OVRSkeleton.SkeletonType.XRHandLeft : OVRSkeleton.SkeletonType.XRHandRight;
            var meshType = left ? OVRMesh.MeshType.XRHandLeft : OVRMesh.MeshType.XRHandRight;
            typeof(OVRSkeleton).GetMethod("SetSkeletonType", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                ?.Invoke(skel, new object[] { skelType });
            if (mesh != null)
                typeof(OVRMesh).GetMethod("SetMeshType", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                    ?.Invoke(mesh, new object[] { meshType });
            foreach (var c in new UnityEngine.Object[] { hand, skel, mesh })
                if (c != null) { EditorUtility.SetDirty(c); PrefabUtility.RecordPrefabInstancePropertyModifications(c); }
            if (skel.GetSkeletonType() != skelType)
                throw new Exception($"[Week1Setup] {go.name}: skeleton type is {skel.GetSkeletonType()}, expected {skelType}");
            var meshName = mesh != null ? new SerializedObject(mesh).FindProperty("_meshType")?.enumDisplayNames is { } n
                ? n[new SerializedObject(mesh).FindProperty("_meshType").enumValueIndex] : "?" : "none";
            Debug.Log($"[Week1Setup] {go.name}: skeleton {skel.GetSkeletonType()}, mesh {meshName}");
            return hand;
        }
        var leftHand = MakeHand("LeftHandAnchor", true);
        var rightHand = MakeHand("RightHandAnchor", false);

        var bench = new GameObject("Week1Bench");
        var hud = bench.AddComponent<BenchHud>();
        hud.head = centerEye;
        AssignHudMaterials(hud);
        var mic = bench.AddComponent<MicStream>();
        var hands = bench.AddComponent<HandCapture>();
        hands.leftHand = leftHand;
        hands.rightHand = rightHand;
        hands.leftSkeleton = leftHand.GetComponent<OVRSkeleton>();
        hands.rightSkeleton = rightHand.GetComponent<OVRSkeleton>();
        hands.head = centerEye;

        var camGo = new GameObject("PassthroughCamera (Left)");
        camGo.transform.SetParent(bench.transform, false);
        var pca = camGo.AddComponent<PassthroughCameraAccess>();
        pca.CameraPosition = PassthroughCameraAccess.CameraPositionType.Left;
        pca.RequestedResolution = new Vector2Int(1280, 960);
        pca.enabled = false;

        var app = bench.AddComponent<BenchApp>();
        app.head = centerEye;
        app.hud = hud;
        app.mic = mic;
        app.hands = hands;
        app.passthroughCamera = pca;
        app.faceModel = AssetDatabase.LoadAssetAtPath<ModelAsset>("Assets/Week1/Models/blaze_face_short_range.onnx");
        app.faceAnchors = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/Week1/Models/blazeface_anchors.csv");
        if (app.faceModel == null || app.faceAnchors == null)
            Debug.LogError("[Week1Setup] Face model/anchors missing — run tools/fetch_dependencies.sh first.");

        var light = new GameObject("Directional Light").AddComponent<Light>();
        light.type = LightType.Directional;
        light.transform.rotation = Quaternion.Euler(50, -30, 0);

        Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
        EditorSceneManager.SaveScene(scene, ScenePath);
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        Debug.Log($"[Week1Setup] Scene saved to {ScenePath}");
    }

    /// <summary>
    /// The HUD needs real material assets: shaders only referenced through Shader.Find at runtime are
    /// stripped from player builds (the panel then renders pink and its setup throws).
    /// </summary>
    public static void AssignHudMaterials(BenchHud hud)
    {
        const string folder = "Assets/Week1/Materials";
        Directory.CreateDirectory(folder);
        Material Ensure(string name, string shaderName, Color color)
        {
            var path = $"{folder}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                var shader = Shader.Find(shaderName) ?? throw new Exception($"Shader '{shaderName}' not found");
                mat = new Material(shader) { color = color };
                AssetDatabase.CreateAsset(mat, path);
            }
            return mat;
        }
        hud.panelMaterial = Ensure("HudPanel", "Universal Render Pipeline/Unlit", new Color(0.04f, 0.06f, 0.12f, 0.85f));
        MakeTransparent(hud.panelMaterial);
        hud.textMaterial = Ensure("HudText", "GUI/Text Shader", Color.white);
        EditorUtility.SetDirty(hud);
    }

    /// <summary>The panel/text material assets (created on first use) for any world-space UI.</summary>
    public static (Material panel, Material text) UiMaterials()
    {
        var tmp = new GameObject("tmp").AddComponent<BenchHud>();
        AssignHudMaterials(tmp);
        var result = (tmp.panelMaterial, tmp.textMaterial);
        UnityEngine.Object.DestroyImmediate(tmp.gameObject);
        return result;
    }

    /// <summary>
    /// URP Unlit, alpha-blended. Passthrough shows through wherever the app's alpha is low, and opaque URP
    /// output left alpha at 0 on Quest — dark panels vanished. A transparent surface writes real alpha.
    /// </summary>
    static void MakeTransparent(Material m)
    {
        m.SetFloat("_Surface", 1);           // Transparent
        m.SetFloat("_Blend", 0);             // Alpha
        m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        m.SetFloat("_SrcBlendAlpha", (float)UnityEngine.Rendering.BlendMode.One);
        m.SetFloat("_DstBlendAlpha", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        m.SetFloat("_ZWrite", 0);
        m.SetOverrideTag("RenderType", "Transparent");
        m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent - 10; // draw before text
        EditorUtility.SetDirty(m);
    }

    [MenuItem("Meta/Week 1 Bench/Fix HUD Materials (current scene)")]
    public static void FixHudMaterials()
    {
        var hud = UnityEngine.Object.FindFirstObjectByType<BenchHud>();
        if (hud == null) throw new Exception("No BenchHud in the open scene — open Assets/Week1/Week1Bench.unity");
        AssignHudMaterials(hud);
        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkSceneDirty(hud.gameObject.scene);
        EditorSceneManager.SaveScene(hud.gameObject.scene);
        Debug.Log($"[Week1Setup] HUD materials: {hud.panelMaterial.shader.name}, {hud.textMaterial.shader.name}");
    }

    static void SetBool(SerializedObject so, string name, bool value)
    {
        var p = so.FindProperty(name);
        if (p != null) p.boolValue = value;
        else Debug.LogWarning($"[Week1Setup] {so.targetObject.GetType().Name}.{name} not found");
    }

    public static void SetEnum(UnityEngine.Object target, string field, string enumName)
    {
        var so = new SerializedObject(target);
        var p = so.FindProperty(field);
        if (p == null) { Debug.LogWarning($"[Week1Setup] {target.GetType().Name}.{field} not found"); return; }
        var idx = Array.IndexOf(p.enumNames, enumName);
        if (idx < 0) { Debug.LogWarning($"[Week1Setup] {enumName} not in {string.Join(",", p.enumNames)}"); return; }
        p.enumValueIndex = idx;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    // ---------------- build ----------------

    [MenuItem("Meta/Week 1 Bench/Build APK")]
    public static void BuildApk()
    {
        Directory.CreateDirectory("Builds");
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            locationPathName = "Builds/Week1Bench.apk",
            target = BuildTarget.Android,
            options = BuildOptions.None,
        });
        Debug.Log($"[Week1Setup] Build {report.summary.result}: {report.summary.totalSize / (1024 * 1024)} MB, {report.summary.totalErrors} errors");
        if (Application.isBatchMode && report.summary.result != BuildResult.Succeeded) EditorApplication.Exit(1);
    }
}
