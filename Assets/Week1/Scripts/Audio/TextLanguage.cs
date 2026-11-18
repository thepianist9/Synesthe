using System.Collections.Generic;

namespace Week1Bench
{
    /// <summary>
    /// The language of a recognised sentence, from its text: the writing system (Chinese, Japanese, Korean, Arabic,
    /// Hebrew, Devanagari, Thai, Greek, Cyrillic) or, for Latin script, its common short words and letters.
    /// The audio language check (Whisper tiny) is right on only ~80% of sentences (on the test videos: German heard as
    /// Swedish or English, Spanish as Galician, Arabic as Persian or Japanese) and through a loudspeaker far less;
    /// the multilingual recognizer, left to guess, writes the right language almost always, so its text is the
    /// better witness. Null when the text doesn't say (too short, or no telling words).
    /// </summary>
    public static class TextLanguage
    {
        static readonly Dictionary<string, string[]> Words = new()
        {
            ["en"] = new[] { "the", "and", "is", "are", "you", "that", "it", "of", "to", "what", "have", "this", "with", "for",
                "not", "they", "we", "my", "me", "i", "i'm", "it's", "don't", "do", "so", "but", "like", "just", "yeah",
                "really", "think", "know", "about", "would", "can", "there", "be", "at", "your", "was", "very", "because", "how",
                "now", "right", "um", "uh", "am", "if", "or", "all", "some", "an", "he", "she", "our", "will", "get", "when",
                "then", "from", "go", "going", "want", "people", "one" },
            ["de"] = new[] { "der", "die", "das", "und", "ist", "ich", "nicht", "ein", "eine", "zu", "mit", "sie", "es", "auf",
                "auch", "wir", "was", "wie", "aber", "noch", "sich", "bei", "von", "dem", "den", "für", "mein", "meine",
                "habe", "hat", "sind", "wenn", "dass", "nein", "sehr", "gut", "jetzt", "schon", "mal", "kann", "heißt",
                "oder", "nur", "du", "bin", "mir", "mich", "weil", "gibt", "ganz", "beim", "im", "vom", "zum", "wo", "hier",
                "immer", "viel", "heute" },
            ["es"] = new[] { "el", "la", "los", "las", "que", "y", "en", "es", "un", "una", "por", "con", "para", "pero", "muy",
                "porque", "como", "más", "mi", "me", "yo", "se", "lo", "le", "qué", "sí", "también", "está", "son", "hay",
                "del", "al", "pues", "bueno", "tengo", "soy", "tu", "estoy", "ahora", "vamos", "hoy", "aquí", "voy", "ir",
                "hacer", "eso", "esto", "todo", "nada", "mucho", "cuando", "donde", "quiero" },
            ["fr"] = new[] { "le", "la", "les", "des", "et", "est", "je", "tu", "il", "elle", "nous", "vous", "que", "qui",
                "pas", "un", "une", "du", "au", "en", "ce", "c'est", "mais", "avec", "pour", "dans", "sur", "très", "oui",
                "non", "moi", "ça", "suis", "vais", "j'ai", "on", "parce", "aussi", "être", "fait", "bien" },
            ["it"] = new[] { "il", "lo", "la", "che", "di", "e", "è", "non", "un", "una", "per", "con", "sono", "ho", "mi",
                "ma", "anche", "molto", "perché", "come", "questo", "cosa", "io", "gli", "del", "della", "ci", "ti",
                "bene", "allora", "sì", "sei" },
            ["pt"] = new[] { "o", "a", "os", "as", "que", "e", "é", "não", "um", "uma", "para", "com", "por", "mas", "muito",
                "eu", "você", "isso", "está", "são", "tem", "do", "da", "no", "na", "também", "sim", "então", "meu",
                "minha", "estou", "agora" },
            ["nl"] = new[] { "de", "het", "een", "en", "is", "ik", "je", "niet", "dat", "van", "op", "te", "zijn", "maar",
                "met", "wat", "er", "ook", "voor", "hij", "ze", "we", "heb", "nog", "wel", "dus", "als", "naar" },
            ["sv"] = new[] { "och", "att", "det", "är", "jag", "en", "ett", "inte", "som", "på", "med", "för", "har", "du",
                "vi", "men", "vad", "så", "den", "om", "till", "kan", "mycket", "också", "nej" },
            ["da"] = new[] { "og", "at", "det", "er", "jeg", "en", "et", "ikke", "som", "på", "med", "for", "har", "du",
                "vi", "men", "hvad", "så", "den", "til", "kan", "meget", "også", "nej" },
            ["nb"] = new[] { "og", "å", "det", "er", "jeg", "en", "et", "ikke", "som", "på", "med", "for", "har", "du",
                "vi", "men", "hva", "så", "den", "til", "kan", "veldig", "også", "nei" },
            ["pl"] = new[] { "i", "w", "nie", "się", "to", "że", "na", "jest", "z", "do", "jak", "ale", "co", "tak", "mi",
                "ja", "ty", "jestem", "bardzo", "czy", "po", "dla", "już", "tylko" },
            ["fi"] = new[] { "ja", "on", "ei", "se", "että", "mutta", "minä", "sinä", "hän", "kun", "niin", "myös", "olen",
                "oli", "mitä", "tämä" },
            ["tr"] = new[] { "ve", "bir", "bu", "da", "de", "ne", "için", "çok", "ben", "sen", "var", "yok", "gibi", "ama",
                "mı", "mi", "evet", "hayır" },
        };

        // Letters that belong to few languages: each kind found once adds this much.
        static readonly (string chars, string[] langs)[] Letters =
        {
            ("ß", new[] { "de" }), ("äöü", new[] { "de", "sv", "fi", "tr" }), ("ñ¿¡", new[] { "es" }),
            ("çœèêëîïûù", new[] { "fr" }), ("ãõ", new[] { "pt" }), ("ìò", new[] { "it" }), ("øæ", new[] { "da", "nb" }),
            ("å", new[] { "sv", "da", "nb" }), ("łąęśżźćń", new[] { "pl" }), ("ışğ", new[] { "tr" }),
        };
        const float LetterWeight = 1.5f;

        static Dictionary<string, List<string>> _byWord;

        /// <summary>The sentence's language ("de", "zh"…), or null if the text doesn't tell.</summary>
        public static string Of(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var script = Script(text);
            if (script != null) return script;

            if (_byWord == null)
            {
                var m = new Dictionary<string, List<string>>();
                foreach (var (lang, ws) in Words)
                    foreach (var w in ws)
                    {
                        if (!m.TryGetValue(w, out var l)) m[w] = l = new List<string>();
                        l.Add(lang);
                    }
                _byWord = m;
            }
            var score = new Dictionary<string, float>();
            var tokens = 0;
            foreach (var raw in text.ToLowerInvariant().Split(' ', '\t', '\n'))
            {
                var w = raw.Trim('.', ',', '!', '?', ';', ':', '"', '«', '»', '(', ')', '¿', '¡', '…', '-');
                if (w.Length == 0) continue;
                tokens++;
                if (!_byWord.TryGetValue(w, out var langs)) continue;
                foreach (var l in langs) score[l] = (score.TryGetValue(l, out var s) ? s : 0f) + 1f / langs.Count;
            }
            var lower = text.ToLowerInvariant();
            foreach (var (chars, langs) in Letters)
                if (lower.IndexOfAny(chars.ToCharArray()) >= 0)
                    foreach (var l in langs) score[l] = (score.TryGetValue(l, out var s) ? s : 0f) + LetterWeight / langs.Length;
            if (tokens < 3) return null;
            string best = null; float b = 0, second = 0;
            foreach (var (l, s) in score)
            {
                if (s > b) { second = b; b = s; best = l; }
                else if (s > second) second = s;
            }
            return b >= 2f && b >= 1.5f * second + 0.5f ? best : null;
        }

        /// <summary>The writing system a language is written in ("latn", "han", "arab"…).</summary>
        public static string ScriptOf(string lang) => lang switch
        {
            "zh" => "han", "ja" => "jpan", "ko" => "hang", "ar" => "arab", "he" => "hebr", "hi" => "deva", "th" => "thai",
            "el" => "grek", "ru" or "uk" or "bg" => "cyrl", null => null, _ => "latn",
        };

        /// <summary>The writing system of a text (null: no letters).</summary>
        public static string ScriptOfText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var lang = Script(text);
            if (lang != null) return ScriptOf(lang);
            foreach (var c in text) if (c < 0x250 && char.IsLetter(c)) return "latn";
            return null;
        }

        // Writing systems that name the language outright (the ones the multilingual recognizer writes).
        static string Script(string text)
        {
            int han = 0, kana = 0, hangul = 0, arabic = 0, hebrew = 0, deva = 0, thai = 0, greek = 0, cyr = 0, letters = 0;
            bool uk = false, bg = false, ru = false;
            foreach (var c in text)
            {
                if (char.IsLetter(c)) letters++;
                if (c >= 0x4E00 && c <= 0x9FFF) han++;
                else if (c >= 0x3040 && c <= 0x30FF) kana++;
                else if (c >= 0xAC00 && c <= 0xD7AF) hangul++;
                else if (c >= 0x0600 && c <= 0x06FF) arabic++;
                else if (c >= 0x0590 && c <= 0x05FF) hebrew++;
                else if (c >= 0x0900 && c <= 0x097F) deva++;
                else if (c >= 0x0E00 && c <= 0x0E7F) thai++;
                else if (c >= 0x0370 && c <= 0x03FF) greek++;
                else if (c >= 0x0400 && c <= 0x04FF)
                {
                    cyr++;
                    var l = char.ToLowerInvariant(c);
                    if (l == 'і' || l == 'ї' || l == 'є' || l == 'ґ') uk = true;
                    if (l == 'ы' || l == 'э' || l == 'ё') ru = true;
                    if (l == 'ъ') bg = true;
                }
            }
            if (letters == 0) return null;
            float Share(int n) => n / (float)letters;
            if (Share(kana) > 0.1f) return "ja";
            if (Share(han) > 0.3f) return "zh";
            if (Share(hangul) > 0.3f) return "ko";
            if (Share(arabic) > 0.3f) return "ar";
            if (Share(hebrew) > 0.3f) return "he";
            if (Share(deva) > 0.3f) return "hi";
            if (Share(thai) > 0.3f) return "th";
            if (Share(greek) > 0.3f) return "el";
            if (Share(cyr) > 0.3f) return uk ? "uk" : bg && !ru ? "bg" : "ru";
            return null;
        }
    }
}
