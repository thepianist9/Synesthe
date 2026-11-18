using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace MetaStarter.Captions
{
    /// <summary>
    /// Recorded signs: one gloss (e.g. "HELLO") → a short motion stored as humanoid muscle frames (see <see cref="SignPose"/>),
    /// so a sign recorded once plays on any humanoid avatar. Signs ship in this asset; signs recorded on the headset are
    /// saved as JSON in persistentDataPath/signs/ and loaded on top (they win over the asset).
    /// Words with no sign here are fingerspelled.
    /// </summary>
    [CreateAssetMenu(menuName = "Synesthe/Sign Dictionary")]
    public class SignDictionary : ScriptableObject
    {
        [System.Serializable]
        public class Sign
        {
            public string gloss;
            /// <summary>Whole signed sentences only: the English it translates (matched against the caption).</summary>
            public string english = "";
            /// <summary>Where the recording came from (dataset and licence, or the signer who recorded it).</summary>
            public string credit = "";
            public float fps = 30f;
            /// <summary>Frames × <see cref="SignPose.Muscles"/> muscle values, frame after frame.</summary>
            public float[] muscles = System.Array.Empty<float>();
            /// <summary>The signer's face: frames × <see cref="SignPose.FaceChannels"/> weights (0–1); empty = none recorded.</summary>
            public float[] face = System.Array.Empty<float>();

            /// <summary>The sign itself, frames [coreStart, coreEnd]: recordings start with the hands rising from the
            /// signer's lap and end with them dropping back. Between two signs of a sentence those play as a dip, so a
            /// signer goes straight from one sign's core to the next. coreEnd 0 = the whole clip.</summary>
            public int coreStart, coreEnd;

            public int Frames => muscles.Length / SignPose.Muscles;
            public float Seconds => Frames / Mathf.Max(1f, fps);

            /// <summary>The seconds to play: from the core's start when a sign comes before it in the sentence (otherwise
            /// from the clip's start, hands rising), to the core's end. The recorded drop back to the lap isn't played,
            /// even after a sentence's last sign: it was slow and ended in a long still hold, and the avatar's own ease
            /// to its rest pose does the same job.</summary>
            public (float from, float to) Span(bool afterSign)
            {
                var f = Mathf.Max(1f, fps);
                var hasCore = coreEnd > coreStart && coreEnd < Frames;
                return (afterSign && hasCore ? coreStart / f : 0f, hasCore ? coreEnd / f : Seconds);
            }

            /// <summary>The pose at <paramref name="t"/> seconds, interpolated between frames.</summary>
            public void Sample(float t, float[] into)
            {
                var n = Frames;
                if (n == 0) return;
                var f = Mathf.Clamp(t * fps, 0, n - 1);
                var a = (int)f;
                var b = Mathf.Min(a + 1, n - 1);
                var k = f - a;
                for (var i = 0; i < SignPose.Muscles; i++)
                    into[i] = Mathf.Lerp(muscles[a * SignPose.Muscles + i], muscles[b * SignPose.Muscles + i], k);
            }

            /// <summary>The recorded face at <paramref name="t"/> seconds, raising <paramref name="into"/> to it; false if none.</summary>
            public bool SampleFace(float t, float[] into)
            {
                var c = SignPose.FaceChannels.Length;
                var n = face.Length / c;
                if (n == 0) return false;
                var f = Mathf.Clamp(t * fps, 0, n - 1);
                var a = (int)f;
                var b = Mathf.Min(a + 1, n - 1);
                var k = f - a;
                for (var i = 0; i < c; i++)
                    into[i] = Mathf.Max(into[i], Mathf.Lerp(face[a * c + i], face[b * c + i], k));
                return true;
            }
        }

        [Tooltip("Sign language of these signs (ASL, DGS, BSL…). Fingerspelling and gloss rules must match it.")]
        public string language = "ASL";
        public List<Sign> signs = new();
        [Tooltip("Whole sentences signed by a fluent signer, played instead of sign-by-sign when the caption matches.")]
        public List<Sign> sentences = new();

        Dictionary<string, Sign> _byGloss;

        public static string RecordedFolder => Path.Combine(Application.persistentDataPath, "signs");

        /// <summary>Forget the gloss lookup, so the next lookup sees <see cref="signs"/> as they are now (after a re-import).</summary>
        public void Rebuild() => _byGloss = null;

        public bool TryGet(string gloss, out Sign sign)
        {
            if (_byGloss == null) Build();
            return _byGloss.TryGetValue(gloss.ToUpperInvariant(), out sign);
        }

        /// <summary>The recorded sentence that says what <paramref name="text"/> says: the same words in order, apart from
        /// contractions, fillers and greetings' spelling ("Well, hi, how are you?" ≈ "Hello, how are you?"). Allowing even
        /// one word to differ plays sentences that mean something else ("I can't hear him" for "I can't hear you").</summary>
        public bool TryMatch(string text, out Sign sentence, float similarity = 1f)
        {
            sentence = null;
            var words = Words(text);
            if (words.Length == 0) return false;
            var best = 0f;
            foreach (var s in sentences)
            {
                if (s.Frames == 0) continue;
                var other = Words(s.english);
                var score = 1f - (float)Distance(words, other) / Mathf.Max(words.Length, other.Length);
                if (score > best) { best = score; sentence = s; }
            }
            if (best >= similarity) return true;
            sentence = null;
            return false;
        }

        static readonly Dictionary<string, string> Spoken = new()
        {
            ["i'm"] = "i am", ["you're"] = "you are", ["it's"] = "it is", ["that's"] = "that is", ["don't"] = "do not",
            ["can't"] = "can not", ["cannot"] = "can not", ["let's"] = "let us", ["what's"] = "what is", ["where's"] = "where is",
            ["hi"] = "hello", ["thanks"] = "thank you", ["ok"] = "okay",
        };

        // Words speech recognition picks up that a signer wouldn't sign.
        static readonly HashSet<string> Fillers = new() { "um", "uh", "uhm", "er", "oh", "well", "so" };

        static string[] Words(string text)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var c in text.ToLowerInvariant().Replace('’', '\''))
                sb.Append(char.IsLetterOrDigit(c) || c == '\'' ? c : ' ');
            var words = new List<string>();
            foreach (var w in sb.ToString().Split(' ', System.StringSplitOptions.RemoveEmptyEntries))
                if (!Fillers.Contains(w)) words.AddRange((Spoken.TryGetValue(w, out var full) ? full : w).Split(' '));
            return words.ToArray();
        }

        // Word edit distance.
        static int Distance(string[] a, string[] b)
        {
            var d = new int[b.Length + 1];
            for (var j = 0; j <= b.Length; j++) d[j] = j;
            for (var i = 1; i <= a.Length; i++)
            {
                var diag = d[0];
                d[0] = i;
                for (var j = 1; j <= b.Length; j++)
                {
                    var up = d[j];
                    d[j] = Mathf.Min(Mathf.Min(d[j] + 1, d[j - 1] + 1), diag + (a[i - 1] == b[j - 1] ? 0 : 1));
                    diag = up;
                }
            }
            return d[b.Length];
        }

        public int Count { get { if (_byGloss == null) Build(); return _byGloss.Count; } }

        /// <summary>Adds (or replaces) a sign and saves it to <see cref="RecordedFolder"/>.</summary>
        public void Save(Sign sign)
        {
            if (_byGloss == null) Build();
            sign.gloss = sign.gloss.ToUpperInvariant();
            _byGloss[sign.gloss] = sign;
            Directory.CreateDirectory(RecordedFolder);
            File.WriteAllText(Path.Combine(RecordedFolder, FileName(sign.gloss)), JsonUtility.ToJson(sign));
        }

        void Build()
        {
            _byGloss = new Dictionary<string, Sign>();
            foreach (var s in signs) if (!string.IsNullOrEmpty(s.gloss)) _byGloss[s.gloss.ToUpperInvariant()] = s;
            if (!Directory.Exists(RecordedFolder)) return;
            foreach (var file in Directory.GetFiles(RecordedFolder, "*.json"))
            {
                try
                {
                    var s = JsonUtility.FromJson<Sign>(File.ReadAllText(file));
                    if (s != null && !string.IsNullOrEmpty(s.gloss) && s.Frames > 0) _byGloss[s.gloss.ToUpperInvariant()] = s;
                }
                catch (System.Exception e) { Debug.LogWarning($"[Captions] Skipping recorded sign {file}: {e.Message}"); }
            }
        }

        static string FileName(string gloss)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) gloss = gloss.Replace(c, '_');
            return gloss + ".json";
        }
    }
}
