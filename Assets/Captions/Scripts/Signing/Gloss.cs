using System.Collections.Generic;
using System.Text;

namespace MetaStarter.Captions
{
    /// <summary>
    /// English caption → ASL-style gloss: the signs to make, in sign order. A small rule set, not a translator —
    /// it gets the most common structure right and leaves the rest in English order:
    ///   • drops words ASL doesn't sign (articles, forms of "be", "do" as a helper, infinitive "to")
    ///   • time words (today, tomorrow, yesterday, …) go first: ASL sets the time frame before the event
    ///   • wh-questions put the wh-word at the end, with lowered eyebrows; yes/no questions raise the eyebrows
    ///   • "not"/"n't" → NOT after the verb's helper is dropped ("I don't know" → ME NOT KNOW)
    ///   • drops small words a signer leaves out (and, so, just, at, in, on, with…): the caption below shows them
    /// Every gloss is then a dictionary sign (trying <see cref="Forms"/>: base form, irregular past, a synonym) or,
    /// for names and short words, fingerspelled.
    /// </summary>
    public static class Gloss
    {
        public enum Kind { Statement, YesNoQuestion, WhQuestion }

        public readonly struct Sentence
        {
            public readonly List<string> Signs;
            public readonly Kind Kind;
            /// <summary>Glosses that were capitalised mid-sentence in the caption: names, always fingerspelled.</summary>
            public readonly HashSet<string> Names;
            public Sentence(List<string> signs, Kind kind, HashSet<string> names = null) { Signs = signs; Kind = kind; Names = names ?? new HashSet<string>(); }
            public override string ToString() => string.Join(" ", Signs) + (Kind == Kind.Statement ? "" : Kind == Kind.WhQuestion ? " (wh?)" : " (y/n?)");
        }

        static readonly HashSet<string> Dropped = new()
        {
            "a", "an", "the", "is", "am", "are", "was", "were", "be", "been", "being", "to", "of", "um", "uh", "erm",
            "hmm", "mm", "do", "does", "did",
            // Small words a signer leaves out, or that the sign order already carries; the caption shows them.
            "and", "so", "oh", "ah", "just", "really", "actually", "very", "at", "on", "in", "into", "with", "for", "by",
            "about", "kind", "sort", "then", "well", "even", "quite", "pretty", "basically", "literally", "let's", "lets", "as",
        };
        // Spoken forms of a recorded sign: irregular verbs, then words that share the sign.
        static readonly Dictionary<string, string> Irregular = new()
        {
            ["WENT"] = "GO", ["GONE"] = "GO", ["GOING"] = "GO", ["SAID"] = "SAY", ["TOLD"] = "SAY", ["TELL"] = "SAY",
            ["SAW"] = "SEE", ["SEEN"] = "SEE", ["ATE"] = "EAT", ["EATEN"] = "EAT", ["MADE"] = "MAKE", ["CAME"] = "COME",
            ["TOOK"] = "TAKE", ["TAKEN"] = "TAKE", ["GAVE"] = "GIVE", ["GIVEN"] = "GIVE", ["FOUND"] = "FIND",
            ["KNEW"] = "KNOW", ["KNOWN"] = "KNOW", ["THOUGHT"] = "THINK", ["FELT"] = "FEEL", ["HEARD"] = "HEAR",
            ["SLEPT"] = "SLEEP", ["HAD"] = "HAVE", ["HAS"] = "HAVE", ["MET"] = "MEET", ["SAT"] = "SIT", ["DRANK"] = "DRINK",
            ["WROTE"] = "WRITE", ["WRITTEN"] = "WRITE", ["WOKE"] = "WAKE", ["MEANT"] = "MEAN", ["LIVED"] = "LIVE",
            ["HAPPENED"] = "HAPPEN", ["CHILDREN"] = "CHILD", ["PEOPLE"] = "PERSON", ["BETTER"] = "BETTER", ["BEST"] = "GOOD",
            ["BUILT"] = "BUILD", ["FORGOT"] = "FORGET", ["FORGOTTEN"] = "FORGET", ["GOT"] = "GET", ["GOTTEN"] = "GET",
            ["CHOSE"] = "CHOOSE", ["CHOSEN"] = "CHOOSE", ["BOUGHT"] = "BUY", ["TAUGHT"] = "TEACH", ["BROUGHT"] = "BRING",
            ["HELD"] = "HOLD", ["DROVE"] = "DRIVE", ["DRIVEN"] = "DRIVE", ["BEGAN"] = "START", ["BEGUN"] = "START",
            ["BEGIN"] = "START", ["SHOWN"] = "SHOW", ["SPOKE"] = "TALK", ["SPOKEN"] = "TALK", ["LEARNT"] = "LEARN",
            ["KIDS"] = "KID", ["FRIENDS"] = "FRIEND", ["PARENT"] = "PARENTS", ["SHARED"] = "SHARE",
        };
        static readonly Dictionary<string, string> Synonyms = new()
        {
            ["HEY"] = "HELLO", ["THANKS"] = "THANK-YOU", ["THANK"] = "THANK-YOU", ["GREAT"] = "GOOD", ["ALRIGHT"] = "OK",
            ["SURE"] = "YES", ["YUP"] = "YES", ["NAH"] = "NO", ["PROBABLY"] = "MAYBE", ["PERHAPS"] = "MAYBE",
            ["MIGHT"] = "MAYBE", ["QUICK"] = "FAST", ["QUICKLY"] = "FAST", ["SLOWLY"] = "SLOW", ["SPEAK"] = "TALK",
            ["CHAT"] = "TALK", ["GLAD"] = "HAPPY", ["UPSET"] = "MAD", ["ILL"] = "SICK", ["UNWELL"] = "SICK",
            ["MUM"] = "MOM", ["MOMMY"] = "MOM", ["MUMMY"] = "MOM", ["DADDY"] = "DAD", ["PAPA"] = "DAD", ["MAMA"] = "MOM",
            ["DINNER"] = "EAT", ["LUNCH"] = "EAT", ["BREAKFAST"] = "EAT", ["MEAL"] = "FOOD", ["DOC"] = "DOCTOR",
            ["PILLS"] = "MEDICINE", ["PILL"] = "MEDICINE", ["MEDS"] = "MEDICINE", ["SORE"] = "PAIN", ["ACHE"] = "PAIN",
            ["RESTROOM"] = "BATHROOM", ["LOO"] = "TOILET", ["GOODBYE"] = "BYE", ["CIAO"] = "BYE", ["WATCH"] = "LOOK",
            ["DONE"] = "FINISH", ["ALREADY"] = "FINISH", ["EVERYONE"] = "ALL", ["EVERYBODY"] = "ALL", ["EVERYTHING"] = "ALL",
            ["SAME"] = "SAME", ["ALSO"] = "ALSO", ["TOO"] = "TOO", ["HIM"] = "HE", ["HIS"] = "HE", ["HER"] = "SHE",
            ["THEY"] = "IT", ["THEM"] = "IT", ["THIS"] = "THAT", ["THESE"] = "THAT", ["THOSE"] = "THAT", ["HERE"] = "THERE",
            ["LOT"] = "A-LOT", ["LOTS"] = "A-LOT", ["GOTTA"] = "MUST", ["SHOULD"] = "MUST", ["NOPE"] = "NO",
            ["CAN'T"] = "CANNOT", ["BREATH"] = "BREATHE",
            ["ANYTHING"] = "ANY", ["ANYONE"] = "ANY", ["ANYBODY"] = "ANY", ["PHONE"] = "TELEPHONE", ["STORE"] = "SHOP",
            ["CAPTION"] = "SUBTITLE", ["SUBTITLES"] = "SUBTITLE", ["TOUGH"] = "HARD", ["DIFFICULT"] = "HARD", ["SIMPLE"] = "EASY",
            ["AMAZING"] = "WOW", ["AWESOME"] = "COOL", ["FANTASTIC"] = "WOW", ["DAMN"] = "WOW", ["TEACHERS"] = "TEACHER",
            ["PROFESSOR"] = "TEACHER", ["MOVIES"] = "MOVIE", ["FILM"] = "MOVIE", ["CELLPHONE"] = "TELEPHONE",
            ["TALKING"] = "TALK", ["SPEAKING"] = "TALK", ["VOICE"] = "SPEECH", ["START"] = "START", ["FINISHED"] = "FINISH",
            ["PURCHASE"] = "BUY", ["GROCERY"] = "SHOP", ["MARKET"] = "SHOP", ["EXCITING"] = "EXCITED", ["ALLOW"] = "LET", ["MOMENT"] = "MINUTE", ["CHOOSE"] = "PICK", ["SELECT"] = "PICK",
        };

        /// <summary>The glosses to try for a word, best first: itself, an irregular form, the base form without
        /// -s/-es/-ed/-ing/-ly, then a word that shares the sign.</summary>
        public static IEnumerable<string> Forms(string gloss)
        {
            yield return gloss;
            if (Irregular.TryGetValue(gloss, out var irr)) yield return irr;
            foreach (var b in Bases(gloss))
            {
                yield return b;
                if (Synonyms.TryGetValue(b, out var bs)) yield return bs;
            }
            if (Synonyms.TryGetValue(gloss, out var syn)) yield return syn;
        }

        static IEnumerable<string> Bases(string g)
        {
            var n = g.Length;
            if (n > 4 && g.EndsWith("ING")) { var stem = g[..^3]; yield return stem; yield return stem + "E"; if (stem[^1] == stem[^2]) yield return stem[..^1]; }
            if (n > 3 && g.EndsWith("ED")) { yield return g[..^2]; yield return g[..^1]; if (n > 4 && g[^3] == g[^4]) yield return g[..^3]; if (g.EndsWith("IED")) yield return g[..^3] + "Y"; }
            if (n > 3 && g.EndsWith("IES")) yield return g[..^3] + "Y";
            if (n > 3 && g.EndsWith("ES")) yield return g[..^2];
            if (n > 2 && g.EndsWith("S") && !g.EndsWith("SS")) yield return g[..^1];
            if (n > 4 && g.EndsWith("LY")) yield return g[..^2];
            if (n > 3 && g.EndsWith("'S")) yield return g[..^2];
        }
        static readonly HashSet<string> TimeWords = new()
        {
            "today", "tomorrow", "yesterday", "tonight", "now", "later", "soon", "morning", "afternoon", "evening",
            "night", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday", "week", "weekend", "month", "year",
        };
        static readonly HashSet<string> WhWords = new() { "who", "what", "where", "when", "why", "how", "which" };
        static readonly HashSet<string> Auxiliaries = new()
        {
            "is", "are", "am", "was", "were", "do", "does", "did", "can", "could", "will", "would", "should", "have", "has", "may",
        };
        static readonly Dictionary<string, string> Replace = new()
        {
            ["i"] = "ME", ["me"] = "ME", ["my"] = "MY", ["mine"] = "MY", ["we"] = "WE", ["us"] = "WE",
            ["don't"] = "NOT", ["doesn't"] = "NOT", ["didn't"] = "NOT", ["isn't"] = "NOT", ["aren't"] = "NOT",
            ["wasn't"] = "NOT", ["not"] = "NOT", ["won't"] = "WILL NOT", ["can't"] = "CAN'T", ["cannot"] = "CAN'T",
            ["i'm"] = "ME", ["you're"] = "YOU", ["we're"] = "WE", ["they're"] = "THEY", ["it's"] = "IT", ["that's"] = "THAT",
            ["i'll"] = "ME WILL", ["you'll"] = "YOU WILL", ["we'll"] = "WE WILL", ["i've"] = "ME",
            ["okay"] = "OK", ["yeah"] = "YES", ["yep"] = "YES", ["nope"] = "NO", ["gonna"] = "WILL", ["wanna"] = "WANT",
        };

        public static Sentence FromText(string text)
        {
            var raw = Words(text, out var questionMark);
            var kind = KindOf(raw, questionMark);
            var names = NamesIn(text);

            var time = new List<string>();
            var rest = new List<string>();
            var wh = new List<string>();
            for (var i = 0; i < raw.Count; i++)
            {
                var w = raw[i];
                // THANK-YOU already carries the "you".
                if (w == "you" && i > 0 && (raw[i - 1] == "thank" || raw[i - 1] == "thanks")) continue;
                if (Replace.TryGetValue(w, out var r)) { rest.AddRange(r.Split(' ')); continue; }
                if (Dropped.Contains(w)) continue;
                // "Good morning", "good night": a greeting, signed in that order, not a time frame.
                if (TimeWords.Contains(w) && !(i > 0 && raw[i - 1] == "good")) time.Add(w.ToUpperInvariant());
                else if (kind == Kind.WhQuestion && WhWords.Contains(w)) wh.Add(w.ToUpperInvariant());
                else rest.Add(w.ToUpperInvariant());
            }
            var signs = new List<string>(time);
            signs.AddRange(rest);
            signs.AddRange(wh);
            return new Sentence(signs, kind, names);
        }

        // Capitalised words that don't start a sentence ("I met Vanessa"): names, which signers fingerspell.
        static HashSet<string> NamesIn(string text)
        {
            var names = new HashSet<string>();
            var start = true;
            foreach (var token in text.Split(' ', System.StringSplitOptions.RemoveEmptyEntries))
            {
                var w = token.Trim(',', '.', '!', '?', ';', ':', '"', '(', ')').Replace('’', '\'');
                if (w.Length > 1 && !start && char.IsUpper(w[0]) && w != "I" && !w.StartsWith("I'") && !w.Contains('-'))
                    names.Add(w.Split('\'')[0].ToUpperInvariant());
                start = token.EndsWith(".") || token.EndsWith("!") || token.EndsWith("?");
            }
            return names;
        }

        static Kind KindOf(List<string> raw, bool questionMark)
        {
            var first = raw.Count > 0 ? raw[0] : "";
            var hasWh = raw.Exists(WhWords.Contains);
            return hasWh && (questionMark || WhWords.Contains(first) || Auxiliaries.Contains(first)) ? Kind.WhQuestion
                : questionMark || (Auxiliaries.Contains(first) && raw.Count > 2) ? Kind.YesNoQuestion
                : Kind.Statement;
        }

        // Lower-case words with apostrophes kept ("don't"); numbers kept; other punctuation dropped.
        static List<string> Words(string text, out bool questionMark)
        {
            questionMark = text.TrimEnd().EndsWith("?");
            var words = new List<string>();
            var sb = new StringBuilder();
            void Flush()
            {
                var w = sb.ToString().Trim('\'');
                if (w.Length > 0) words.Add(w);
                sb.Clear();
            }
            foreach (var ch in text.ToLowerInvariant().Replace('’', '\''))
            {
                if (char.IsLetterOrDigit(ch) || ch == '\'') sb.Append(ch);
                else Flush();
            }
            Flush();
            return words;
        }
    }
}
