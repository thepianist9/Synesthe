using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MetaStarter.Captions;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Measures how sentences flow and how much of them gets a sign, without rendering. Plays each sentence as
/// SignAvatar does (each sign's core after the first, sentence pace, the eased lead into each sign, its hold, the
/// follow spring) on a hidden copy of the character, and tracks the dominant (right) wrist:
///   dip   between two signs, how far the wrist falls below the lower of where the first ends and the next starts (cm):
///         a hand dropping towards the lap between words, which fluent signers don't do;
///   low   the share of the sentence (first sign to last) with the wrist in the lower torso (under 30% of hips → head).
/// Coverage: each sentence's words as SignAvatar resolves them (signed, fingerspelled, left to the caption) and
/// the missing words, most frequent first.
///   unity command eval: return SignFlow.Measure("tools/signs/common_sentences.txt", "Temp/SignFlow");
/// Writes &lt;out&gt;/transitions.tsv, sentences.tsv, missing.tsv; returns a summary.
/// </summary>
public static class SignFlow
{
    const string ModelPath = "Assets/Captions/Signing/Avatar/Models/Realistic.fbx";
    static readonly HashSet<string> Clause = new() { "AND", "BUT", "BECAUSE", "IF", "SO", "OR", "THEN", "WHEN", "WHILE", "UNTIL", "BEFORE", "AFTER" };
    static readonly HashSet<string> Subjects = new() { "I", "ME", "YOU", "HE", "SHE", "IT", "WE", "THEY", "MY", "YOUR", "HE-SHE-IT", "HESHEIT" };

    /// <summary>When true, signs play whole inside sentences (no core), to see what the core does.</summary>
    public static bool NoCore;

    class Item { public SignDictionary.Sign Sign; public string Letters; public string Gloss; public float From, To, Rate = 0.85f, Gap = 0.08f, Lead; public bool Last; }

    public static string Measure(string sentencesFile, string outDir, int maxSpelled = 5)
    {
        Directory.CreateDirectory(outDir);
        var dict = AssetDatabase.LoadAssetAtPath<SignDictionary>("Assets/Captions/Signing/SignDictionary.asset");
        var lines = File.ReadAllLines(sentencesFile).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("#")).ToList();
        var go = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath));
        go.hideFlags = HideFlags.HideAndDontSave;
        var trans = new StringBuilder("sentence\tfrom\tto\tend_cm\tstart_cm\tlowest_cm\tdip_cm\tseconds\n");
        var sents = new StringBuilder("sentence\tsigned\tspelled\tleft\tlow_pct\tworst_dip_cm\tseconds\n");
        var missing = new Dictionary<string, int>();
        var dips = new List<float>();
        float lowFrames = 0, allFrames = 0;
        int words = 0, signedW = 0, spelledW = 0, leftW = 0;
        try
        {
            go.transform.SetPositionAndRotation(new Vector3(100, 0, 0), Quaternion.identity);
            var anim = go.GetComponent<Animator>();
            var pose = new SignPose(anim);
            var fit = new HandFit(pose);
            var ready = fit.Ready(Handshapes.Ready());
            var neutral = Handshapes.Neutral();
            var arms = Enumerable.Range(0, SignPose.Muscles).Where(i =>
            {
                var n = HumanTrait.MuscleName[i];
                return n.Contains("Shoulder") || n.Contains(" Arm ") || n.Contains("Forearm") || n.Contains("Hand ");
            }).ToArray();
            pose.Apply(ready);
            var root = go.transform.position.y;
            var hips = anim.GetBoneTransform(HumanBodyBones.Hips).position.y;
            var headY = anim.GetBoneTransform(HumanBodyBones.Head).position.y;
            var wrist = anim.GetBoneTransform(HumanBodyBones.RightHand);
            var letters = new Dictionary<char, float[]>();
            float[] Letter(char c)
            {
                if (letters.TryGetValue(c, out var m)) return m;
                m = Handshapes.Spelling(c, ready);
                fit.Thumb(m, Handshapes.SpotOf(c));
                return letters[c] = m;
            }
            const float dt = 1f / 60f;

            foreach (var text in lines)
            {
                // Resolve as SignAvatar.Final/Enqueue does.
                var items = new List<Item>();
                int sSigned = 0, sSpelled = 0, sLeft = 0;
                if (dict.TryMatch(text, out var wholeSentence))
                    items.Add(new Item { Sign = wholeSentence, Gloss = wholeSentence.english, To = wholeSentence.Span(false).to, Last = true });
                else
                    foreach (var (part, recorded) in SignAvatar.Clauses(dict, text))
                    {
                        if (recorded != null) { items.Add(new Item { Sign = recorded, Gloss = recorded.english, To = recorded.Span(false).to }); continue; }
                        var sentence = Gloss.FromText(part);
                        foreach (var g in sentence.Signs)
                        {
                            words++;
                            SignDictionary.Sign found = null;
                            if (!sentence.Names.Contains(g))
                                foreach (var f in Gloss.Forms(g)) if (dict.TryGet(f, out found)) break;
                            if (found != null)
                            {
                                signedW++; sSigned++;
                                if (items.Count > 0 && items[^1].Sign == found) continue;
                                items.Add(new Item { Sign = found, Gloss = found.gloss });
                                continue;
                            }
                            var spell = new string(g.Where(Handshapes.Has).Select(char.ToUpperInvariant).ToArray());
                            if (spell.Length == 0) continue;
                            if (!sentence.Names.Contains(g)) missing[g] = missing.GetValueOrDefault(g) + 1;
                            if (!sentence.Names.Contains(g) && spell.Length > maxSpelled) { leftW++; sLeft++; continue; }
                            spelledW++; sSpelled++;
                            items.Add(new Item { Letters = spell, Gloss = g });
                        }
                    }
                if (items.Count == 0) { sents.Append($"{text}\t{sSigned}\t{sSpelled}\t{sLeft}\t\t\t\n"); continue; }
                var wordByWord = items.Count > 1;
                for (var i = 0; i < items.Count; i++)
                {
                    var it = items[i];
                    if (it.Sign != null && !dict.sentences.Contains(it.Sign))
                    {
                        (it.From, it.To) = NoCore ? (0f, it.Sign.Seconds) : it.Sign.Span(i > 0);
                        it.Rate = SignAvatar.RateFor(it.To - it.From, 0.85f, wordByWord);
                    }
                    it.Last = i == items.Count - 1;
                    if (!it.Last && Clause.Contains(items[i + 1].Gloss)) it.Gap = 0.13f;
                    else if (i == 0 && !it.Last && Subjects.Contains(it.Gloss)) it.Gap = 0.1f;
                }

                // Play it: from hands in front (between sentences), lead → sign → hold for each item.
                var now = (float[])neutral.Clone();
                var vel = new float[now.Length];
                var target = new float[now.Length];
                var from = new float[now.Length];
                var first = new float[now.Length];
                var trace = new List<(int item, int phase, float h)>(); // phase 0 lead, 1 body, 2 hold
                void Sample(Item it, float signTime, float[] into)
                {
                    if (it.Sign != null) { System.Array.Copy(ready, into, into.Length); it.Sign.Sample(Mathf.Min(it.From + signTime, it.To), into); return; }
                    var pos = signTime * 4.5f;
                    var idx = Mathf.Min((int)pos, it.Letters.Length - 1);
                    System.Array.Copy(Letter(it.Letters[idx]), into, into.Length);
                    Handshapes.Motion(it.Letters[idx], Mathf.Clamp01(pos - idx), into);
                }
                for (var i = 0; i < items.Count; i++)
                {
                    var it = items[i];
                    System.Array.Copy(now, from, from.Length);
                    Sample(it, 0f, first);
                    var far = arms.Max(k => Mathf.Abs(first[k] - from[k]));
                    it.Lead = Mathf.Clamp(0.12f + 0.3f * far, 0.14f, 0.4f);
                    var body = it.Sign != null ? (it.To - it.From) / it.Rate : it.Letters.Length / 4.5f;
                    var hold = it.Last ? 0.4f : it.Gap;
                    for (var t = 0f; t < it.Lead + body + hold; t += dt)
                    {
                        int phase;
                        if (t < it.Lead)
                        {
                            var u = t / it.Lead;
                            var k = u * u * u * (u * (u * 6f - 15f) + 10f);
                            for (var m = 0; m < target.Length; m++) target[m] = Mathf.LerpUnclamped(from[m], first[m], k);
                            phase = 0;
                        }
                        else
                        {
                            var st = (t - it.Lead) * (it.Sign != null ? it.Rate : 1f);
                            if (it.Sign == null && st * 4.5f >= it.Letters.Length) System.Array.Copy(ready, target, target.Length);
                            else Sample(it, st, target);
                            phase = t < it.Lead + body ? 1 : 2;
                        }
                        SignPose.Follow(now, target, vel, dt);
                        pose.Apply(now);
                        trace.Add((i, phase, wrist.position.y));
                    }
                }

                // Measure: transitions (end of one body → start of the next body).
                var worst = 0f;
                for (var i = 0; i + 1 < items.Count; i++)
                {
                    var endIdx = trace.FindLastIndex(x => x.item == i && x.phase == 1);
                    var startIdx = trace.FindIndex(x => x.item == i + 1 && x.phase == 1);
                    if (endIdx < 0 || startIdx < 0) continue;
                    var e = trace[endIdx].h;
                    var s = trace[startIdx].h;
                    var lo = float.MaxValue;
                    for (var k = endIdx; k <= startIdx; k++) lo = Mathf.Min(lo, trace[k].h);
                    var dip = Mathf.Max(0f, Mathf.Min(e, s) - lo) * 100f;
                    dips.Add(dip);
                    worst = Mathf.Max(worst, dip);
                    trans.Append($"{text}\t{items[i].Gloss}\t{items[i + 1].Gloss}\t{100 * (e - root):0.0}\t{100 * (s - root):0.0}\t{100 * (lo - root):0.0}\t{dip:0.0}\t{(startIdx - endIdx) * dt:0.00}\n");
                }
                var a0 = trace.FindIndex(x => x.phase == 1);
                var a1 = trace.FindLastIndex(x => x.phase == 1);
                var lowLine = hips + 0.3f * (headY - hips);
                var low = 0;
                for (var k = a0; k <= a1; k++) if (trace[k].h < lowLine) low++;
                lowFrames += low;
                allFrames += a1 - a0 + 1;
                sents.Append($"{text}\t{sSigned}\t{sSpelled}\t{sLeft}\t{100f * low / Mathf.Max(1, a1 - a0 + 1):0}\t{worst:0.0}\t{trace.Count * dt:0.0}\n");
            }
        }
        finally { Object.DestroyImmediate(go); }
        File.WriteAllText(Path.Combine(outDir, "transitions.tsv"), trans.ToString());
        File.WriteAllText(Path.Combine(outDir, "sentences.tsv"), sents.ToString());
        File.WriteAllText(Path.Combine(outDir, "missing.tsv"), string.Join("\n", missing.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}\t{kv.Value}")) + "\n");
        dips.Sort();
        var median = dips.Count > 0 ? dips[dips.Count / 2] : 0f;
        return $"{lines.Count} sentences, {dips.Count} joins: dip mean {(dips.Count > 0 ? dips.Average() : 0):0.0} cm, median {median:0.0}, over 5 cm {dips.Count(d => d > 5)}, over 10 cm {dips.Count(d => d > 10)}; " +
               $"wrist low {100 * lowFrames / Mathf.Max(1, allFrames):0.0}% of signing; words {words}: signed {signedW}, spelled {spelledW}, left {leftW}, missing kinds {missing.Count}";
    }
}
