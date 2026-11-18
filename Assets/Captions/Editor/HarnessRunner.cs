using System.IO;
using MetaStarter.Captions;
using Unity.InferenceEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Week1Bench;

/// <summary>
/// Runs a video through the whole caption pipeline in the Editor (VideoHarness) and quits when it is done.
///   Meta ▸ Speaker Captions ▸ Run Video Harness  (uses the paths below)
/// Command line (not batch mode — Play mode needs the Editor loop):
///   Unity -projectPath . -executeMethod HarnessRunner.Run -harnessVideo v.mp4 -harnessAudio a.wav -harnessOut dir
/// The video must be 16:9 (it is letterboxed into the Quest camera's 4:3); the audio a 16 kHz mono float WAV.
/// Output: dir/log.txt (every [Captions] line with its video time) and dir/frame_NNNN.jpg.
/// From the command line it does NOT quit Unity when done: Unity 6000.6 + OVRPlugin crash on quit (OVRPlugin's
/// analytics thread locks a mutex that is already destroyed). It leaves Play mode and tools/run_ava.sh stops the
/// process once done.txt exists.
/// </summary>
public static class HarnessRunner
{
    const string ScenePath = "Assets/Captions/Testing/Harness.unity";
    const string DoneKey = "HarnessRunner.out";

    [MenuItem("Meta/Synesthe/Run Video Harness")]
    public static void Run()
    {
        var video = Arg("-harnessVideo", Path.GetFullPath("tools/harness_data/two_people.mp4"));
        var audio = Arg("-harnessAudio", Path.GetFullPath("tools/harness_data/two_people.wav"));
        var output = Arg("-harnessOut", Path.GetFullPath("tools/harness_data/out"));
        var frames = Arg("-harnessFrames", "");
        var fps = float.Parse(Arg("-harnessFps", "10"), System.Globalization.CultureInfo.InvariantCulture);
        RunClip(video, audio, output, frames, fps);
    }

    /// <summary>Same as <see cref="Run"/>, from an already open Editor (e.g. `unity command eval`): no Unity restart
    /// per clip. Done when output/done.txt exists and the Editor has left Play mode.</summary>
    public static void RunClip(string video, string audio, string output, string frames, float fps, bool byteTrack = true, bool signing = false)
    {
        if (Directory.Exists(output)) Directory.Delete(output, true);
        Directory.CreateDirectory(output);
        BuildScene(video, audio, output, frames, fps, byteTrack, signing);
        SessionState.SetString(DoneKey, output);
        EditorApplication.delayCall += () => EditorApplication.EnterPlaymode();
    }

    // Quit once the harness has written done.txt (survives the domain reload of entering Play mode).
    [InitializeOnLoadMethod]
    static void WatchForDone()
    {
        var output = SessionState.GetString(DoneKey, "");
        if (string.IsNullOrEmpty(output)) return;
        var started = EditorApplication.timeSinceStartup;
        EditorApplication.CallbackFunction check = null;
        check = () =>
        {
            var done = File.Exists(Path.Combine(output, "done.txt"));
            if (!done && EditorApplication.timeSinceStartup - started < 600) return;
            // Once only: a watcher left running would throw the next run (in the same domain) out of Play mode.
            EditorApplication.update -= check;
            SessionState.EraseString(DoneKey);
            Debug.Log(done ? "[HarnessRunner] Done" : "[HarnessRunner] Timed out");
            EditorApplication.ExitPlaymode(); // the runner script stops the process (quitting crashes in OVRPlugin)
        };
        EditorApplication.update += check;
    }

    static void BuildScene(string video, string audio, string output, string frames, float fps, bool byteTrack, bool signing)
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var camGo = new GameObject("View Camera");
        var cam = camGo.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = Color.black;
        cam.nearClipPlane = 0.05f;
        cam.farClipPlane = 100f;
        cam.tag = "MainCamera";
        var light = new GameObject("Light").AddComponent<Light>();
        light.type = LightType.Directional;

        var (panel, _) = Week1ProjectSetup.UiMaterials();
        var root = new GameObject("Speaker Captions");
        var mic = root.AddComponent<MicStream>();
        mic.androidSource = MicStream.SourceUnity; // unused: the harness feeds the soundtrack

        var tracker = root.AddComponent<PersonTracker>();
        tracker.faceModel = AssetDatabase.LoadAssetAtPath<ModelAsset>("Assets/Week1/Models/blaze_face_short_range.onnx");
        tracker.faceAnchors = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/Week1/Models/blazeface_anchors.csv");
        tracker.landmarkModel = AssetDatabase.LoadAssetAtPath<ModelAsset>("Assets/Week1/Models/face_landmark.tflite");
        tracker.faceIdModel = AssetDatabase.LoadAssetAtPath<ModelAsset>("Assets/Week1/Models/sface.onnx");
        tracker.useDepth = false; // no depth sensor for a video: distance from face size
        tracker.byteTrack = byteTrack;
        tracker.meshTracking = Arg("-harnessMesh", tracker.meshTracking ? "1" : "0") == "1"; // A/B: detect to find faces, follow them with the mesh

        var director = root.AddComponent<CaptionDirector>();
        director.activeSpeakerEncoder = AssetDatabase.LoadAssetAtPath<ModelAsset>("Assets/Week1/Models/lr_asd_encoder.onnx");
        director.activeSpeakerBackend = AssetDatabase.LoadAssetAtPath<ModelAsset>("Assets/Week1/Models/lr_asd_backend.onnx");
        director.mic = mic;
        director.tracker = tracker;
        director.head = camGo.transform;
        director.panelMaterial = panel;
        director.bubbleMaterial = CaptionsSetup.EnsureBubbleMaterial();
        director.bodyFont = MetaStarter.Captions.Editor.UiQuality.TextFontAsset() ?? CaptionsSetup.EnsureFontAsset("Inter-SemiBold");
        director.nameFont = director.bodyFont;
        director.showDebug = true;
        director.noMic = true;
        director.useOnsets = Arg("-harnessOnsets", "1") != "0";
        director.useFusion = Arg("-harnessFusion", "1") != "0";
        // Read in Play mode by VisionBackend (statics don't survive entering it; the process environment does).
        System.Environment.SetEnvironmentVariable("CAPTIONS_VMESH", Arg("-harnessMeshBackend", ""));
        if (Arg("-harnessPitchTurn", "1") == "0") director.usePitchTurns = false;
        if (Arg("-harnessVoiceTurn", "1") == "0") director.voiceTurnBelow = director.voiceTurnTwiceBelow = -2f; // A/B: no cut by voice

        if (Arg("-harnessSigning", "0") == "1" || signing)
        {
            director.mode = CaptionDirector.CaptionMode.Signing;
            CaptionsSetup.AddSignAvatar(root, director, tracker, camGo.transform, director.bubbleMaterial, director.bodyFont);
        }

        var harness = root.AddComponent<VideoHarness>();
        harness.videoPath = video;
        harness.audioPath = audio;
        harness.framesFolder = frames;
        harness.framesFps = fps;
        harness.outputFolder = output;
        harness.viewCamera = cam;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        harness.horizontalFov = float.Parse(Arg("-harnessFov", "80"), inv);
        // Head-worn recordings: the soundtrack's real level is the Quest mic's (+ offset); and the wearer threshold.
        var rawOffset = Arg("-harnessRawOffset", "");
        if (rawOffset != "") harness.rawLevelOffsetDb = float.Parse(rawOffset, inv);
        harness.questGain = Arg("-harnessQuestGain", "0") == "1";
        var wearerDb = Arg("-harnessWearerDb", "");
        if (wearerDb != "") director.wearerRawDb = float.Parse(wearerDb, inv);
        // Tagged speakers and the wearer's own voice: -harnessTag "6.5:right:Sam" -harnessWearer "12.4-15.2,…"
        var tags = Arg("-harnessTag", "").Split(',', System.StringSplitOptions.RemoveEmptyEntries);
        for (var i = 1; i < tags.Length; i++) harness.moreTags.Add(tags[i]);
        var tag = tags.Length > 0 ? tags[0].Split(':') : new string[0];
        if (tag.Length == 3)
        {
            harness.tagAt = float.Parse(tag[0], System.Globalization.CultureInfo.InvariantCulture);
            harness.tagSide = tag[1] == "left" ? -1f : 1f;
            harness.tagName = tag[2];
        }
        foreach (var w in Arg("-harnessWearer", "").Split(',', System.StringSplitOptions.RemoveEmptyEntries))
        {
            var ab = w.Split('-');
            harness.wearerWindows.Add(new Vector2(float.Parse(ab[0], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(ab[1], System.Globalization.CultureInfo.InvariantCulture)));
        }
        foreach (var w in Arg("-harnessEnrol", "").Split(',', System.StringSplitOptions.RemoveEmptyEntries))
        {
            var ab = w.Split('-');
            harness.enrolWindows.Add(new Vector2(float.Parse(ab[0], inv), float.Parse(ab[1], inv)));
        }
        harness.director = director;
        harness.tracker = tracker;
        tracker.frameSource = harness;

        Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
        EditorSceneManager.SaveScene(scene, ScenePath);
    }

    static string Arg(string name, string fallback)
    {
        var args = System.Environment.GetCommandLineArgs();
        for (var i = 0; i < args.Length - 1; i++) if (args[i] == name) return args[i + 1];
        // An Editor left open between runs (RunClip from `unity command eval`): the same settings as environment
        // variables of the Editor process, e.g. Environment.SetEnvironmentVariable("harnessMesh", "1").
        return System.Environment.GetEnvironmentVariable(name.TrimStart('-')) ?? fallback;
    }
}
