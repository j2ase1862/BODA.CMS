using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;

namespace BODA.CMS.Services
{
    /// <summary>
    /// 감시 서버(Collector) 로봇 구성 동기화 — 앱에서 고른 제조사/IP가 웹 대시보드에도 반영되게
    /// PUT /api/robots 를 호출한다. 감시 서버가 없거나 꺼져 있으면 조용히 생략(최선 노력).
    ///
    /// 대상 주소: 기본 http://localhost:5100 (감시 서버와 같은 PC).
    /// 감시 서버가 다른 PC면 환경 변수 BODA_COLLECTOR_URL 로 지정 (예: http://192.168.1.50:5100).
    /// </summary>
    public sealed class CollectorSync
    {
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
        private readonly string _baseUrl =
            Environment.GetEnvironmentVariable("BODA_COLLECTOR_URL")?.TrimEnd('/') ?? "http://localhost:5100";

        /// <summary>감시 서버(웹 대시보드) 기본 주소 — 앱에서 브라우저로 열 때도 같은 규칙을 쓴다.</summary>
        public string BaseUrl => _baseUrl;

        private sealed record RobotDto(string RobotId, string Vendor, string Host);

        /// <summary>동기화 시도 — 결과를 사람이 읽을 로그 문장으로 돌려준다 (실패해도 예외 없음).</summary>
        public async Task<string> TrySyncAsync(string vendorId, string host)
        {
            try
            {
                // 여러 대를 감시 중인 서버는 건드리지 않는다 — 이 앱은 로봇 1대 진단 화면이라
                // 전체 목록을 덮으면 현장 구성이 날아간다.
                List<RobotDto> current;
                using (HttpResponseMessage get = await _http.GetAsync($"{_baseUrl}/api/robots"))
                {
                    if (!get.IsSuccessStatusCode)
                        return $"감시 서버 동기화 생략 — 응답 {(int)get.StatusCode} ({_baseUrl})";
                    current = await get.Content.ReadFromJsonAsync<List<RobotDto>>() ?? new List<RobotDto>();
                }
                if (current.Count > 1)
                    return "감시 서버가 여러 로봇을 감시 중 — 자동 반영을 생략합니다 (appsettings.json 으로 관리하세요).";

                // 같은 벤더면 현장에서 지어준 이름(RobotId)을 보존, 벤더가 바뀌면 새 기본 이름.
                string robotId = current.Count == 1 && string.Equals(current[0].Vendor, vendorId, StringComparison.OrdinalIgnoreCase)
                    ? current[0].RobotId
                    : $"{vendorId}-01";

                var payload = new[] { new RobotDto(robotId, vendorId, host) };
                using HttpResponseMessage put = await _http.PutAsJsonAsync($"{_baseUrl}/api/robots", payload);
                return put.IsSuccessStatusCode
                    ? $"감시 서버(대시보드)에 반영됨: {vendorId} @ {host}"
                    : $"감시 서버 반영 실패 — 응답 {(int)put.StatusCode}";
            }
            catch (Exception ex)
            {
                return $"감시 서버 미접속 — 동기화 생략 (이 PC에 감시 서버가 없으면 정상): {ex.GetBaseException().Message}";
            }
        }
    }
}
