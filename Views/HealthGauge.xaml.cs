using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BODA.CMS.Views
{
    /// <summary>
    /// CBM 건강도 원형 게이지 (0~100). Score 비율만큼 12시 방향부터 시계 방향으로 호를 채운다.
    /// 값·색은 카드 헤더의 CBM 칩과 같은 바인딩을 쓴다 (HealthScore / CbmBrush).
    /// </summary>
    public partial class HealthGauge : UserControl
    {
        public static readonly DependencyProperty ScoreProperty = DependencyProperty.Register(
            nameof(Score), typeof(double), typeof(HealthGauge),
            new PropertyMetadata(100.0, (d, _) => ((HealthGauge)d).Redraw()));

        public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
            nameof(Accent), typeof(Brush), typeof(HealthGauge),
            new PropertyMetadata(Brushes.Gray, (d, _) => ((HealthGauge)d).Redraw()));

        public HealthGauge()
        {
            InitializeComponent();
            Redraw();
        }

        public double Score { get => (double)GetValue(ScoreProperty); set => SetValue(ScoreProperty, value); }
        public Brush Accent { get => (Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }

        private void Redraw()
        {
            double score = Math.Clamp(Score, 0, 100);
            Label.Text = ((int)Math.Round(score)).ToString();
            Arc.Stroke = Accent;

            // 12시 기준 시계 방향 호. 360°는 ArcSegment 로 못 그리므로 359.9°로 상한.
            double sweep = Math.Min(359.9, score / 100.0 * 360.0);
            const double cx = 19, cy = 19, r = 12;
            double rad = (sweep - 90) * Math.PI / 180.0;
            var start = new Point(cx, cy - r);
            var end = new Point(cx + r * Math.Cos(rad), cy + r * Math.Sin(rad));

            var figure = new PathFigure
            {
                StartPoint = start,
                Segments =
                {
                    new ArcSegment(end, new Size(r, r), 0, sweep > 180,
                                   SweepDirection.Clockwise, isStroked: true),
                },
            };
            Arc.Data = new PathGeometry { Figures = { figure } };
        }
    }
}
