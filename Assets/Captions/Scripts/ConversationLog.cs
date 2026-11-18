using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace MetaStarter.Captions
{
    /// <summary>
    /// Saved conversations. Every finished caption line of a conversation (who, when, what) is kept in memory while it
    /// runs; nothing reaches storage until the wearer chooses Save conversation (the pinch menu). From then on the
    /// whole conversation so far, and every later line, is written to
    /// persistentDataPath/conversations/&lt;yyyy-MM-dd_HHmmss&gt;.jsonl (one JSON record per line, written as each caption
    /// finishes, so a crash loses at most the line being said). Stopping the save keeps what was written and drops
    /// the lines said while it was off. When a saved conversation ends, <see cref="ConversationSummarizer"/> summarises it
    /// on the headset (…/&lt;id&gt;.summary.txt).
    /// Text only, never audio, and only on the headset (app storage; reachable over USB, not by other apps).
    /// Records: a header {"start", "mode"}; lines {"t" (s since start), "id", "who", "color", "you", "text", "src"} (the
    /// same id again = that line was read again after a language switch: the newer text wins); an end {"end", "seconds"}.
    /// </summary>
    public class ConversationLog : MonoBehaviour
    {
        public CaptionDirector director;

        public static string Folder => Path.Combine(Application.persistentDataPath, "conversations");
        public static string PathOf(string id) => Path.Combine(Folder, id + ".jsonl");
        public static string SummaryPath(string id) =>
            Path.Combine(Folder, IsDay(id) ? "day_" + id.Substring(DayPrefix.Length) + ".summary.txt" : id + ".summary.txt");

        /// <summary>A day's summary has the id "day:yyyy-MM-dd": one summary of all that day's saved conversations, made
        /// from their own summaries.</summary>
        public const string DayPrefix = "day:";
        /// <summary>First line of a summary written online (by Claude) rather than on the headset.</summary>
        public const string OnlineMark = "@online\n";
        public static bool IsDay(string id) => id != null && id.StartsWith(DayPrefix, StringComparison.Ordinal);
        public static string DayOf(string id) => IsDay(id) ? id : DayPrefix + id.Substring(0, Math.Min(10, id.Length));

        /// <summary>A conversation is running (started, not yet ended).</summary>
        public bool Running { get; private set; }
        /// <summary>The running conversation is being saved.</summary>
        public bool Saving { get; private set; }
        /// <summary>The running conversation's id (its start time), or null.</summary>
        public string CurrentId { get; private set; }
        /// <summary>Lines in the running conversation so far.</summary>
        public int LineCount => _lines.Count;
        /// <summary>Captions recorded in the running conversation (updates of one caption count again).</summary>
        public int Said { get; private set; }
        /// <summary>A saved conversation ended (its id): time to summarise it.</summary>
        public event Action<string> Ended;
        public event Action Changed;

        [Serializable] class Header { public string start; public string mode; }
        [Serializable] class End { public string end; public double seconds; }
        [Serializable]
        public class Line
        {
            public double t; public int id; public string who; public string color; public bool you; public string text; public string src;
        }

        readonly List<Line> _lines = new();   // this conversation's lines not yet written (all of them while not saving)
        DateTime _start;
        float _startTime;
        StreamWriter _file;

        void OnEnable()
        {
            if (director != null) director.TranscriptLine += OnLine;
            SignReader.Signed += OnSigned;
        }

        void OnDisable()
        {
            if (director != null) director.TranscriptLine -= OnLine;
            SignReader.Signed -= OnSigned;
            EndConversation();
        }

        void OnApplicationQuit() => EndConversation();

        /// <summary>A conversation starts (Start conversation in the setup). Not saved until <see cref="SetSaving"/>.</summary>
        public void BeginConversation()
        {
            EndConversation();
            _start = DateTime.Now;
            _startTime = Time.realtimeSinceStartup;
            CurrentId = _start.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture);
            _lines.Clear();
            Said = 0;
            Running = true;
            Saving = false;
            // Saved (and so summarised) unless you turned saving off: Save in the conversation menu, remembered.
            if (PlayerPrefs.GetInt(SavePref, 1) == 1) SetSaving(true);
            Changed?.Invoke();
        }

        /// <summary>Whether new conversations are saved: what you last chose with Save in the conversation menu.</summary>
        public const string SavePref = "captions.save";

        /// <summary>The conversation is over (back to the setup, or the app closes). A saved one is closed and handed on
        /// to be summarised; an unsaved one is forgotten.</summary>
        public void EndConversation()
        {
            if (!Running) return;
            Running = false;
            var saved = Saving;
            if (Saving)
            {
                Write(JsonUtility.ToJson(new End { end = Stamp(DateTime.Now), seconds = Math.Round(Time.realtimeSinceStartup - _startTime, 1) }));
                _file?.Dispose();
                _file = null;
                Debug.Log($"[Captions] Conversation {CurrentId} saved");
            }
            Saving = false;
            _lines.Clear();
            var id = CurrentId;
            CurrentId = null;
            Changed?.Invoke();
            if (saved && id != null) Ended?.Invoke(id);
        }

        /// <summary>Start (or stop) saving the running conversation. Starting writes everything said so far; stopping
        /// keeps what was written, and what's said while it's off is not kept.</summary>
        public void SetSaving(bool on)
        {
            if (!Running || on == Saving) return;
            if (on)
            {
                try
                {
                    Directory.CreateDirectory(Folder);
                    var path = PathOf(CurrentId);
                    var fresh = !File.Exists(path);
                    _file = new StreamWriter(path, append: true) { AutoFlush = true };
                    if (fresh) Write(JsonUtility.ToJson(new Header { start = Stamp(_start), mode = director != null ? director.mode.ToString() : "" }));
                    foreach (var l in _lines) Write(JsonUtility.ToJson(l));
                    _lines.Clear();
                    Saving = true;
                    Debug.Log($"[Captions] Saving conversation {CurrentId}");
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[Captions] Can't save the conversation: {e.Message}");
                    _file?.Dispose();
                    _file = null;
                }
            }
            else
            {
                _file?.Dispose();
                _file = null;
                Saving = false;
                Debug.Log($"[Captions] Stopped saving conversation {CurrentId}");
            }
            Changed?.Invoke();
        }

        void OnLine(int id, TrackedPerson who, bool you, string text) =>
            Add(new Line { id = id, who = you ? "You" : who?.Label ?? "Someone", color = you || who == null ? null : "#" + ColorUtility.ToHtmlStringRGB(who.Color), you = you, text = text, src = "speech" });

        // Signed in sign to speech: the wearer's own words. Ids for these count down so they never meet a caption's.
        int _signId;
        void OnSigned(string text) => Add(new Line { id = --_signId, who = "You", you = true, text = text, src = "sign" });

        void Add(Line l)
        {
            if (!Running || string.IsNullOrWhiteSpace(l.text)) return;
            Said++;
            l.t = Math.Round(Time.realtimeSinceStartup - _startTime, 1);
            if (Saving) Write(JsonUtility.ToJson(l));
            else
            {
                // Not saved (yet): keep it in memory, replacing a corrected line rather than adding it again.
                var i = _lines.FindIndex(x => x.id == l.id);
                if (i >= 0) { l.t = _lines[i].t; _lines[i] = l; }
                else _lines.Add(l);
            }
        }

        void Write(string json)
        {
            try { _file?.WriteLine(json); }
            catch (Exception e) { Debug.LogWarning($"[Captions] Conversation not written: {e.Message}"); }
        }

        static string Stamp(DateTime d) => d.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

        // ---------------- reading back ----------------

        /// <summary>A saved conversation, as listed in Past conversations.</summary>
        public sealed class Saved
        {
            public string Id;
            public DateTime Start;
            public double Seconds;
            public List<Line> Lines = new();       // in order, corrections applied
            public List<string> People = new();    // named speakers, in order of first line (not "You"/"Someone"/"Person n")
            public string Summary;                 // null: not summarised yet
            public List<string> Parts;             // a day: its conversations, oldest first (null for a conversation)
            public int Summarised;                 // a day: how many of them have a summary
            public bool Stale;                     // a day: a conversation was summarised (or added) after the day was
            public bool Online;                    // the summary was written online (Claude), not on the headset
            public bool IsDay => Parts != null;
        }

        /// <summary>The saved conversations, newest first.</summary>
        public static List<string> List()
        {
            var ids = new List<string>();
            if (!Directory.Exists(Folder)) return ids;
            foreach (var f in Directory.GetFiles(Folder, "*.jsonl")) ids.Add(Path.GetFileNameWithoutExtension(f));
            ids.Sort(StringComparer.Ordinal);
            ids.Reverse();
            return ids;
        }

        /// <summary>The saved conversations with, before each day's newest one, that day's summary when the day has
        /// two or more conversations. Newest first.</summary>
        public static List<string> ListWithDays()
        {
            var ids = List();
            var all = new List<string>();
            for (var i = 0; i < ids.Count; i++)
            {
                var day = DayOf(ids[i]);
                if ((i == 0 || DayOf(ids[i - 1]) != day) && i + 1 < ids.Count && DayOf(ids[i + 1]) == day) all.Add(day);
                all.Add(ids[i]);
            }
            return all;
        }

        /// <summary>A day's saved conversations, oldest first.</summary>
        public static List<string> ConversationsOn(string dayId)
        {
            var day = dayId.Substring(DayPrefix.Length);
            var ids = List().FindAll(x => x.StartsWith(day, StringComparison.Ordinal));
            ids.Reverse();
            return ids;
        }

        static Saved LoadDay(string id)
        {
            var s = new Saved { Id = id, Parts = ConversationsOn(id) };
            DateTime.TryParseExact(id.Substring(DayPrefix.Length), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out s.Start);
            var sp = SummaryPath(id);
            var written = File.Exists(sp) ? File.GetLastWriteTimeUtc(sp) : DateTime.MinValue;
            ReadSummary(s);
            foreach (var part in s.Parts)
            {
                var c = Load(part);
                s.Seconds += c.Seconds;
                foreach (var p in c.People) if (!s.People.Contains(p)) s.People.Add(p);
                if (c.Summary == null) continue;
                s.Summarised++;
                if (s.Summary != null && File.GetLastWriteTimeUtc(SummaryPath(part)) > written) s.Stale = true;
            }
            return s;
        }

        public static Saved Load(string id)
        {
            if (IsDay(id)) return LoadDay(id);
            var s = new Saved { Id = id };
            DateTime.TryParseExact(id, "yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out s.Start);
            try
            {
                var byId = new Dictionary<int, int>();
                // Shared: the running conversation's file is still open for writing (summarised so far from the menu).
                using var fs = new FileStream(PathOf(id), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs);
                for (var raw = reader.ReadLine(); raw != null; raw = reader.ReadLine())
                {
                    if (raw.StartsWith("{\"start\"") || raw.Length < 3) continue;
                    if (raw.StartsWith("{\"end\"")) { s.Seconds = JsonUtility.FromJson<End>(raw).seconds; continue; }
                    Line l;
                    try { l = JsonUtility.FromJson<Line>(raw); }
                    catch { continue; } // a line cut short by a crash
                    if (l == null || string.IsNullOrEmpty(l.text)) continue;
                    if (byId.TryGetValue(l.id, out var at)) { l.t = s.Lines[at].t; s.Lines[at] = l; continue; }
                    byId[l.id] = s.Lines.Count;
                    s.Lines.Add(l);
                }
            }
            catch (Exception e) { Debug.LogWarning($"[Captions] Can't read conversation {id}: {e.Message}"); }
            if (s.Seconds <= 0 && s.Lines.Count > 0) s.Seconds = s.Lines[^1].t; // ended by a crash: as long as its last line
            foreach (var l in s.Lines)
                if (!l.you && l.who != "Someone" && !l.who.StartsWith("Person ") && !s.People.Contains(l.who)) s.People.Add(l.who);
            ReadSummary(s);
            return s;
        }

        static void ReadSummary(Saved s)
        {
            var sp = SummaryPath(s.Id);
            if (!File.Exists(sp)) return;
            s.Summary = File.ReadAllText(sp);
            if (s.Summary.StartsWith(OnlineMark, StringComparison.Ordinal)) { s.Online = true; s.Summary = s.Summary.Substring(OnlineMark.Length); }
        }

        public static void Delete(string id)
        {
            try
            {
                if (IsDay(id)) { File.Delete(SummaryPath(id)); return; }
                File.Delete(PathOf(id));
                File.Delete(SummaryPath(id));
                Debug.Log($"[Captions] Conversation {id} deleted");
            }
            catch (Exception e) { Debug.LogWarning($"[Captions] Can't delete conversation {id}: {e.Message}"); }
        }

        /// <summary>The conversation as plain "Name: words" lines (for the summary model).</summary>
        public static string PlainText(Saved s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var l in s.Lines) sb.Append(l.who).Append(l.src == "sign" ? " (signed)" : "").Append(": ").Append(l.text).Append('\n');
            return sb.ToString();
        }
    }
}
