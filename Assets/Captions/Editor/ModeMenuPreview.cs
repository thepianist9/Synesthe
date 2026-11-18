using System.IO;
using MetaStarter.Captions;
using TMPro;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Renders the start screen (<see cref="ModeMenu"/>) or the launch animation (<see cref="SplashLogo"/>) to PNGs
/// without entering Play mode, over a backdrop picture.
///   ModeMenuPreview.Render("out.png", signing: false, textScale: 1f, backdrop: "frame.jpg")
///   ModeMenuPreview.RenderSplash("outDir", frames: 50, backdrop: "frame.jpg")   → outDir/splash_000.png …
/// Batch: -executeMethod ModeMenuPreview.RenderBatch -previewOut out.png [-previewSigning] [-previewBackdrop frame.jpg]
///        [-previewSplash 50]   (out.png's folder gets the splash frames instead)
/// The backdrop picture stands in for the passthrough camera, so the glass blurs it as on the headset.
/// </summary>
public static class ModeMenuPreview
{
    public static string Render(string path, bool signing = false, float textScale = 1f, string backdrop = null, bool micTab = false, int setupStage = -1, bool settingsTab = false, bool quick = false, int quickState = 0)
    {
        var root = new GameObject("Mode menu preview") { hideFlags = HideFlags.HideAndDontSave };
        var rt = new RenderTexture(1600, 800, 24, RenderTextureFormat.ARGB32) { antiAliasing = 8 };
        try
        {
            var cam = Stage(root, rt, backdrop);
            var director = root.AddComponent<CaptionDirector>();
            director.mode = signing ? CaptionDirector.CaptionMode.Signing
                : setupStage == 7 ? CaptionDirector.CaptionMode.SignToSpeech : CaptionDirector.CaptionMode.Bubbles;
            var (panel, _) = Week1ProjectSetup.UiMaterials();
            var mics = root.AddComponent<MicSettings>();
            mics.mic = root.AddComponent<Week1Bench.MicStream>();
            mics.director = director;
            mics.Init();
            var menu = root.AddComponent<ModeMenu>();
            menu.micSettings = mics;
            menu.director = director;
            menu.head = cam.transform;
            menu.bubbleMaterial = CaptionsSetup.EnsureBubbleMaterial();
            menu.panelMaterial = panel;
            menu.font = Font();
            menu.iconFont = MetaStarter.Captions.Editor.UiQuality.IconFontAsset();
            CaptionBubble.TextScale = textScale;
            menu.Build();
            if (quick) menu.OpenQuickForPreview(cam.transform, quickState);
            else if (setupStage >= 0) menu.OpenSetupForPreview(cam.transform, setupStage);
            else menu.OpenForPreview(cam.transform, micTab, settingsTab);
            Snap(root, cam, rt, path);
            return path;
        }
        finally
        {
            CaptionBubble.TextScale = 1f;
            Shader.SetGlobalFloat("_PtCamEnabled", 0f);
            rt.Release();
            Object.DestroyImmediate(root);
        }
    }

    public static string RenderSplash(string outDir, int frames = 50, string backdrop = null)
    {
        var root = new GameObject("Splash preview") { hideFlags = HideFlags.HideAndDontSave };
        var rt = new RenderTexture(1200, 600, 24, RenderTextureFormat.ARGB32) { antiAliasing = 8 };
        try
        {
            var cam = Stage(root, rt, backdrop);
            var (panel, _) = Week1ProjectSetup.UiMaterials();
            var splash = root.AddComponent<SplashLogo>();
            splash.head = cam.transform;
            splash.glassMaterial = CaptionsSetup.EnsureBubbleMaterial();
            splash.imageMaterial = panel;
            splash.font = Font();
            splash.Build();
            splash.Place(cam.transform);
            Directory.CreateDirectory(outDir);
            for (var i = 0; i < frames; i++)
            {
                splash.Pose(SplashLogo.Duration * i / (frames - 1));
                Snap(root, cam, rt, Path.Combine(outDir, $"splash_{i:000}.png"));
            }
            return outDir;
        }
        finally
        {
            Shader.SetGlobalFloat("_PtCamEnabled", 0f);
            rt.Release();
            Object.DestroyImmediate(root);
        }
    }

    static TMP_FontAsset Font() => MetaStarter.Captions.Editor.UiQuality.TextFontAsset();

    // Camera at eye height, and the backdrop picture filling its view.
    static Camera Stage(GameObject root, RenderTexture rt, string backdrop)
    {
        var camGo = new GameObject("Preview camera");
        camGo.transform.SetParent(root.transform, false);
        camGo.transform.position = new Vector3(200, 1.5f, 0);
        var cam = camGo.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.32f, 0.3f, 0.28f);
        cam.fieldOfView = 34f;
        cam.nearClipPlane = 0.05f;
        cam.aspect = (float)rt.width / rt.height;
        if (string.IsNullOrEmpty(backdrop) || !File.Exists(backdrop)) return cam;

        // The picture stands in for the passthrough camera: the glass blurs it as it blurs the room on the headset
        // (same globals GlassBackdrop sets).
        var tex = new Texture2D(2, 2);
        tex.LoadImage(File.ReadAllBytes(backdrop));
        tex.wrapMode = TextureWrapMode.Clamp;
        var bg = GameObject.CreatePrimitive(PrimitiveType.Quad);
        Object.DestroyImmediate(bg.GetComponent<Collider>());
        bg.transform.SetParent(root.transform, false);
        bg.transform.position = camGo.transform.position + Vector3.forward * 3f;
        var tanV = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
        bg.transform.localScale = new Vector3(2f * 3f * tanV * cam.aspect, 2f * 3f * tanV, 1);
        var mat = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { mainTexture = tex };
        bg.GetComponent<MeshRenderer>().sharedMaterial = mat;
        Shader.SetGlobalTexture("_PtCamTex", tex);
        Shader.SetGlobalMatrix("_PtWorldToCam", Matrix4x4.TRS(camGo.transform.position, camGo.transform.rotation, Vector3.one).inverse);
        Shader.SetGlobalVector("_PtIntrinsics", new Vector4(0.5f / (tanV * cam.aspect), 0.5f / tanV, 0.5f, 0.5f));
        Shader.SetGlobalFloat("_PtCamEnabled", 1f);
        return cam;
    }

    static void Snap(GameObject root, Camera cam, RenderTexture rt, string path)
    {
        foreach (var t in root.GetComponentsInChildren<TextMeshPro>(true)) t.ForceMeshUpdate();
        cam.targetTexture = rt;
        cam.Render();
        RenderTexture.active = rt;
        var shot = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
        shot.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        RenderTexture.active = null;
        File.WriteAllBytes(path, shot.EncodeToPNG());
        Object.DestroyImmediate(shot);
    }

    public static void RenderBatch()
    {
        var args = System.Environment.GetCommandLineArgs();
        string Arg(string key) { var i = System.Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var outPath = Arg("-previewOut");
        var splash = Arg("-previewSplash");
        if (splash != null)
            Debug.Log("[ModeMenuPreview] " + RenderSplash(Path.GetDirectoryName(outPath), int.Parse(splash), Arg("-previewBackdrop")));
        else
            Debug.Log("[ModeMenuPreview] " + Render(outPath, System.Array.IndexOf(args, "-previewSigning") >= 0, 1f, Arg("-previewBackdrop")));
        File.WriteAllText(outPath + ".done", "ok");
    }
}
