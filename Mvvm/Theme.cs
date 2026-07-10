using System.Windows.Media;

namespace BODA.CMS.Mvvm
{
    /// <summary>
    /// 다크 테마 상태 색 팔레트 — 상태 브러시는 코드(ViewModel)에서 지정되므로 여기 한 곳에 모은다.
    /// Themes/Theme.xaml·웹 대시보드(wwwroot)와 동일 값. 브러시는 Freeze — 드라이버 스레드에서
    /// 만들어 UI 로 넘겨도 안전하다.
    /// </summary>
    public static class Theme
    {
        /// <summary>정상/수신 중 (초록)</summary>
        public static readonly Brush Ok = Freeze("#4FC08D");
        /// <summary>주의/연결 중 (주황)</summary>
        public static readonly Brush Warn = Freeze("#E0A836");
        /// <summary>이상/실패 (빨강)</summary>
        public static readonly Brush Bad = Freeze("#E06C6C");
        /// <summary>대기/보조 (회색)</summary>
        public static readonly Brush Muted = Freeze("#8A93A0");

        private static SolidColorBrush Freeze(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }
    }
}
