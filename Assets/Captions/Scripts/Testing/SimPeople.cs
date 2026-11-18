using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace MetaStarter.Captions
{
    /// <summary>
    /// People for the Meta XR Simulator, whose rooms are empty. A video of people talking is cut into life-size
    /// cards (one per person) standing in the room around where you start; its soundtrack is the speech
    /// (<see cref="CaptionDirector.PushAudio"/>, in step with the picture). A stand-in for the headset camera follows
    /// your head, sees only the cards, and feeds <see cref="PersonTracker"/> (as <see cref="IFrameSource"/>), so the
    /// whole app runs as on the headset: tagging, bubbles beside each person, signing, turning away and back.
    /// Set up by SimPeopleRunner (Meta ▸ Speaker Captions ▸ Play in Simulator with People).
    /// </summary>
    public class SimPeople : MonoBehaviour, IFrameSource
    {
        [Tooltip("Folder of numbered JPG frames of the video (ffmpeg -vf fps=N dir/%05d.jpg).")]
        public string framesFolder;
        public float framesFps = 30f;
        [Tooltip("The soundtrack: 32-bit float mono 16 kHz WAV. Empty = use the microphone instead.")]
        public string audioPath;
        public CaptionDirector director;
        public PersonTracker tracker;
        [Tooltip("The viewer's head (centre eye).")]
        public Transform head;

        [System.Serializable]
        public class Card
        {
            [Tooltip("Part of the video picture showing this person (x, y, width, height; 0–1, bottom-left origin).")]
            public Rect crop = new(0, 0, 0.5f, 1);
            [Tooltip("Where they stand: degrees to the right of your starting view, and metres away.")]
            public float angle, distance = 1.6f;
        }
        public List<Card> cards = new()
        {
            new Card { crop = new Rect(0, 0, 0.5f, 1), angle = -22, distance = 1.5f },
            new Card { crop = new Rect(0.5f, 0, 0.5f, 1), angle = 22, distance = 1.7f },
        };
        [Tooltip("Width of the whole video picture in the room (m): sets how big the people are (a face is ~0.16 m).")]
        public float pictureWidth = 1.1f;
        [Tooltip("Height of the face above the floor (m), where the picture's faceHeight line goes.")]
        public float faceAboveFloor = 1.55f;
        [Range(0, 1)] public float faceHeight = 0.72f;   // faces sit this high up the picture
        public bool loop = true;

        [Header("Stand-in headset camera")]
        public float horizontalFov = 80f;
        public float cameraFps = 15f;
        public Vector3 cameraOffset = new(-0.032f, -0.02f, 0.06f); // the left passthrough camera, from the centre eye

        const int W = 1280, H = 960, Layer = 30;
        Camera _cam;
        RenderTexture _rt;
        Texture2D _frame;
        string[] _frames;
        float[] _audio;
        int _audioPos, _shownIdx = -1;
        long _id = -1;
        double _clock;
        float _nextRender;
        bool _started;
        Pose _pose;

        public bool IsPlaying => _id >= 0;
        public Texture Texture => _rt;
        public long FrameId => _id;
        public Pose Pose => _pose;
        public string Resolution => $"{W}x{H} (simulated people)";

        public Ray ViewportPointToRay(Vector2 viewport, Pose pose)
        {
            var tanX = Mathf.Tan(horizontalFov * 0.5f * Mathf.Deg2Rad);
            var dir = new Vector3((viewport.x * 2 - 1) * tanX, (viewport.y * 2 - 1) * tanX * H / W, 1f);
            return new Ray(pose.position, pose.rotation * dir.normalized);
        }

        void Start()
        {
            Application.runInBackground = true; // else Play mode stalls while the simulator window has focus
            _frames = Directory.GetFiles(framesFolder, "*.jpg");
            System.Array.Sort(_frames, System.StringComparer.Ordinal);
            _frame = new Texture2D(2, 2, TextureFormat.RGB24, false) { wrapMode = TextureWrapMode.Clamp };
            _frame.LoadImage(File.ReadAllBytes(_frames[0]));
            if (!string.IsNullOrEmpty(audioPath)) _audio = ReadFloatWav(audioPath);

            _rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32);
            var go = new GameObject("Simulated headset camera");
            go.transform.SetParent(transform, false);
            _cam = go.AddComponent<Camera>();
            _cam.enabled = false; // rendered by hand, at cameraFps
            _cam.targetTexture = _rt;
            _cam.cullingMask = 1 << Layer;
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = new Color(0.35f, 0.33f, 0.31f); // a dim room
            _cam.nearClipPlane = 0.05f;
            _cam.aspect = (float)W / H;
            _cam.fieldOfView = 2f * Mathf.Atan(Mathf.Tan(horizontalFov * 0.5f * Mathf.Deg2Rad) * H / W) * Mathf.Rad2Deg;
            StartCoroutine(PlaceCards());
        }

        // Once the headset pose has settled: the cards around the starting view, faces at standing eye height.
        System.Collections.IEnumerator PlaceCards()
        {
            yield return new WaitForSeconds(0.5f);
            var fwd = head.forward;
            fwd.y = 0;
            fwd = fwd.sqrMagnitude < 1e-4f ? Vector3.forward : fwd.normalized;
            var aspect = _frame.width / (float)_frame.height;
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            foreach (var c in cards)
            {
                var dir = Quaternion.AngleAxis(c.angle, Vector3.up) * fwd;
                var face = head.position + dir * c.distance;
                face.y = head.position.y - 1.6f + faceAboveFloor; // the head is at ~1.6 m standing
                var size = new Vector2(pictureWidth * c.crop.width, pictureWidth / aspect * c.crop.height);
                var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
                Destroy(q.GetComponent<Collider>());
                q.name = "Simulated person";
                q.layer = Layer;
                q.transform.SetParent(transform, true);
                q.transform.localScale = new Vector3(size.x, size.y, 1);
                // The face line of the picture at face height; the card turned towards where you stand.
                var faceInCard = (faceHeight - c.crop.y) / c.crop.height;
                q.transform.SetPositionAndRotation(face + Vector3.up * (0.5f - faceInCard) * size.y,
                    Quaternion.LookRotation(dir, Vector3.up));
                var m = new Material(shader);
                m.SetTexture("_BaseMap", _frame);
                m.SetTextureScale("_BaseMap", c.crop.size);
                m.SetTextureOffset("_BaseMap", c.crop.position);
                q.GetComponent<MeshRenderer>().sharedMaterial = m;
            }
            Debug.Log($"[SimPeople] {cards.Count} people placed; video {_frames.Length / framesFps:F0} s from {framesFolder}");
            _started = true;
        }

        void Update()
        {
            if (!_started) return;
            if (_audio != null && _audioPos == 0 && _clock == 0)
            {
                if (!director.Listening) { RenderCamera(); return; } // models still loading: show the still picture
                director.UseExternalAudio();
            }
            // Hold the first picture until faces are tracked, so no speech plays with nobody seen.
            if (tracker.Ready) _clock += Time.deltaTime;
            var length = _frames.Length / framesFps;
            if (_clock >= length && loop) { _clock -= length; _audioPos = 0; _shownIdx = -1; }

            var idx = Mathf.Min((int)(_clock * framesFps), _frames.Length - 1);
            if (idx != _shownIdx)
            {
                _frame.LoadImage(File.ReadAllBytes(_frames[idx]));
                _shownIdx = idx;
            }
            if (_audio != null)
            {
                var upTo = Mathf.Min(_audio.Length, (int)(_clock * 16000));
                if (upTo > _audioPos)
                {
                    var chunk = new float[upTo - _audioPos];
                    System.Array.Copy(_audio, _audioPos, chunk, 0, chunk.Length);
                    _audioPos = upTo;
                    director.PushAudio(chunk, Time.realtimeSinceStartupAsDouble);
                }
            }
            RenderCamera();
        }

        void RenderCamera()
        {
            if (Time.time < _nextRender) return;
            _nextRender = Time.time + 1f / cameraFps;
            _pose = new Pose(head.TransformPoint(cameraOffset), head.rotation);
            _cam.transform.SetPositionAndRotation(_pose.position, _pose.rotation);
            _cam.Render();
            _id++;
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
            if (_rt != null) _rt.Release();
        }
    }
}
