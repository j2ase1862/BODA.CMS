using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using System.Windows.Threading;
using BODA.CMS.Analytics;
using BODA.CMS.Core.Telemetry;
using BODA.CMS.ViewModels;
using HelixToolkit.Wpf;

namespace BODA.CMS.Views
{
    /// <summary>
    /// 로봇 상태 뷰 — 3D 협동로봇(자세 = 실시간 관절 각도, 관절 색 = CBM 기준선 이탈) +
    /// 축×신호 z 히트맵 + 축별 미니 추세선. 마우스 드래그로 회전, 휠로 확대.
    ///
    /// 3D 는 HelixToolkit.Wpf(WPF Viewport3D) — 원기둥·구 수백 폴리곤이라 저사양 iGPU 에서도
    /// 가볍다. 갱신은 5Hz UI 타이머(차트와 동일 주기), 로봇 모드가 아닐 때는 각도 갱신도 없다.
    /// 치수는 실제 기구학이 아니라 6축 협동로봇의 일반 비례를 따른 개략 모델이다.
    /// </summary>
    public partial class RobotStatusView : UserControl
    {
        private const int MaxAxes = 6;
        private const int SparkCapacity = 150; // 5Hz × 30초

        private static readonly Brush WarnBrush = Frozen("#E0A836");
        private static readonly Brush BadBrush = Frozen("#E06C6C");
        private static readonly Brush NeutralCell = Frozen("#232A32");
        private static readonly Brush DimWarnCell = Frozen("#6E5A24");
        private static readonly Brush LabelBrush = Frozen("#8A93A0");
        // 차트(ScottPlot Category10)와 동일한 축 색.
        private static readonly Brush[] AxisBrushes =
            new[] { "#1F77B4", "#FF7F0E", "#2CA02C", "#D62728", "#9467BD", "#8C564B" }
                .Select(Frozen).ToArray();

        // 3D 재질 — 상태별로 미리 만들어 참조만 교체한다 (같은 참조 재대입은 WPF no-op).
        private static readonly Material ArmMaterial = MakeMaterial("#DCE4EE", emissive: null);
        private static readonly Material DarkMaterial = MakeMaterial("#39434F", emissive: null);
        private static readonly Material OkMaterial = MakeMaterial("#3AA97C", "#123526");
        private static readonly Material WarnMaterial = MakeMaterial("#E0A836", "#4A3608");
        private static readonly Material BadMaterial = MakeMaterial("#E06C6C", "#571F1F");
        private static readonly Material MutedMaterial = MakeMaterial("#5A6470", emissive: null);

        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };

        // 관절 회전(실시간 각도 반영)과 상태 마커(색 교체) — BuildRobot 에서 채움
        private readonly AxisAngleRotation3D[] _rot = new AxisAngleRotation3D[MaxAxes];
        private readonly GeometryModel3D[] _jointMarkers = new GeometryModel3D[MaxAxes];
        // 기본 자세 오프셋: 각도 0일 때 제품 사진 같은 자세가 되게.
        private static readonly double[] RestOffsets = { 0, -35, 60, 0, 30, 0 };
        // 렌더링용 관절 가동범위(±°) — 일반 협동로봇 스펙 수준. 실로봇 각도는 어차피 한계 안이라
        // 왜곡이 없고, 시뮬레이터의 무제한 사인파가 비현실적 자세(자기관통)를 만드는 것만 막는다.
        private static readonly double[] JointLimits = { 360, 115, 150, 360, 120, 360 };

        // 히트맵: 신호 목록이 바뀔 때만 그리드 재구성
        private string[] _heatSignals = Array.Empty<string>();
        private Border[,] _heatCells = new Border[0, 0];

        // 스파크라인: 축 수가 정해지면 행 구성
        private readonly List<double>[] _sparkData = new List<double>[MaxAxes];
        private Polyline[] _sparkLines = Array.Empty<Polyline>();
        private RobotTelemetryFrame? _lastSeenFrame;

        public RobotStatusView()
        {
            InitializeComponent();
            BuildRobot();
            for (int i = 0; i < MaxAxes; i++) _sparkData[i] = new List<double>(SparkCapacity);
            _timer.Tick += (_, _) => OnTick();
            Loaded += (_, _) => _timer.Start();
            Unloaded += (_, _) => _timer.Stop();
        }

        private TelemetrySourceViewModel? Vm => DataContext as TelemetrySourceViewModel;

        private static SolidColorBrush Frozen(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }

        private static Material MakeMaterial(string diffuse, string? emissive)
        {
            var group = new MaterialGroup();
            group.Children.Add(new DiffuseMaterial(Frozen(diffuse)));
            group.Children.Add(new SpecularMaterial(Frozen("#30FFFFFF"), 40));
            if (emissive is not null)
                group.Children.Add(new EmissiveMaterial(Frozen(emissive)));
            group.Freeze();
            return group;
        }

        // ── 3D 로봇 모델 ────────────────────────────────────────────────────────

        private static GeometryModel3D Cylinder(Point3D from, Point3D to, double radius, Material material)
        {
            var mb = new MeshBuilder(true, false);
            mb.AddCylinder(from, to, radius, 24, true, true);
            MeshGeometry3D mesh = mb.ToMesh(true);
            return new GeometryModel3D(mesh, material);
        }

        private static GeometryModel3D Box(Point3D center, double dx, double dy, double dz, Material material)
        {
            var mb = new MeshBuilder(true, false);
            mb.AddBox(center, dx, dy, dz);
            return new GeometryModel3D(mb.ToMesh(true), material);
        }

        /// <summary>관절 그룹 생성: 부모의 attach 지점으로 이동 + 회전축. 자식 지오메트리는 로컬 원점 기준.</summary>
        private Model3DGroup JointGroup(Model3DGroup parent, int axisIndex, Vector3D rotationAxis, Vector3D attach)
        {
            _rot[axisIndex] = new AxisAngleRotation3D(rotationAxis, RestOffsets[axisIndex]);
            var transform = new Transform3DGroup();
            transform.Children.Add(new RotateTransform3D(_rot[axisIndex]));
            transform.Children.Add(new TranslateTransform3D(attach));
            var group = new Model3DGroup { Transform = transform };
            parent.Children.Add(group);
            return group;
        }

        private void BuildRobot()
        {
            var root = new Model3DGroup();

            // 바닥 원판 + 받침대 (정적)
            root.Children.Add(Cylinder(new Point3D(0, -0.006, 0), new Point3D(0, 0, 0), 0.33,
                MakeMaterial("#1A2129", null)));
            root.Children.Add(Cylinder(new Point3D(0, 0, 0), new Point3D(0, 0.06, 0), 0.085, DarkMaterial));

            // J1 (베이스 요) — Y축 회전
            Model3DGroup g1 = JointGroup(root, 0, new Vector3D(0, 1, 0), new Vector3D(0, 0.06, 0));
            _jointMarkers[0] = Cylinder(new Point3D(0, 0, 0), new Point3D(0, 0.05, 0), 0.062, MutedMaterial);
            g1.Children.Add(_jointMarkers[0]);
            g1.Children.Add(Cylinder(new Point3D(0, 0.05, 0), new Point3D(0, 0.17, 0), 0.048, ArmMaterial));

            // J2 (어깨 피치) — Z축 회전. 실물처럼 상완을 기둥과 다른 측면 평면(z+0.055)에 배치
            // — 팔이 접힐 때 기둥·베이스를 옆으로 비켜 지나가 자기관통이 없다.
            Model3DGroup g2 = JointGroup(g1, 1, new Vector3D(0, 0, 1), new Vector3D(0, 0.17, 0));
            _jointMarkers[1] = Cylinder(new Point3D(0, 0, -0.05), new Point3D(0, 0, 0.095), 0.05, MutedMaterial);
            g2.Children.Add(_jointMarkers[1]);
            g2.Children.Add(Cylinder(new Point3D(0, 0, 0.055), new Point3D(0, 0.28, 0.055), 0.038, ArmMaterial));

            // J3 (팔꿈치 피치) — 전완은 한 평면 더 바깥(z+0.055)으로: 상완과도 겹치지 않고 접힌다
            Model3DGroup g3 = JointGroup(g2, 2, new Vector3D(0, 0, 1), new Vector3D(0, 0.28, 0.055));
            _jointMarkers[2] = Cylinder(new Point3D(0, 0, -0.01), new Point3D(0, 0, 0.10), 0.041, MutedMaterial);
            g3.Children.Add(_jointMarkers[2]);
            g3.Children.Add(Cylinder(new Point3D(0, 0, 0.055), new Point3D(0, 0.23, 0.055), 0.032, ArmMaterial));

            // J4 (손목 롤) — 팔 축(Y) 회전
            Model3DGroup g4 = JointGroup(g3, 3, new Vector3D(0, 1, 0), new Vector3D(0, 0.23, 0.055));
            _jointMarkers[3] = Cylinder(new Point3D(0, 0, 0), new Point3D(0, 0.05, 0), 0.037, MutedMaterial);
            g4.Children.Add(_jointMarkers[3]);
            g4.Children.Add(Cylinder(new Point3D(0, 0.05, 0), new Point3D(0, 0.10, 0), 0.031, ArmMaterial));

            // J5 (손목 피치)
            Model3DGroup g5 = JointGroup(g4, 4, new Vector3D(0, 0, 1), new Vector3D(0, 0.10, 0));
            _jointMarkers[4] = Cylinder(new Point3D(0, 0, -0.042), new Point3D(0, 0, 0.042), 0.033, MutedMaterial);
            g5.Children.Add(_jointMarkers[4]);
            g5.Children.Add(Cylinder(new Point3D(0, 0, 0), new Point3D(0, 0.055, 0), 0.027, ArmMaterial));

            // J6 (플랜지 롤) + 그리퍼 손가락
            Model3DGroup g6 = JointGroup(g5, 5, new Vector3D(0, 1, 0), new Vector3D(0, 0.055, 0));
            _jointMarkers[5] = Cylinder(new Point3D(0, 0, 0), new Point3D(0, 0.028, 0), 0.028, MutedMaterial);
            g6.Children.Add(_jointMarkers[5]);
            g6.Children.Add(Box(new Point3D(0.016, 0.05, 0), 0.009, 0.045, 0.02, ArmMaterial));
            g6.Children.Add(Box(new Point3D(-0.016, 0.05, 0), 0.009, 0.045, 0.02, ArmMaterial));

            Viewport.Children.Add(new ModelVisual3D { Content = root });
        }

        private static double Pos(float[]? arr, int i) =>
            arr is not null && i < arr.Length ? arr[i] : 0;

        private void UpdateRobot(RobotTelemetryFrame frame, double[] worstZ, bool[] learned)
        {
            float[] p = frame.JointPositionDeg;
            int axes = Math.Min(MaxAxes, frame.AxisCount);

            for (int i = 0; i < MaxAxes; i++)
            {
                if (i < axes)
                    _rot[i].Angle = RestOffsets[i] + Math.Clamp(Pos(p, i), -JointLimits[i], JointLimits[i]);
                _jointMarkers[i].Material = i >= axes ? MutedMaterial
                    : !learned[i] ? MutedMaterial
                    : worstZ[i] < 2 ? OkMaterial
                    : worstZ[i] < 4 ? WarnMaterial
                    : BadMaterial;
            }
        }

        // ── 히트맵 ──────────────────────────────────────────────────────────────

        private void UpdateHeatmap(IReadOnlyList<CbmAxisDetail> details, int axisCount)
        {
            string[] signals = details.Select(d => d.Signal).Distinct().OrderBy(s => s).ToArray();
            if (!signals.SequenceEqual(_heatSignals)) BuildHeatGrid(signals, axisCount);

            var map = details.ToDictionary(d => (d.Signal, d.Axis));
            for (int r = 0; r < _heatSignals.Length; r++)
            {
                for (int a = 0; a < axisCount; a++)
                {
                    Border cell = _heatCells[r, a];
                    if (map.TryGetValue((_heatSignals[r], a), out CbmAxisDetail? d))
                    {
                        cell.Opacity = d.Learned ? 1.0 : 0.35;
                        cell.Background = !d.Learned || d.WorstZ < 1.5 ? NeutralCell
                            : d.WorstZ < 3 ? DimWarnCell
                            : d.WorstZ < 5 ? WarnBrush
                            : BadBrush;
                        cell.ToolTip = d.Learned
                            ? $"J{a + 1} {d.Signal}  z={d.WorstZ:0.0}{(d.AlertActive ? " (알림 활성)" : "")}"
                            : $"J{a + 1} {d.Signal}  기준선 학습 중";
                    }
                    else
                    {
                        cell.Opacity = 0.35;
                        cell.Background = NeutralCell;
                        cell.ToolTip = null;
                    }
                }
            }
        }

        private void BuildHeatGrid(string[] signals, int axisCount)
        {
            _heatSignals = signals;
            _heatCells = new Border[signals.Length, axisCount];
            Heat.Children.Clear();
            Heat.RowDefinitions.Clear();
            Heat.ColumnDefinitions.Clear();

            Heat.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            for (int a = 0; a < axisCount; a++)
                Heat.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            Heat.RowDefinitions.Add(new RowDefinition());
            foreach (string _ in signals) Heat.RowDefinitions.Add(new RowDefinition());

            for (int a = 0; a < axisCount; a++)
            {
                var h = new TextBlock
                {
                    Text = $"J{a + 1}", Foreground = LabelBrush, FontSize = 10.5,
                    HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 4, 3),
                };
                Grid.SetRow(h, 0);
                Grid.SetColumn(h, a + 1);
                Heat.Children.Add(h);
            }

            for (int r = 0; r < signals.Length; r++)
            {
                var label = new TextBlock
                {
                    Text = signals[r], Foreground = LabelBrush, FontSize = 10.5,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 2),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                Grid.SetRow(label, r + 1);
                Grid.SetColumn(label, 0);
                Heat.Children.Add(label);

                for (int a = 0; a < axisCount; a++)
                {
                    var cell = new Border
                    {
                        Height = 15, CornerRadius = new CornerRadius(3),
                        Margin = new Thickness(0, 0, 4, 3), Background = NeutralCell,
                    };
                    Grid.SetRow(cell, r + 1);
                    Grid.SetColumn(cell, a + 1);
                    Heat.Children.Add(cell);
                    _heatCells[r, a] = cell;
                }
            }
        }

        // ── 스파크라인 ──────────────────────────────────────────────────────────

        private static (float[]? Data, string Label) SparkSource(RobotTelemetryFrame f) =>
            f.JointTorqueNm is not null ? (f.JointTorqueNm, "토크Nm")
            : f.MotorCurrentA is not null ? (f.MotorCurrentA, "전류A")
            : f.TemperatureC is not null ? (f.TemperatureC, "온도℃")
            : (f.JointPositionDeg, "위치°");

        private void AccumulateSpark(RobotTelemetryFrame frame)
        {
            (float[]? data, _) = SparkSource(frame);
            if (data is null) return;
            for (int i = 0; i < Math.Min(MaxAxes, data.Length); i++)
            {
                List<double> buf = _sparkData[i];
                buf.Add(data[i]);
                if (buf.Count > SparkCapacity) buf.RemoveAt(0);
            }
        }

        private void UpdateSparks(RobotTelemetryFrame frame)
        {
            int axes = Math.Min(MaxAxes, frame.AxisCount);
            (_, string label) = SparkSource(frame);
            SparkHeader.Text = $"축별 {label} 추세 (최근 30초)";

            if (_sparkLines.Length != axes) BuildSparkRows(axes);

            for (int i = 0; i < axes; i++)
            {
                List<double> buf = _sparkData[i];
                if (buf.Count < 2) continue;

                double min = buf.Min(), max = buf.Max();
                double span = Math.Max(max - min, 1e-6);
                const double w = 190, h = 15;

                var pts = new PointCollection();
                for (int k = 0; k < buf.Count; k++)
                    pts.Add(new Point(k * w / (SparkCapacity - 1), h - (buf[k] - min) / span * (h - 2) - 1));
                pts.Freeze();
                _sparkLines[i].Points = pts;
            }
        }

        private void BuildSparkRows(int axes)
        {
            Sparks.Children.Clear();
            _sparkLines = new Polyline[axes];
            for (int i = 0; i < axes; i++)
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 3) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
                row.ColumnDefinitions.Add(new ColumnDefinition());

                var label = new TextBlock
                {
                    Text = $"J{i + 1}", FontSize = 10.5, Foreground = AxisBrushes[i],
                    FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(label, 0);
                row.Children.Add(label);

                _sparkLines[i] = new Polyline
                {
                    Stroke = AxisBrushes[i], StrokeThickness = 1.4,
                    Width = 190, Height = 15, HorizontalAlignment = HorizontalAlignment.Left,
                };
                Grid.SetColumn(_sparkLines[i], 1);
                row.Children.Add(_sparkLines[i]);
                Sparks.Children.Add(row);
            }
        }

        // ── 타이머 ──────────────────────────────────────────────────────────────

        private void OnTick()
        {
            TelemetrySourceViewModel? vm = Vm;
            RobotTelemetryFrame? frame = vm?.LastFrame;
            if (vm is null || frame is null) return;

            // 다른 모드에서도 추세 이력은 쌓아 둔다 (로봇 모드 진입 즉시 그래프가 차 있게).
            if (!ReferenceEquals(frame, _lastSeenFrame))
            {
                _lastSeenFrame = frame;
                AccumulateSpark(frame);
            }

            if (vm.View != CardView.Robot) return; // 시각 갱신은 로봇 모드에서만 (저사양 배려)

            IReadOnlyList<CbmAxisDetail> details = vm.CbmDetails;
            var worstZ = new double[MaxAxes];
            var learned = new bool[MaxAxes];
            foreach (CbmAxisDetail d in details)
            {
                if (d.Axis >= MaxAxes) continue;
                worstZ[d.Axis] = Math.Max(worstZ[d.Axis], d.WorstZ);
                learned[d.Axis] |= d.Learned;
            }

            UpdateRobot(frame, worstZ, learned);
            UpdateHeatmap(details, Math.Min(MaxAxes, frame.AxisCount));
            UpdateSparks(frame);
        }
    }
}
