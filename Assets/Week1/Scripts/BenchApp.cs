using System;
using System.Collections.Generic;
using System.IO;
using Meta.XR;
using Unity.InferenceEngine;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Week1Bench
{
    /// <summary>
    /// Week 1 on-device test runner for Quest 3. Pick a test, run it, read PASS/FAIL on the panel.
    /// Results land in persistentDataPath/bench (CSV per second + summary JSON + results.txt).
    ///
    /// Controls (hands-first):
    ///   left index pinch (tap)          → next test
    ///   right index pinch (hold 1 s)    → start / abort
    ///   controllers: A = start/abort, B = next test · Editor: keys 1–6, Space
    ///   adb: am start -n &lt;pkg&gt;/com.unity3d.player.UnityPlayerGameActivity -e test 5 -e duration 1200
    /// </summary>
    public class BenchApp : MonoBehaviour
    {
        [Header("Scene references")]
        public Transform head;
        public BenchHud hud;
        public MicStream mic;
        public HandCapture hands;
        public PassthroughCameraAccess passthroughCamera;

        [Header("Models")]
        public ModelAsset faceModel;
        public TextAsset faceAnchors;

        readonly List<BenchTest> _tests = new()
        {
            new Test1FaceDetect(), new Test2AudioStack(), new Test3CaptionLatency(),
            new Test4HandRecorder(), new Test5AllAtOnce(), new Test6MicSources(),
        };
        readonly Dictionary<string, TestResult> _results = new();

        BenchContext _ctx;
        int _selected;
        bool _running;
        float _rightPinchHeld;
        bool _leftPinchWas;
        int _fpsFrames;
        float _fpsTimer;

        void Start()
        {
            Application.targetFrameRate = -1;
            OVRManager.display.displayFrequency = MaxRefresh();

            _ctx = new BenchContext
            {
                Perf = new PerfMonitor(), Hud = hud, Mic = mic, Hands = hands, Camera = passthroughCamera,
                FaceModel = faceModel, FaceAnchors = faceAnchors, Head = head,
            };
            if (passthroughCamera != null) passthroughCamera.enabled = false; // enabled by tests that need it

            ReadLaunchExtras(out var autoTest, out var duration);
            _ctx.DurationOverride = duration;
            StartCoroutine(Boot(autoTest));
        }

        static float MaxRefresh()
        {
            var rates = OVRManager.display.displayFrequenciesAvailable;
            var best = 72f;
            if (rates != null) foreach (var r in rates) if (r <= 90.5f && r > best) best = r;
            return best;
        }

        System.Collections.IEnumerator Boot(string autoTest)
        {
            RequestMicPermission();
            yield return ModelFiles.EnsureCopied(msg => hud.Set("Week 1 bench", msg));
            if (ModelFiles.Error != null) hud.Set("Week 1 bench — setup problem", ModelFiles.Error);
            ShowMenu();
            var idx = _tests.FindIndex(t => t.Id == autoTest);
            if (idx >= 0) { _selected = idx; RunSelected(); }
        }

        void Update()
        {
            var dt = Time.unscaledDeltaTime;
            _fpsFrames++;
            _fpsTimer += dt;
            if (_fpsTimer >= 1f) { _ctx.FpsLastSecond = _fpsFrames / _fpsTimer; _fpsFrames = 0; _fpsTimer = 0; }
            if (_running)
            {
                _ctx.Perf.Tick(dt);
                _ctx.RaiseFrameTick(dt);
            }
            HandleInput(dt);
        }

        void HandleInput(float dt)
        {
            var next = OVRInput.GetDown(OVRInput.Button.Two);
            var startStop = OVRInput.GetDown(OVRInput.Button.One);

            // Pinch controls are disabled while recording signs (Test 4) — signing would trigger them.
            var pinchAllowed = !(_running && _tests[_selected] is Test4HandRecorder);
            if (pinchAllowed && hands != null)
            {
                var left = hands.LeftPinch;
                if (left && !_leftPinchWas && !_running) next = true;
                _leftPinchWas = left;
                _rightPinchHeld = hands.RightPinch ? _rightPinchHeld + dt : 0;
                if (_rightPinchHeld >= 1f) { startStop = true; _rightPinchHeld = -10f; }
            }
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.spaceKey.wasPressedThisFrame) startStop = true;
                if (kb.nKey.wasPressedThisFrame) next = true;
                for (var i = 0; i < _tests.Count; i++)
                    if (kb[(Key)((int)Key.Digit1 + i)].wasPressedThisFrame && !_running) { _selected = i; ShowMenu(); }
            }
#endif
            if (next && !_running) { _selected = (_selected + 1) % _tests.Count; ShowMenu(); }
            if (startStop)
            {
                if (_running) _ctx.AbortRequested = true;
                else RunSelected();
            }
        }

        void ShowMenu()
        {
            var lines = new System.Text.StringBuilder();
            for (var i = 0; i < _tests.Count; i++)
            {
                var t = _tests[i];
                var status = _results.TryGetValue(t.Id, out var r) ? (r.Pass ? "  PASS" : "  FAIL") : "";
                lines.Append(i == _selected ? "▶ " : "   ").Append(t.Title).Append(status).Append('\n');
            }
            var sel = _tests[_selected];
            lines.Append('\n').Append(sel.Instructions).Append("\nPass: ").Append(sel.PassRule);
            if (_results.TryGetValue(sel.Id, out var last)) lines.Append("\nLast: ").Append(last.Headline);
            hud.Set("Week 1 bench · Quest 3", lines.ToString(),
                "Left pinch: next test · Right pinch (hold 1 s): start/abort · A/B on controllers");
            hud.Caption("");
        }

        /// <summary>For automation (Editor / CLI): select a test by id ("1"–"5") and start it.</summary>
        public void RunTestById(string id, float duration = 0)
        {
            var idx = _tests.FindIndex(t => t.Id == id);
            if (idx < 0 || _running) return;
            _selected = idx;
            _ctx.DurationOverride = duration;
            RunSelected();
        }

        public bool IsRunning => _running;
        public string LastResult(string id) => _results.TryGetValue(id, out var r) ? $"{(r.Pass ? "PASS" : "FAIL")} · {r.Headline}" : null;

        async void RunSelected()
        {
            if (_running) return;
            if (!ModelFiles.Ready) { hud.Set("Models missing", ModelFiles.Error ?? "Still copying models…"); return; }
            var test = _tests[_selected];
            _running = true;
            _ctx.AbortRequested = false;
            hud.Set(test.Title, "Starting…\n\n" + test.Instructions, "Right pinch (hold 1 s) or A to abort");
            TestResult result;
            try
            {
                result = await test.Run(_ctx);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                result = new TestResult { Name = test.Title, Headline = $"Crashed: {e.GetType().Name}: {e.Message}" };
            }
            finally
            {
                if (passthroughCamera != null) passthroughCamera.enabled = false;
                _running = false;
            }
            if (_ctx.AbortRequested) result.Headline = "(aborted) " + result.Headline;
            _results[test.Id] = result;
            AppendResultsFile(test, result);
            ShowMenu();
            hud.Caption((result.Pass ? "PASS · " : "FAIL · ") + result.Headline);
        }

        static void AppendResultsFile(BenchTest test, TestResult r)
        {
            Directory.CreateDirectory(BenchLog.Folder);
            File.AppendAllText(Path.Combine(BenchLog.Folder, "results.txt"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\t{test.Title}\t{(r.Pass ? "PASS" : "FAIL")}\t{r.Headline}\n");
        }

        static void RequestMicPermission()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone))
                UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Microphone);
#endif
        }

        static void ReadLaunchExtras(out string test, out float duration)
        {
            test = null;
            duration = 0;
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var unity = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = unity.GetStatic<AndroidJavaObject>("currentActivity");
                using var intent = activity.Call<AndroidJavaObject>("getIntent");
                test = intent.Call<string>("getStringExtra", "test");
                Test6MicSources.RawOnly = intent.Call<string>("getStringExtra", "sources") == "raw";
                var d = intent.Call<string>("getStringExtra", "duration");
                if (!string.IsNullOrEmpty(d)) float.TryParse(d, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out duration);
            }
            catch (Exception e) { Debug.LogWarning($"[Bench] Could not read launch extras: {e.Message}"); }
#endif
        }

        void OnApplicationQuit() => _ctx.AbortRequested = true;
    }
}
