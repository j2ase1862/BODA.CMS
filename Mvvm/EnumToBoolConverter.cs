using System;
using System.Globalization;
using System.Windows.Data;

namespace BODA.CMS.Mvvm
{
    /// <summary>
    /// enum ↔ bool 컨버터 — 라디오(세그먼트) 버튼을 enum 속성에 바인딩할 때 사용.
    /// ConverterParameter 에 enum 멤버 이름을 문자열로 준다.
    /// </summary>
    public sealed class EnumToBoolConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value?.ToString() == parameter as string;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is true && parameter is string name
                ? Enum.Parse(targetType, name)
                : Binding.DoNothing;
    }
}
