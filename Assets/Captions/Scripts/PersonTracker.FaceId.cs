using System.Collections.Generic;
using Unity.InferenceEngine;
using UnityEngine;

namespace MetaStarter.Captions
{
    // Who is who by face. Matching by direction alone mixes up neighbours who sit 20–40° apart while the wearer turns
    // their head (EasyCom: a returning face got a new number or another person's tag). So a few times a second each
    // tracked face is embedded (SFace) and compared with every person seen so far:
    // - it looks like someone out of view far more than the person it is tracked as: it is them (they take over the
    //   track, keeping their number and tag);
    // - it looks like nobody it is tracked as, nor anyone else: a stranger (a new person takes over the track);
    // - otherwise it is added to the person's gallery.
    // A track that has a gallery needs two such embeddings in a row before it changes hands.
    public partial class PersonTracker
    {
        [Header("Who is who by face")]
        [Tooltip("SFace (face_recognition_sface_2021dec, cleaned: Assets/Week1/Models/sface.onnx). Empty = by direction only.")]
        public ModelAsset faceIdModel;
        public bool reidByFace = true;
        [Tooltip("Similarity (cosine, best-3 mean against the gallery) above which a face is that person.")]
        public float sameFaceSimilarity = 0.45f;
        [Tooltip("How much more a face must look like someone else than like the person it is tracked as.")]
        public float switchMargin = 0.15f;
        [Tooltip("Below this similarity to their own gallery (and nobody else matching) a face is a stranger.")]
        public float strangerSimilarity = 0.3f;
        [Tooltip("Once a person has a few faces, a new one joins their gallery only at this similarity or more: a neighbour's " +
                 "face picked up for a moment (EasyCom session 4) would otherwise make the gallery match both.")]
        public float galleryAddSimilarity = 0.45f;
        [Tooltip("Faces narrower than this (source pixels) are not embedded: too blurry to tell people apart.")]
        public float minEmbedFacePx = 28f;
        [Tooltip("Head turn limit for embedding: nose offset from between the eyes, sideways, in eye distances. EasyCom: " +
                 "the nearest person by face is right 97–99% up to 0.6, 69% beyond.")]
        public float maxEmbedYaw = 0.55f;
        [Tooltip("Nose drop below the eyes, in eye distances, allowed for embedding (looking down/up beyond: skipped). " +
                 "EasyCom: 99% right within 0.5–0.9, ~75% outside.")]
        public float minEmbedPitch = 0.45f, maxEmbedPitch = 0.95f;
        [Tooltip("Seconds between embeddings of one person (twice this once their gallery is full).")]
        public float embedInterval = 0.4f;
        public bool logFaceId = true;

        const int GallerySize = 20, GalleryKeep = 5, YoungGallery = 5;
        const float DuplicateSimilarity = 0.4f;
        const float VisibleForSwap = 0.3f; // a person seen this recently is another face in the picture: not a candidate

        FaceEmbedder _embedder;
        bool _embedBusy;
        int _embedCount;
        RenderTexture _embedRt;
        const int EmbedPatch = 160;
        readonly List<(TrackedPerson p, long stamp, Vector2 re, Vector2 le, Vector2 nose, Vector2 mouth)> _embedAsks = new();
        readonly Queue<(TrackedPerson p, float[] rgb)> _embedReady = new();

        bool FaceIdOn => reidByFace && faceIdModel != null && captureFaceCrops;

        void AskEmbedding(TrackedPerson p, Vector2 re, Vector2 le, Vector2 nose, Vector2 mouth, float boxWidth, float now)
        {
            if (!FaceIdOn || boxWidth < minEmbedFacePx) return;
            // Turned or tilted faces embed poorly (a profile looks more like another profile than like the same person
            // face-on), and one such picture can hand a track to someone else: skip them.
            var mid = (re + le) / 2f;
            var eyes = Vector2.Distance(re, le);
            if (eyes < 1e-3f) return;
            var yaw = Mathf.Abs(nose.x - mid.x) / eyes;
            var pitch = (nose.y - mid.y) / eyes;
            if (yaw > maxEmbedYaw || pitch < minEmbedPitch || pitch > maxEmbedPitch || eyes < 0.25f * boxWidth) return;
            var interval = p.Gallery.Count >= GallerySize ? 2f * embedInterval : embedInterval;
            if (now - p.EmbedAskedT < interval) return;
            p.EmbedAskedT = now;
            _embedAsks.Add((p, _procStamp, re, le, nose, mouth));
            if (_embedAsks.Count > 8) _embedAsks.RemoveAt(0);
        }

        // Cut each asked-for face from the camera frame its detection saw (kept in the crop ring), aligned to SFace's
        // template; then embed one at a time.
        void EmbedFromRing()
        {
            if (!FaceIdOn) return;
            for (var i = 0; i < _embedAsks.Count;)
            {
                var a = _embedAsks[i];
                var slot = -1;
                for (var k = 0; k < _ringCount; k++)
                    if (_ringStamp[RingSlot(k)] == a.stamp) { slot = RingSlot(k); break; }
                if (slot < 0 && _ringCount > 0 && a.stamp > _ringStamp[RingSlot(_ringCount - 1)]) { i++; continue; } // not kept yet
                _embedAsks.RemoveAt(i);
                if (slot >= 0) CutAligned(a.p, _ring[slot], a.re, a.le, a.nose, a.mouth);
            }
            if (!_embedBusy && _embedReady.Count > 0) RunEmbedding(_embedReady.Dequeue());
        }

        void CutAligned(TrackedPerson p, Texture tex, Vector2 re, Vector2 le, Vector2 nose, Vector2 mouth)
        {
            var (a, b, tx, ty) = FaceEmbedder.Align(re, le, nose, mouth);
            // The axis-aligned square in the picture that holds the (turned) 112×112 input.
            const float S = FaceEmbedder.Size;
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (var c in new[] { new Vector2(0, 0), new Vector2(S, 0), new Vector2(0, S), new Vector2(S, S) })
            {
                var x = a * c.x - b * c.y + tx; var y = b * c.x + a * c.y + ty;
                minX = Mathf.Min(minX, x); maxX = Mathf.Max(maxX, x); minY = Mathf.Min(minY, y); maxY = Mathf.Max(maxY, y);
            }
            var side = Mathf.Max(maxX - minX, maxY - minY);
            var roi = new Rect((minX + maxX - side) / 2f, (minY + maxY - side) / 2f, side, side);
            if (_embedRt == null) _embedRt = new RenderTexture(EmbedPatch, EmbedPatch, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(tex, _embedRt, new Vector2(roi.width / tex.width, roi.height / tex.height), new Vector2(roi.x / tex.width, 1f - roi.yMax / tex.height));
            UnityEngine.Rendering.AsyncGPUReadback.Request(_embedRt, 0, TextureFormat.RGBA32, req =>
            {
                if (req.hasError) return;
                const int m = EmbedPatch, n = FaceEmbedder.Size;
                var rgba = req.GetData<byte>();
                var ch = new float[3][];
                for (var c = 0; c < 3; c++) ch[c] = new float[m * m];
                for (var y = 0; y < m; y++)
                for (var x = 0; x < m; x++)
                {
                    var i = ((m - 1 - y) * m + x) * 4; // readback rows start at the bottom
                    for (var c = 0; c < 3; c++) ch[c][y * m + x] = rgba[i + c];
                }
                var rgb = new float[3 * n * n];
                var scale = m / side;
                for (var v = 0; v < n; v++)
                for (var u = 0; u < n; u++)
                {
                    float cu = u + 0.5f, cv = v + 0.5f;
                    var px = ((a * cu - b * cv + tx) - roi.x) * scale - 0.5f;
                    var py = ((b * cu + a * cv + ty) - roi.y) * scale - 0.5f;
                    for (var c = 0; c < 3; c++) rgb[c * n * n + v * n + u] = Bilinear(ch[c], m, px, py);
                }
                _embedReady.Enqueue((p, rgb));
                if (_embedReady.Count > 4) _embedReady.Dequeue();
            });
        }

        /// <summary>Load the face ID model now (main thread only) instead of when the first face crop arrives.</summary>
        public void PreloadFaceId()
        {
            if (!FaceIdOn || _embedder != null) return;
            try { _embedder = new FaceEmbedder(faceIdModel); }
            catch (System.Exception ex) { Debug.LogException(ex); reidByFace = false; }
        }

        async void RunEmbedding((TrackedPerson p, float[] rgb) job)
        {
            _embedBusy = true;
            try
            {
                _embedder ??= new FaceEmbedder(faceIdModel);
                var e = await _embedder.Run(job.rgb);
                if (this != null) OnEmbedding(job.p, e);
            }
            catch (System.Exception ex) { Debug.LogException(ex); reidByFace = false; }
            finally { _embedBusy = false; }
        }

        static float GallerySimilarity(TrackedPerson p, float[] e)
        {
            if (p.Gallery.Count == 0) return float.NaN;
            float b1 = -1, b2 = -1, b3 = -1;
            foreach (var g in p.Gallery)
            {
                var s = FaceEmbedder.Cosine(g, e);
                if (s > b1) { b3 = b2; b2 = b1; b1 = s; }
                else if (s > b2) { b3 = b2; b2 = s; }
                else if (s > b3) b3 = s;
            }
            return p.Gallery.Count >= 3 ? (b1 + b2 + b3) / 3f : p.Gallery.Count == 2 ? (b1 + b2) / 2f : b1;
        }

        void OnEmbedding(TrackedPerson p, float[] e)
        {
            if (!People.Contains(p)) return; // went away or handed over meanwhile
            var now = Time.time;
            var own = GallerySimilarity(p, e);
            TrackedPerson other = null;
            var otherSim = float.MinValue;
            void Consider(TrackedPerson q)
            {
                if (q == p || q.Gallery.Count < 3) return;
                if (People.Contains(q) && now - q.LastSeen < VisibleForSwap) return;
                var s = GallerySimilarity(q, e);
                if (s > otherSim) { otherSim = s; other = q; }
            }
            foreach (var q in People) Consider(q);
            foreach (var q in _away) Consider(q);

            if (logFaceId && ++_embedCount % 50 == 1)
                Debug.Log($"[Captions] face id #{_embedCount}: {p.Label} own {own:F2} ({p.Gallery.Count}), best other {(other != null ? other.Label : "-")} {Mathf.Max(otherSim, -1f):F2}");
            var young = p.Gallery.Count < YoungGallery;
            TrackedPerson verdict = null; // someone else
            var stranger = false;
            if (other != null && otherSim > sameFaceSimilarity && (float.IsNaN(own) || otherSim > own + switchMargin)) verdict = other;
            else if (!young && own < strangerSimilarity) stranger = true;

            if (verdict != null || stranger)
            {
                if (p.SwapTo == verdict && p.SwapVotes > 0) p.SwapVotes++;
                else { p.SwapTo = verdict; p.SwapVotes = 1; }
                // An automatic "Person N" tag doesn't protect a copy from merging into someone named by hand (2026-11-09:
                // with auto tagging every duplicate got a tag at once, so a girl tagged Friend came back as Person 4, 5…).
                if (p.SwapVotes >= (young ? 1 : 2) && verdict != null && p.Tag != null
                    && (verdict.Tag == null || AutoTagged(verdict) && !AutoTagged(p))
                    && MeanGallerySimilarity(verdict, p) > DuplicateSimilarity)
                {
                    // The untagged "someone else" is a copy of p (built while p was briefly lost): fold it into p, keep the tag.
                    if (logFaceId) Debug.Log($"[Captions] {verdict.Label} was {p.Label} all along: merged (tag kept)");
                    ReleaseAutoTag(verdict);
                    foreach (var g in verdict.Gallery) if (p.Gallery.Count < GallerySize) p.Gallery.Add(g);
                    _away.Remove(verdict); People.Remove(verdict);
                    p.SwapVotes = 0; p.SwapTo = null;
                    AddToGallery(p, e);
                    return;
                }
                if (p.SwapVotes >= (young ? 1 : 2))
                {
                    var q = verdict ?? NewIdentity();
                    if (logFaceId)
                        Debug.Log($"[Captions] Face of {p.Label} is {(verdict != null ? q.Label : "someone new: " + q.Label)} " +
                                  $"(own {own:F2}, {(verdict != null ? q.Label : "best other")} {Mathf.Max(otherSim, -1f):F2})");
                    HandOver(p, q, e);
                }
                return;
            }
            p.SwapVotes = 0; p.SwapTo = null;
            p.LastOwnSim = own;
            if (float.IsNaN(own) || own > 0.35f) p.GoodPosition = p.HeadPosition;
            if (young || own >= galleryAddSimilarity) AddToGallery(p, e);
        }

        // How much the faces in a's gallery look like b (mean of each one's similarity to b's gallery).
        static float MeanGallerySimilarity(TrackedPerson a, TrackedPerson b)
        {
            if (a.Gallery.Count == 0 || b.Gallery.Count == 0) return float.NaN;
            float sum = 0;
            foreach (var g in a.Gallery) sum += GallerySimilarity(b, g);
            return sum / a.Gallery.Count;
        }

        static void AddToGallery(TrackedPerson p, float[] e)
        {
            if (p.Gallery.Count < GallerySize) { p.Gallery.Add(e); return; }
            // Full: replace a near-duplicate if there is one, else the oldest after the first few.
            var dup = -1; var dupSim = 0.85f;
            for (var i = GalleryKeep; i < p.Gallery.Count; i++)
            {
                var s = FaceEmbedder.Cosine(p.Gallery[i], e);
                if (s > dupSim) { dupSim = s; dup = i; }
            }
            if (dup < 0) { dup = GalleryKeep; }
            p.Gallery.RemoveAt(dup);
            p.Gallery.Add(e);
        }

        TrackedPerson NewIdentity()
        {
            var q = new TrackedPerson { Id = _nextId, Color = Palette[(_nextId - 1) % Palette.Length] };
            _nextId++;
            return q;
        }

        // The face tracked as p is q: q takes over the track (position, mouth history, crops, speaking), p is remembered
        // where its face last matched it (or dropped if it never had a face of its own).
        void HandOver(TrackedPerson p, TrackedPerson q, float[] e)
        {
            var i = People.IndexOf(p);
            if (i < 0) return;
            // A tag put on a track that barely knew its face (a duplicate started a moment before the tap) belongs to
            // the face that was tapped: it moves with it (EasyCom session 4: P6 lost a second after tagging).
            if (p.Tag != null && q.Tag == null && p.Gallery.Count < YoungGallery)
            {
                if (logFaceId) Debug.Log($"[Captions] Tag {p.Tag} moves from Person {p.Id} to Person {q.Id} (same face)");
                q.Tag = p.Tag; p.Tag = null;
            }
            // An automatic tag given to a face a moment before it was recognised (someone remembered from an earlier
            // session, or back in view) isn't needed: q has its own. Take it back so no "Person 5" ghost is left.
            else if (p.Tag != null && q.Tag != null && p.Gallery.Count < YoungGallery && p.Tag.StartsWith("Person "))
            {
                if (p.Tag == $"Person {_autoCount}") _autoCount--;
                p.Tag = null;
            }
            if (q.NoPosition && logFaceId) Debug.Log($"[Captions] {q.Label} recognised from an earlier session");
            q.NoPosition = false;
            var qWasHere = People.IndexOf(q);
            _away.Remove(q);
            if (qWasHere >= 0) People.RemoveAt(qWasHere);
            i = People.IndexOf(p);
            People[i] = q;
            AutoTag(q); // a new face in auto tagging gets its own tag and colour

            q.HeadPosition = p.HeadPosition; q.Velocity = p.Velocity; q.LastFilterTime = p.LastFilterTime;
            q.DistanceMeters = p.DistanceMeters; q.LastSeen = p.LastSeen; q.FaceBoxPx = p.FaceBoxPx;
            q.BoxWorld = p.BoxWorld; q.OuterLipWorld = p.OuterLipWorld; q.InnerLipWorld = p.InnerLipWorld;
            q.MouthActivity = p.MouthActivity; q.MouthOpen = p.MouthOpen;
            Move(p.MouthSamples, q.MouthSamples); Move(p.OpenHistory, q.OpenHistory); Move(p.ActivityHistory, q.ActivityHistory);
            Move(p.Crops, q.Crops); Move(p.AsdFeatures, q.AsdFeatures); Move(p.Speaking, q.Speaking); Move(p.CropDets, q.CropDets);
            q.AsdNext = p.AsdNext; q.SpeakingUntil = p.SpeakingUntil;
            q.CropPrev = p.CropPrev; q.CropsDoneTo = p.CropsDoneTo;
            q.AlignPatch = p.AlignPatch; q.AlignQ = p.AlignQ; q.AlignC = p.AlignC; q.AlignSide = p.AlignSide; q.AlignT = p.AlignT;
            q.EmbedAskedT = p.EmbedAskedT; q.SwapVotes = 0; q.SwapTo = null; q.GoodPosition = q.HeadPosition;
            AddToGallery(q, e);
            for (var k = 0; k < _embedAsks.Count; k++)
                if (_embedAsks[k].p == p) { var a = _embedAsks[k]; a.p = q; _embedAsks[k] = a; }

            // p: its own face is elsewhere. Keep it (tagged, or it has a gallery) where it last matched.
            p.SwapVotes = 0; p.SwapTo = null;
            p.AlignPatch = null; p.AlignT = float.MinValue; p.CropPrev = (float.MinValue, default); p.CropsDoneTo = float.MinValue;
            p.AsdNext = int.MinValue; p.SpeakingUntil = float.MinValue;
            // An untagged (or automatically tagged) p whose gallery looks like q was q all along (a duplicate): fold it in
            // rather than keep it as a ghost (ghosts also count toward the auto tagging limit).
            if ((p.Tag == null || AutoTagged(p)) && p.Gallery.Count > 0)
            {
                if (MeanGallerySimilarity(p, q) > DuplicateSimilarity)
                {
                    ReleaseAutoTag(p);
                    foreach (var g in p.Gallery) if (q.Gallery.Count < GallerySize) q.Gallery.Add(g);
                    _away.Remove(p);
                    if (logFaceId) Debug.Log($"[Captions] {p.Label} was {q.Label} all along: merged");
                    return;
                }
            }
            if (p.Tag != null || p.Gallery.Count >= 3)
            {
                if (p.Gallery.Count > 0) p.HeadPosition = p.GoodPosition;
                if (!_away.Contains(p)) _away.Add(p);
            }
        }

        static bool AutoTagged(TrackedPerson p) => p.Tag != null && p.Tag.StartsWith("Person ");

        // A duplicate's automatic tag goes; if it was the newest number, the next new person gets it again.
        void ReleaseAutoTag(TrackedPerson p)
        {
            if (!AutoTagged(p)) return;
            if (p.Tag == $"Person {_autoCount}") _autoCount--;
            p.Tag = null;
        }

        static void Move<T>(List<T> from, List<T> to) { to.Clear(); to.AddRange(from); from.Clear(); }
        static void Move<T>(Queue<T> from, Queue<T> to) { to.Clear(); foreach (var x in from) to.Enqueue(x); from.Clear(); }

        void DisposeFaceId()
        {
            _embedder?.Dispose();
            if (_embedRt != null) _embedRt.Release();
        }
    }
}
