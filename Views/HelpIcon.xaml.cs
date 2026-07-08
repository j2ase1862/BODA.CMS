using System.Windows;
using System.Windows.Controls;

namespace BODA.CMS.Views
{
    /// <summary>
    /// 컨트롤 옆에 붙이는 도움말 아이콘 — 클릭하면 설명 팝업, 바깥 클릭 시 닫힘.
    /// 사용: &lt;views:HelpIcon HelpTitle="제목" HelpText="설명…" /&gt;
    /// </summary>
    public partial class HelpIcon : UserControl
    {
        public static readonly DependencyProperty HelpTitleProperty =
            DependencyProperty.Register(nameof(HelpTitle), typeof(string), typeof(HelpIcon),
                new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty HelpTextProperty =
            DependencyProperty.Register(nameof(HelpText), typeof(string), typeof(HelpIcon),
                new PropertyMetadata(string.Empty));

        public HelpIcon() => InitializeComponent();

        public string HelpTitle
        {
            get => (string)GetValue(HelpTitleProperty);
            set => SetValue(HelpTitleProperty, value);
        }

        public string HelpText
        {
            get => (string)GetValue(HelpTextProperty);
            set => SetValue(HelpTextProperty, value);
        }
    }
}
