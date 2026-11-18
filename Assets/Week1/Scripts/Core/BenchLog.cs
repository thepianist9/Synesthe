using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace Week1Bench
{
    /// <summary>
    /// Writes per-second CSV rows and a final summary JSON for each test run to
    /// Application.persistentDataPath/bench/. Pull them with:
    ///   adb pull /sdcard/Android/data/&lt;package&gt;/files/bench
    /// </summary>
    public class BenchLog : IDisposable
    {
        public static string Folder => Path.Combine(Application.persistentDataPath, "bench");

        readonly StreamWriter _csv;
        readonly string[] _columns;
        public string RunId { get; }
        public string CsvPath { get; }

        public BenchLog(string testName, params string[] columns)
        {
            Directory.CreateDirectory(Folder);
            RunId = $"{testName}_{DateTime.Now:yyyyMMdd_HHmmss}";
            CsvPath = Path.Combine(Folder, RunId + ".csv");
            _columns = columns;
            _csv = new StreamWriter(CsvPath, false, new UTF8Encoding(false));
            _csv.WriteLine(string.Join(",", columns));
        }

        public void Row(params object[] values)
        {
            if (values.Length != _columns.Length)
                Debug.LogWarning($"[Bench] {RunId}: row has {values.Length} values, expected {_columns.Length}");
            var sb = new StringBuilder();
            for (var i = 0; i < values.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(Format(values[i]));
            }
            _csv.WriteLine(sb.ToString());
        }

        public void Summary(Dictionary<string, object> fields)
        {
            var sb = new StringBuilder("{\n");
            var first = true;
            foreach (var kv in fields)
            {
                if (!first) sb.Append(",\n");
                first = false;
                sb.Append("  \"").Append(kv.Key).Append("\": ");
                sb.Append(kv.Value switch
                {
                    null => "null",
                    bool b => b ? "true" : "false",
                    string s => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"",
                    _ => Format(kv.Value)
                });
            }
            sb.Append("\n}\n");
            File.WriteAllText(Path.Combine(Folder, RunId + "_summary.json"), sb.ToString());
            Debug.Log($"[Bench] {RunId} summary:\n{sb}");
        }

        static string Format(object v) => v switch
        {
            null => "",
            float f => f.ToString("0.###", CultureInfo.InvariantCulture),
            double d => d.ToString("0.###", CultureInfo.InvariantCulture),
            bool b => b ? "1" : "0",
            string s => s.Contains(",") || s.Contains("\"") ? "\"" + s.Replace("\"", "\"\"") + "\"" : s,
            _ => Convert.ToString(v, CultureInfo.InvariantCulture)
        };

        public void Dispose()
        {
            _csv.Flush();
            _csv.Dispose();
        }
    }
}
