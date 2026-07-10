using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BODA.CMS.Core.Telemetry;
using BODA.CMS.ViewModels;
using ScottPlot;
using ScottPlot.Plottables;

namespace BODA.CMS.Views
{
    /// <summary>
    /// 채널 카드의 라이브 스트립 차트 — 선택 신호 1개를 축별 라인(J1~J6)으로 그린다.
    ///
    /// 데이터 흐름: 드라이버 스레드가 VM 큐에 전 샘플 적재 → 이 뷰의 UI 타이머(10Hz)가
    /// 드레인해서 자체 링 버퍼(축별 double[])에 쌓고, ScottPlot Signal이 그 배열을 그대로 그린다.
    /// (DataStreamer는 Add 내부 NRE가 있어 사용하지 않는다 — 버퍼 관리는 전부 이쪽 책임.)
    /// ScottPlot 접근은 UI 스레드에서만 일어난다. 차트 렌더링은 순수 뷰 관심사라 코드비하인드에 둔다.
    /// </summary>
    public partial class SignalChartView : UserControl
    {
        private const double WindowSeconds = 30; // 스크롤 창 길이

        // 5Hz — 전체 리렌더(축별 수천 점 × 6라인, Skia CPU 렌더)가 UI 스레드를 점유하므로
        // 저사양 PC(2코어급)에서도 입력이 밀리지 않는 수준으로. 스트립 차트 체감 차이는 없다.
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
        private readonly IPalette _palette = new ScottPlot.Palettes.Category10();
        private double[][] _buffers = Array.Empty<double[]>();
        private string? _currentSignal;
        private bool _primed; // 첫 샘플 수신 전에는 버퍼가 0으로 차 있어 프리필 필요

        public SignalChartView()
        {
            InitializeComponent();
            StylePlot();
            _timer.Tick += (_, _) => OnTick();
            Loaded += (_, _) => _timer.Start();
            Unloaded += (_, _) => _timer.Stop();
        }

        private TelemetrySourceViewModel? Vm => DataContext as TelemetrySourceViewModel;

        private void StylePlot()
        {
            Plot plot = PlotControl.Plot;
            // 판독 표의 다크 카드와 톤 일치.
            plot.FigureBackground.Color = Color.FromHex("#1B1F24");
            plot.DataBackground.Color = Color.FromHex("#14171B");
            plot.Axes.Color(Color.FromHex("#D6E1EC"));
            plot.Grid.MajorLineColor = Color.FromHex("#2A2F36");
            plot.Legend.BackgroundColor = Color.FromHex("#1B1F24");
            plot.Legend.FontColor = Color.FromHex("#D6E1EC");
            plot.Legend.OutlineColor = Color.FromHex("#2A2F36");
            plot.ShowLegend(Edge.Right);
        }

        private void OnTick()
        {
            TelemetrySourceViewModel? vm = Vm;
            if (vm is null) return;

            // 표 모드에서는 ScottPlot을 건드리지 않고 큐만 비운다
            // (차트 모드 진입 시 오래된 샘플이 쏟아지지 않게).
            if (!vm.IsChartMode)
            {
                while (vm.TryDequeueChartFrame(out _)) { }
                _currentSignal = null; // 다음 진입 때 새로 구성
                return;
            }

            try
            {
                string? label = vm.SelectedChartSignal?.Label;
                if (label != _currentSignal)
                {
                    RebuildSeries(vm, label);
                }

                bool added = false;
                while (vm.TryDequeueChartFrame(out RobotTelemetryFrame frame))
                {
                    if (label is null) continue; // 신호 미정이면 큐만 비운다
                    double[]? values = TelemetrySignals.Extract(frame, label);
                    if (values is null || values.Length != _buffers.Length) continue;
                    Append(values);
                    added = true;
                }

                if (added)
                {
                    PlotControl.Plot.Axes.AutoScale();
                    PlotControl.Refresh();
                }
            }
            catch (Exception ex)
            {
                // 차트 이상이 모니터링 앱 전체를 죽여선 안 된다 — 이 카드의 차트만 멈춘다.
                _timer.Stop();
                System.Diagnostics.Debug.WriteLine($"[SignalChartView] 차트 갱신 중단: {ex}");
                try
                {
                    System.IO.File.AppendAllText(
                        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BODA.CMS.crash.log"),
                        $"[{DateTime.Now:HH:mm:ss}] SignalChartView 차트 중단:\n{ex}\n\n");
                }
                catch { /* 로그 실패 무시 */ }
            }
        }

        /// <summary>버퍼를 한 칸 왼쪽으로 밀고 최신 샘플을 오른쪽 끝에 추가 (스트립 차트 스크롤).</summary>
        private void Append(double[] values)
        {
            if (!_primed)
            {
                // 첫 샘플로 전체 프리필 — 0에서 실측값으로 튀는 가짜 계단을 없앤다.
                for (int j = 0; j < values.Length; j++) Array.Fill(_buffers[j], values[j]);
                _primed = true;
                return;
            }

            for (int j = 0; j < values.Length; j++)
            {
                double[] buf = _buffers[j];
                Array.Copy(buf, 1, buf, 0, buf.Length - 1);
                buf[^1] = values[j];
            }
        }

        private void RebuildSeries(TelemetrySourceViewModel vm, string? label)
        {
            _currentSignal = label;
            _primed = false;
            Plot plot = PlotControl.Plot;
            plot.Clear();

            if (label is null)
            {
                _buffers = Array.Empty<double[]>();
                PlotControl.Refresh();
                return;
            }

            RobotCapabilities caps = vm.Source.Capabilities;
            int points = Math.Max(100, (int)(caps.NominalSampleRateHz * WindowSeconds));
            double samplePeriod = 1.0 / caps.NominalSampleRateHz;

            _buffers = new double[caps.AxisCount][];
            for (int j = 0; j < caps.AxisCount; j++)
            {
                _buffers[j] = new double[points];
                Signal sig = plot.Add.Signal(_buffers[j], samplePeriod);
                sig.Color = _palette.GetColor(j);
                sig.LegendText = $"J{j + 1}";
            }

            plot.Axes.Bottom.Label.Text = $"{label} — 최근 {WindowSeconds:0}초 (초)";
            plot.Axes.Bottom.Label.ForeColor = Color.FromHex("#D6E1EC");
            plot.Axes.Bottom.Label.FontName = "Malgun Gothic"; // 기본 폰트엔 한글 글리프가 없다
            PlotControl.Refresh();
        }
    }
}
