using System.Windows.Media;

namespace BODA.CMS.ViewModels
{
    /// <summary>알림 리스트의 한 줄 (불변 — 생성 후 갱신 없음).</summary>
    public sealed class AlertItem
    {
        public AlertItem(string text, Brush brush)
        {
            Text = text;
            Brush = brush;
        }

        public string Text { get; }
        public Brush Brush { get; }
    }
}
