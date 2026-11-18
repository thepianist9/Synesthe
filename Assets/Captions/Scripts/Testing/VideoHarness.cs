using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Video;

namespace MetaStarter.Captions
{
    /// <summary>
    /// Editor test bench: runs a video file through the whole caption pipeline instead of the headset camera and mic.
    ///   video frames → letterboxed into a 1280×960 "camera" (the Quest camera's shape) → PersonTracker (IFrameSource)
    ///   soundtrack (16 kHz float WAV, made with ffmpeg) → CaptionDirector.PushAudio, in step with the video clock
    /// The view camera matches the virtual camera, so faces, bubbles and the video line up. Every captureInterval
    /// seconds of video it saves a JPG of what the app shows; every log line is written with its video time.
    /// Set it up in Edit mode (tracker.frameSource = this) before entering Play mode.
    /// </summary>
    public class VideoHarness : MonoBehaviour, IFrameSource
    {
        public string videoPath;
        [Tooltip("Alternative to videoPath: a folder of numbered JPG frames (ffmpeg -vf fps=N dir/%05d.jpg). Unity's " +
                 "VideoPlayer often decodes nothing when the Editor runs in the background; images always work.")]
        public string framesFolder;
        public float framesFps = 10f;
        public string audioPath;       // 32-bit float mono 16 kHz WAV
        public string outputFolder;
        public Camera viewCamera;
        public CaptionDirector director;
        public PersonTracker tracker;
        [Tooltip("Horizontal field of view of the virtual camera (degrees). Sets how far away faces appear.")]
        public float horizontalFov = 80f;
        [Tooltip("The video's lens is equidistant (angle grows evenly across the picture, like EasyCom's wide camera) " +
                 "rather than a pinhole: face directions then follow the head pose. Off = pinhole of horizontalFov.")]
        public bool equidistantLens;
        [Tooltip("Width / height of the video as recorded (it is squeezed into 16:9 here); 0 = 16:9.")]
        public float sourceAspect;
        public float captureInterval = 0.1f;
        public float tailSeconds = 5f;
        [Tooltip("Video-time stretches (start, end s) that are the wearer talking: the simulated Quest mic hears them " +
                 "wearerBoostDb louder than the people in the video (the wearer's mouth is a few cm from it).")]
        public List<Vector2> wearerWindows = new();
        public float wearerBoostDb = 18f;
        [Tooltip("The people in the video, as the Quest mic would hear them from ~1 m (raw dBFS, typical speech).")]
        public float othersRawDb = -62f;
        [Tooltip("Real recordings from a head-worn mic (EasyCom glasses): the Quest-mic level is the soundtrack's own " +
                 "level plus this (undoes any gain applied to the file). NaN = off.")]
        public float rawLevelOffsetDb = float.NaN;
        [Tooltip("Tag someone at this video time (s; negative = never): the visible face nearest tagSide (-1 left, " +
                 "+1 right of the picture) gets tagName, as if tagged in the setup.")]
        public float tagAt = -1f;
        public float tagSide = 1f;
        public string tagName = "Sam";
        [Tooltip("More people to tag, each \"time:left|right:name\" (picks among faces not yet tagged).")]
        public List<string> moreTags = new();
        [Tooltip("The setup's \"Teach it your voice\" step: learning starts at the first window; speech outside the " +
                 "windows isn't learned (the clip's reading-aloud stretches).")]
        public List<Vector2> enrolWindows = new();
        [Tooltip("The wearer's recorded head rotation, one line per video frame: \"qx qy qz qw\" (Unity camera axes, " +
                 "relative to the first frame; tools: ec_headpose.py). The virtual camera turns with it, as the Quest's " +
                 "head tracking would, so people stay put in the room while the picture pans. Empty = fixed camera.")]
        public string headPoseFile;
        [Tooltip("Labelled face centres, one line per face per video frame: \"frame participant x y\" (0–1 of the picture; " +
                 "frame = (video time − truthOffset) × truthFps). Tags \"time:gt<participant>:Name\" use them: like the " +
                 "wearer tapping that person, once exactly one tracked face sits on that participant's labelled face.")]
        public string truthFacesFile;
        public float truthOffset, truthFps = 20f;
        Dictionary<int, List<(int pid, Vector2 at)>> _truth;
        Quaternion[] _headPose;
        Quaternion _baseRot;
        bool _enrolStarted;
        float _speechDb;

        const int W = 1280, H = 960;
        VideoPlayer _player;
        RenderTexture _videoRt, _camRt, _shotRt;
        float[] _audio;
        int _audioPos;
        bool _started, _done;
        float _nextShot;
        int _shotIndex;
        StreamWriter _log;
        double _endTime = -1, _clock;
        long _shown = -1, _camShown = -1;
        bool _seeking;

        public bool IsPlaying => _started && (_player != null || _frames != null) && _endTime < 0;
        string[] _frames;
        Texture2D _frameTex;
        double Length => _frames != null ? _frames.Length / (double)framesFps : _player.length;
        public Texture Texture => _camRt;
        public long FrameId => _camShown; // the frame now in Texture (letterboxed), not the newest decoded one
        [Tooltip("Tell the tracker how late each video frame is shown (decoding lags the clock the audio follows), so " +
                 "face crops get the frame's own time. Off: crops are stamped when they arrive.")]
        public bool reportFrameAge = true;
        /// <summary>Clock time minus the middle of the frame on screen: how far the picture lags the audio.</summary>
        public float MeasuredFrameAge => _camShown < 0 ? 0f : Mathf.Max(0f, (float)(_clock - (_camShown + 0.5) / (_frames != null ? framesFps : _player.frameRate)));
        public float FrameAge => reportFrameAge ? MeasuredFrameAge : 0f;
        // The pose belongs to the frame on show (the camera transform may already have moved on a frame).
        public Pose Pose => new(viewCamera.transform.position, HeadAt(_camShown));
        public Ray ViewportPointToRay(Vector2 viewport, Pose pose)
        {
            var r = viewCamera.ViewportPointToRay(viewport);
            var local = Quaternion.Inverse(viewCamera.transform.rotation) * r.direction;
            if (equidistantLens)
            {
                // The video fills the width and 720 of the H rows, centred; equal angle per source pixel both ways.
                var band = 720f / H * W / 1280f;
                var ax = (viewport.x - 0.5f) * horizontalFov * Mathf.Deg2Rad;
                var ay = (viewport.y - 0.5f) / band * horizontalFov / (sourceAspect > 0 ? sourceAspect : 16f / 9f) * Mathf.Deg2Rad;
                local = new Vector3(Mathf.Sin(ax) * Mathf.Cos(ay), Mathf.Sin(ay), Mathf.Cos(ax) * Mathf.Cos(ay));
            }
            return new Ray(r.origin, pose.rotation * local);
        }
        Quaternion HeadAt(long frame) =>
            _headPose == null ? viewCamera.transform.rotation : _baseRot * _headPose[(int)System.Math.Clamp(frame, 0, _headPose.Length - 1)];
        public string Resolution => $"{W}x{H} (video)";

        void Awake()
        {
            Directory.CreateDirectory(outputFolder);
            _log = new StreamWriter(Path.Combine(outputFolder, "log.txt")) { AutoFlush = true };
            Application.logMessageReceived += OnLog;

            _audio = ReadFloatWav(audioPath);
            _speechDb = SpeechLevel(_audio);
            _videoRt = new RenderTexture(1280, 720, 0, RenderTextureFormat.ARGB32);
            _camRt = new RenderTexture(W, H, 0, RenderTextureFormat.ARGB32);
            _shotRt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32);

            if (!string.IsNullOrEmpty(framesFolder))
            {
                _frames = Directory.GetFiles(framesFolder, "*.jpg");
                System.Array.Sort(_frames, System.StringComparer.Ordinal);
                _frameTex = new Texture2D(2, 2, TextureFormat.RGB24, false);
            }
            else
            {
            _player = gameObject.AddComponent<VideoPlayer>();
            _player.url = videoPath;
            _player.renderMode = VideoRenderMode.RenderTexture;
            _player.targetTexture = _videoRt;
            _player.audioOutputMode = VideoAudioOutputMode.None;
            _player.playOnAwake = false;
            _player.isLooping = false;
            _player.sendFrameReadyEvents = true;
            _player.frameReady += (_, frame) => { _shown = frame; _seeking = false; };
            _player.Prepare();
            }

            // The view camera is the virtual camera: 4:3, fixed in place.
            viewCamera.aspect = (float)W / H;
            viewCamera.fieldOfView = 2f * Mathf.Atan(Mathf.Tan(horizontalFov * 0.5f * Mathf.Deg2Rad) * H / W) * Mathf.Rad2Deg;
            _baseRot = viewCamera.transform.rotation;
            if (!string.IsNullOrEmpty(headPoseFile))
            {
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                _headPose = System.Array.ConvertAll(File.ReadAllLines(headPoseFile), l =>
                {
                    var f = l.Split(' ');
                    return new Quaternion(float.Parse(f[0], inv), float.Parse(f[1], inv), float.Parse(f[2], inv), float.Parse(f[3], inv));
                });
                Debug.Log($"[Harness] Head pose: {_headPose.Length} frames from {Path.GetFileName(headPoseFile)}");
            }
            if (!string.IsNullOrEmpty(truthFacesFile))
            {
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                _truth = new Dictionary<int, List<(int, Vector2)>>();
                foreach (var l in File.ReadAllLines(truthFacesFile))
                {
                    var f = l.Split(' ');
                    if (f.Length < 4) continue;
                    var k = int.Parse(f[0]);
                    if (!_truth.TryGetValue(k, out var list)) _truth[k] = list = new List<(int, Vector2)>();
                    list.Add((int.Parse(f[1]), new Vector2(float.Parse(f[2], inv), float.Parse(f[3], inv))));
                }
            }
            MakeBackdrop();
        }

        // The video behind everything, filling the virtual camera's view exactly.
        void MakeBackdrop()
        {
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Destroy(q.GetComponent<Collider>());
            q.name = "Video backdrop";
            // Corners from the camera's real projection (XR/Editor may override fieldOfView), so the video lines up
            // with the rays the tracker uses.
            _backdrop = q.transform;
            FitBackdrop();
            var m = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { mainTexture = _camRt };
            m.SetTexture("_BaseMap", _camRt);
            m.renderQueue = 1000;
            q.GetComponent<MeshRenderer>().sharedMaterial = m;
        }

        Transform _backdrop;

        // Re-fitted every frame: the camera's projection can change after Awake (XR / Editor).
        void FitBackdrop()
        {
            const float d = 30f;
            Vector3 At(float x, float y) { var r = viewCamera.ViewportPointToRay(new Vector3(x, y, 0)); return r.origin + r.direction * (d / Vector3.Dot(r.direction, viewCamera.transform.forward)); }
            var bl = At(0, 0); var tr = At(1, 1); var br = At(1, 0);
            _backdrop.SetPositionAndRotation((bl + tr) / 2f, viewCamera.transform.rotation);
            _backdrop.localScale = new Vector3(Vector3.Distance(bl, br), Vector3.Distance(br, tr), 1f);
        }

        void LateUpdate()
        {
            if (_headPose != null) viewCamera.transform.rotation = HeadAt(_shown);
            if (_backdrop != null) FitBackdrop();
        }

        void Update()
        {
            if (_done) return;
            if (!_started)
            {
                if ((_frames != null || _player.isPrepared) && director.Listening)
                {
                    director.UseExternalAudio();
                    _started = true;
                    Debug.Log($"[Harness] Playing {(_frames != null ? framesFolder : Path.GetFileName(videoPath))} ({Length:F1} s)");
                }
                return;
            }
            Letterbox();
            // Hold the clock at frame 0 until face tracking has warmed up, so no speech plays with nobody tracked.
            if (!tracker.Ready)
            {
                if (_frames != null && _shown < 0) { _frameTex.LoadImage(File.ReadAllBytes(_frames[0])); Graphics.Blit(_frameTex, _videoRt); _shown = 0; }
                return;
            }

            // The video runs on our own clock, frame by frame: the Editor's video player often stays paused after
            // Play(), but seeking to a frame always works.
            _clock += Time.unscaledDeltaTime;
            var atEnd = _clock >= Length;
            if (_frames != null)
            {
                var idx = (long)System.Math.Min(_clock * framesFps, _frames.Length - 1);
                if (idx > _shown)
                {
                    _frameTex.LoadImage(File.ReadAllBytes(_frames[idx]));
                    Graphics.Blit(_frameTex, _videoRt);
                    _shown = idx;
                }
            }
            var target = _frames != null ? -1 : (long)System.Math.Min(_clock * _player.frameRate, _player.frameCount - 1);
            if (_frames == null && !_seeking && target > _shown)
            {
                _seeking = true;
                if (target == _shown + 1) _player.StepForward(); else _player.frame = target;
            }

            // Audio follows the video clock.
            var upTo = Mathf.Min(_audio.Length, (int)(_clock * 16000));
            if (_endTime >= 0) upTo = Mathf.Min(_audio.Length, (int)(Length * 16000)); // the rest of the clip, not of the file (AVA soundtracks run on past the clip)
            if (upTo > _audioPos)
            {
                var chunk = new float[upTo - _audioPos];
                System.Array.Copy(_audio, _audioPos, chunk, 0, chunk.Length);
                var at = _audioPos / 16000f;
                _audioPos = upTo;
                var raw = chunk;
                if (questGain && !float.IsNaN(rawLevelOffsetDb)) chunk = QuestGain(chunk);
                director.PushAudio(chunk, Time.realtimeSinceStartupAsDouble);
                chunk = raw;
                if (!float.IsNaN(rawLevelOffsetDb)) director.simRawDb = Mathf.Max(ChunkDb(chunk) + rawLevelOffsetDb, -120f);
                else if (wearerWindows.Count > 0) director.simRawDb = SimulatedRawDb(chunk, at);
            }
            if (_endTime >= 0) director.PushAudio(new float[(int)(16000 * Time.deltaTime) + 1], Time.realtimeSinceStartupAsDouble); // silence after the end

            if (tagAt >= 0 && VideoTime >= tagAt) TagOne();
            for (var i = 0; i < moreTags.Count; i++) // in list order: "left" tags then go left to right
            {
                var t = moreTags[i].Split(':');
                if (VideoTime < float.Parse(t[0], System.Globalization.CultureInfo.InvariantCulture)) continue;
                var ok = t[1].StartsWith("gt") ? TagTruth(int.Parse(t[1][2..]), t[2])
                    : t[1].StartsWith("x")
                    ? TagAt(float.Parse(t[1][1..], System.Globalization.CultureInfo.InvariantCulture), t[2])
                    : Tag(t[1] == "left" ? -1f : 1f, t[2]);
                if (ok) moreTags.RemoveAt(i--);
                else if (!t[1].StartsWith("x") && !t[1].StartsWith("gt")) break; // keep the order of "left"/"right" tags: wait until this one is tagged
            }
            if (enrolWindows.Count > 0)
            {
                if (!_enrolStarted && VideoTime >= enrolWindows[0].x) { _enrolStarted = true; director.StartLearningVoice(); }
                var inside = false;
                foreach (var w in enrolWindows) inside |= VideoTime >= w.x && VideoTime <= w.y;
                director.enrolHold = !inside;
            }
            if (VideoTime >= _nextPos)
            {
                _nextPos += 0.1f;
                LogPositions();
            }
            if (VideoTime >= _nextShot)
            {
                _nextShot += captureInterval;
                Capture();
            }
            if (_endTime < 0 && atEnd) _endTime = Time.timeAsDouble;
            if (_endTime >= 0 && Time.timeAsDouble - _endTime > tailSeconds)
            {
                _done = true;
                Debug.Log("[Harness] Done");
                File.WriteAllText(Path.Combine(outputFolder, "done.txt"), "done");
            }
        }

        double VideoTime => _clock;

        // What the Quest's raw mic path does (MicStream autoGain): the soundtrack at its Quest raw level
        // (rawLevelOffsetDb), constant +maxAutoGainDb, compressor above compressorThresholdDb, soft limiter.
        [Tooltip("With rawLevelOffsetDb: feed the recognizer what the Quest would (raw level + MicStream's gain and compressor).")]
        public bool questGain;
        float _env = 0.0063f;
        float[] QuestGain(float[] x)
        {
            var m = director.mic;
            var pre = Mathf.Pow(10f, rawLevelOffsetDb / 20f);
            var makeup = Mathf.Pow(10f, m.maxAutoGainDb / 20f);
            var threshold = Mathf.Pow(10f, m.compressorThresholdDb / 20f);
            var attack = Mathf.Exp(-1f / (0.005f * 16000f));
            var release = Mathf.Exp(-1f / (m.agcReleaseSeconds * 16000f));
            var y = new float[x.Length];
            for (var i = 0; i < x.Length; i++)
            {
                var a = Mathf.Abs(x[i] * pre) * makeup;
                _env = (a > _env ? attack : release) * _env + (1f - (a > _env ? attack : release)) * a;
                var v = x[i] * pre * (_env > threshold ? makeup * threshold / _env : makeup);
                var av = Mathf.Abs(v);
                y[i] = av < 0.5f ? v : Mathf.Sign(v) * (0.5f + 0.5f * (float)System.Math.Tanh((av - 0.5f) / 0.5f));
            }
            return y;
        }

        // The simulated Quest mic: this chunk's level, shifted so the video's people sit at othersRawDb, plus
        // wearerBoostDb inside a wearer stretch.
        static float ChunkDb(float[] chunk)
        {
            double sum = 0;
            foreach (var v in chunk) sum += v * v;
            return chunk.Length > 0 && sum > 0 ? 10f * Mathf.Log10((float)(sum / chunk.Length)) : -120f;
        }

        float SimulatedRawDb(float[] chunk, float at)
        {
            var raw = ChunkDb(chunk) - _speechDb + othersRawDb;
            foreach (var w in wearerWindows) if (at >= w.x && at <= w.y) raw += wearerBoostDb;
            return Mathf.Max(raw, -120f);
        }

        // Typical speech level of the soundtrack: median dB of its loud 50 ms frames (the top half).
        static float SpeechLevel(float[] audio)
        {
            var levels = new List<float>();
            for (var i = 0; i + 800 <= audio.Length; i += 800)
            {
                double sum = 0;
                for (var j = i; j < i + 800; j++) sum += audio[j] * audio[j];
                if (sum > 0) levels.Add(10f * Mathf.Log10((float)(sum / 800)));
            }
            if (levels.Count == 0) return -30f;
            levels.Sort();
            return levels[levels.Count * 3 / 4];
        }

        void TagOne() { if (Tag(tagSide, tagName)) tagAt = -1f; }

        // "time:x0.62:Name" — the visible untagged face nearest that x of the picture (0–1), once one is within 0.08
        // (like tapping that person; "left"/"right" picks the wrong face when only one of two is detected yet).
        bool TagAt(float atX, string name)
        {
            TrackedPerson pick = null;
            var best = 0.08f;
            foreach (var p in tracker.People)
            {
                if (!p.Visible || p.Tag != null) continue;
                var d = Mathf.Abs(viewCamera.WorldToViewportPoint(p.HeadPosition).x - atX);
                if (d < best) { best = d; pick = p; }
            }
            if (pick == null) return false;
            pick.Tag = name;
            Debug.Log($"[Captions] Tagged Person {pick.Id} as {pick.Tag} (harness, x {atX:F2})");
            return true;
        }

        // Tag the tracked face sitting on this participant's labelled face (none other near either).
        bool TagTruth(int pid, string name)
        {
            if (_truth == null || !_truth.TryGetValue((int)System.Math.Floor((VideoTime - truthOffset) * truthFps), out var faces)) return false;
            var gt = faces.Find(f => f.pid == pid);
            if (gt.pid != pid) return false;
            foreach (var f in faces) if (f.pid != pid && Mathf.Abs(f.at.x - gt.at.x) < 0.1f) return false; // two faces close: wait
            TrackedPerson pick = null;
            foreach (var p in tracker.People)
            {
                if (Time.time - p.LastSeen > 0.15f) continue;
                var v = viewCamera.WorldToViewportPoint(p.HeadPosition);
                var y = ((1f - v.y) * H - (H - 720) / 2f) / 720f;
                if (Mathf.Abs(v.x - gt.at.x) > 0.1f || Mathf.Abs(y - gt.at.y) > 0.2f) continue;
                if (Mathf.Abs(v.x - gt.at.x) > 0.04f) return false; // someone near but not on it: wait
                if (pick != null) return false;
                pick = p;
            }
            if (pick == null || pick.Tag != null) return false;
            pick.Tag = name;
            Debug.Log($"[Captions] Tagged Person {pick.Id} as {pick.Tag} (harness, labelled face of participant {pid})");
            return true;
        }

        bool Tag(float side, string name)
        {
            TrackedPerson pick = null;
            var best = float.MinValue;
            foreach (var p in tracker.People)
            {
                if (!p.Visible || p.Tag != null) continue;
                var x = viewCamera.WorldToViewportPoint(p.HeadPosition).x * side;
                if (x > best) { best = x; pick = p; }
            }
            if (pick == null) return false;
            pick.Tag = name;
            Debug.Log($"[Captions] Tagged Person {pick.Id} as {pick.Tag} (harness)");
            return true;
        }
        float _nextPos;

        // Where each person in view is in the video picture (0–1, top-left origin), for scoring against labelled
        // face boxes (tools/ava_score.py): "pos <tab> Person N <tab> x <tab> y".
        void LogPositions()
        {
            foreach (var p in tracker.People)
            {
                if (Time.time - p.LastSeen > 0.3f) continue;
                var v = viewCamera.WorldToViewportPoint(p.HeadPosition);
                var x = v.x;
                var y = ((1f - v.y) * H - (H - 720) / 2f) / 720f;
                _log?.WriteLine($"{VideoTime:F2}\t{Time.time - VideoTime:F3}\tpos\t{p.Label}\t{x:F3}\t{y:F3}");
            }
        }

        // 16:9 video into the middle of a 4:3 frame, black above and below (like holding a TV in the Quest camera's view).
        void Letterbox()
        {
            _camShown = _shown;
            var prev = RenderTexture.active;
            RenderTexture.active = _camRt;
            GL.Clear(true, true, Color.black);
            GL.PushMatrix();
            GL.LoadPixelMatrix(0, W, H, 0);
            Graphics.DrawTexture(new Rect(0, (H - 720) / 2f, 1280, 720), _videoRt);
            GL.PopMatrix();
            RenderTexture.active = prev;
        }

        void Capture()
        {
            var old = viewCamera.targetTexture;
            viewCamera.targetTexture = _shotRt;
            FitBackdrop(); // for this render target's projection
            viewCamera.Render();
            viewCamera.targetTexture = old;
            var prev = RenderTexture.active;
            RenderTexture.active = _shotRt;
            var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            File.WriteAllBytes(Path.Combine(outputFolder, $"frame_{_shotIndex++:D4}.jpg"), tex.EncodeToJPG(85));
            Destroy(tex);
        }

        void OnLog(string message, string stack, LogType type)
        {
            if (!message.StartsWith("[Captions]") && !message.StartsWith("[Harness]")) return;
            // video time, app clock (Time.time) — word times in the log are on the app clock
            _log?.WriteLine($"{(_started ? VideoTime : -1):F2}\t{Time.time - (_started ? VideoTime : 0):F3}\t{message}");
        }

        static float[] ReadFloatWav(string path)
        {
            var b = File.ReadAllBytes(path);
            var i = 12;
            while (System.Text.Encoding.ASCII.GetString(b, i, 4) != "data") i += 8 + System.BitConverter.ToInt32(b, i + 4);
            var n = System.BitConverter.ToInt32(b, i + 4) / 4;
            var f = new float[n];
            System.Buffer.BlockCopy(b, i + 8, f, 0, n * 4);
            return f;
        }

        void OnDestroy()
        {
            Application.logMessageReceived -= OnLog;
            _log?.Dispose();
            if (_videoRt != null) _videoRt.Release();
            if (_camRt != null) _camRt.Release();
            if (_shotRt != null) _shotRt.Release();
        }
    }
}
