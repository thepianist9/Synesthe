using System.Collections.Generic;
using Meta.XR;
using Unity.InferenceEngine;
using UnityEngine;

namespace Week1Bench
{
    /// <summary>Shared objects every test can use.</summary>
    public class BenchContext
    {
        public PerfMonitor Perf;
        public BenchHud Hud;
        public MicStream Mic;
        public HandCapture Hands;
        public PassthroughCameraAccess Camera;
        public ModelAsset FaceModel;
        public TextAsset FaceAnchors;
        public Transform Head;
        public float DurationOverride; // seconds; from the "duration" intent extra (0 = test default)

        public bool AbortRequested;

        /// <summary>Raised by BenchApp once per rendered frame with the unscaled frame time.</summary>
        public event System.Action<float> FrameTick;
        public void RaiseFrameTick(float dt) => FrameTick?.Invoke(dt);

        // Frames rendered in the last full second, updated by BenchApp.
        public float FpsLastSecond;

        public async Awaitable<bool> WaitForCamera(float timeout = 10f)
        {
            if (Camera == null) return false;
            Camera.enabled = true;
            var t = 0f;
            while (!Camera.IsPlaying && t < timeout && !AbortRequested)
            {
                t += Time.unscaledDeltaTime;
                await Awaitable.NextFrameAsync();
            }
            return Camera.IsPlaying;
        }
    }

    public class TestResult
    {
        public string Name;
        public bool Pass;
        public string Headline;
        public Dictionary<string, object> Fields = new();
    }

    public abstract class BenchTest
    {
        public abstract string Id { get; }
        public abstract string Title { get; }
        public abstract string Instructions { get; }
        public abstract string PassRule { get; }

        public abstract Awaitable<TestResult> Run(BenchContext ctx);

        protected static float Elapsed(float start) => Time.realtimeSinceStartup - start;

        protected static void AddPerf(TestResult r, PerfMonitor p)
        {
            r.Fields["target_hz"] = p.TargetHz;
            r.Fields["avg_fps"] = p.AvgFps;
            r.Fields["p95_frame_ms"] = p.PercentileMs(95);
            r.Fields["p99_frame_ms"] = p.PercentileMs(99);
            r.Fields["pct_frames_over_budget"] = p.PercentOverBudget;
            r.Fields["pct_seconds_full_rate"] = p.PercentGoodSeconds;
            r.Fields["cpu_frame_ms"] = p.CpuFrameMs;
            r.Fields["gpu_frame_ms"] = p.GpuFrameMs;
            r.Fields["holds_frame_rate"] = p.HoldsFrameRate;
            r.Fields["worst_thermal"] = PerfMonitor.ThermalName(p.WorstThermalStatus);
            r.Fields["battery_drop_pct"] = p.BatteryDrop;
            r.Fields["duration_s"] = p.Elapsed;
        }

        protected static string F(float? v, string fmt = "F1") => v.HasValue ? v.Value.ToString(fmt) : "";
    }
}
