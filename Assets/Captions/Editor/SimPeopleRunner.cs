using System.IO;
using MetaStarter.Captions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Plays the app in the Meta XR Simulator with people in the room (<see cref="SimPeople"/>): the Captions scene,
/// plus two people from tools/harness_data/yt (frames + soundtrack) standing either side of you.
///   Meta ▸ Speaker Captions ▸ Play in Simulator with People
/// Command line (not batch mode — Play mode needs the Editor loop and the simulator window):
///   Unity -projectPath . -executeMethod SimPeopleRunner.Run [-simFrames dir] [-simAudio a.wav] [-simMic]
/// The scene change is not saved. Unity 6000.6 + OVRPlugin crash on quit: stop the process instead of quitting.
/// </summary>
public static class SimPeopleRunner
{
    const string ScenePath = "Assets/Captions/Captions.unity";

    [MenuItem("Meta/Synesthe/Play in Simulator with People")]
    public static void Run()
    {
        var frames = Arg("-simFrames", Path.GetFullPath("tools/harness_data/yt/frames"));
        var audio = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-simMic") >= 0
            ? "" : Arg("-simAudio", Path.GetFullPath("tools/harness_data/yt/audio.wav"));
        Week1ProjectSetup.ConfigureSimulatorPlayMode();
        if (!File.Exists(ScenePath)) CaptionsSetup.BuildScene();
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        var tracker = Object.FindFirstObjectByType<PersonTracker>();
        var director = Object.FindFirstObjectByType<CaptionDirector>();
        var people = new GameObject("Simulated people").AddComponent<SimPeople>();
        people.framesFolder = frames;
        people.framesFps = Directory.GetFiles(frames, "*.jpg").Length / Mathf.Max(1f, WavSeconds(audio, 120f));
        people.audioPath = audio;
        people.director = director;
        people.tracker = tracker;
        people.head = director.head;
        tracker.frameSource = people;
        tracker.useDepth = false; // the simulator's depth doesn't see the cards: distance from face size
        Debug.Log($"[SimPeopleRunner] {frames} at {people.framesFps:F1} fps, sound: {(audio != "" ? audio : "microphone")}");
        EditorApplication.delayCall += () => EditorApplication.EnterPlaymode();
    }

    static float WavSeconds(string path, float fallback) =>
        string.IsNullOrEmpty(path) || !File.Exists(path) ? fallback : (new FileInfo(path).Length - 44) / 4f / 16000f;

    static string Arg(string key, string fallback)
    {
        var args = System.Environment.GetCommandLineArgs();
        var i = System.Array.IndexOf(args, key);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
    }
}
