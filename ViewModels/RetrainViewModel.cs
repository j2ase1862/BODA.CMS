using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using BODA.CMS.Mvvm;
using BODA.CMS.Services;

namespace BODA.CMS.ViewModels
{
    /// <summary>
    /// 재학습 창 상태 — DB 인벤토리 조회 → 구간·필터 선택 → 학습 실행 → 모델 교체 → 앱 핫리로드.
    /// 실제 파이프라인은 <see cref="ModelRetrainService"/>, 이 클래스는 입력 검증과 진행 표시만.
    /// </summary>
    public sealed class RetrainViewModel : ViewModelBase
    {
        private readonly ModelRetrainService _service = new();
        private readonly Action _reloadModels; // MainViewModel.ReloadMlModels — 교체 후 카드들 핫리로드
        private readonly StringBuilder _log = new();
        private CancellationTokenSource? _cts;

        private string _connectionString = "Host=localhost;Port=5432;Database=boda_cms;Username=postgres;Password=postgres";
        private DataInventoryRow? _selectedRow;
        private string _sinceText = "";
        private string _untilText = "";
        private string _robotId = "";
        private string _channel = "";
        private string _minWindowsText = "5000";
        private string _statusMessage = "① 데이터 조회 → ② 구간 확인 → ③ 재학습 시작";
        private Brush _statusBrush = Theme.Muted;
        private string _logText = "";
        private bool _isBusy;

        public RetrainViewModel(Action reloadModels)
        {
            _reloadModels = reloadModels;
            QueryCommand = new AsyncRelayCommand(QueryInventoryAsync, () => !IsBusy);
            StartCommand = new AsyncRelayCommand(StartAsync, () => !IsBusy);
            CancelCommand = new AsyncRelayCommand(() => { _cts?.Cancel(); return Task.CompletedTask; }, () => IsBusy);
        }

        /// <summary>수집 DB 접속 문자열 — Collector appsettings 와 동일 형식(Npgsql).</summary>
        public string ConnectionString { get => _connectionString; set => SetProperty(ref _connectionString, value); }

        /// <summary>로봇×채널별 수집 범위 — '데이터 조회'로 채워진다.</summary>
        public ObservableCollection<DataInventoryRow> Inventory { get; } = new();

        /// <summary>선택 시 구간·필터 입력을 해당 행의 범위로 자동 채움 (이후 수동 편집 가능).</summary>
        public DataInventoryRow? SelectedRow
        {
            get => _selectedRow;
            set
            {
                if (!SetProperty(ref _selectedRow, value) || value is null) return;
                RobotId = value.RobotId;
                Channel = value.Channel;
                SinceText = value.MinTime.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                UntilText = value.MaxTime.LocalDateTime.AddMinutes(1).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            }
        }

        public string SinceText { get => _sinceText; set => SetProperty(ref _sinceText, value); }
        public string UntilText { get => _untilText; set => SetProperty(ref _untilText, value); }
        public string RobotId { get => _robotId; set => SetProperty(ref _robotId, value); }
        public string Channel { get => _channel; set => SetProperty(ref _channel, value); }
        public string MinWindowsText { get => _minWindowsText; set => SetProperty(ref _minWindowsText, value); }

        public string StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }
        public Brush StatusBrush { get => _statusBrush; set => SetProperty(ref _statusBrush, value); }
        public string LogText { get => _logText; set => SetProperty(ref _logText, value); }

        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                if (!SetProperty(ref _isBusy, value)) return;
                QueryCommand.RaiseCanExecuteChanged();
                StartCommand.RaiseCanExecuteChanged();
                CancelCommand.RaiseCanExecuteChanged();
            }
        }

        public AsyncRelayCommand QueryCommand { get; }
        public AsyncRelayCommand StartCommand { get; }
        public AsyncRelayCommand CancelCommand { get; }

        /// <summary>창이 닫힐 때 진행 중인 학습 취소 (RetrainWindow.OnClosed).</summary>
        public void CancelIfRunning() => _cts?.Cancel();

        private async Task QueryInventoryAsync()
        {
            IsBusy = true;
            try
            {
                SetStatus("데이터 조회 중…", Theme.Warn);
                var rows = await _service.QueryInventoryAsync(ConnectionString, CancellationToken.None);

                Inventory.Clear();
                foreach (DataInventoryRow row in rows) Inventory.Add(row);

                if (rows.Count == 0)
                {
                    SetStatus("수집된 데이터가 없습니다 — Collector 서비스·로봇 가동을 확인하세요", Theme.Bad);
                    return;
                }
                SelectedRow = Inventory[0];
                SetStatus($"로봇×채널 {rows.Count}개 조회됨 — 구간을 확인한 뒤 재학습을 시작하세요", Theme.Ok);
                AppendLog($"데이터 조회 완료 — {rows.Count}개 항목.");
            }
            catch (Exception ex)
            {
                SetStatus("DB 접속 실패 — 접속 문자열·PostgreSQL 서비스 확인", Theme.Bad);
                AppendLog("DB 접속 실패: " + ex.GetBaseException().Message);
            }
            finally { IsBusy = false; }
        }

        private async Task StartAsync()
        {
            if (!TryBuildRequest(out RetrainRequest? req, out string? error))
            {
                SetStatus(error!, Theme.Bad);
                return;
            }

            IsBusy = true;
            _cts = new CancellationTokenSource();
            try
            {
                AppendLog("── 재학습 시작 ──");
                SetStatus("재학습 중… (데이터 양에 따라 수 분 걸립니다)", Theme.Warn);
                bool trained = await _service.TrainAsync(req!, AppendLogSafe, _cts.Token);
                if (!trained)
                {
                    SetStatus("재학습 실패 — 아래 로그를 확인하세요", Theme.Bad);
                    return;
                }

                SetStatus("모델 교체 중…", Theme.Warn);
                bool deployed = await _service.DeployAsync(AppendLogSafe);
                if (!deployed)
                {
                    SetStatus("모델 교체 실패 — 아래 로그를 확인하세요", Theme.Bad);
                    return;
                }

                _reloadModels();
                AppendLog("실행 중인 채널 카드에 새 모델을 다시 로드했습니다 (앱 재시작 불필요).");
                SetStatus("완료 — 새 모델이 적용되었습니다. 하루 정도 ML 알람 빈도를 지켜보세요.", Theme.Ok);
            }
            catch (OperationCanceledException)
            {
                AppendLog("사용자가 재학습을 취소했습니다.");
                SetStatus("취소됨", Theme.Muted);
            }
            catch (Exception ex)
            {
                AppendLog("오류: " + ex.GetBaseException().Message);
                SetStatus("실패 — 아래 로그를 확인하세요", Theme.Bad);
            }
            finally
            {
                _cts.Dispose();
                _cts = null;
                IsBusy = false;
            }
        }

        private bool TryBuildRequest(out RetrainRequest? req, out string? error)
        {
            req = null;
            if (!DateTime.TryParse(SinceText, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime since))
            {
                error = "시작 시각 형식이 올바르지 않습니다 (예: 2026-07-10 09:00)";
                return false;
            }

            DateTimeOffset? until = null;
            if (!string.IsNullOrWhiteSpace(UntilText))
            {
                if (!DateTime.TryParse(UntilText, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime u))
                {
                    error = "종료 시각 형식이 올바르지 않습니다 (예: 2026-07-10 18:00, 비우면 현재까지)";
                    return false;
                }
                until = new DateTimeOffset(u); // Unspecified → 로컬 시각으로 해석
                if (until <= new DateTimeOffset(since))
                {
                    error = "종료 시각이 시작 시각보다 빠릅니다";
                    return false;
                }
            }

            if (!int.TryParse(MinWindowsText, out int minWindows) || minWindows < 1)
            {
                error = "최소 윈도 수는 1 이상의 정수여야 합니다 (기본 5000)";
                return false;
            }

            req = new RetrainRequest(ConnectionString, new DateTimeOffset(since), until,
                string.IsNullOrWhiteSpace(RobotId) ? null : RobotId,
                string.IsNullOrWhiteSpace(Channel) ? null : Channel,
                minWindows);
            error = null;
            return true;
        }

        private void SetStatus(string message, Brush brush)
        {
            StatusMessage = message;
            StatusBrush = brush;
        }

        private void AppendLog(string line)
        {
            _log.AppendLine($"[{DateTime.Now:HH:mm:ss}] {line}");
            LogText = _log.ToString();
        }

        // 프로세스 출력 스레드에서도 올라온다 → UI 스레드로 마샬링.
        private void AppendLogSafe(string line)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess()) AppendLog(line);
            else dispatcher.BeginInvoke(() => AppendLog(line));
        }
    }
}
