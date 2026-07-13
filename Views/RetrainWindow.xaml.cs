using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using BODA.CMS.ViewModels;

namespace BODA.CMS.Views
{
    /// <summary>
    /// AI 재학습 창 — 상태·로직은 전부 <see cref="RetrainViewModel"/>, 여기는 다크 타이틀바와
    /// 로그 자동 스크롤 등 뷰 잡무만.
    /// </summary>
    public partial class RetrainWindow : Window
    {
        private readonly RetrainViewModel _vm;

        public RetrainWindow(RetrainViewModel viewModel)
        {
            InitializeComponent();
            DataContext = _vm = viewModel;
        }

        // 다크 테마에 맞춰 OS 타이틀바도 어둡게 (MainWindow 와 동일 — 미지원 OS 는 무시).
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var handle = new WindowInteropHelper(this).Handle;
            int on = 1;
            _ = DwmSetWindowAttribute(handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, sizeof(int));
        }

        // 창을 닫아도 백그라운드 파이썬이 계속 돌지 않도록 취소.
        protected override void OnClosed(EventArgs e)
        {
            _vm.CancelIfRunning();
            base.OnClosed(e);
        }

        private void OnLogChanged(object sender, TextChangedEventArgs e) =>
            ((TextBox)sender).ScrollToEnd();

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    }
}
