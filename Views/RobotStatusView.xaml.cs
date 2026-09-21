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
        /// <summary>손목 3축 구조 — 벤더에 따라 다르다. J1~J3(베이스 요·어깨·팔꿈치 피치)는 공통.</summary>
        internal enum WristKind
        {
            /// <summary>UR형 오프셋 손목: J4 피치(J3 와 평행) → d5 링크 → J5(링크 방향 축) → d6 측면 링크 → J6(공구축). UR·JAKA Zu.</summary>
            OffsetPitch,
            /// <summary>롤-피치-롤 손목: J4 전완 롤 → J5 피치 → J6 플랜지 롤. Doosan M/A/H 등 전통 6축.</summary>
            RollPitchRoll,
        }

        /// <summary>벤더별 3D 프로필: 영점 오프셋·회전 방향(θ = offset + sign·q) + 손목 구조.</summary>
        internal sealed record RobotProfile(double[] ZeroOffsets, WristKind Wrist, double[] JointSigns);

        // 영점(zero) 규약 — 모델은 θ=0 에서 각 링크가 앞 링크를 직진(로컬 +Y)하는 규약.
        // UR 은 q=0 에서 팔이 수평으로 뻗은 규약이라 J2·J4 에 +90°(URDF 대조). Doosan·JAKA 등 "0° = 수직 상향" 벤더는 0.
        // 시뮬레이터는 UR 규약·UR 손목으로 출력한다. 미등록 벤더는 롤-피치-롤·오프셋 0 (전통 6축이 다수).
        private static readonly double[] UrZeroOffsets = { 0, 90, 0, 90, 0, 0 };
        private static readonly double[] NoZeroOffsets = { 0, 0, 0, 0, 0, 0 };

        // 회전 방향 — 컨트롤러가 보고하는 각도의 +방향이 모델 회전축의 오른손 +방향과 반대면 -1.
        // 모델 축은 J2·J3·(롤-피치-롤)J5 가 +Z, J1·J4·J6 이 +Y 다. 값은 **실기 대조로만** 바꾼다(추측 금지).
        //
        // 두산 J5: v0.7.7 에서 -1 로 넣었다가 같은 날 오후 실기 사진 대조에서 되돌렸다. 손목이 아래로 꺾인
        // 실기(J5=-90.03)를 v0.7.7 은 위로 그렸다 — 반전이 없는 쪽이 맞다. 부호만 보지 말고 공구축이
        // 어디를 향하는지로 판정할 것(ToolTipOffset 기하 테스트가 그 역할).
        private static readonly double[] NoFlips = { 1, 1, 1, 1, 1, 1 };

        private static readonly RobotProfile DefaultProfile = new(NoZeroOffsets, WristKind.RollPitchRoll, NoFlips);
        private static readonly Dictionary<string, RobotProfile> ProfilesByVendor = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ur"] = new(UrZeroOffsets, WristKind.OffsetPitch, NoFlips),
            ["sim"] = new(UrZeroOffsets, WristKind.OffsetPitch, NoFlips),
            ["doosan"] = new(NoZeroOffsets, WristKind.RollPitchRoll, NoFlips),
            // JAKA Zu: UR 과 같은 평행 3축 + 오프셋 손목, 영점은 수직 상향으로 알려짐 — 실기 대조 전 잠정.
            ["jaka"] = new(NoZeroOffsets, WristKind.OffsetPitch, NoFlips),
            // Rokae xMate: 손목 구조·영점 미확인 — 기본(롤-피치-롤·0·부호 +) 유지. 실기 확인 후 갱신.
        };
        /// <summary>검증용 손목 구조 강제: BODA_CMS_3D_WRIST=rpr | offset (시뮬레이터로 두산형 손목 확인 등).</summary>
        private const string WristOverrideEnv = "BODA_CMS_3D_WRIST";

        private RobotProfile _profile = DefaultProfile;
        private string? _profileVendor;
        private WristKind _builtWrist;
        private ModelVisual3D? _robotVisual;

        internal static RobotProfile ResolveProfile(string vendorId)
        {
            RobotProfile profile = ProfilesByVendor.TryGetValue(vendorId, out RobotProfile? p) ? p : DefaultProfile;
            string? forced = Environment.GetEnvironmentVariable(WristOverrideEnv);
            if (string.Equals(forced, "rpr", StringComparison.OrdinalIgnoreCase)) profile = profile with { Wrist = WristKind.RollPitchRoll };
            else if (string.Equals(forced, "offset", StringComparison.OrdinalIgnoreCase)) profile = profile with { Wrist = WristKind.OffsetPitch };
            return profile;
        }

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
            BuildRobot(DefaultProfile.Wrist);
            // 뷰포트 높이를 폭에 비례(300:235)시켜 창이 넓어져도 화면비가 유지되게 — WPF PerspectiveCamera 의
            // FieldOfView 는 '수평' 화각이라 폭만 늘면 세로 화각이 줄어 로봇 위가 잘린다. 상한 420.
            Viewport.SizeChanged += (_, _) =>
            {
                double h = Math.Clamp(Viewport.ActualWidth * 235.0 / 300.0, 235, 420);
                if (Math.Abs(Viewport.Height - h) > 1) Viewport.Height = h;
            };
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

        /// <summary>관절 프레임: 부모 원점 기준 회전축과 부착 지점. 자세를 결정하는 값이라 형상과 따로 표로 둔다.</summary>
        internal readonly record struct JointFrame(Vector3D Axis, Vector3D Attach);

        // 관절 프레임 표 — 팔(J1~J3)은 벤더 공통, 손목(J4~J6)만 구조별로 갈린다.
        // 측면 오프셋도 실물처럼 지그재그: 어깨(0) → 상완 바깥(+0.085) → 전완 안쪽(-0.065 또는 0).
        private static readonly JointFrame[] ArmFrames =
        {
            new(new Vector3D(0, 1, 0), new Vector3D(0, 0.06, 0)),       // J1 베이스 요
            new(new Vector3D(0, 0, 1), new Vector3D(0, 0.10, 0)),       // J2 어깨 피치
            new(new Vector3D(0, 0, 1), new Vector3D(0, 0.265, 0.085)),  // J3 팔꿈치 피치
        };
        private static readonly JointFrame[] OffsetPitchWristFrames =
        {
            new(new Vector3D(0, 0, 1), new Vector3D(0, 0.245, 0)),      // J4 손목1 피치(J3 와 평행)
            new(new Vector3D(0, 1, 0), new Vector3D(0, 0.062, 0)),      // J5 손목2(링크 방향 축)
            new(new Vector3D(0, 0, 1), new Vector3D(0, 0, 0.062)),      // J6 플랜지(공구축)
        };
        private static readonly JointFrame[] RollPitchRollWristFrames =
        {
            new(new Vector3D(0, 1, 0), new Vector3D(0, 0.245, -0.065)), // J4 전완 롤
            new(new Vector3D(0, 0, 1), new Vector3D(0, 0.10, 0)),       // J5 손목 피치
            new(new Vector3D(0, 1, 0), new Vector3D(0, 0.055, 0)),      // J6 플랜지 롤
        };

        /// <summary>베이스 요 45°: q1=0 에서 팔 평면이 기본 카메라(+X+Z 대각) 시선에 정면이 되게. 형상엔 영향 없음.</summary>
        internal const double BaseYawDeg = 45;

        /// <summary>J1~J6 관절 프레임(루트 → 플랜지 순).</summary>
        internal static JointFrame[] ChainFor(WristKind wrist) =>
            ArmFrames.Concat(wrist == WristKind.OffsetPitch ? OffsetPitchWristFrames : RollPitchRollWristFrames).ToArray();

        /// <summary>플랜지(J6) 로컬 기준 그리퍼 손가락 끝 — 공구가 어디를 향하는지 판정하는 지점.</summary>
        internal static Vector3D ToolTipOffset(WristKind wrist) => wrist == WristKind.OffsetPitch
            ? new Vector3D(0, 0, 0.0705)
            : new Vector3D(0, 0.0725, 0);

        /// <summary>관절 변환: 부모 원점에서 회전한 뒤 부착 지점으로 이동. 모델과 테스트가 같은 합성 순서를 쓴다.</summary>
        internal static Transform3DGroup JointTransform(JointFrame frame, AxisAngleRotation3D rotation)
        {
            var transform = new Transform3DGroup();
            transform.Children.Add(new RotateTransform3D(rotation));
            transform.Children.Add(new TranslateTransform3D(frame.Attach));
            return transform;
        }

        /// <summary>관절 그룹 생성: 부모의 attach 지점으로 이동 + 회전축. 자식 지오메트리는 로컬 원점 기준.</summary>
        private Model3DGroup JointGroup(Model3DGroup parent, int axisIndex, JointFrame frame)
        {
            _rot[axisIndex] = new AxisAngleRotation3D(frame.Axis, 0);
            var group = new Model3DGroup { Transform = JointTransform(frame, _rot[axisIndex]) };
            parent.Children.Add(group);
            return group;
        }

        /// <summary>모델 전체 구성. 손목 구조가 바뀌면(벤더 전환) 통째로 다시 만든다 — 수백 폴리곤이라 비용 무시.</summary>
        private void BuildRobot(WristKind wrist)
        {
            if (_robotVisual is not null) Viewport.Children.Remove(_robotVisual);
            _builtWrist = wrist;

            JointFrame[] chain = ChainFor(wrist);
            var root = new Model3DGroup
            {
                Transform = new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), BaseYawDeg)),
            };

            // 바닥 원판 + 받침대 (정적)
            root.Children.Add(Cylinder(new Point3D(0, -0.006, 0), new Point3D(0, 0, 0), 0.33,
                MakeMaterial("#1A2129", null)));
            root.Children.Add(Cylinder(new Point3D(0, 0, 0), new Point3D(0, 0.06, 0), 0.085, DarkMaterial));

            // 팔 부분은 UR(UR5e ×0.62 축소)과 대조한 것: J1 수직, J2·J3 평행 피치(로컬 Z). 손목(J4~J6)은 벤더별 빌더.
            // 각 그룹의 자식 지오메트리는 그룹 로컬 원점 기준이고, 관절 위치·축은 ChainFor 표에서 온다.

            // J1 (베이스 요) — Y축 회전
            Model3DGroup g1 = JointGroup(root, 0, chain[0]);
            _jointMarkers[0] = Cylinder(new Point3D(0, 0, 0), new Point3D(0, 0.05, 0), 0.062, MutedMaterial);
            g1.Children.Add(_jointMarkers[0]);
            g1.Children.Add(Cylinder(new Point3D(0, 0.05, 0), new Point3D(0, 0.10, 0), 0.048, ArmMaterial));

            // J2 (어깨 피치) — Z축. 어깨 하우징은 J2 축 방향 원기둥, 상완은 그 바깥 끝에서 시작
            Model3DGroup g2 = JointGroup(g1, 1, chain[1]);
            _jointMarkers[1] = Cylinder(new Point3D(0, 0, -0.045), new Point3D(0, 0, 0.10), 0.05, MutedMaterial);
            g2.Children.Add(_jointMarkers[1]);
            g2.Children.Add(Cylinder(new Point3D(0, 0, 0.085), new Point3D(0, 0.265, 0.085), 0.038, ArmMaterial));

            // J3 (팔꿈치 피치) — 전완은 팔꿈치 안쪽(어깨 평면 근처)으로 되돌아온다
            Model3DGroup g3 = JointGroup(g2, 2, chain[2]);
            _jointMarkers[2] = Cylinder(new Point3D(0, 0, -0.10), new Point3D(0, 0, 0.035), 0.042, MutedMaterial);
            g3.Children.Add(_jointMarkers[2]);
            g3.Children.Add(Cylinder(new Point3D(0, 0, -0.065), new Point3D(0, 0.245, -0.065), 0.032, ArmMaterial));

            if (wrist == WristKind.OffsetPitch) BuildWristOffsetPitch(g3, chain);
            else BuildWristRollPitchRoll(g3, chain);

            _robotVisual = new ModelVisual3D { Content = root };
            Viewport.Children.Add(_robotVisual);
        }

        /// <summary>UR형 오프셋 손목 (UR5e ×0.62 대조). g3 원점 = 팔꿈치, 전완은 z=-0.065 평면, 끝은 y=0.245.</summary>
        private void BuildWristOffsetPitch(Model3DGroup g3, JointFrame[] chain)
        {
            // J4 (손목1 피치) — J3 와 평행. 하우징이 전완 끝에서 바깥으로 나가고 짧은 링크(d5)가 이어진다
            Model3DGroup g4 = JointGroup(g3, 3, chain[3]);
            _jointMarkers[3] = Cylinder(new Point3D(0, 0, -0.085), new Point3D(0, 0, 0.035), 0.033, MutedMaterial);
            g4.Children.Add(_jointMarkers[3]);
            g4.Children.Add(Cylinder(new Point3D(0, 0, 0), new Point3D(0, 0.062, 0), 0.028, ArmMaterial));

            // J5 (손목2) — J4→J5 링크 방향(Y) 축. 하우징은 그 축 방향 원기둥, 이어 d6 링크가 측면(+Z)으로
            Model3DGroup g5 = JointGroup(g4, 4, chain[4]);
            _jointMarkers[4] = Cylinder(new Point3D(0, -0.03, 0), new Point3D(0, 0.03, 0), 0.033, MutedMaterial);
            g5.Children.Add(_jointMarkers[4]);
            g5.Children.Add(Cylinder(new Point3D(0, 0, 0), new Point3D(0, 0, 0.062), 0.026, ArmMaterial));

            // J6 (손목3 = 플랜지) — 공구축(Z) 회전 + 그리퍼 손가락은 +Z 방향
            Model3DGroup g6 = JointGroup(g5, 5, chain[5]);
            _jointMarkers[5] = Cylinder(new Point3D(0, 0, 0), new Point3D(0, 0, 0.025), 0.028, MutedMaterial);
            g6.Children.Add(_jointMarkers[5]);
            g6.Children.Add(Box(new Point3D(0.014, 0, 0.048), 0.008, 0.02, 0.045, ArmMaterial));
            g6.Children.Add(Box(new Point3D(-0.014, 0, 0.048), 0.008, 0.02, 0.045, ArmMaterial));
        }

        /// <summary>롤-피치-롤 손목 (Doosan 등 전통 6축). 손목이 전완 평면(z=-0.065)에서 일직선으로 이어진다.</summary>
        private void BuildWristRollPitchRoll(Model3DGroup g3, JointFrame[] chain)
        {
            // J4 (전완 롤) — 전완 축(Y) 회전. 하우징은 전완 끝의 롤 축 원기둥
            Model3DGroup g4 = JointGroup(g3, 3, chain[3]);
            _jointMarkers[3] = Cylinder(new Point3D(0, 0, 0), new Point3D(0, 0.05, 0), 0.036, MutedMaterial);
            g4.Children.Add(_jointMarkers[3]);
            g4.Children.Add(Cylinder(new Point3D(0, 0.05, 0), new Point3D(0, 0.10, 0), 0.030, ArmMaterial));

            // J5 (손목 피치) — 롤 축에 수직(Z). 하우징은 피치 축 방향 원기둥
            Model3DGroup g5 = JointGroup(g4, 4, chain[4]);
            _jointMarkers[4] = Cylinder(new Point3D(0, 0, -0.042), new Point3D(0, 0, 0.042), 0.033, MutedMaterial);
            g5.Children.Add(_jointMarkers[4]);
            g5.Children.Add(Cylinder(new Point3D(0, 0, 0), new Point3D(0, 0.055, 0), 0.027, ArmMaterial));

            // J6 (플랜지 롤) — 공구축(Y) 회전 + 그리퍼 손가락은 +Y 방향
            Model3DGroup g6 = JointGroup(g5, 5, chain[5]);
            _jointMarkers[5] = Cylinder(new Point3D(0, 0, 0), new Point3D(0, 0.028, 0), 0.028, MutedMaterial);
            g6.Children.Add(_jointMarkers[5]);
            g6.Children.Add(Box(new Point3D(0.014, 0.05, 0), 0.008, 0.045, 0.02, ArmMaterial));
            g6.Children.Add(Box(new Point3D(-0.014, 0.05, 0), 0.008, 0.045, 0.02, ArmMaterial));
        }

        private static double Pos(float[]? arr, int i) =>
            arr is not null && i < arr.Length ? arr[i] : 0;

        /// <summary>컨트롤러 각도 q(deg) → 모델 회전각 θ = 영점 오프셋 + 회전 방향 × q.</summary>
        internal static double ModelAngle(RobotProfile profile, int axisIndex, double q)
        {
            double offset = axisIndex < profile.ZeroOffsets.Length ? profile.ZeroOffsets[axisIndex] : 0;
            double sign = axisIndex < profile.JointSigns.Length ? profile.JointSigns[axisIndex] : 1;
            return offset + sign * q;
        }

        private void UpdateRobot(RobotTelemetryFrame frame, double[] worstZ, bool[] learned)
        {
            float[] p = frame.JointPositionDeg;
            int axes = Math.Min(MaxAxes, frame.AxisCount);

            if (!string.Equals(_profileVendor, frame.VendorId, StringComparison.OrdinalIgnoreCase))
            {
                _profileVendor = frame.VendorId;
                _profile = ResolveProfile(frame.VendorId);
                if (_profile.Wrist != _builtWrist) BuildRobot(_profile.Wrist);
            }
            // 클램프 없음 — 실기 각도는 그대로 보여야 자세가 맞는다(UR J2 는 -180°대도 정상 범위).
            for (int i = 0; i < MaxAxes; i++)
            {
                if (i < axes)
                    _rot[i].Angle = ModelAngle(_profile, i, Pos(p, i));
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

            // 셀 열은 창 폭에 따라 늘어나되(별 크기) 32~96px 로 제한 — 좁은 창에서 겹치지 않고 넓은 창에서 과장되지 않게
            Heat.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            for (int a = 0; a < axisCount; a++)
                Heat.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 32, MaxWidth = 96 });
            HeatColumn.MaxWidth = 64 + 96 * axisCount;
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
                // 폭은 레이아웃이 준 실제 폭(창 크기에 따라 늘어남) — 첫 틱(레이아웃 전)은 기본값
                double w = _sparkLines[i].ActualWidth > 1 ? _sparkLines[i].ActualWidth : 190;
                const double h = 15;

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
                    Height = 15, HorizontalAlignment = HorizontalAlignment.Stretch,
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
