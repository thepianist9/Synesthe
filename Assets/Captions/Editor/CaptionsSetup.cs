using System.IO;
using Meta.XR;
using TMPro;
using UnityEngine.TextCore.LowLevel;
using MetaStarter.Captions;
using Unity.InferenceEngine;
using UnityEditor;
using UnityEditor.Android;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using Week1Bench;

/// <summary>
/// Builds the Speaker Captions prototype as its own app (separate package id, so the Week 1 bench stays installed).
///   Meta ▸ Speaker Captions ▸ Build Scene
///   Meta ▸ Speaker Captions ▸ Build APK
/// Project-wide settings (Android, XR loader, permissions) come from Week1ProjectSetup.SetupAll.
/// </summary>
public static class CaptionsSetup
{
    const string ScenePath = "Assets/Captions/Captions.unity";
    const string SearchModelPath = "Assets/Week1/Models/face_detection_full_range.tflite";
    const string PackageId = "com.metastarter.captions";
    const string ProductName = "Synesthe";

    [MenuItem("Meta/Synesthe/Build Scene")]
    public static void BuildScene()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var rig = Week1ProjectSetup.CreatePassthroughRig(scene, out var centerEye);
        var (panel, _) = Week1ProjectSetup.UiMaterials();
        var bubbleMaterial = EnsureBubbleMaterial();
        var bodyFont = MetaStarter.Captions.Editor.UiQuality.TextFontAsset() ?? EnsureFontAsset("Inter-SemiBold");
        var nameFont = bodyFont;
        var iconFont = MetaStarter.Captions.Editor.UiQuality.IconFontAsset();

        var root = new GameObject("Speaker Captions");

        // Learned in Week 1: Unity's default mic (VOICE_RECOGNITION) suppresses other people on Quest 3;
        // MIC + automatic gain hears them.
        var mic = root.AddComponent<MicStream>();
        mic.androidSource = MicStream.SourceMic;
        mic.autoGain = true;
        mic.maxAutoGainDb = 50f;          // constant boost + compressor: see MicStream.autoGain
        mic.compressorThresholdDb = -22f;
        mic.agcReleaseSeconds = 0.4f;
        mic.channels = 1; // Quest delivers one mixed channel even when asked for stereo

        var camGo = new GameObject("PassthroughCamera (Left)");
        camGo.transform.SetParent(root.transform, false);
        var pca = camGo.AddComponent<PassthroughCameraAccess>();
        pca.CameraPosition = PassthroughCameraAccess.CameraPositionType.Left;
        pca.RequestedResolution = new Vector2Int(1280, 960);
        pca.enabled = false; // PersonTracker enables it once ready

        var tracker = root.AddComponent<PersonTracker>();
        // Real face distance from the depth sensor (adds an EnvironmentDepthManager; needs the scene permission).
        tracker.depthRaycast = root.AddComponent<EnvironmentRaycastManager>();
        tracker.cameraAccess = pca;
        tracker.faceModel = AssetDatabase.LoadAssetAtPath<ModelAsset>("Assets/Week1/Models/blaze_face_short_range.onnx");
        tracker.faceAnchors = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/Week1/Models/blazeface_anchors.csv");
        tracker.landmarkModel = AssetDatabase.LoadAssetAtPath<ModelAsset>("Assets/Week1/Models/face_landmark.tflite");
        tracker.searchModel = AssetDatabase.LoadAssetAtPath<ModelAsset>(SearchModelPath);
        // Face ID (SFace): recognises a person coming back and merges duplicates. Without it someone who drops out
        // of view for a moment comes back as a new person (2026-11-09: a scene rebuild had left it unset).
        tracker.faceIdModel = AssetDatabase.LoadAssetAtPath<ModelAsset>("Assets/Week1/Models/sface.onnx");
        if (tracker.faceIdModel == null) Debug.LogWarning("[Captions] sface.onnx missing — no face ID: people out of view come back as new people");
        if (tracker.landmarkModel == null) Debug.LogWarning("[Captions] face_landmark.tflite missing — lips fall back to the BlazeFace proxy. Run tools/fetch_dependencies.sh");
        if (tracker.faceModel == null || tracker.faceAnchors == null)
            Debug.LogError("[CaptionsSetup] Face model/anchors missing — run tools/fetch_dependencies.sh first.");

        // Liquid glass refracts the passthrough camera image (the one face tracking already uses).
        var backdrop = root.AddComponent<GlassBackdrop>();
        backdrop.cameraAccess = pca;

        var director = root.AddComponent<CaptionDirector>();
        director.activeSpeakerEncoder = AssetDatabase.LoadAssetAtPath<ModelAsset>("Assets/Week1/Models/lr_asd_encoder.onnx");
        director.activeSpeakerBackend = AssetDatabase.LoadAssetAtPath<ModelAsset>("Assets/Week1/Models/lr_asd_backend.onnx");
        if (director.activeSpeakerEncoder == null || director.activeSpeakerBackend == null)
            Debug.LogWarning("[Captions] lr_asd_encoder/backend.onnx missing — speaker choice falls back to lip rules. See tools/export_lr_asd.py");
        director.mic = mic;
        director.tracker = tracker;
        director.head = centerEye;
        director.panelMaterial = panel;
        director.bubbleMaterial = bubbleMaterial;
        director.bodyFont = bodyFont;
        director.nameFont = nameFont;

        // Hands for pointing: the system hand ray (OVRHand pointer pose) and pinch.
        OVRHand AddHand(string anchor, string handType)
        {
            var go = System.Array.Find(rig.GetComponentsInChildren<Transform>(true), t => t.name == anchor).gameObject;
            var hand = go.AddComponent<OVRHand>();
            Week1ProjectSetup.SetEnum(hand, "HandType", handType);
            return hand;
        }
        // Microphone choice (shown on the start screen's microphone tab).
        var micSettings = root.AddComponent<MicSettings>();
        micSettings.mic = mic;
        micSettings.director = director;

        // Start screen: speech bubbles or signing.
        var modes = root.AddComponent<ModeMenu>();
        modes.director = director;
        modes.head = centerEye;
        modes.leftHand = AddHand("LeftHandAnchor", "HandLeft");
        modes.rightHand = AddHand("RightHandAnchor", "HandRight");
        modes.rightController = System.Array.Find(rig.GetComponentsInChildren<Transform>(true), t => t.name == "RightControllerAnchor");
        modes.leftController = System.Array.Find(rig.GetComponentsInChildren<Transform>(true), t => t.name == "LeftControllerAnchor");
        modes.bubbleMaterial = bubbleMaterial;
        modes.panelMaterial = panel;
        modes.font = bodyFont;
        modes.iconFont = iconFont;
        modes.micSettings = micSettings;
        modes.logo = Icon("logo");
        modes.tracker = tracker;
        modes.signAvatar = AddSignAvatar(root, director, tracker, centerEye, bubbleMaterial, bodyFont);

        Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
        EditorSceneManager.SaveScene(scene, ScenePath);
        Debug.Log($"[CaptionsSetup] Scene saved to {ScenePath}");
    }

    const string AvatarFolder = "Assets/Captions/Signing/Avatar";

    /// <summary>Signing mode: the avatar (Meta Movement SDK's sample character, Oculus SDK licence), the sign
    /// dictionary, and a light for it (everything else in the app is unlit).</summary>
    internal static SignAvatar AddSignAvatar(GameObject root, CaptionDirector director, PersonTracker tracker, Transform head,
        Material bubbleMaterial, TMP_FontAsset font)
    {
        var model = AssetDatabase.LoadAssetAtPath<GameObject>($"{AvatarFolder}/Models/Realistic.fbx");
        if (model == null)
        {
            Debug.LogWarning("[CaptionsSetup] Signing avatar missing (Assets/Captions/Signing/Avatar) — signing mode disabled.");
            return null;
        }
        LimitAvatarTextures();
        var go = new GameObject("Signing");
        go.transform.SetParent(root.transform, false);
        var avatar = go.AddComponent<SignAvatar>();
        avatar.director = director;
        avatar.tracker = tracker;
        avatar.head = head;
        avatar.avatarModel = model;
        avatar.signers = Signers();
        avatar.maxAvatars = 1;
        avatar.dictionary = EnsureSignDictionary();
        avatar.bubbleMaterial = bubbleMaterial;
        avatar.font = font;

        var light = new GameObject("Avatar light").AddComponent<Light>();
        light.transform.SetParent(go.transform, false);
        light.type = LightType.Directional;
        light.intensity = 1.1f;
        light.shadows = LightShadows.None;
        light.transform.rotation = Quaternion.Euler(35, 150, 0);
        return avatar;
    }

    /// <summary>The MetaHuman signers set up so far (MetaHumanSetup), in the order Settings cycles through them.</summary>
    internal static GameObject[] Signers()
    {
        var list = new System.Collections.Generic.List<GameObject>();
        foreach (var name in new[] { "Aera", "Victor", "Tuya" })
            if (AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/Captions/Signing/{name}/{name}.prefab") is { } prefab) list.Add(prefab);
        return list.ToArray();
    }

    static SignDictionary EnsureSignDictionary()
    {
        const string path = "Assets/Captions/Signing/SignDictionary.asset";
        var d = AssetDatabase.LoadAssetAtPath<SignDictionary>(path);
        if (d != null) return d;
        d = ScriptableObject.CreateInstance<SignDictionary>();
        AssetDatabase.CreateAsset(d, path);
        return d;
    }

    // The sample character's textures are 2–4K (120 MB): 1K on Quest is plenty for a small avatar.
    static void LimitAvatarTextures()
    {
        foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { $"{AvatarFolder}/Textures" }))
        {
            var importer = (TextureImporter)AssetImporter.GetAtPath(AssetDatabase.GUIDToAssetPath(guid));
            var android = importer.GetPlatformTextureSettings("Android");
            if (android.overridden && android.maxTextureSize == 1024) continue;
            android.overridden = true;
            android.maxTextureSize = 1024;
            android.format = TextureImporterFormat.ASTC_6x6;
            importer.SetPlatformTextureSettings(android);
            importer.SaveAndReimport();
        }
    }

    // The app logo as the Android app icon (adaptive: Unity derives the legacy icon from it), for this build only,
    // like the name. Returns the project's icons, for RestoreAppIcons.
    static (PlatformIconKind kind, Texture2D[][] textures)[] SetAppIcons(Texture2D background, Texture2D foreground)
    {
        var kinds = new[] { AndroidPlatformIconKind.Adaptive };
        var old = new (PlatformIconKind, Texture2D[][])[kinds.Length];
        for (var k = 0; k < kinds.Length; k++)
        {
            var icons = PlayerSettings.GetPlatformIcons(NamedBuildTarget.Android, kinds[k]);
            old[k] = (kinds[k], System.Array.ConvertAll(icons, i => i.GetTextures()));
            foreach (var icon in icons)
                icon.SetTextures(background, foreground);
            PlayerSettings.SetPlatformIcons(NamedBuildTarget.Android, kinds[k], icons);
        }
        return old;
    }

    static void RestoreAppIcons((PlatformIconKind kind, Texture2D[][] textures)[] old)
    {
        foreach (var (kind, textures) in old)
        {
            var icons = PlayerSettings.GetPlatformIcons(NamedBuildTarget.Android, kind);
            for (var i = 0; i < icons.Length && i < textures.Length; i++) icons[i].SetTextures(textures[i]);
            PlayerSettings.SetPlatformIcons(NamedBuildTarget.Android, kind, icons);
        }
    }

    // White line icons for the start screen (Assets/Captions/UI/Icons): clamped, with transparent edges.
    internal static Texture2D Icon(string name)
    {
        var path = $"Assets/Captions/UI/Icons/{name}.png";
        if (AssetImporter.GetAtPath(path) is TextureImporter ti && (ti.wrapMode != TextureWrapMode.Clamp || !ti.alphaIsTransparency))
        {
            ti.wrapMode = TextureWrapMode.Clamp;
            ti.alphaIsTransparency = true;
            ti.mipmapEnabled = true;
            ti.SaveAndReimport();
        }
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    internal static Material EnsureBubbleMaterial()
    {
        const string path = "Assets/Captions/Materials/LiquidGlassBubble.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat != null) return mat;
        var shader = Shader.Find("MetaStarter/LiquidGlassBubble") ?? throw new System.Exception("Shader MetaStarter/LiquidGlassBubble not found");
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        mat = new Material(shader);
        AssetDatabase.CreateAsset(mat, path);
        return mat;
    }

    /// <summary>
    /// TextMeshPro SDF font asset from a TTF in Assets/Captions/Fonts (dynamic atlas, pre-filled with ASCII).
    /// Imports TMP Essential Resources first if they are missing.
    /// </summary>
    internal static TMP_FontAsset EnsureFontAsset(string ttfName)
    {
        if (!Directory.Exists("Assets/TextMesh Pro"))
        {
            TMP_PackageResourceImporter.ImportResources(true, false, false);
            AssetDatabase.Refresh();
        }
        var assetPath = $"Assets/Captions/Fonts/{ttfName} SDF.asset";
        var existing = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(assetPath);
        if (existing != null) return existing;

        var font = AssetDatabase.LoadAssetAtPath<Font>($"Assets/Captions/Fonts/{ttfName}.ttf");
        if (font == null)
        {
            Debug.LogError($"[CaptionsSetup] {ttfName}.ttf missing — run tools/fetch_dependencies.sh. Falling back to TMP default font.");
            return TMP_Settings.defaultFontAsset;
        }
        var fa = TMP_FontAsset.CreateFontAsset(font, 90, 9, GlyphRenderMode.SDFAA, 1024, 1024, AtlasPopulationMode.Dynamic, true);
        fa.name = $"{ttfName} SDF";
        AssetDatabase.CreateAsset(fa, assetPath);
        fa.material.name = $"{ttfName} SDF Material";
        AssetDatabase.AddObjectToAsset(fa.material, fa);
        foreach (var tex in fa.atlasTextures)
        {
            tex.name = $"{ttfName} SDF Atlas";
            AssetDatabase.AddObjectToAsset(tex, fa);
        }
        var ascii = new System.Text.StringBuilder();
        for (var c = 32; c < 127; c++) ascii.Append((char)c);
        fa.TryAddCharacters(ascii.ToString() + "…’“”●–—éèàüöäß");
        EditorUtility.SetDirty(fa);
        AssetDatabase.SaveAssets();
        return fa;
    }

    /// <summary>For batch builds (-executeMethod): a fresh scene, then the APK.</summary>
    public static void BuildSceneAndApk()
    {
        BuildScene();
        BuildApk();
    }

    /// <summary>The APK with only the speech models the scene uses: the English live model, plus the multilingual one
    /// and the language check (other languages). adb A/B tests of another English model need <see cref="BuildApkAllModels"/>.</summary>
    [MenuItem("Meta/Synesthe/Build APK")]
    public static void BuildApk() => BuildApk(allModels: false);

    [MenuItem("Meta/Synesthe/Build APK (all models)")]
    public static void BuildApkAllModels() => BuildApk(allModels: true);

    static void BuildApk(bool allModels)
    {
        if (!File.Exists(ScenePath)) BuildScene();
        AddMissingModels();
        // Builds use the in-memory state of open scenes; save so the file and the build agree.
        EditorSceneManager.SaveOpenScenes();
        var hidden = allModels ? new System.Collections.Generic.List<string>() : HideUnusedModels();
        var ok = false;
        try { ok = Build(); }
        finally { RestoreModels(hidden); }
        if (Application.isBatchMode && !ok) EditorApplication.Exit(1);
    }

    // Model fields added after the scene was built: set them on the saved scene instead of rebuilding it (a rebuild
    // once dropped a model reference, 2026-11-09).
    static void AddMissingModels()
    {
        var scene = EditorSceneManager.GetActiveScene();
        if (scene.path != ScenePath) scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var tracker = Object.FindFirstObjectByType<PersonTracker>();
        if (tracker == null || tracker.searchModel != null) return;
        tracker.searchModel = AssetDatabase.LoadAssetAtPath<ModelAsset>(SearchModelPath);
        if (tracker.searchModel == null) { Debug.LogWarning($"[CaptionsSetup] {SearchModelPath} missing — searching with the short-range model. Run tools/fetch_dependencies.sh"); return; }
        EditorUtility.SetDirty(tracker);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log("[CaptionsSetup] Scene: full-range search model set");
    }

    const string ModelsDir = "Assets/StreamingAssets/Models";

    // Folders ending in "~" are ignored by Unity, so they don't go into the APK. Renamed back after the build.
    static System.Collections.Generic.List<string> HideUnusedModels()
    {
        var hidden = new System.Collections.Generic.List<string>();
        var director = Object.FindFirstObjectByType<CaptionDirector>();
        var live = director != null ? director.speechModel : SpeechRecognizers.Default;
        var unused = new System.Collections.Generic.List<string>();
        foreach (SpeechModel m in System.Enum.GetValues(typeof(SpeechModel)))
            if (m != live && m != SpeechRecognizers.Multilingual) unused.Add(SpeechRecognizers.Folder(m));
        foreach (var name in unused)
        {
            var dir = $"{ModelsDir}/{name}";
            if (!Directory.Exists(dir)) continue;
            Directory.Move(dir, dir + "~");
            if (File.Exists(dir + ".meta")) File.Move(dir + ".meta", dir + "~.meta~");
            hidden.Add(dir);
        }
        if (hidden.Count > 0) Debug.Log($"[CaptionsSetup] Left out of the APK: {string.Join(", ", hidden)}");
        return hidden;
    }

    static void RestoreModels(System.Collections.Generic.List<string> hidden)
    {
        foreach (var dir in hidden)
        {
            try
            {
                if (Directory.Exists(dir + "~") && !Directory.Exists(dir)) Directory.Move(dir + "~", dir);
                if (File.Exists(dir + "~.meta~") && !File.Exists(dir + ".meta")) File.Move(dir + "~.meta~", dir + ".meta");
            }
            catch (System.Exception e) { Debug.LogError($"[CaptionsSetup] Couldn't put {dir} back (it's at {dir}~): {e.Message}"); }
        }
    }

    static bool Build()
    {
        var target = NamedBuildTarget.Android;
        var oldId = PlayerSettings.GetApplicationIdentifier(target);
        var oldName = PlayerSettings.productName;
        var oldIcons = SetAppIcons(Icon("logo_bg"), Icon("logo_fg"));
        // Release: no stack trace on every info/warning log line (costly on the headset); errors keep theirs.
        var oldLogTrace = PlayerSettings.GetStackTraceLogType(LogType.Log);
        var oldWarnTrace = PlayerSettings.GetStackTraceLogType(LogType.Warning);
        PlayerSettings.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
        PlayerSettings.SetStackTraceLogType(LogType.Warning, StackTraceLogType.None);
        // The Horizon Store takes APKs under 1 GB; with the two speech models (~650 MB each) it was 1.59 GB. Split: the
        // APK keeps code and the scene, StreamingAssets (the models) go in the main expansion file (OBB, up to 4 GB),
        // which the store installs with the app. ModelFiles copies from it the same way (UnityWebRequest on
        // streamingAssetsPath). Sideloading: push the OBB to /sdcard/Android/obb/<package>/ before launching.
        var oldSplit = PlayerSettings.Android.splitApplicationBinary;
        PlayerSettings.Android.splitApplicationBinary = true;
        // Store builds (tools/build_release.sh): signed with the release key, which lives outside the repo with its
        // password in the macOS Keychain, and a version code above every earlier upload. Without these variables the
        // build is debug-signed, for sideloading.
        var keystore = System.Environment.GetEnvironmentVariable("SOPHIE_KEYSTORE");
        var keystorePass = System.Environment.GetEnvironmentVariable("SOPHIE_KEYSTORE_PASS");
        var release = !string.IsNullOrEmpty(keystore) && File.Exists(keystore) && !string.IsNullOrEmpty(keystorePass);
        var oldCustom = PlayerSettings.Android.useCustomKeystore;
        var oldKeystore = PlayerSettings.Android.keystoreName;
        var oldAlias = PlayerSettings.Android.keyaliasName;
        var oldCode = PlayerSettings.Android.bundleVersionCode;
        var oldVersion = PlayerSettings.bundleVersion;
        if (release)
        {
            PlayerSettings.Android.useCustomKeystore = true;
            PlayerSettings.Android.keystoreName = keystore;
            PlayerSettings.Android.keystorePass = keystorePass;
            PlayerSettings.Android.keyaliasName = "sophie";
            PlayerSettings.Android.keyaliasPass = keystorePass;
        }
        if (int.TryParse(System.Environment.GetEnvironmentVariable("SOPHIE_VERSION_CODE"), out var versionCode))
        {
            PlayerSettings.Android.bundleVersionCode = versionCode;
            PlayerSettings.bundleVersion = $"0.2.{versionCode}";
        }
        try
        {
            PlayerSettings.SetApplicationIdentifier(target, PackageId);
            PlayerSettings.productName = ProductName;
            Directory.CreateDirectory("Builds");
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = "Builds/SpeakerCaptions.apk",
                target = BuildTarget.Android,
                options = BuildOptions.None,
            });
            var ok = report.summary.result == BuildResult.Succeeded;
            // The store and Android look for main.<versionCode>.<package>.obb.
            var obb = $"Builds/main.{PlayerSettings.Android.bundleVersionCode}.{PackageId}.obb";
            if (ok && File.Exists("Builds/SpeakerCaptions.main.obb"))
            {
                if (File.Exists(obb)) File.Delete(obb);
                File.Move("Builds/SpeakerCaptions.main.obb", obb);
            }
            Debug.Log($"[CaptionsSetup] Build {report.summary.result}: {report.summary.totalErrors} errors → Builds/SpeakerCaptions.apk + {obb}" +
                      $" ({(release ? "release-signed" : "debug-signed")}, version code {PlayerSettings.Android.bundleVersionCode})");
            return ok;
        }
        finally
        {
            PlayerSettings.SetApplicationIdentifier(target, oldId);
            PlayerSettings.productName = oldName;
            PlayerSettings.Android.splitApplicationBinary = oldSplit;
            PlayerSettings.Android.useCustomKeystore = oldCustom;
            PlayerSettings.Android.keystoreName = oldKeystore;
            PlayerSettings.Android.keyaliasName = oldAlias;
            PlayerSettings.Android.keystorePass = "";
            PlayerSettings.Android.keyaliasPass = "";
            PlayerSettings.Android.bundleVersionCode = oldCode;
            PlayerSettings.bundleVersion = oldVersion;
            RestoreAppIcons(oldIcons);
            PlayerSettings.SetStackTraceLogType(LogType.Log, oldLogTrace);
            PlayerSettings.SetStackTraceLogType(LogType.Warning, oldWarnTrace);
        }
    }
}
