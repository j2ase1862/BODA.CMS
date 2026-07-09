using BODA.CMS.Analytics;
using BODA.CMS.Analytics.Ml;
using BODA.CMS.Core.Telemetry;

namespace BODA.CMS.Collector.Dashboard
{
    /// <summary>웹 대시보드/저장용 알림 1건.</summary>
    public sealed record AlertRecord(
        DateTime AtUtc, string RobotId, string Vendor, string Channel,
        string Signal, int Axis, string Severity, string Kind, string Message)
    {
        public static AlertRecord From(string robotId, CbmAlert a) => new(
            a.AtUtc, robotId, a.VendorId, a.ChannelId, a.Signal, a.AxisIndex,
            a.Severity.ToString(), a.Kind, a.Message);
    }

    /// <summary>
    /// 웹 대시보드가 읽는 수집기 현재 상태 — 채널별 연결/수집률/CBM/ML 스냅샷 + 최근 알림 링.
    /// 쓰기는 수집 펌프(드라이버 스레드), 읽기는 HTTP 핸들러 — 내부 lock으로 직렬화.
    /// </summary>
    public sealed class DashboardState
    {
        private sealed class Entry
        {
            public required string RobotId;
            public required string Vendor;
            public required string Channel;
            public required string DisplayName;
            public required string Tier;
            public string State = "Disconnected";
            public DateTime? LastFrameUtc;
            public long FramesTotal;
            public readonly Queue<DateTime> Recent = new(); // 수집률 계산용 최근 5초 창
            public CbmMonitor? Cbm;
            public MlAnomalyMonitor? Ml;
        }

        private static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(5);
        private const int AlertRingCapacity = 200;

        private readonly object _gate = new();
        private readonly Dictionary<(string Robot, string Channel), Entry> _entries = new();
        private readonly LinkedList<AlertRecord> _alerts = new();

        /// <summary>로봇 재구성 시 채널 등록을 비운다 — 알림 링은 이력이므로 유지.</summary>
        public void ClearChannels()
        {
            lock (_gate) _entries.Clear();
        }

        public void Register(string robotId, IRobotTelemetrySource source, CbmMonitor cbm, MlAnomalyMonitor? ml)
        {
            RobotCapabilities c = source.Capabilities;
            lock (_gate)
            {
                _entries[(robotId, c.ChannelId)] = new Entry
                {
                    RobotId = robotId,
                    Vendor = c.VendorId,
                    Channel = c.ChannelId,
                    DisplayName = c.DisplayName,
                    Tier = ProductTierEvaluator.Evaluate(c).ToString(),
                    Cbm = cbm,
                    Ml = ml,
                };
            }
        }

        public void OnFrame(string robotId, string channel, DateTime utcNow)
        {
            lock (_gate)
            {
                if (!_entries.TryGetValue((robotId, channel), out Entry? e)) return;
                e.LastFrameUtc = utcNow;
                e.FramesTotal++;
                e.Recent.Enqueue(utcNow);
                while (e.Recent.Count > 0 && utcNow - e.Recent.Peek() > RateWindow) e.Recent.Dequeue();
            }
        }

        public void SetState(string robotId, string channel, TelemetrySourceState state)
        {
            lock (_gate)
            {
                if (_entries.TryGetValue((robotId, channel), out Entry? e)) e.State = state.ToString();
            }
        }

        public void AddAlert(AlertRecord alert)
        {
            lock (_gate)
            {
                _alerts.AddFirst(alert);
                while (_alerts.Count > AlertRingCapacity) _alerts.RemoveLast();
            }
        }

        public IReadOnlyList<AlertRecord> GetAlerts(int take)
        {
            lock (_gate) return _alerts.Take(take).ToArray();
        }

        /// <summary>/api/status 응답 본문 (익명 DTO — System.Text.Json camelCase 직렬화).</summary>
        public object GetStatus()
        {
            lock (_gate)
            {
                DateTime now = DateTime.UtcNow;
                return _entries.Values
                    .OrderBy(e => e.RobotId).ThenBy(e => e.Channel)
                    .Select(e =>
                    {
                        while (e.Recent.Count > 0 && now - e.Recent.Peek() > RateWindow) e.Recent.Dequeue();
                        CbmSnapshot? cbm = e.Cbm?.Snapshot;
                        MlSnapshot? ml = e.Ml?.Snapshot;
                        return new
                        {
                            robotId = e.RobotId,
                            vendor = e.Vendor,
                            channel = e.Channel,
                            displayName = e.DisplayName,
                            tier = e.Tier,
                            state = e.State,
                            rateHz = Math.Round(e.Recent.Count / RateWindow.TotalSeconds, 1),
                            framesTotal = e.FramesTotal,
                            lastFrameUtc = e.LastFrameUtc,
                            cbm = cbm is null ? null : new
                            {
                                phase = cbm.Phase.ToString(),
                                learningProgress = Math.Round(cbm.LearningProgress, 2),
                                healthScore = cbm.HealthScore,
                                activeAlerts = cbm.ActiveAlertCount,
                                worst = cbm.WorstDescription,
                            },
                            ml = ml is null ? null : new
                            {
                                scoredWindows = ml.ScoredWindows,
                                activeAlerts = ml.ActiveAlertCount,
                                worst = ml.WorstDescription,
                            },
                        };
                    })
                    .ToArray();
            }
        }
    }
}
