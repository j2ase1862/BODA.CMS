namespace BODA.CMS.Core.Telemetry
{
    /// <summary>텔레메트리 소스의 연결 상태.</summary>
    public enum TelemetrySourceState
    {
        /// <summary>미연결(초기 상태 또는 정상 해제).</summary>
        Disconnected,
        /// <summary>연결 시도 중.</summary>
        Connecting,
        /// <summary>연결됨 — 프레임 수신 중.</summary>
        Connected,
        /// <summary>비정상 종료(연결 끊김·반복 오류). 재연결 필요.</summary>
        Faulted,
    }
}
