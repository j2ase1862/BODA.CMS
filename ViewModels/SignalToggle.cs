using BODA.CMS.Mvvm;

namespace BODA.CMS.ViewModels
{
    /// <summary>채널 카드의 신호 표시 선택 1개 (예: "위치°", "전류A", "cur_raw").</summary>
    public sealed class SignalToggle : ViewModelBase
    {
        private bool _isSelected = true;

        public SignalToggle(string label) => Label = label;

        /// <summary>판독 표의 행 라벨과 동일한 문자열 — 필터 키로도 쓴다.</summary>
        public string Label { get; }

        public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }

        // 콤보박스 항목·UIA 접근성 이름이 라벨과 일치하도록.
        public override string ToString() => Label;
    }
}
