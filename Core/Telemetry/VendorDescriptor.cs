using System;

namespace BODA.CMS.Core.Telemetry
{
    /// <summary>
    /// 벤더 카탈로그 항목 — 컴포지션 루트가 등록하고, UI는 이 목록으로 제조사 선택을 렌더한다.
    /// 새 제조사 지원 = 드라이버 모듈 구현 후 카탈로그에 항목 추가뿐 (ROADMAP §3 벤더 격리).
    /// </summary>
    /// <param name="VendorId">벤더 식별자("doosan", "jaka", ...).</param>
    /// <param name="DisplayName">UI 표시명 (예: "두산로보틱스").</param>
    /// <param name="CreateSources">이 벤더의 채널 드라이버 세트를 생성하는 팩터리. 제조사 전환 시마다 호출된다.</param>
    public sealed record VendorDescriptor(
        string VendorId,
        string DisplayName,
        Func<IRobotTelemetrySource[]> CreateSources)
    {
        // 콤보박스 항목·UIA 접근성 이름이 표시명과 일치하도록 (record 기본 ToString은 전체 필드 나열).
        public override string ToString() => DisplayName;
    }
}
