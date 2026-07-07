using System;
using System.Collections.Generic;

namespace BODA.CMS.Core.Telemetry
{
    /// <summary>
    /// 프레임에 존재하는 신호(라벨 + 축별 값 + 표시 형식)의 단일 열거 원천 —
    /// 판독 표·차트·CBM이 같은 라벨 체계를 공유한다(벤더 무관).
    /// </summary>
    public static class TelemetrySignals
    {
        public const string PositionLabel = "위치°";
        public const string VelocityLabel = "속도°/s";

        public static IEnumerable<(string Label, double[] Values, string Format)> Enumerate(RobotTelemetryFrame f)
        {
            static double[] D(float[] v) => Array.ConvertAll(v, x => (double)x);

            yield return (PositionLabel, D(f.JointPositionDeg), "0.00");

            if (f.JointVelocityDegS is { } vel) yield return (VelocityLabel, D(vel), "0.0");
            if (f.JointTorqueNm is { } jts) yield return ("토크Nm", D(jts), "0.00");
            if (f.ModelTorqueNm is { } mdl) yield return ("모델Nm", D(mdl), "0.00");
            if (f.ExternalTorqueNm is { } ext) yield return ("외란Nm", D(ext), "0.00");
            if (f.MotorCurrentA is { } cur) yield return ("전류A", D(cur), "0.00");
            if (f.TemperatureC is { } tmp) yield return ("온도℃", D(tmp), "0.0");

            if (f.VendorRaw is not null)
                foreach ((string key, double[] raw) in f.VendorRaw)
                    yield return (key, raw, "0");
        }

        /// <summary>프레임에서 라벨에 해당하는 축별 값 추출. 없으면 null.</summary>
        public static double[]? Extract(RobotTelemetryFrame f, string label)
        {
            foreach ((string l, double[] values, _) in Enumerate(f))
                if (l == label) return values;
            return null;
        }
    }
}
