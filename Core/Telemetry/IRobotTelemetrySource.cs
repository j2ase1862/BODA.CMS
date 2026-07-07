using System;
using System.Threading;
using System.Threading.Tasks;

namespace BODA.CMS.Core.Telemetry
{
    /// <summary>
    /// 모든 벤더 드라이버가 구현하는 단일 계약 (ROADMAP §2).
    /// 파이프라인 위쪽(UI·저장·분석)은 이 인터페이스와 <see cref="RobotTelemetryFrame"/>에만 의존한다 —
    /// 벤더 SDK 타입·레지스터 주소·스케일 상수가 드라이버 밖으로 새면 설계 실패(§3 벤더 격리 원칙).
    /// </summary>
    public interface IRobotTelemetrySource : IAsyncDisposable
    {
        /// <summary>이 드라이버·이 채널이 뭘 줄 수 있는가 — 등급 자동 판정의 근거.</summary>
        RobotCapabilities Capabilities { get; }

        /// <summary>현재 연결 상태.</summary>
        TelemetrySourceState State { get; }

        /// <summary>
        /// 정규화 프레임 수신. ⚠️ 드라이버 내부 스레드(네이티브 콜백/폴링)에서 발화 —
        /// UI 마샬링은 구독자 책임.
        /// </summary>
        event EventHandler<RobotTelemetryFrame>? FrameReceived;

        /// <summary>연결 상태 변화. 드라이버 내부 스레드에서 발화 가능.</summary>
        event EventHandler<TelemetrySourceState>? StateChanged;

        /// <summary>로그성 통지(컨트롤러 버전, 로봇 상태 변화, 일시적 읽기 오류 등). 사람이 읽는 문자열.</summary>
        event EventHandler<string>? Notification;

        /// <summary>접속 + 수신 시작. 실패 시 예외를 던지고 상태는 Disconnected/Faulted로 남는다.</summary>
        Task ConnectAsync(RobotEndpoint endpoint, CancellationToken ct = default);

        /// <summary>수신 중지 + 접속 해제(정상 종료). 미연결이면 no-op.</summary>
        Task DisconnectAsync();
    }
}
