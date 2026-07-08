namespace BODA.CMS.Drivers.Rokae
{
    /// <summary>
    /// xCore 상태 조회 1회분 — <b>클라이언트 구현이 단위 정규화를 책임진다</b>
    /// (위치 °, 속도 °/s, 토크 Nm, 전류 A, 온도 ℃ — §2 규약. SDK가 라디안을 주면 여기서 환산).
    /// null 배열 = 해당 신호 미제공(0 채움 금지).
    /// </summary>
    public sealed record RokaeRobotState(
        float[] JointPositionDeg,
        float[]? JointVelocityDegS = null,
        float[]? JointTorqueNm = null,     // xMate 전 관절 토크센서 실측
        float[]? MotorCurrentA = null,
        float[]? TemperatureC = null);

    /// <summary>
    /// xCore 컨트롤러 상태 조회 클라이언트 추상화.
    ///
    /// 배경: xCore SDK의 와이어 프로토콜은 비공개(프리빌트 라이브러리)라 순수 C# 재구현이 불가.
    /// 공식 xCoreSDK-CSharp(Apache-2.0, C++/CLI 래퍼 xCoreSDK_cli.dll — x64 Windows, .NET≥5,
    /// NuGet 없음·GitHub Releases zip)를 확보하면 이 인터페이스의 SDK 구현체
    /// (XCoreSdkStateClient)를 추가한다 — libs/rokae/ 번들 방식(두산 DRFL 전례).
    /// ⚠️ 비개입 규약: 구현은 상태 조회 API만 사용한다 — 모션 명령·제어권 취득·RCI 금지.
    /// </summary>
    public interface IRokaeStateClient : IAsyncDisposable
    {
        Task ConnectAsync(string host, CancellationToken ct);

        /// <summary>상태 1회 조회(읽기 전용). 실패 시 예외 — 소스가 연속 실패를 링크 사망으로 판정.</summary>
        Task<RokaeRobotState> QueryStateAsync(CancellationToken ct);

        Task DisconnectAsync();
    }
}
