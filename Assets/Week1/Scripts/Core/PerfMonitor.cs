using System.Collections.Generic;
using UnityEngine;

namespace Week1Bench
{
    /// <summary>
    /// Frame-rate, frame-time, CPU/GPU and thermal sampling for one test run.
    /// Device metrics come from OVRPlugin perf metrics and Android's PowerManager.
    /// </summary>
    public class PerfMonitor
    {
        readonly List<float> _frameMs = new List<float>(16384);
        readonly List<float> _windowFps = new List<float>(4096); // fps of each full 1-s window
        readonly FrameTiming[] _timings = new FrameTiming[1];
        float _elapsed;
        int _framesOverBudget;
        int _windowFrames;
        float _windowTime;

        public float TargetHz { get; private set; } = 72f;
        public float BudgetMs => 1000f / TargetHz;
        public float Elapsed => _elapsed;
        public int Frames => _frameMs.Count;
        public float AvgFps => _elapsed > 0 ? _frameMs.Count / _elapsed : 0;
        public float PercentOverBudget => _frameMs.Count == 0 ? 0 : 100f * _framesOverBudget / _frameMs.Count;
        public int WorstThermalStatus { get; private set; }
        public float BatteryStart { get; private set; } = -1;

        // Latest device samples (may be null on Editor / unsupported devices).
        public float? CpuUtilAvg, CpuUtilWorst, GpuUtil, AppCpuMs, AppGpuMs;
        // Unity FrameTimingManager (needs PlayerSettings.enableFrameTimingStats).
        public float CpuFrameMs, GpuFrameMs;
        public int ThermalStatus = -1;

        float _sampleTimer;

        public void Begin()
        {
            _frameMs.Clear();
            _windowFps.Clear();
            _windowFrames = 0;
            _windowTime = 0;
            _elapsed = 0;
            _framesOverBudget = 0;
            WorstThermalStatus = -1;
            BatteryStart = SystemInfo.batteryLevel;
            var hz = OVRPlugin.systemDisplayFrequency;
            TargetHz = hz > 1 ? hz : Mathf.Max(60f, (float)Screen.currentResolution.refreshRateRatio.value);
        }

        public void Tick(float unscaledDt)
        {
            var ms = unscaledDt * 1000f;
            _frameMs.Add(ms);
            _elapsed += unscaledDt;
            // 10% tolerance so vsync jitter is not counted as a dropped frame.
            if (ms > BudgetMs * 1.1f) _framesOverBudget++;

            // Unity's per-frame delta time jitters on Quest even at a steady display rate,
            // so the pass/fail check uses whole 1-second windows instead.
            _windowFrames++;
            _windowTime += unscaledDt;
            if (_windowTime >= 1f)
            {
                _windowFps.Add(_windowFrames / _windowTime);
                _windowFrames = 0;
                _windowTime = 0;
            }

            FrameTimingManager.CaptureFrameTimings();
            if (FrameTimingManager.GetLatestTimings(1, _timings) > 0)
            {
                CpuFrameMs = (float)_timings[0].cpuFrameTime;
                GpuFrameMs = (float)_timings[0].gpuFrameTime;
            }

            _sampleTimer += unscaledDt;
            if (_sampleTimer >= 1f)
            {
                _sampleTimer = 0;
                SampleDevice();
            }
        }

        void SampleDevice()
        {
            CpuUtilAvg = OVRPlugin.GetPerfMetricsFloat(OVRPlugin.PerfMetrics.System_CpuUtilAveragePercentage_Float);
            CpuUtilWorst = OVRPlugin.GetPerfMetricsFloat(OVRPlugin.PerfMetrics.System_CpuUtilWorstPercentage_Float);
            GpuUtil = OVRPlugin.GetPerfMetricsFloat(OVRPlugin.PerfMetrics.System_GpuUtilPercentage_Float);
            AppCpuMs = OVRPlugin.GetPerfMetricsFloat(OVRPlugin.PerfMetrics.App_CpuTime_Float);
            AppGpuMs = OVRPlugin.GetPerfMetricsFloat(OVRPlugin.PerfMetrics.App_GpuTime_Float);
            ThermalStatus = ReadThermalStatus();
            if (ThermalStatus > WorstThermalStatus) WorstThermalStatus = ThermalStatus;
        }

        public float PercentileMs(float p)
        {
            if (_frameMs.Count == 0) return 0;
            var copy = new List<float>(_frameMs);
            copy.Sort();
            var i = Mathf.Clamp(Mathf.CeilToInt(p / 100f * copy.Count) - 1, 0, copy.Count - 1);
            return copy[i];
        }

        public float BatteryDrop => BatteryStart < 0 || SystemInfo.batteryLevel < 0 ? 0 : (BatteryStart - SystemInfo.batteryLevel) * 100f;

        /// <summary>% of 1-second windows that reached at least 97% of the refresh rate (first window = warm-up, ignored).</summary>
        public float PercentGoodSeconds
        {
            get
            {
                if (_windowFps.Count <= 1) return 0;
                var good = 0;
                for (var i = 1; i < _windowFps.Count; i++) if (_windowFps[i] >= TargetHz * 0.97f) good++;
                return 100f * good / (_windowFps.Count - 1);
            }
        }

        /// <summary>Holds the refresh rate if ≥ 95% of 1-second windows reach 97% of the target.</summary>
        public bool HoldsFrameRate => PercentGoodSeconds >= 95f;

        public static string ThermalName(int s) => s switch
        {
            0 => "none", 1 => "light", 2 => "moderate", 3 => "severe",
            4 => "critical", 5 => "emergency", 6 => "shutdown", _ => "n/a"
        };

        static int ReadThermalStatus()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var unity = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = unity.GetStatic<AndroidJavaObject>("currentActivity");
                using var pm = activity.Call<AndroidJavaObject>("getSystemService", "power");
                return pm.Call<int>("getCurrentThermalStatus"); // API 29+
            }
            catch { return -1; }
#else
            return -1;
#endif
        }

        public string Summary() =>
            $"{AvgFps:F1}/{TargetHz:F0} fps · {PercentGoodSeconds:F0}% of seconds at full rate\n" +
            $"CPU {CpuFrameMs:F1} ms · GPU {GpuFrameMs:F1} ms per frame (budget {BudgetMs:F1}) · thermal {ThermalName(ThermalStatus)}";

        static string Fmt(float? v) => v.HasValue ? v.Value.ToString("F0") : "–";
    }
}
