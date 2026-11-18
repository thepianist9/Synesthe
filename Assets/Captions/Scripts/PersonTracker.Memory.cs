using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace MetaStarter.Captions
{
    // People you named, remembered from one session to the next: each tagged person's face prints (SFace embeddings),
    // voice profile (TitaNet, see VoiceProfiles) and name are kept in a file on the headset (never anywhere else). At start they wait, out of view, until their
    // face is seen again; then face ID gives the track back its name and colour. Settings ▸ Remember people turns it
    // off, which deletes the file.
    public partial class PersonTracker
    {
        const string RememberKey = "captions.rememberPeople";
        const int MaxRemembered = 12;       // most recently seen first
        const float SaveEverySeconds = 15f;

        /// <summary>Keep named people (their face prints) between sessions. On by default; stored on the headset only.</summary>
        [System.NonSerialized] public bool rememberPeople = true;
        float _memoryCheckAt;
        string _memorySaved;

        /// <summary>A person's voice profile (sum of unit fingerprints, how many), for saving with them; set by CaptionDirector.</summary>
        [System.NonSerialized] public System.Func<int, (float[] sum, int count)> VoiceOf;

        /// <summary>Voice profiles of people remembered from earlier sessions (by person id), to seed VoiceProfiles with.</summary>
        public IEnumerable<(int id, float[] sum, int count)> SavedVoices()
        {
            foreach (var p in People) if (p.SavedVoice != null) yield return (p.Id, p.SavedVoice, p.SavedVoiceCount);
            foreach (var p in _away) if (p.SavedVoice != null) yield return (p.Id, p.SavedVoice, p.SavedVoiceCount);
        }

        static string MemoryPath => Path.Combine(Application.persistentDataPath, "people.json");

        [System.Serializable]
        class SavedPerson { public string tag; public Color color; public int dim; public float[] faces; public float[] voice; public int voiceWindows; }
        [System.Serializable]
        class SavedPeople { public List<SavedPerson> people = new(); }

        void Awake()
        {
            rememberPeople = PlayerPrefs.GetInt(RememberKey, 1) == 1;
            if (rememberPeople) LoadPeople();
        }

        /// <summary>A person whose name is worth keeping: tagged, and with enough face prints to be recognised.</summary>
        bool Remembered(TrackedPerson p) => rememberPeople && p.Tag != null && p.Gallery.Count >= 3;

        /// <summary>How many people are remembered (in view or not).</summary>
        public int RememberedCount
        {
            get
            {
                var n = 0;
                foreach (var p in People) if (Remembered(p)) n++;
                foreach (var p in _away) if (Remembered(p)) n++;
                return n;
            }
        }

        /// <summary>Turn remembering on or off. Off forgets everyone saved (the file is deleted).</summary>
        public void SetRememberPeople(bool on)
        {
            rememberPeople = on;
            PlayerPrefs.SetInt(RememberKey, on ? 1 : 0);
            PlayerPrefs.Save();
            if (on) { SavePeople(); return; }
            _away.RemoveAll(p => p.NoPosition);
            try { if (File.Exists(MemoryPath)) File.Delete(MemoryPath); }
            catch (System.Exception e) { Debug.LogWarning($"[Captions] Couldn't delete remembered people: {e.Message}"); }
            _memorySaved = null;
            Debug.Log("[Captions] Remembered people forgotten");
        }

        // An auto tag "Person 4" kept from before: new auto tags count on from it, so two people never share a name.
        void BumpAutoCount(TrackedPerson p)
        {
            if (p.Tag != null && p.Tag.StartsWith("Person ") && int.TryParse(p.Tag.Substring(7), out var n) && n > _autoCount)
                _autoCount = n;
        }

        void TickMemory()
        {
            if (!rememberPeople || Time.unscaledTime < _memoryCheckAt) return;
            _memoryCheckAt = Time.unscaledTime + SaveEverySeconds;
            SavePeople();
        }

        void OnApplicationPause(bool paused) { if (paused) SavePeople(); }

        void SavePeople()
        {
            if (!rememberPeople) return;
            var keep = new List<TrackedPerson>();
            foreach (var p in People) if (Remembered(p)) keep.Add(p);
            foreach (var p in _away) if (Remembered(p) && !p.NoPosition) keep.Add(p);
            keep.Sort((a, b) => b.LastSeen.CompareTo(a.LastSeen));
            foreach (var p in _away) if (Remembered(p) && p.NoPosition) keep.Add(p); // known from before, not seen yet
            if (keep.Count > MaxRemembered) keep.RemoveRange(MaxRemembered, keep.Count - MaxRemembered);

            var data = new SavedPeople();
            foreach (var p in keep)
            {
                var dim = p.Gallery[0].Length;
                var faces = new float[p.Gallery.Count * dim];
                for (var i = 0; i < p.Gallery.Count; i++) System.Array.Copy(p.Gallery[i], 0, faces, i * dim, dim);
                var (voice, windows) = VoiceOf?.Invoke(p.Id) ?? default;
                if (voice == null) { voice = p.SavedVoice; windows = p.SavedVoiceCount; } // not heard yet this session
                data.people.Add(new SavedPerson { tag = p.Tag, color = p.Color, dim = dim, faces = faces, voice = voice, voiceWindows = voice != null ? windows : 0 });
            }
            var json = JsonUtility.ToJson(data);
            if (json == _memorySaved) return;
            try
            {
                File.WriteAllText(MemoryPath + ".tmp", json);
                if (File.Exists(MemoryPath)) File.Delete(MemoryPath);
                File.Move(MemoryPath + ".tmp", MemoryPath);
                _memorySaved = json;
                Debug.Log($"[Captions] Remembered people saved: {data.people.Count}");
            }
            catch (System.Exception e) { Debug.LogWarning($"[Captions] Couldn't save remembered people: {e.Message}"); }
        }

        void LoadPeople()
        {
            try
            {
                if (!File.Exists(MemoryPath)) return;
                var json = File.ReadAllText(MemoryPath);
                var data = JsonUtility.FromJson<SavedPeople>(json);
                if (data?.people == null) return;
                var names = new List<string>();
                foreach (var s in data.people)
                {
                    if (string.IsNullOrEmpty(s.tag) || s.dim <= 0 || s.faces == null || s.faces.Length < 3 * s.dim) continue;
                    var p = NewIdentity();
                    p.Tag = s.tag;
                    p.Color = s.color;
                    p.NoPosition = true;
                    p.LastSeen = float.MinValue / 2;
                    for (var i = 0; i + s.dim <= s.faces.Length; i += s.dim)
                    {
                        var e = new float[s.dim];
                        System.Array.Copy(s.faces, i, e, 0, s.dim);
                        p.Gallery.Add(e);
                    }
                    if (s.voice != null && s.voice.Length > 0 && s.voiceWindows > 0) { p.SavedVoice = s.voice; p.SavedVoiceCount = s.voiceWindows; }
                    _away.Add(p);
                    BumpAutoCount(p);
                    names.Add(p.SavedVoice != null ? $"{p.Tag} (+voice)" : p.Tag);
                }
                _memorySaved = json;
                Debug.Log($"[Captions] Remembered people loaded: {(names.Count > 0 ? string.Join(", ", names) : "none")}");
            }
            catch (System.Exception e) { Debug.LogWarning($"[Captions] Couldn't load remembered people: {e.Message}"); }
        }
    }
}
