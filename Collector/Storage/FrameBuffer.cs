using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace BODA.CMS.Collector.Storage
{
    /// <summary>
    /// 수집 → 저장 사이의 bounded 버퍼. 저장이 밀리면(DB 장애 등) 오래된 프레임부터 드롭 —
    /// 수집 펌프는 절대 저장을 기다리며 블록되지 않는다(비개입 수집이 우선).
    /// </summary>
    public sealed class FrameBuffer
    {
        private readonly Channel<TelemetryRecord> _channel;
        private long _dropped;

        public FrameBuffer(IOptions<CollectorOptions> options)
        {
            _channel = Channel.CreateBounded<TelemetryRecord>(
                new BoundedChannelOptions(options.Value.Storage.BufferCapacity)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                },
                _ => Interlocked.Increment(ref _dropped));
        }

        public long DroppedCount => Interlocked.Read(ref _dropped);

        /// <summary>드라이버 스레드에서 호출 — 버퍼가 차 있으면 가장 오래된 항목이 드롭된다(논블로킹).</summary>
        public void Post(TelemetryRecord record) => _channel.Writer.TryWrite(record);

        public ChannelReader<TelemetryRecord> Reader => _channel.Reader;
    }
}
