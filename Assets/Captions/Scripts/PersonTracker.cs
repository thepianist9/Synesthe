using System.Collections.Generic;
using Meta.XR;
using Unity.InferenceEngine;
using UnityEngine;
using Week1Bench;

namespace MetaStarter.Captions
{
    /// <summary>Anything that can stand in for the passthrough camera (tests: a video file played in the Editor).</summary>
    public interface IFrameSource
    {
        bool IsPlaying { get; }
        Texture Texture { get; }
        long FrameId { get; }   // changes with every new frame
        Pose Pose { get; }
        Ray ViewportPointToRay(Vector2 viewport, Pose pose);
        string Resolution { get; }
        /// <summary>Seconds between the moment the current frame shows and now (a video decoded late); 0 = live.</summary>
        float FrameAge => 0f;
    }

    /// <summary>A person seen by the passthrough camera, placed in the room.</summary>
    public class TrackedPerson
    {
        public int Id;
        internal float[] SavedVoice;     // voice profile kept from an earlier session (PersonTracker.Memory), if any
        internal int SavedVoiceCount;
        public Color Color;
        public Vector3 HeadPosition;     // world space, smoothed
        public float DistanceMeters;
        public float LastSeen;           // Time.time
        public float MouthActivity;      // 0..1, how much the mouth moved in the last ~0.6 s
        public float MouthOpen = -1f;    // lip gap / eye distance from the face mesh (0 closed … ~0.3 wide); -1 without lip landmarks
        internal readonly Queue<(float t, float v)> MouthSamples = new();
        internal readonly Queue<(float t, float v)> OpenHistory = new();    // MouthOpen over the last few seconds (lip sync)
        internal readonly Queue<(float t, float v)> ActivityHistory = new(); // MouthActivity over the last few seconds
        // Debug overlay only (PersonTracker.keepDebugGeometry): face box corners and lip outlines in the room, from the
        // latest camera frame; null when not measured.
        public Vector3[] BoxWorld;
        public Vector3[] OuterLipWorld, InnerLipWorld;
        internal Vector3 Velocity;       // One Euro filter state (filtered speed of HeadPosition)
        internal float LastFilterTime;
        // Active speaker detection (CaptionDirector runs LR-ASD on these): grey 112×112 mouth-area crops with their
        // camera time, the model's features of the last ~1.5 s (frame k = k/25 s, encoded once), and the speaking
        // logits it produced per moment (> 0 = speaking).
        internal readonly List<(float t, byte[] grey)> Crops = new();
        /// <summary>Contrast of the mouth-area crops (standard deviation of grey 0–255, smoothed); -1 before the first.
        /// Low in a dim room or on a dark, flat face — where lip reading is least reliable.</summary>
        public float CropContrast = -1f;
        internal readonly List<(int k, float[] audio, float[] visual)> AsdFeatures = new();
        internal int AsdNext = int.MinValue;   // first frame not yet encoded
        internal readonly List<(float t, float logit)> Speaking = new();
        public float SpeakingUntil = float.MinValue; // Speaking is filled up to here
        // Crop squares (centre x, centre y, side; source pixels) from detections not yet turned into crops, with the
        // camera frame each detection saw; the last one used (its frame's time); camera frames cropped up to here.
        internal readonly List<(long stamp, Vector3 box)> CropDets = new();
        internal (float t, Vector3 box) CropPrev = (float.MinValue, default);
        internal float CropsDoneTo = float.MinValue;
        // Crop steadying (image alignment): last frame's 64×64 patch, its centre, the steadied crop centre and side.
        internal float[] AlignPatch;
        internal Vector2 AlignQ, AlignC;
        internal float AlignSide, AlignT = float.MinValue;
        // Who they are by face (FaceEmbedder): SFace embeddings of this person, when the last one was asked for, the
        // similarity of the newest to the rest, where they were when it last matched, and votes for "this face is
        // someone else" (SwapTo: that person, null: a stranger).
        internal readonly List<float[]> Gallery = new();
        internal float EmbedAskedT = float.MinValue, LastOwnSim = float.NaN;
        internal Vector3 GoodPosition;
        internal int SwapVotes;
        internal TrackedPerson SwapTo;
        internal Rect FaceBoxPx;         // last face box (source pixels, top-left origin): where to look for it next
        public bool Visible => Time.time - LastSeen < 0.6f;
        /// <summary>Known from an earlier session (or out of view so long that where they were means nothing): only their
        /// face can bring them back, not the direction they were last seen in.</summary>
        internal bool NoPosition;

        /// <summary>Mean LR-ASD speaking logit between two times; NaN if not scored there.</summary>
        public float SpeakingBetween(float from, float to)
        {
            float sum = 0; var n = 0;
            foreach (var s in Speaking)
                if (s.t >= from && s.t <= to) { sum += s.logit; n++; }
            return n > 0 ? sum / n : float.NaN;
        }
        /// <summary>The name the viewer gave them in the setup (<see cref="ModeMenu"/>), e.g. "Doctor"; null = untagged.</summary>
        public string Tag;
        public string Label => Tag ?? $"Person {Id}";

        /// <summary>This face's "not talking" level: the 10th percentile of its mouth activity over the last ~12 s.
        /// A turned or small face makes the face mesh jitter, so its mouth always looks a bit busy.</summary>
        public float MouthBaseline()
        {
            if (ActivityHistory.Count < 20) return 0f;
            var v = new List<float>(ActivityHistory.Count);
            foreach (var s in ActivityHistory) v.Add(s.v);
            v.Sort();
            // Capped: someone who talks nonstop would otherwise raise their own "still" level and lose the caption.
            return Mathf.Min(v[v.Count / 10], 0.4f);
        }

        /// <summary>Mouth-open samples between two times (Time.time), for lip sync.</summary>
        public void OpenSamples(float from, float to, List<(float t, float v)> into)
        {
            into.Clear();
            foreach (var s in OpenHistory)
                if (s.t >= from && s.t <= to) into.Add(s);
        }

        /// <summary>Average mouth activity between two times (Time.time); -1 if the face wasn't tracked then.</summary>
        public float MouthActivityBetween(float from, float to, int minSamples = 1)
        {
            float sum = 0; var n = 0;
            foreach (var s in ActivityHistory)
                if (s.t >= from && s.t <= to) { sum += s.v; n++; }
            return n >= Mathf.Max(1, minSamples) ? sum / n : -1f;
        }
    }

    /// <summary>
    /// Finds faces in the passthrough camera image (BlazeFace on CPU: on Quest 3, 14 ms vs 83 ms on the GPU, and
    /// running it a few layers per frame was as slow; the app still holds 72 fps with two faces),
    /// places each one in 3D and keeps a stable ID per person.
    ///
    /// Distance comes from apparent face size: rays through the left and right edge of the face box span
    /// an angle θ, and an adult face is ~16 cm wide, so distance = width / (2·tan(θ/2)). No depth API needed.
    /// Mouth activity is how much the mouth opens and closes over time. With lip landmarks (MediaPipe Face Mesh on
    /// each face) that is the inner-lip gap; without them, a cheap proxy from BlazeFace's six keypoints
    /// (mouth–nose distance relative to eye distance).
    /// </summary>
    public partial class PersonTracker : MonoBehaviour
    {
        public PassthroughCameraAccess cameraAccess;
        [Tooltip("Optional IFrameSource used instead of the passthrough camera (Editor tests).")]
        public MonoBehaviour frameSource;
        public ModelAsset faceModel;
        public TextAsset faceAnchors;
        [Tooltip("BlazeFace full range (face_detection_full_range.tflite) for the search squares that look for new people; " +
                 "following known faces stays on the short-range model. Empty: search with the short-range model. " +
                 "EasyCom glasses video, the whole search cycle: 95% of faces found vs 90%, faces under 70 px wide 32% vs 0%. " +
                 "Launch extra: -e fullsearch 0")]
        public ModelAsset searchModel;
        public bool fullRangeSearch = true;
        [Tooltip("MediaPipe Face Mesh (face_landmark.tflite). Leave empty to use the BlazeFace mouth proxy.")]
        public ModelAsset landmarkModel;
        public bool useLipLandmarks = true;
        [Tooltip("Off = no face tracking at all (frame-rate tests). Launch extra: -e vision 0")]
        public bool visionEnabled = true;
        /// <summary>No face detection, lip reading or face crops while set (the models stay loaded): nothing is looked
        /// for until a conversation starts or a setup step needs faces (<see cref="CaptionDirector.facesWanted"/>).
        /// The camera keeps running: the glass panels show it.</summary>
        [System.NonSerialized] public bool paused;
        [Tooltip("Run the face mesh on at most this many faces per frame (largest first) — it costs CPU per face.")]
        public int maxLandmarkFaces = 3;
        [Tooltip("Keep face boxes and lip outlines in world space for the debug overlay.")]
        public bool keepDebugGeometry;
        // Mouth history kept per person: a finished sentence is matched to faces up to ~8 s back.
        const float HistorySeconds = 12f;

        [Header("Detection")]
        public float detectHz = 15f;
        [Tooltip("Re-detect each face seen in the last 0.5 s in a zoomed square this many face widths across. BlazeFace " +
                 "(short range, 128 px) needs a face ~16 px wide in its input: across the whole view that is < ~1.1 m; " +
                 "zoomed in, any face once found (EasyCom: 93% of faces vs 61% at 1.1–1.5 m, 10% at 1.5–2.2 m). 0 = off.")]
        public float trackZoom = 3f;
        [Tooltip("Faces re-detected per frame this way, nearest first.")]
        public int maxTrackedFaces = 4;
        [Tooltip("MediaPipe-style tracking: between searches, follow each face with the face mesh alone (its box from " +
                 "the last frame's landmarks) on every camera frame — smooth boxes and lip readings at the camera rate " +
                 "for about the cost of the mesh. The face detector only runs every searchSeconds (or when a face is " +
                 "lost) to find new faces. Off: detector + mesh every frame at detectHz.")]
        // Off. On the EasyCom harness it picked the wrong talker more often (cause not found yet), and on Quest 3
        // (2026-11-08, 6-min Bubbles stress, face detector on the GPU) it cost more than it saved: the mesh is the CPU
        // part (~55 ms a face), so 376–416 words came through vs 521 with it off, and the speaker model ran less than
        // half as often. That was with the mesh on the CPU: on the headset it is now on by default with the mesh on the
        // GPU (see the launch extras below); this field is the Editor/harness default.
        public bool meshTracking = false;
        public float searchSeconds = 0.5f;
        [Tooltip("Tagging by hand, with mesh tracking: once faces are followed there are no regular searches. The detector " +
                 "runs again when a face is lost, the view is empty, or a tagging step is open. Seconds between searches " +
                 "otherwise (0 = never).")]
        public float handSearchSeconds = 0f;
        /// <summary>Set while a tagging step is open: search for faces as often as auto tagging does.</summary>
        [System.NonSerialized] public bool lookingForFaces;
        [Tooltip("Auto tagging: only faces nearer than this (m) become new people; people already followed stay at any " +
                 "distance. 0 = any distance.")]
        public float autoTagMaxDistance = 2f;
        int _confirmSearches;
        // How long until the next search: auto tagging (or a tagging step, or nobody followed) keeps looking for new
        // faces; tagging by hand only follows the faces it has.
        // Also searches while someone seen in the last 5 s isn't followed (their track was lost, or drifted onto
        // another face and was dropped): before, one dropped track meant that person was never looked for again
        // (2026-11-09 harness: a two-person talk went to one person, 92% of words unsure). And every
        // meshCheckSeconds anyway, since a track can drift onto the wrong face without being lost.
        float SearchInterval()
        {
            if (autoTag || lookingForFaces || _meshTracks.Count == 0) return searchSeconds;
            foreach (var p in People)
                if (Time.time - p.LastSeen < 5f && !_meshTracks.Exists(t => t.Person == p)) return searchSeconds;
            return handSearchSeconds > 0f ? Mathf.Min(handSearchSeconds, meshCheckSeconds) : meshCheckSeconds;
        }
        [Tooltip("Mesh tracking: search with the face detector at least this often (s), to catch a track on the wrong face.")]
        public float meshCheckSeconds = 2f;
        bool TooFarForNew(Detection d) => autoTag && autoTagMaxDistance > 0f && d.Distance > autoTagMaxDistance;
        [Tooltip("Mesh tracking rate cap (Hz); the Quest passthrough camera gives up to 30 frames a second.")]
        public float trackHz = 10f; // the mesh costs ~60 ms of CPU per face on Quest 3: at 30 Hz it would starve speech-to-text
        /// <summary>Share of detectHz/trackHz to run at (0.25–1). Lowered by CaptionDirector while speech-to-text is
        /// behind: both compete for the same CPU cores, and late captions are worse than a slower face update.</summary>
        [System.NonSerialized] public float visionShare = 1f;
        [Tooltip("Finding new faces: alternate the whole centre square with zoomed tiles (3 x 2, each 60% of the image " +
                 "height) across the full camera width — further faces, and the image sides the centre square leaves out.")]
        public bool searchTiles = true;
        public float minScore = 0.6f;
        [Tooltip("Camera images are read with the top row first; viewport y grows upwards.")]
        public bool flipY = true;

        [Header("Geometry")]
        [Tooltip("Measure each face's distance with the depth sensor (MRUK EnvironmentRaycastManager). The camera sits " +
                 "beside the eyes, so a wrong distance shifts the face box sideways and up/down — the face-size guess " +
                 "below is wrong for faces in videos, children or very large/small heads.")]
        public EnvironmentRaycastManager depthRaycast;
        public bool useDepth = true;
        [Tooltip("Fallback when depth isn't available: distance from apparent face size, assuming this face width (m).")]
        public float faceWidthMeters = 0.16f;
        [Tooltip("Ignore faces smaller than this (degrees across) — photos, thumbnails, people > ~2.5 m away.")]
        public float minFaceAngleDegrees = 4f;
        [Tooltip("Off (default): faces on screens and in photos (a video call, a TV) are tagged like people in the room. " +
                 "On: with the depth sensor, faces whose real width isn't a person's are ignored.")]
        public bool ignoreScreenFaces = false;
        [Tooltip("With the depth sensor, a face's real width is known: narrower than this (m) is a face in a video or " +
                 "photo (a laptop, phone or poster), not a person in the room, and never becomes a person.")]
        public float minFaceWidthMeters = 0.09f;
        [Tooltip("…and wider than this (m) isn't a face either (a pattern on a wall, a big screen up close).")]
        public float maxFaceWidthMeters = 0.35f;
        float _sizeLogAt = -99f;
        public float matchRadiusMeters = 0.45f;
        [Tooltip("ByteTrack-style matching (Zhang et al. 2022), in room space: predict each person's position from " +
                 "their speed, match all faces to people at once (closest pairs first), give faces scoring between " +
                 "lowScore and minScore (turned heads, blur, partly hidden) a second pass that can only continue a " +
                 "person, and start a new person only after confirmHits detections in a row. Off: the older greedy " +
                 "nearest match, one face at a time, high-scoring faces only.")]
        public bool byteTrack = true;
        public float lowScore = 0.35f;
        public int confirmHits = 3;
        public float forgetAfterSeconds = 3f;
        [Tooltip("People out of view are remembered this long (s). A face that reappears near where one was is the " +
                 "same person again — same number, colour and voice link — instead of a new person.")]
        public float rememberSeconds = 600f;
        [Tooltip("Re-identification for tagged people: how far (degrees, seen from you) a returning face may be from " +
                 "the direction they were in. By direction, not room position: face-size distance is often off by a " +
                 "metre at a few metres, while seated people stay within ~5° (EasyCom), and neighbours are 20–40° apart.")]
        public float taggedReidDegrees = 14f;
        [Tooltip("The same for untagged people.")]
        public float reidDegrees = 10f;
        [Header("Smoothing (One Euro filter)")]
        [Tooltip("Lower = steadier when still (Hz). Face distance from face size is noisy, so keep this low.")]
        public float minCutoffHz = 0.6f;
        [Tooltip("Higher = follows quick movements with less lag.")]
        public float speedCoefficient = 1.5f;

        public readonly List<TrackedPerson> People = new();
        /// <summary>Someone was tagged (in view or remembered out of view): then only tagged people are captioned.</summary>
        public bool AnyTagged => People.Exists(p => p.Tag != null) || _away.Exists(p => p.Tag != null && !p.NoPosition);
        /// <summary>Auto tagging (the setup's "Tag automatically"): everyone who comes into view is tagged "Person 1, 2…" with a
        /// colour of their own, up to <see cref="autoTagLimit"/> people. Off: only the people you tag by hand.</summary>
        [System.NonSerialized] public bool autoTag;
        public int autoTagLimit = 6;
        int _autoCount;

        /// <summary>Forget the tags (a mode was just started): everyone is untagged and back to their default colour.
        /// With <see cref="rememberPeople"/> on, people known by face keep their name; the ones out of view wait until
        /// their face is seen again (so they don't count as "someone is tagged" meanwhile).</summary>
        public void ClearTags()
        {
            foreach (var p in People) if (!Remembered(p)) Untag(p);
            foreach (var p in _away) if (!Remembered(p)) Untag(p); else p.NoPosition = true;
            _autoCount = 0;
            foreach (var p in _away) BumpAutoCount(p);
            foreach (var p in People) BumpAutoCount(p);
        }

        /// <summary>Take a person's name off (and out of the remembered people).</summary>
        public void Untag(TrackedPerson p) { p.Tag = null; p.Color = Palette[(p.Id - 1) % Palette.Length]; }

        /// <summary>Turn auto tagging on or off. On: the people in view now are tagged at once.</summary>
        public void SetAutoTag(bool on)
        {
            autoTag = on;
            if (on) foreach (var p in People) AutoTag(p);
        }

        void AutoTag(TrackedPerson p)
        {
            if (!autoTag || p.Tag != null) return;
            var used = new List<Color>();
            foreach (var q in People) if (q.Tag != null) used.Add(q.Color);
            foreach (var q in _away) if (q.Tag != null && !q.NoPosition) used.Add(q.Color); // remembered from before: not here
            if (used.Count >= autoTagLimit) return;
            p.Tag = $"Person {++_autoCount}";
            p.Color = System.Array.Find(Palette, c => !used.Contains(c));
            if (p.Color.a == 0f) p.Color = Palette[(_autoCount - 1) % Palette.Length];
            Debug.Log($"[Captions] Auto-tagged Person {p.Id} as {p.Tag}");
        }
        public bool Ready { get; private set; }
        public string Status { get; private set; } = "starting";
        public float DetectionsPerSecond { get; private set; }
        /// <summary>Share of recent frames handled by mesh tracking alone (no face detector), 0–1.</summary>
        public float MeshTrackShare { get; private set; }
        public bool LipLandmarks => _landmarker != null;
        /// <summary>Average face-mesh time per face, ms (includes waiting for the result).</summary>
        public float LandmarkMs { get; private set; }
        /// <summary>Average face detection time, ms (includes waiting for the result).</summary>
        public float DetectMs { get; private set; }
        /// <summary>Main-thread time per detection / per face mesh, ms (the part that can drop frames).</summary>
        public float DetectMainMs { get; private set; }
        public float LandmarkMainMs { get; private set; }
        /// <summary>Share of recent faces placed with the depth sensor (rest: face-size guess), 0–1.</summary>
        public float DepthShare { get; private set; }
        /// <summary>Why the last face did or didn't get a depth distance (diag log).</summary>
        public string DepthStatus { get; private set; } = "";

        static readonly Color[] Palette =
        {
            new(0.20f, 0.83f, 0.60f), new(0.96f, 0.45f, 0.71f), new(0.38f, 0.65f, 0.98f),
            new(0.98f, 0.75f, 0.14f), new(0.65f, 0.55f, 0.98f), new(0.98f, 0.45f, 0.35f),
        };

        FaceDetector _detector;
        FaceDetector _search; // full range for the search square, or null (the short-range detector searches)
        readonly List<FaceDetector> _zoomDetectors = new();
        FaceLandmarker _landmarker;
        /// <summary>Keep grey face crops for active speaker detection (set by CaptionDirector).</summary>
        [System.NonSerialized] public bool captureFaceCrops;
        /// <summary>Debug: log each detection's eye points and box width (source pixels) that set the crop.</summary>
        [System.NonSerialized] public bool logCropInputs;
        int _nextId = 1;
        readonly List<TrackedPerson> _away = new(); // out of view, remembered for rememberSeconds
        int _secCount;
        float _secStart;

        IFrameSource _src;
        bool CamPlaying => _src?.IsPlaying ?? cameraAccess.IsPlaying;
        Texture CamTexture() => _src != null ? _src.Texture : cameraAccess.GetTexture();
        Pose CamPose() => _src?.Pose ?? cameraAccess.GetCameraPose();
        Ray CamRay(Vector2 viewport, Pose pose) => _src?.ViewportPointToRay(viewport, pose) ?? cameraAccess.ViewportPointToRay(viewport, pose);
        long CamStamp() => _src?.FrameId ?? cameraAccess.Timestamp.Ticks;

        async void Start()
        {
            _src = frameSource as IFrameSource;
            if ((cameraAccess == null && _src == null) || faceModel == null || faceAnchors == null)
            {
                Status = "missing camera or face model reference";
                return;
            }
            if (_src != null)
            {
                while (!_src.IsPlaying) { if (this == null) return; await Awaitable.NextFrameAsync(); }
                await RunTracking();
                return;
            }
            // The camera can't start while the app is paused (headset off, "finding position in room" dialog);
            // the SDK then disables itself. Keep retrying instead of giving up — the app may resume much later.
            cameraAccess.enabled = true;
            var t0 = Time.realtimeSinceStartup;
            var lastTry = t0;
            var warned = false;
            while (!cameraAccess.IsPlaying)
            {
                if (this == null) return;
                if (!cameraAccess.enabled && Time.realtimeSinceStartup - lastTry > 2f)
                {
                    lastTry = Time.realtimeSinceStartup;
                    cameraAccess.enabled = true;
                }
                if (!warned && Time.realtimeSinceStartup - t0 > 15f)
                {
                    warned = true;
                    Status = "passthrough camera not started yet (HEADSET_CAMERA permission? Quest 3/3S only) — still trying";
                    Debug.LogWarning($"[Captions] {Status}");
                }
                await Awaitable.NextFrameAsync();
            }
            if (warned) Debug.Log($"[Captions] Passthrough camera started after {Time.realtimeSinceStartup - t0:F0} s");
            await RunTracking();
        }

        // Testing aid: adb shell am start … -e vision 0 (read here: the tracker starts before the caption launch extras).
        void ReadLaunchExtras()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var unity = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = unity.GetStatic<AndroidJavaObject>("currentActivity");
                using var intent = activity.Call<AndroidJavaObject>("getIntent");
                var vision = intent.Call<string>("getStringExtra", "vision");
                if (!string.IsNullOrEmpty(vision)) visionEnabled = vision != "0";
                // A/B lip sampling: -e mesh 1 (follow faces by mesh between searches), -e zoom 0 (no zoom squares).
                var mesh = intent.Call<string>("getStringExtra", "mesh");
                // On by default on the headset since 2026-11-09, with the face mesh on the GPU (VisionBackend.Mesh), so it
                // no longer takes CPU from speech-to-text; -e mesh 0 turns it off.
                meshTracking = string.IsNullOrEmpty(mesh) || mesh != "0";
                var full = intent.Call<string>("getStringExtra", "fullsearch");
                if (!string.IsNullOrEmpty(full)) fullRangeSearch = full != "0";
                float? F(string key) => float.TryParse(intent.Call<string>("getStringExtra", key) ?? "",
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
                // -e trackhz 10 (mesh rate), -e search 0.5 (auto search interval), -e handsearch 0, -e nearby 2 (m).
                trackHz = F("trackhz") ?? trackHz;
                searchSeconds = F("search") ?? searchSeconds;
                handSearchSeconds = F("handsearch") ?? handSearchSeconds;
                autoTagMaxDistance = F("nearby") ?? autoTagMaxDistance;
                var zoom = intent.Call<string>("getStringExtra", "zoom");
                if (float.TryParse(zoom ?? "", System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var z)) trackZoom = z;
                if (!string.IsNullOrEmpty(mesh) || !string.IsNullOrEmpty(zoom))
                    Debug.Log($"[Captions] Launch: mesh tracking {meshTracking}, zoom {trackZoom}");
                Debug.Log($"[Captions] Tracking: mesh {meshTracking} at ≤ {trackHz} Hz, search every {searchSeconds} s (by hand: {(handSearchSeconds > 0 ? handSearchSeconds + " s" : "only when needed")}), new people within {autoTagMaxDistance} m when auto tagging");
            }
            catch (System.Exception e) { Debug.LogWarning($"[Captions] launch extras: {e.Message}"); }
#endif
        }

        async Awaitable RunTracking()
        {
            ReadLaunchExtras();
            if (!visionEnabled)
            {
                Status = "vision off (test)";
                Debug.Log("[Captions] Person tracker off (-e vision 0)");
                return;
            }
            _detector = new FaceDetector(faceModel, faceAnchors, VisionBackend.Detector) { ScoreThreshold = byteTrack ? lowScore : minScore };
            Status = "warming up face model";
            await _detector.Detect(CamTexture()); // first run is slow; keep it out of the loop
            if (fullRangeSearch && searchModel != null)
            {
                _search = new FaceDetector(searchModel, null, VisionBackend.Detector) { ScoreThreshold = _detector.ScoreThreshold };
                await _search.Detect(CamTexture());
            }
            // On the GPU, one detector per zoom square: all squares of a search are sent together and read back
            // together. One after another, each waited for its own readback: with 3 faces a search took 250–400 ms on
            // the headset (2026-11-08), longer than the 16 kept camera frames, so most detections lost their frame
            // and the speaker model got no crops ("crops don't cover the chunk", 1,257 frames lost in 6 minutes).
            if (_detector.Backend != BackendType.CPU && trackZoom > 0)
                for (var i = 0; i < maxTrackedFaces; i++)
                {
                    var z = new FaceDetector(faceModel, faceAnchors, _detector.Backend) { ScoreThreshold = _detector.ScoreThreshold };
                    await z.Detect(CamTexture());
                    _zoomDetectors.Add(z);
                }
            if (useLipLandmarks && landmarkModel != null)
            {
                _landmarker = new FaceLandmarker(landmarkModel, VisionBackend.Mesh);
                var t = CamTexture();
                await _landmarker.Run(t, new Rect(0, 0, t.height, t.height));
            }
            Ready = true;
            Status = "tracking";
            _secStart = Time.realtimeSinceStartup;
            Debug.Log($"[Captions] Person tracker ready · camera {(_src != null ? _src.Resolution : cameraAccess.CurrentResolution.ToString())} · lips: {(_landmarker != null ? "face mesh" : "BlazeFace proxy")} · face detector on {VisionBackend.Detector} (search: {(_search != null ? "full range" : "short range")}), face mesh on {VisionBackend.Mesh}, speaker model on {VisionBackend.Type}, mesh tracking {meshTracking}");

            var lastStamp = long.MinValue;
            var nextDetect = 0f;
            var lastFrame = Time.realtimeSinceStartup;
            var lastRestart = 0f;
            while (this != null && enabled)
            {
                if (paused)
                {
                    lastFrame = Time.realtimeSinceStartup; // not a stalled camera
                    DetectionsPerSecond = 0f;
                    await Awaitable.NextFrameAsync();
                    continue;
                }
                // Watchdog: after a pause the camera may stay stopped or disabled. No new frame for 3 s → restart it.
                if (_src == null && Time.realtimeSinceStartup - lastFrame > 3f && Time.realtimeSinceStartup - lastRestart > 3f)
                {
                    lastRestart = Time.realtimeSinceStartup;
                    Debug.LogWarning($"[Captions] No camera frame for {Time.realtimeSinceStartup - lastFrame:F0} s (playing {cameraAccess.IsPlaying}, enabled {cameraAccess.enabled}) — restarting camera");
                    cameraAccess.enabled = false;
                    await Awaitable.NextFrameAsync();
                    cameraAccess.enabled = true;
                }
                if (CamPlaying && CamStamp() != lastStamp && Time.realtimeSinceStartup >= nextDetect)
                {
                    lastStamp = CamStamp();
                    lastFrame = Time.realtimeSinceStartup;
                    var tracking = meshTracking && _landmarker != null;
                    nextDetect = Time.realtimeSinceStartup + 1f / ((tracking ? trackHz : detectHz) * Mathf.Clamp(visionShare, 0.25f, 1f));
                    // Pose and texture belong to the same camera frame; grab both before awaiting.
                    var pose = CamPose();
                    var tex = CamTexture();
                    var grabbed = Time.time; // the face samples belong to this moment, not to when inference finishes
                    var grabStamp = CamStamp();
                    List<FaceDetector.Face> faces; Rect crop; float[] opens; Vector2[][] points; TrackedPerson[] forced = null;
                    var search = !tracking || Time.realtimeSinceStartup >= _nextSearch;
                    if (search)
                    {
                        var dsw = System.Diagnostics.Stopwatch.StartNew();
                        float mainMs;
                        (faces, crop, mainMs) = await DetectAll(tex);
                        DetectMs = Mathf.Lerp(DetectMs, (float)dsw.Elapsed.TotalMilliseconds, 0.1f);
                        DetectMainMs = Mathf.Lerp(DetectMainMs, mainMs, 0.1f);
                        (opens, points) = await MouthOpenings(faces, crop, tex);
                        _nextSearch = Time.realtimeSinceStartup + searchSeconds;
                        if (tracking) forced = MeshOwners(faces, tex);
                    }
                    else
                    {
                        (faces, opens, points, forced) = await TrackByMesh(tex);
                        crop = new Rect(0, 0, tex.width, tex.height);
                    }
                    MeshTrackShare = Mathf.Lerp(MeshTrackShare, search ? 0f : 1f, 0.05f);
                    _procStamp = grabStamp;
                    var owners = Process(faces, opens, points, crop, tex, pose, grabbed, forced, holdDistance: !search);
                    if (tracking && search)
                    {
                        SeedMeshTracks(faces, points, owners, tex);
                        _nextSearch = Time.realtimeSinceStartup + SearchInterval();
                        // A new face not confirmed yet: search again next frame (its zoom square confirms it), not in 0.5 s.
                        // At most 3 times in a row: a face that never becomes a person (too far, a photo) can't keep
                        // the detector running on every frame.
                        var unconfirmed = false;
                        for (var i = 0; i < faces.Count; i++)
                            if (owners[i] == null && faces[i].Score >= minScore) { unconfirmed = true; break; }
                        if (unconfirmed && _confirmSearches < 3) { _nextSearch = 0f; _confirmSearches++; }
                        else if (!unconfirmed) _confirmSearches = 0;
                    }
                    _secCount++;
                }
                else
                {
                    await Awaitable.NextFrameAsync();
                }

                if (Time.realtimeSinceStartup - _secStart >= 1f)
                {
                    DetectionsPerSecond = _secCount / (Time.realtimeSinceStartup - _secStart);
                    _secCount = 0;
                    _secStart = Time.realtimeSinceStartup;
                }
            }
        }

        // Mesh tracking: each face followed by the face mesh, its box placed like the detector's box (fixed offset
        // and scale from the landmarks, measured when the detector last saw it) so distances don't jump between the two.
        // Each track keeps the person it started from (MediaPipe-style): its faces are that person's without matching
        // by position, so a noisy box can't turn into a new person and starve the tagged one.
        class MeshTrack { public Rect Box0; public Vector2 C0; public float E0; public Rect Box; public TrackedPerson Person; public float Roll; } // Roll: last eye line, for the next mesh crop
        readonly List<MeshTrack> _meshTracks = new();
        readonly List<(TrackedPerson p, Rect box, float t)> _lostTracks = new(); // box: where the detector last saw it
        float _nextSearch;
        static Vector2 MeshCentre(Vector2[] p) => (p[FaceLandmarker.RightEyeOuter] + p[FaceLandmarker.LeftEyeOuter] + p[1]) / 3f;
        static float MeshEyes(Vector2[] p) => Vector2.Distance(p[FaceLandmarker.RightEyeOuter], p[FaceLandmarker.LeftEyeOuter]);

        // Search frame: a found face on a mesh track's box (in the picture) is that track's person — matching it by room
        // position instead would compare a face-size distance with the held one and could start a duplicate.
        TrackedPerson[] MeshOwners(List<FaceDetector.Face> faces, Texture tex)
        {
            if (_meshTracks.Count == 0) return null;
            var owners = new TrackedPerson[faces.Count];
            var used = new HashSet<TrackedPerson>();
            for (var i = 0; i < faces.Count; i++)
            {
                var b = faces[i].Box;
                var px = new Rect(b.x * tex.width, b.y * tex.height, b.width * tex.width, b.height * tex.height);
                foreach (var t in _meshTracks)
                {
                    if (used.Contains(t.Person) || !People.Contains(t.Person)) continue;
                    if (Vector2.Distance(px.center, t.Box.center) < 0.5f * Mathf.Max(px.width, t.Box.width)) { owners[i] = t.Person; used.Add(t.Person); break; }
                }
            }
            // A track lost a moment ago (the mesh slid off a turning face before letting go): its person's position
            // went with the mesh, so room matching would start a duplicate. A face near where it was is that person.
            _lostTracks.RemoveAll(l => Time.time - l.t > 1.5f || !People.Contains(l.p));
            for (var i = 0; i < faces.Count; i++)
            {
                if (owners[i] != null) continue;
                var b = faces[i].Box;
                var px = new Rect(b.x * tex.width, b.y * tex.height, b.width * tex.width, b.height * tex.height);
                var best = -1; var bestD = float.MaxValue;
                for (var j = 0; j < _lostTracks.Count; j++)
                {
                    var l = _lostTracks[j];
                    if (used.Contains(l.p) || _meshTracks.Exists(t => t.Person == l.p)) continue;
                    var d = Vector2.Distance(px.center, l.box.center);
                    if (d < Mathf.Max(px.width, l.box.width) && d < bestD) { bestD = d; best = j; }
                }
                if (best < 0) continue;
                owners[i] = _lostTracks[best].p; used.Add(owners[i]);
                _lostTracks.RemoveAt(best);
            }
            return owners;
        }

        void SeedMeshTracks(List<FaceDetector.Face> faces, Vector2[][] points, TrackedPerson[] owners, Texture tex)
        {
            _meshTracks.Clear();
            for (var i = 0; i < faces.Count; i++)
            {
                // No mesh on it (low score, over maxLandmarkFaces) or not a person yet: the next search.
                if (points[i] == null || owners[i] == null || _meshTracks.Exists(t => t.Person == owners[i])) continue;
                var b = faces[i].Box;
                var px = new Rect(b.x * tex.width, b.y * tex.height, b.width * tex.width, b.height * tex.height);
                var e = MeshEyes(points[i]);
                if (e < 1f) continue;
                _meshTracks.Add(new MeshTrack { Box0 = px, C0 = MeshCentre(points[i]), E0 = e, Box = px, Person = owners[i], Roll = FaceLandmarker.Roll(points[i]) });
            }
        }

        // One frame of mesh-only tracking: the mesh on each tracked face's last box → new box, lips and keypoints.
        async Awaitable<(List<FaceDetector.Face>, float[], Vector2[][], TrackedPerson[])> TrackByMesh(Texture tex)
        {
            _meshTracks.RemoveAll(t => !People.Contains(t.Person)); // forgotten meanwhile
            var persons = new List<TrackedPerson>();
            float w = tex.width, h = tex.height;
            var faces = new List<FaceDetector.Face>();
            var opens = new List<float>();
            var pts = new List<Vector2[]>();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var runs = 0;
            for (var i = 0; i < _meshTracks.Count; i++)
            {
                var t = _meshTracks[i];
                var r = await _landmarker.Run(tex, t.Box, t.Roll);
                runs++;
                LandmarkMainMs = Mathf.Lerp(LandmarkMainMs, (float)_landmarker.LastMainThreadMs, 0.1f);
                var e = r.Presence > 0.5f ? MeshEyes(r.Points) : 0f;
                if (e < 1f) { _lostTracks.Add((t.Person, t.Box0, Time.time)); _meshTracks.RemoveAt(i--); _nextSearch = 0f; continue; } // lost: search on the next frame
                var size = t.Box0.size * (e / t.E0);
                var centre = MeshCentre(r.Points) + (t.Box0.center - t.C0) * (e / t.E0);
                var box = new Rect(centre - size / 2f, size);
                // Two tracks drifted onto one face: keep the first.
                if (faces.Exists(f => Vector2.Distance(f.Box.center, new Vector2(centre.x / w, centre.y / h)) * w < 0.5f * size.x))
                { _meshTracks.RemoveAt(i--); continue; }
                t.Box = box;
                t.Roll = FaceLandmarker.Roll(r.Points);
                var p = r.Points;
                Vector2 N(Vector2 q) => new(q.x / w, q.y / h);
                var k = new[]
                {
                    N((p[33] + p[133]) / 2f), N((p[362] + p[263]) / 2f), N(p[1]),
                    N((p[FaceLandmarker.UpperInnerLip] + p[FaceLandmarker.LowerInnerLip]) / 2f), N(p[234]), N(p[454]),
                };
                faces.Add(new FaceDetector.Face
                {
                    Box = Rect.MinMaxRect(box.xMin / w, box.yMin / h, box.xMax / w, box.yMax / h),
                    Keypoints = k, Mouth = k[3], Score = Mathf.Max(minScore, r.Presence),
                });
                opens.Add(r.MouthOpen);
                pts.Add(p);
                persons.Add(t.Person);
            }
            if (runs > 0) LandmarkMs = Mathf.Lerp(LandmarkMs, (float)sw.Elapsed.TotalMilliseconds / runs, 0.1f);
            foreach (var f in faces)
                _recentFaces.Add((Rect.MinMaxRect(f.Box.xMin * w, f.Box.yMin * h, f.Box.xMax * w, f.Box.yMax * h), Time.time));
            return (faces, opens.ToArray(), pts.ToArray(), persons.ToArray());
        }

        int _searchStep;
        // Faces found in the last few frames (source px), zoomed on next frame — also faces not yet a person, so one
        // that only a side tile caught is seen again at once and confirmed instead of waiting for the tile to return.
        readonly List<(Rect box, float t)> _recentFaces = new();

        // One frame's faces: a search square (in turn: the centre square, then each zoomed tile) plus a zoomed square
        // around each face seen just now. Results are mapped to the whole image (crop = the full texture, x and y
        // normalised separately) and merged, keeping the best-scoring box where they overlap.
        async Awaitable<(List<FaceDetector.Face> faces, Rect crop, float mainMs)> DetectAll(Texture tex)
        {
            float w = tex.width, h = tex.height;
            var squares = new List<Rect>();
            var step = _searchStep++;
            if (!searchTiles || step % 2 == 0) squares.Add(new Rect((w - h) * 0.5f, 0, h, h));
            else
            {
                var side = 0.6f * h; var tile = step / 2 % 6;
                squares.Add(new Rect((tile % 3) * (w - side) / 2f, tile < 3 ? 0 : h - side, side, side));
            }
            if (trackZoom > 0)
            {
                _recentFaces.RemoveAll(r => Time.time - r.t > 0.5f);
                var recent = new List<Rect>();
                for (var i = _recentFaces.Count - 1; i >= 0; i--) // newest first, one box per face
                {
                    var r = _recentFaces[i].box;
                    var dup = false;
                    foreach (var k in recent) if (Vector2.Distance(k.center, r.center) < Mathf.Max(k.width, r.width)) { dup = true; break; }
                    if (!dup) recent.Add(r);
                }
                recent.Sort((a, b) => b.width.CompareTo(a.width));
                for (var i = 0; i < Mathf.Min(maxTrackedFaces, recent.Count); i++)
                {
                    var b = recent[i];
                    var side = Mathf.Clamp(b.width * trackZoom, 48f, h);
                    if (side >= 0.9f * h) continue; // already big in the whole view: the search square finds it
                    squares.Add(new Rect(b.center.x - side / 2, b.center.y - side / 2, side, side));
                }
            }
            var all = new List<FaceDetector.Face>();
            var main = 0f;
            // GPU: every square is cut from this same frame and sent now; the readbacks are awaited afterwards.
            var dets = new FaceDetector[squares.Count];
            var sent = new Awaitable<List<FaceDetector.Face>>[squares.Count];
            for (var i = 0; i < squares.Count; i++)
            {
                dets[i] = i == 0 ? _search ?? _detector // the search square: full range when loaded
                    : i <= _zoomDetectors.Count ? _zoomDetectors[i - 1] : _detector; // CPU: none, one after another
                if (dets[i] != _detector || i == 0) sent[i] = dets[i].Detect(tex, squares[i]);
            }
            for (var i = 0; i < squares.Count; i++)
            {
                var found = await (sent[i] ?? dets[i].Detect(tex, squares[i]));
                main += (float)dets[i].LastMainThreadMs;
                var c = dets[i].LastCrop;
                foreach (var f in found)
                {
                    Vector2 Map(Vector2 q) => new((c.x + q.x * c.width) / w, (c.y + q.y * c.height) / h);
                    var min = Map(f.Box.min); var max = Map(f.Box.max);
                    var face = f;
                    face.Box = Rect.MinMaxRect(min.x, min.y, max.x, max.y);
                    face.Mouth = Map(f.Mouth);
                    face.Keypoints = System.Array.ConvertAll(f.Keypoints, Map);
                    all.Add(face);
                }
            }
            all.Sort((a, b) => b.Score.CompareTo(a.Score));
            var kept = new List<FaceDetector.Face>();
            foreach (var f in all)
            {
                var dup = false;
                foreach (var k in kept)
                {
                    // Same face seen in two squares: boxes of different sizes can overlap little, so also merge when
                    // one centre lies within half a face width of the other.
                    var dc = Vector2.Scale(f.Box.center - k.Box.center, new Vector2(w, h)).magnitude;
                    if (Overlap(f.Box, k.Box) > 0.3f || dc < 0.5f * Mathf.Max(f.Box.width, k.Box.width) * w) { dup = true; break; }
                }
                if (!dup) kept.Add(f);
            }
            foreach (var f in kept)
                _recentFaces.Add((Rect.MinMaxRect(f.Box.xMin * w, f.Box.yMin * h, f.Box.xMax * w, f.Box.yMax * h), Time.time));
            return (kept, new Rect(0, 0, w, h), main);
        }

        // Intersection over union in image pixels (boxes are normalised per axis).
        static float Overlap(Rect a, Rect b)
        {
            var inter = Mathf.Max(0, Mathf.Min(a.xMax, b.xMax) - Mathf.Max(a.xMin, b.xMin)) *
                        Mathf.Max(0, Mathf.Min(a.yMax, b.yMax) - Mathf.Max(a.yMin, b.yMin));
            var union = a.width * a.height + b.width * b.height - inter;
            return union <= 0 ? 0 : inter / union;
        }

        // Face mesh on the largest faces → lip gap per face (-1 = not measured).
        async Awaitable<(float[] opens, Vector2[][] points)> MouthOpenings(List<FaceDetector.Face> faces, Rect crop, Texture tex)
        {
            var opens = new float[faces.Count];
            var points = new Vector2[faces.Count][];
            for (var i = 0; i < opens.Length; i++) opens[i] = -1f;
            if (_landmarker == null || faces.Count == 0) return (opens, points);
            var order = new List<int>();
            for (var i = 0; i < faces.Count; i++) if (faces[i].Score >= minScore) order.Add(i); // low-score faces: no lips
            order.Sort((a, b) => faces[b].Box.width.CompareTo(faces[a].Box.width));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var n = Mathf.Min(maxLandmarkFaces, order.Count);
            if (n == 0) return (opens, points);
            for (var j = 0; j < n; j++)
            {
                var b = faces[order[j]].Box; // crop-normalised, top-left origin → source pixels
                var px = new Rect(crop.x + b.x * crop.width, crop.y + b.y * crop.height, b.width * crop.width, b.height * crop.height);
                // The crop is levelled with the detector's eye keypoints (0 right eye, 1 left eye; the crop is square).
                var kp = faces[order[j]].Keypoints;
                var roll = kp != null && kp.Length > 1 ? Mathf.Atan2(kp[1].y - kp[0].y, kp[1].x - kp[0].x) : 0f;
                var r = await _landmarker.Run(tex, px, roll);
                LandmarkMainMs = Mathf.Lerp(LandmarkMainMs, (float)_landmarker.LastMainThreadMs, 0.1f);
                if (r.Presence > 0.5f) { opens[order[j]] = r.MouthOpen; points[order[j]] = r.Points; }
            }
            LandmarkMs = Mathf.Lerp(LandmarkMs, (float)sw.Elapsed.TotalMilliseconds / n, 0.1f);
            return (opens, points);
        }

        // MediaPipe Face Mesh lip contours.
        static readonly int[] OuterLip = { 61, 146, 91, 181, 84, 17, 314, 405, 321, 375, 291, 409, 270, 269, 267, 0, 37, 39, 40, 185 };
        static readonly int[] InnerLip = { 78, 95, 88, 178, 87, 14, 317, 402, 318, 324, 308, 415, 310, 311, 312, 13, 82, 81, 80, 191 };

        // forced: the person each face belongs to (mesh tracks), or null to match faces to people by position.
        TrackedPerson[] Process(List<FaceDetector.Face> faces, float[] opens, Vector2[][] points, Rect crop, Texture tex, Pose cameraPose, float now,
            TrackedPerson[] forced = null, bool holdDistance = false)
        {
            var texSize = new Vector2(tex.width, tex.height);
            var matched = new HashSet<TrackedPerson>(); // mouth measured this frame

            // Crop-normalised (top-left origin) → full-image viewport (bottom-left origin).
            Vector2 ToViewport(Vector2 p)
            {
                var x = (crop.x + p.x * crop.width) / texSize.x;
                var y = (crop.y + p.y * crop.height) / texSize.y;
                return new Vector2(x, flipY ? 1f - y : y);
            }

            // 1. Where is each face in the room?
            var dets = new Detection[faces.Count];
            for (var fi = 0; fi < faces.Count; fi++)
            {
                var f = faces[fi];
                var center = ToViewport(f.Box.center);
                var left = ToViewport(new Vector2(f.Box.xMin, f.Box.center.y));
                var right = ToViewport(new Vector2(f.Box.xMax, f.Box.center.y));
                var rayC = CamRay(center, cameraPose);
                var rayL = CamRay(left, cameraPose);
                var rayR = CamRay(right, cameraPose);
                var angle = Vector3.Angle(rayL.direction, rayR.direction) * Mathf.Deg2Rad;
                var known = forced?[fi];
                if (known == null && angle * Mathf.Rad2Deg < minFaceAngleDegrees) continue;
                // A tracked person keeps their smoothed distance between searches (the mesh box's size is noisy).
                var distance = known != null && holdDistance ? Mathf.Clamp(Vector3.Distance(known.HeadPosition, rayC.origin), 0.3f, 5f)
                    : Mathf.Clamp(faceWidthMeters / (2f * Mathf.Tan(angle / 2f)), 0.3f, 5f);
                var fromDepth = false;
                if (!useDepth) DepthStatus = "off";
                else if (depthRaycast == null || !depthRaycast.isActiveAndEnabled) DepthStatus = "no raycast component";
                else if (!EnvironmentRaycastManager.IsSupported) DepthStatus = "unsupported (spatial data permission?)";
                else
                {
                    depthRaycast.Raycast(rayC, out var hit, 6f);
                    DepthStatus = hit.status.ToString();
                    if (hit.status == EnvironmentRaycastHitStatus.Hit)
                    {
                        // Distance along the camera ray to the real surface at the face centre (a person, or a screen).
                        var d = Vector3.Dot(hit.point - rayC.origin, rayC.direction);
                        if (d > 0.25f && d < 5.5f) { distance = d; fromDepth = true; }
                        else DepthStatus = $"hit at {d:F2} m (ignored)";
                    }
                }
                DepthShare = Mathf.Lerp(DepthShare, fromDepth ? 1f : 0f, 0.05f);
                if (ignoreScreenFaces && known == null && fromDepth)
                {
                    var width = 2f * distance * Mathf.Tan(angle / 2f);
                    if (width < minFaceWidthMeters || width > maxFaceWidthMeters)
                    {
                        if (Time.time - _sizeLogAt > 10f)
                        {
                            _sizeLogAt = Time.time;
                            Debug.Log($"[Captions] Ignored a face {width * 100f:F0} cm wide at {distance:F2} m (a screen or photo, not a person)");
                        }
                        continue;
                    }
                }
                dets[fi] = new Detection
                {
                    Ok = true, Position = rayC.origin + rayC.direction * distance, Origin = rayC.origin, Distance = distance,
                    FromDepth = fromDepth, ImageX = center.x, Low = f.Score < minScore,
                };
            }

            // 2. Which person is each face? (null: not a person yet, or a low-score face that continues nobody)
            var fresh = new HashSet<TrackedPerson>(); // new or back in view: start smoothing afresh
            TrackedPerson[] owners;
            if (forced != null && System.Array.TrueForAll(forced, x => x != null)) owners = forced;
            else
            {
                // Faces with a known person are left out of matching; the rest match the remaining people.
                HashSet<TrackedPerson> taken = null;
                if (forced != null)
                {
                    taken = new HashSet<TrackedPerson>();
                    for (var fi = 0; fi < dets.Length; fi++) if (forced[fi] != null) { taken.Add(forced[fi]); dets[fi].Ok = false; }
                }
                owners = byteTrack ? AssociateByteTrack(dets, now, fresh, taken) : AssociateGreedy(dets, now, fresh);
                if (forced != null)
                    for (var fi = 0; fi < dets.Length; fi++) if (forced[fi] != null) { owners[fi] = forced[fi]; dets[fi].Ok = true; }
            }

            // 3. Update each person from their face.
            for (var fi = 0; fi < faces.Count; fi++)
            {
                var best = owners[fi];
                if (best == null) continue;
                var f = faces[fi];
                var position = dets[fi].Position;
                var distance = dets[fi].Distance;
                // A face whose size-based distance jumps (a false box twice the face, a hand over it) keeps the person's
                // distance: only its direction is used. Otherwise one bad box drags the person toward the camera, its
                // speed with it, and the next frame's matching swaps neighbours (EasyCom session 10).
                var held = best.HeadPosition - dets[fi].Origin;
                var jump = !dets[fi].FromDepth && !fresh.Contains(best) && held.magnitude > 0.2f
                    && (distance < held.magnitude / DistanceJump || distance > held.magnitude * DistanceJump);
                if (jump)
                {
                    distance = held.magnitude;
                    position = dets[fi].Origin + (dets[fi].Position - dets[fi].Origin).normalized * distance;
                }

                // Mouth opening proxy: mouth–nose distance relative to eye distance (scale-invariant).
                var k = System.Array.ConvertAll(f.Keypoints, q => new Vector2(q.x * crop.width, q.y * crop.height));
                var eyeDist = Vector2.Distance(k[0], k[1]);
                var mouthRatio = eyeDist > 1e-5f ? Vector2.Distance(k[2], k[3]) / eyeDist : 0f;
                var lips = opens[fi] >= 0;

                if (!fresh.Contains(best)) Smooth(best, position, now);
                best.DistanceMeters = distance;
                if (keepDebugGeometry)
                {
                    Vector3 World(Vector2 viewport) { var r = CamRay(viewport, cameraPose); return r.origin + r.direction * distance; }
                    Vector2 PixelToViewport(Vector2 px) => new(px.x / texSize.x, flipY ? 1f - px.y / texSize.y : px.y / texSize.y);
                    best.BoxWorld = new[]
                    {
                        World(ToViewport(new Vector2(f.Box.xMin, f.Box.yMin))), World(ToViewport(new Vector2(f.Box.xMax, f.Box.yMin))),
                        World(ToViewport(new Vector2(f.Box.xMax, f.Box.yMax))), World(ToViewport(new Vector2(f.Box.xMin, f.Box.yMax))),
                    };
                    if (points[fi] != null)
                    {
                        var pts = points[fi];
                        best.OuterLipWorld = System.Array.ConvertAll(OuterLip, i => World(PixelToViewport(pts[i])));
                        best.InnerLipWorld = System.Array.ConvertAll(InnerLip, i => World(PixelToViewport(pts[i])));
                    }
                    else best.OuterLipWorld = best.InnerLipWorld = null;
                }
                best.LastSeen = now;
                best.FaceBoxPx = new Rect(crop.x + f.Box.x * crop.width, crop.y + f.Box.y * crop.height,
                    f.Box.width * crop.width, f.Box.height * crop.height);
                if (captureFaceCrops && !jump)
                {
                    Vector2 Px(Vector2 q) => new(crop.x + q.x * crop.width, crop.y + q.y * crop.height);
                    AddCropDetection(best, Px(f.Keypoints[0]), Px(f.Keypoints[1]), f.Box.width * crop.width);
                    if (!dets[fi].Low)
                        AskEmbedding(best, Px(f.Keypoints[0]), Px(f.Keypoints[1]), Px(f.Keypoints[2]), Px(f.Keypoints[3]), f.Box.width * crop.width, now);
                    if (logCropInputs) Debug.Log($"[Captions] cropin {best.Label} {Time.time:F3} {Px(f.Keypoints[0]).x:F1} {Px(f.Keypoints[0]).y:F1} {Px(f.Keypoints[1]).x:F1} {Px(f.Keypoints[1]).y:F1} {f.Box.width * crop.width:F1}");
                }
                if (dets[fi].Low) continue; // a turned or blurred face: keeps the person, but its mouth is guesswork
                matched.Add(best);

                // Lip gap when the face mesh ran on this face; otherwise the BlazeFace proxy. Don't mix the two in one window.
                if (lips != best.MouthOpen >= 0) best.MouthSamples.Clear();
                best.MouthOpen = lips ? opens[fi] : -1f;
                best.MouthSamples.Enqueue((now, lips ? opens[fi] : mouthRatio));
                while (best.MouthSamples.Count > 0 && now - best.MouthSamples.Peek().t > 0.6f) best.MouthSamples.Dequeue();
                best.MouthActivity = Activity(best.MouthSamples, lips ? LipScale : ProxyScale);
                if (lips)
                {
                    best.OpenHistory.Enqueue((now, opens[fi]));
                    while (best.OpenHistory.Count > 0 && now - best.OpenHistory.Peek().t > HistorySeconds) best.OpenHistory.Dequeue();
                }
            }

            // Out of view for a while: remember them (they keep their number) rather than forgetting.
            foreach (var p in People) if (now - p.LastSeen > forgetAfterSeconds) _away.Add(p);
            People.RemoveAll(p => now - p.LastSeen > forgetAfterSeconds);
            // Long gone: forgotten, unless their face is remembered (then only their face can bring them back).
            foreach (var p in _away) if (now - p.LastSeen > rememberSeconds && Remembered(p)) p.NoPosition = true;
            _away.RemoveAll(p => now - p.LastSeen > rememberSeconds && !Remembered(p));
            foreach (var p in People)
            {
                // Frames where the face wasn't found are unknown, not "mouth still": leave them out of the history.
                if (!matched.Contains(p)) { p.MouthActivity *= 0.8f; continue; }
                p.ActivityHistory.Enqueue((now, p.MouthActivity));
                while (p.ActivityHistory.Count > 0 && now - p.ActivityHistory.Peek().t > HistorySeconds) p.ActivityHistory.Dequeue();
            }
            return owners;
        }

        [Tooltip("Matching faces to people without the depth sensor: weight of the along-the-view (distance) part of " +
                 "the gap. Face-size distance is often off by 0.3–0.5 m while the direction is good to a few cm, so a " +
                 "full 3D gap starts duplicates of people who are still there. 1 = plain 3D distance.")]
        public float depthMatchWeight = 0.35f;

        // Gap between a person's position and a detected face: sideways in full, along the camera ray weighted down
        // unless the face's distance came from the depth sensor.
        // Sideways is the angle between them seen from the camera, in metres at the person's distance (clamped to
        // 1–2.5 m), so a person whose distance went wrong doesn't look close to every face.
        float Gap(Vector3 person, in Detection det)
        {
            var dir = (det.Position - det.Origin).normalized;
            var toP = person - det.Origin;
            var range = toP.magnitude;
            var lateral = Vector3.Angle(toP, dir) * Mathf.Deg2Rad * Mathf.Clamp(range, 1f, 2.5f);
            var radial = det.Distance - range;
            var w = det.FromDepth ? 1f : depthMatchWeight;
            return Mathf.Sqrt(lateral * lateral + w * w * radial * radial);
        }
        const float DistanceJump = 1.7f; // face-size distance off by more than this factor: direction only

        struct Detection
        {
            public bool Ok, FromDepth, Low;
            public Vector3 Position, Origin;
            public float Distance, ImageX;
        }

        // Older matching: each face in detector order takes the nearest free person; high-scoring faces only.
        TrackedPerson[] AssociateGreedy(Detection[] dets, float now, HashSet<TrackedPerson> fresh)
        {
            var owners = new TrackedPerson[dets.Length];
            var taken = new HashSet<TrackedPerson>();
            for (var fi = 0; fi < dets.Length; fi++)
            {
                if (!dets[fi].Ok || dets[fi].Low) continue;
                TrackedPerson best = null;
                var bestDist = matchRadiusMeters;
                foreach (var p in People)
                {
                    if (taken.Contains(p)) continue;
                    var d = Gap(p.HeadPosition, dets[fi]);
                    if (d < bestDist) { bestDist = d; best = p; }
                }
                best ??= Reidentify(dets[fi], fresh) ?? (TooFarForNew(dets[fi]) ? null : NewPerson(dets[fi], now, fresh));
                if (best == null) continue;
                taken.Add(best);
                owners[fi] = best;
            }
            return owners;
        }

        // ByteTrack (Zhang et al., ECCV 2022) in room space. High-scoring faces are matched first to every person's
        // predicted position, then low-scoring ones (often a real face turned away or blurred) to the people still
        // unmatched — so a turned head keeps its person instead of dropping out and coming back. Only high-scoring
        // faces can be someone back in view or a new person, and a new person needs confirmHits detections in a row.
        readonly List<(Vector3 pos, float last, int hits)> _tentative = new();
        TrackedPerson[] AssociateByteTrack(Detection[] dets, float now, HashSet<TrackedPerson> fresh, HashSet<TrackedPerson> taken = null)
        {
            var owners = new TrackedPerson[dets.Length];
            var free = new List<TrackedPerson>(People);
            if (taken != null) free.RemoveAll(taken.Contains);
            MatchClosestFirst(dets, false, free, owners, now);
            MatchClosestFirst(dets, true, free, owners, now);
            _tentative.RemoveAll(t => now - t.last > (meshTracking ? Mathf.Max(0.4f, searchSeconds + 0.2f) : 0.4f)); // unmeshed faces: seen again at the next search
            for (var fi = 0; fi < dets.Length; fi++)
            {
                if (!dets[fi].Ok || dets[fi].Low || owners[fi] != null) continue;
                owners[fi] = Reidentify(dets[fi], fresh);
                if (owners[fi] != null || TooFarForNew(dets[fi])) continue;
                var ti = -1;
                var tDist = matchRadiusMeters;
                for (var j = 0; j < _tentative.Count; j++)
                {
                    var d = Gap(_tentative[j].pos, dets[fi]);
                    if (d < tDist) { tDist = d; ti = j; }
                }
                var hits = ti >= 0 ? _tentative[ti].hits + 1 : 1;
                if (ti >= 0) _tentative.RemoveAt(ti);
                if (hits >= confirmHits) owners[fi] = NewPerson(dets[fi], now, fresh);
                else _tentative.Add((dets[fi].Position, now, hits));
            }
            return owners;
        }

        // All face–person pairs closer than matchRadiusMeters, assigned closest first (for a handful of faces this
        // gives the same answer as ByteTrack's Hungarian assignment).
        void MatchClosestFirst(Detection[] dets, bool low, List<TrackedPerson> free, TrackedPerson[] owners, float now)
        {
            var pairs = new List<(float d, int fi, TrackedPerson p)>();
            for (var fi = 0; fi < dets.Length; fi++)
            {
                if (!dets[fi].Ok || dets[fi].Low != low) continue;
                foreach (var p in free)
                {
                    // Where they should be now, from their speed (capped: after a gap the speed is stale).
                    var predicted = p.HeadPosition + p.Velocity * Mathf.Clamp(now - p.LastSeen, 0f, 0.3f);
                    var d = Gap(predicted, dets[fi]);
                    if (d < matchRadiusMeters) pairs.Add((d, fi, p));
                }
            }
            pairs.Sort((a, b) => a.d.CompareTo(b.d));
            foreach (var (_, fi, p) in pairs)
            {
                if (owners[fi] != null || !free.Contains(p)) continue;
                owners[fi] = p;
                free.Remove(p);
            }
        }

        // Someone we saw before, back in view (turned your head away and back)?
        TrackedPerson Reidentify(Detection det, HashSet<TrackedPerson> fresh)
        {
            // Tagged people first: once someone is tagged, an untagged face is never captioned, so losing a tag to a
            // new id would silence that person.
            TrackedPerson back = null;
            var backDist = float.MaxValue;
            for (var pass = 0; pass < 2 && back == null; pass++)
                foreach (var p in _away)
                {
                    if ((p.Tag != null) != (pass == 0) || p.NoPosition) continue;
                    var d = Vector3.Angle(p.HeadPosition - det.Origin, det.Position - det.Origin);
                    if (d < (p.Tag != null ? taggedReidDegrees : reidDegrees) && d < backDist) { backDist = d; back = p; }
                }
            if (back == null) return null;
            _away.Remove(back);
            back.HeadPosition = det.Position;
            back.LastFilterTime = 0;   // restart smoothing at the new position
            back.MouthSamples.Clear(); back.ActivityHistory.Clear(); back.Crops.Clear(); back.AsdFeatures.Clear();
            back.CropDets.Clear(); back.CropPrev = (float.MinValue, default); back.CropsDoneTo = float.MinValue; back.AlignT = float.MinValue;
            back.EmbedAskedT = float.MinValue; back.SwapVotes = 0; back.SwapTo = null; // check the face soon
            People.Add(back);
            fresh.Add(back);
            Debug.Log($"[Captions] {back.Label} is back ({backDist:F0}° from where they were)");
            return back;
        }

        TrackedPerson NewPerson(Detection det, float now, HashSet<TrackedPerson> fresh)
        {
            var p = new TrackedPerson
            {
                Id = _nextId,
                Color = Palette[(_nextId - 1) % Palette.Length],
                HeadPosition = det.Position,
                LastFilterTime = now,
            };
            _nextId++;
            People.Add(p);
            fresh.Add(p);
            AutoTag(p);
            Debug.Log($"[Captions] New {p.Label} at {det.Distance:F2} m ({(det.FromDepth ? "depth sensor" : "face-size guess")}, image x {det.ImageX:F2})");
            return p;
        }

        // LR-ASD crop: a square from the eye points, side max(1.95 × eye distance, box width), centred 0.35 side below the
        // eyes (BlazeFace's box is tighter than the detector it was trained with, so the eyes are the steadier reference).
        // Grey, 112×112, top row first. Points in source pixels, top-left origin.
        // LR-ASD reads mouth movement from how the crop changes frame to frame, so the box must sit on the face in the
        // very frame it cuts. Detections come ~14 times a second and finish ~80 ms after their frame; cutting the newest
        // frame with the last box put it 1–3 frames off on a moving (head-worn) camera, which LR-ASD reads as motion
        // (EasyCom session 5: right partner 61%; one-frame offset with true boxes 93% → 70%). So recent camera frames are
        // kept, and once a detection arrives, every kept frame since the previous one is cut with the box interpolated
        // between the two (offline 88%, true boxes 93%). Crops trail the camera by about one detection plus inference.
        void AddCropDetection(TrackedPerson p, Vector2 eyeA, Vector2 eyeB, float boxWidth)
        {
            var side = Mathf.Max(1.95f * Vector2.Distance(eyeA, eyeB), boxWidth); // turned heads: eyes look closer
            var mid = (eyeA + eyeB) / 2f;
            p.CropDets.Add((_procStamp, new Vector3(mid.x, mid.y + 0.35f * side, side)));
            if (p.CropDets.Count > 16) p.CropDets.RemoveAt(0);
        }
        long _procStamp;

        // Camera frames kept for cropping (oldest overwritten). They must cover a detection interval plus its inference:
        // with speech recognition running, detection slows to ~4/s and finishes ~0.2 s late, so 8 frames (~0.27 s)
        // lost the frame each detection belonged to and no crops were made between detections (LR-ASD never ran).
        const int FrameRing = 16;
        const float CropGapSeconds = 0.6f; // detections further apart: no crops in between (busy: ~2–3 a second per face)
        readonly RenderTexture[] _ring = new RenderTexture[FrameRing];
        readonly long[] _ringStamp = new long[FrameRing];
        readonly float[] _ringTime = new float[FrameRing];
        int _ringNext, _ringCount;
        long _lastCropFrame = long.MinValue;
        /// <summary>How old camera frames are when kept (smoothed, ms); the crops are stamped with the frame's time.</summary>
        public float CropFrameAgeMs { get; private set; }
        /// <summary>Detections whose camera frame had already left the ring (no crops for them), since start.</summary>
        public int CropFramesLost { get; private set; }
        void Update()
        {
            TickMemory();
            if (paused || !captureFaceCrops || !Ready || !CamPlaying) return;
            if (CamStamp() != _lastCropFrame)
            {
                _lastCropFrame = CamStamp();
                var tex = CamTexture();
                // Stamp each frame with the moment it shows, not when it arrived: LR-ASD matches lips to sound,
                // and a frame that arrives late would otherwise be paired with later audio.
                var age = _src?.FrameAge ?? 0f;
                CropFrameAgeMs = Mathf.Lerp(CropFrameAgeMs, age * 1000f, 0.05f);
                var i = _ringNext;
                _ringNext = (i + 1) % FrameRing;
                _ringCount = Mathf.Min(_ringCount + 1, FrameRing);
                if (_ring[i] == null || _ring[i].width != tex.width || _ring[i].height != tex.height)
                {
                    if (_ring[i] != null) _ring[i].Release();
                    _ring[i] = new RenderTexture(tex.width, tex.height, 0, RenderTextureFormat.ARGB32);
                }
                Graphics.Blit(tex, _ring[i]);
                _ringStamp[i] = _lastCropFrame;
                _ringTime[i] = Time.time - age;
            }
            foreach (var p in People) CropFromDetections(p);
            EmbedFromRing();
        }

        // Ring slot k from the oldest kept frame (k = 0) to the newest.
        int RingSlot(int k) => (_ringNext - _ringCount + k + FrameRing) % FrameRing;

        void CropFromDetections(TrackedPerson p)
        {
            while (p.CropDets.Count > 0 && _ringCount > 0)
            {
                var (stamp, box) = p.CropDets[0];
                var slot = -1;
                for (var k = 0; k < _ringCount; k++)
                    if (_ringStamp[RingSlot(k)] == stamp) { slot = RingSlot(k); break; }
                if (slot < 0 && stamp > _ringStamp[RingSlot(_ringCount - 1)]) return; // its frame isn't kept yet
                p.CropDets.RemoveAt(0);
                if (slot < 0) { p.CropPrev = (float.MinValue, default); CropFramesLost++; continue; } // its frame is gone already
                var t = _ringTime[slot];
                var (t0, b0) = p.CropPrev;
                p.CropPrev = (t, box);
                if (t <= p.CropsDoneTo) continue;
                if (t - t0 > CropGapSeconds)
                {
                    CaptureCrop(p, _ring[slot], box, t); // first detection, or back after a gap: its own frame only
                    p.CropsDoneTo = t;
                    continue;
                }
                for (var k = 0; k < _ringCount; k++)
                {
                    var s = RingSlot(k);
                    var tk = _ringTime[s];
                    if (tk <= p.CropsDoneTo || tk <= t0 || tk > t) continue;
                    CaptureCrop(p, _ring[s], Vector3.Lerp(b0, box, (tk - t0) / (t - t0)), tk);
                }
                p.CropsDoneTo = t;
            }
        }

        // The detector's box centre still wanders ~20% of the face from frame to frame on small faces (EasyCom: 64%
        // right partner from interpolated boxes, 93% from the dataset's boxes). So the crop centre follows the picture
        // instead: each frame, a patch twice the face size around the box is aligned to the last frame's patch (phase
        // correlation), the centre moves with the content, and drifts back to the detector slowly (time constant
        // AlignPullSeconds). Offline on EasyCom session 5: 92%, as good as the dataset's boxes.
        const int PatchSize = 2 * ActiveSpeakerModel.CropSize, AlignN = 64;
        const float AlignPullSeconds = 1f, AlignSizeSeconds = 0.22f, AlignGapSeconds = 0.15f, AlignMinResponse = 0.1f;
        RenderTexture _patchRt;

        void CaptureCrop(TrackedPerson p, Texture tex, Vector3 box, float now)
        {
            var side = box.z;
            var q = new Vector2(box.x, box.y);
            var roi = new Rect(q.x - side, q.y - side, 2 * side, 2 * side);
            if (_patchRt == null) _patchRt = new RenderTexture(PatchSize, PatchSize, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(tex, _patchRt, new Vector2(roi.width / tex.width, roi.height / tex.height), new Vector2(roi.x / tex.width, 1f - roi.yMax / tex.height));
            UnityEngine.Rendering.AsyncGPUReadback.Request(_patchRt, 0, TextureFormat.RGBA32, req =>
            {
                if (req.hasError) return;
                const int m = PatchSize, n = ActiveSpeakerModel.CropSize;
                var rgba = req.GetData<byte>();
                var grey = new float[m * m];
                for (var y = 0; y < m; y++)
                for (var x = 0; x < m; x++)
                {
                    var i = ((m - 1 - y) * m + x) * 4; // readback rows start at the bottom
                    grey[y * m + x] = 0.299f * rgba[i] + 0.587f * rgba[i + 1] + 0.114f * rgba[i + 2];
                }
                var small = new float[AlignN * AlignN];
                for (var y = 0; y < AlignN; y++)
                for (var x = 0; x < AlignN; x++)
                    small[y * AlignN + x] = Bilinear(grey, m, (x + 0.5f) * m / AlignN - 0.5f, (y + 0.5f) * m / AlignN - 0.5f);

                var dt = now - p.AlignT;
                if (p.AlignPatch == null || dt <= 0f || dt > AlignGapSeconds) { p.AlignC = q; p.AlignSide = side; }
                else
                {
                    var shift = PhaseShift(p.AlignPatch, small, out var response);
                    var move = q - p.AlignQ; // the patches sit at different places in the picture
                    if (response > AlignMinResponse) move += shift * (2f * side / AlignN);
                    p.AlignC += move;
                    p.AlignC += (q - p.AlignC) * (1f - Mathf.Exp(-dt / AlignPullSeconds));
                    p.AlignSide += (side - p.AlignSide) * (1f - Mathf.Exp(-dt / AlignSizeSeconds));
                    p.AlignSide = Mathf.Min(p.AlignSide, 1.9f * side);
                    var lim = side - p.AlignSide / 2f; // keep the crop inside the patch
                    var off = p.AlignC - q;
                    p.AlignC = q + new Vector2(Mathf.Clamp(off.x, -lim, lim), Mathf.Clamp(off.y, -lim, lim));
                }
                p.AlignPatch = small; p.AlignQ = q; p.AlignT = now;

                // The crop: AlignC ± AlignSide/2, sampled from the patch (which covers q ± side).
                var crop = new byte[n * n];
                var scale = m / (2f * side);
                var x0 = (p.AlignC.x - p.AlignSide / 2f - (q.x - side)) * scale;
                var y0 = (p.AlignC.y - p.AlignSide / 2f - (q.y - side)) * scale;
                var step = p.AlignSide / n * scale;
                for (var y = 0; y < n; y++)
                for (var x = 0; x < n; x++)
                    crop[y * n + x] = (byte)Mathf.Clamp(Bilinear(grey, m, x0 + (x + 0.5f) * step - 0.5f, y0 + (y + 0.5f) * step - 0.5f) + 0.5f, 0f, 255f);
                p.Crops.Add((now, crop));
                double sum = 0, sum2 = 0;
                foreach (var b in crop) { sum += b; sum2 += b * b; }
                var std = (float)System.Math.Sqrt(System.Math.Max(0, sum2 / crop.Length - (sum / crop.Length) * (sum / crop.Length)));
                p.CropContrast = p.CropContrast < 0 ? std : Mathf.Lerp(p.CropContrast, std, 0.2f);
                var cut = 0;
                while (cut < p.Crops.Count && now - p.Crops[cut].t > 2.5f) cut++;
                if (cut > 0) p.Crops.RemoveRange(0, cut);
            });
        }

        static float Bilinear(float[] img, int w, float x, float y)
        {
            x = Mathf.Clamp(x, 0f, w - 1.001f); y = Mathf.Clamp(y, 0f, w - 1.001f);
            int ix = (int)x, iy = (int)y; float fx = x - ix, fy = y - iy;
            var i = iy * w + ix;
            return (img[i] * (1 - fx) + img[i + 1] * fx) * (1 - fy) + (img[i + w] * (1 - fx) + img[i + w + 1] * fx) * fy;
        }

        // How far the content of b moved relative to a (pixels, +x right, +y down), both AlignN×AlignN: phase correlation
        // with a Hann window and a 5×5 weighted centroid at the peak (as OpenCV's phaseCorrelate). response = peak mass.
        static float[] _hann;
        internal static Vector2 PhaseShift(float[] a, float[] b, out float response)
        {
            const int N = AlignN;
            if (_hann == null)
            {
                _hann = new float[N * N];
                for (var y = 0; y < N; y++)
                for (var x = 0; x < N; x++)
                    _hann[y * N + x] = 0.25f * (1 - Mathf.Cos(2 * Mathf.PI * x / (N - 1))) * (1 - Mathf.Cos(2 * Mathf.PI * y / (N - 1)));
            }
            var ar = new float[N * N]; var ai = new float[N * N]; var br = new float[N * N]; var bi = new float[N * N];
            for (var i = 0; i < N * N; i++) { ar[i] = a[i] * _hann[i]; br[i] = b[i] * _hann[i]; }
            Fft2(ar, ai, false); Fft2(br, bi, false);
            for (var i = 0; i < N * N; i++)
            {
                // B · conj(A), normalised to unit magnitude
                var re = br[i] * ar[i] + bi[i] * ai[i];
                var im = bi[i] * ar[i] - br[i] * ai[i];
                var mag = Mathf.Sqrt(re * re + im * im) + 1e-9f;
                ar[i] = re / mag; ai[i] = im / mag;
            }
            Fft2(ar, ai, true);
            var best = 0;
            for (var i = 1; i < N * N; i++) if (ar[i] > ar[best]) best = i;
            int px = best % N, py = best / N;
            float sx = 0, sy = 0, sw = 0;
            for (var dy = -2; dy <= 2; dy++)
            for (var dx = -2; dx <= 2; dx++)
            {
                var v = ar[((py + dy + N) % N) * N + (px + dx + N) % N];
                sx += v * (px + dx); sy += v * (py + dy); sw += v;
            }
            response = sw;
            if (sw <= 0) return Vector2.zero;
            float fx = sx / sw, fy = sy / sw;
            if (fx > N / 2f) fx -= N;
            if (fy > N / 2f) fy -= N;
            return new Vector2(fx, fy);
        }

        // In-place 2D FFT (N×N, N a power of two); the inverse is scaled by 1/N².
        static void Fft2(float[] re, float[] im, bool inverse)
        {
            const int N = AlignN;
            var rr = new float[N]; var ii = new float[N];
            for (var y = 0; y < N; y++)
            {
                System.Array.Copy(re, y * N, rr, 0, N); System.Array.Copy(im, y * N, ii, 0, N);
                Fft1(rr, ii, inverse);
                System.Array.Copy(rr, 0, re, y * N, N); System.Array.Copy(ii, 0, im, y * N, N);
            }
            for (var x = 0; x < N; x++)
            {
                for (var y = 0; y < N; y++) { rr[y] = re[y * N + x]; ii[y] = im[y * N + x]; }
                Fft1(rr, ii, inverse);
                for (var y = 0; y < N; y++) { re[y * N + x] = rr[y]; im[y * N + x] = ii[y]; }
            }
            if (inverse)
                for (var i = 0; i < N * N; i++) { re[i] /= N * N; im[i] /= N * N; }
        }

        static void Fft1(float[] re, float[] im, bool inverse)
        {
            var n = re.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                var bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
            }
            for (var len = 2; len <= n; len <<= 1)
            {
                var ang = 2 * Mathf.PI / len * (inverse ? 1 : -1);
                float wr = Mathf.Cos(ang), wi = Mathf.Sin(ang);
                for (var i = 0; i < n; i += len)
                {
                    float cr = 1, ci = 0;
                    for (var k = 0; k < len / 2; k++)
                    {
                        int u = i + k, v = i + k + len / 2;
                        var tr = re[v] * cr - im[v] * ci; var ti = re[v] * ci + im[v] * cr;
                        re[v] = re[u] - tr; im[v] = im[u] - ti;
                        re[u] += tr; im[u] += ti;
                        var ncr = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = ncr;
                    }
                }
            }
        }

        // One Euro filter (Casiez et al. 2012): heavy smoothing when the face is still (kills detector jitter),
        // light smoothing when it moves (no lag on real movement).
        void Smooth(TrackedPerson p, Vector3 raw, float now)
        {
            var dt = Mathf.Max(1e-3f, now - p.LastFilterTime);
            if (p.LastFilterTime <= 0f || dt > 0.5f) { p.HeadPosition = raw; p.Velocity = Vector3.zero; p.LastFilterTime = now; return; }
            static float Alpha(float cutoff, float dt) { var tau = 1f / (2f * Mathf.PI * cutoff); return 1f / (1f + tau / dt); }
            var v = (raw - p.HeadPosition) / dt;
            p.Velocity = Vector3.Lerp(p.Velocity, v, Alpha(1f, dt));
            var cutoff = minCutoffHz + speedCoefficient * p.Velocity.magnitude;
            p.HeadPosition = Vector3.Lerp(p.HeadPosition, raw, Alpha(cutoff, dt));
            p.LastFilterTime = now;
        }

        // Standard deviation of the mouth signal that normal talking reaches (→ activity ~1).
        const float ProxyScale = 0.04f; // BlazeFace mouth–nose / eye distance
        const float LipScale = 0.06f;   // face mesh inner-lip gap / eye-corner distance

        static float Activity(Queue<(float t, float v)> samples, float scale)
        {
            if (samples.Count < 3) return 0;
            float mean = 0;
            foreach (var s in samples) mean += s.v;
            mean /= samples.Count;
            float var = 0;
            foreach (var s in samples) var += (s.v - mean) * (s.v - mean);
            return Mathf.Clamp01(Mathf.Sqrt(var / samples.Count) / scale);
        }

        void OnDestroy()
        {
            SavePeople();
            _detector?.Dispose();
            _search?.Dispose();
            foreach (var z in _zoomDetectors) z.Dispose();
            _landmarker?.Dispose();
            foreach (var r in _ring) if (r != null) r.Release();
            if (_patchRt != null) _patchRt.Release();
            DisposeFaceId();
            if (cameraAccess != null) cameraAccess.enabled = false;
        }
    }
}
