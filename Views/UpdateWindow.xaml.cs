using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using BODA.CMS.Services;

namespace BODA.CMS.Views
{
    /// <summary>
    /// 인앱 업데이트 창 (2단계). 흐름: [지금 설치] → MSI 다운로드(진행률) → SHA-256 검증 → UAC 부트스트랩 기동
    /// → 앱 종료(부트스트랩이 설치 후 재실행). 실패·거부는 상태문구로 알리고 버튼을 되살린다.
    /// </summary>
    public partial class UpdateWindow : Window
    {
        private readonly UpdateInfo _info;
        private readonly UpdateInstallService _installer;
        private CancellationTokenSource? _cts;

        public UpdateWindow(UpdateInfo info, string currentVersionText, UpdateInstallService installer)
        {
            InitializeComponent();
            _info = info;
            _installer = installer;

            Headline.Text = $"새 버전 {info.LatestTagName} 이 출시되었습니다.";
            VersionLine.Text = $"현재 버전 {currentVersionText}  →  최신 버전 {info.LatestTagName}"
                + (info.PublishedAt > DateTime.MinValue ? $"   ·   게시 {info.PublishedAt.ToLocalTime():yyyy-MM-dd}" : string.Empty);
            // Markdown 제목 기호만 걷어낸 plain text
            Notes.Text = Regex.Replace(info.ReleaseNotes.Trim(), @"^#+\s*", "", RegexOptions.Multiline);

            if (string.IsNullOrEmpty(info.DownloadUrl))
            {
                InstallButton.IsEnabled = false;
                InstallButton.ToolTip = "이 릴리스에는 모니터 앱 MSI가 없습니다 — 다운로드 페이지에서 받으세요.";
            }
            if (UpdateInstallService.IsDryRun) Status.Text = "검증 모드(DRYRUN): 설치는 건너뛰고 다운로드·검증·재실행만 수행합니다.";
            Closing += (_, _) => _cts?.Cancel();
        }

        private async void OnInstallClick(object sender, RoutedEventArgs e)
        {
            SetBusy(true);
            Progress.Visibility = Visibility.Visible;
            Status.Text = $"다운로드 중… {_info.DownloadFileName}";
            _cts = new CancellationTokenSource();
            var progress = new Progress<UpdateDownloadProgress>(p =>
            {
                if (p.TotalBytes > 0)
                {
                    Progress.IsIndeterminate = false;
                    Progress.Value = 100.0 * p.BytesReceived / p.TotalBytes;
                    Status.Text = $"다운로드 중… {p.BytesReceived / 1048576.0:0.0} / {p.TotalBytes / 1048576.0:0.0} MB";
                }
                else
                {
                    Progress.IsIndeterminate = true;
                    Status.Text = $"다운로드 중… {p.BytesReceived / 1048576.0:0.0} MB";
                }
            });

            UpdateDownloadResult download;
            try { download = await _installer.DownloadAsync(_info, progress, _cts.Token); }
            catch (OperationCanceledException) { Status.Text = "다운로드를 취소했습니다."; SetBusy(false); return; }

            if (!download.Success)
            {
                Status.Text = download.Error;
                Progress.Visibility = Visibility.Collapsed;
                SetBusy(false);
                return;
            }

            Progress.IsIndeterminate = false;
            Progress.Value = 100;
            Status.Text = "무결성 확인 완료. 관리자 권한 승인 후 설치가 시작됩니다…";
            UpdateInstallLaunchResult launch = _installer.LaunchInstaller(download.FilePath);
            if (!launch.Success)
            {
                Status.Text = launch.Error + (launch.UserDeclined ? " 다시 시도하려면 [지금 설치]를 누르세요." : $" (로그: {launch.LogPath})");
                SetBusy(false);
                return;
            }

            // 부트스트랩이 이 프로세스의 종료를 기다린다 — 설치 후 자동 재실행.
            Status.Text = "설치 프로그램을 시작했습니다. 앱을 종료합니다 — 설치가 끝나면 자동으로 다시 실행됩니다.";
            await System.Threading.Tasks.Task.Delay(800);
            Application.Current.Shutdown();
        }

        private void SetBusy(bool busy)
        {
            InstallButton.IsEnabled = !busy && !string.IsNullOrEmpty(_info.DownloadUrl);
            PageButton.IsEnabled = !busy;
            LaterButton.Content = busy ? "취소" : "나중에";
        }

        private void OnLaterClick(object sender, RoutedEventArgs e)
        {
            if (_cts is { IsCancellationRequested: false } && LaterButton.Content as string == "취소") { _cts.Cancel(); return; }
            Close();
        }

        private void OnPageClick(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(_info.ReleaseUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Status.Text = "브라우저 열기 실패: " + ex.GetBaseException().Message;
            }
        }
    }
}
