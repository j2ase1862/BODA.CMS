using System.Buffers.Binary;
using System.Text;

namespace BODA.CMS.Drivers.UR
{
    /// <summary>
    /// UR RTDE(Real-Time Data Exchange) 와이어 프로토콜 — 패키지 조립·해석.
    /// 형식: [uint16 size(BE)][uint8 type][payload]. 수치는 전부 빅엔디언, 문자열은 UTF-8.
    /// 프로토콜 v2 기준(레시피 id·출력 주기 지정) — URControl 3.10+/5.4+ (RTDE Guide 공개 문서).
    /// </summary>
    internal static class RtdeProtocol
    {
        public const byte RequestProtocolVersion = 86; // 'V'
        public const byte GetUrControlVersion = 118;   // 'v'
        public const byte TextMessage = 77;            // 'M'
        public const byte DataPackage = 85;            // 'U'
        public const byte SetupOutputs = 79;           // 'O'
        public const byte Start = 83;                  // 'S'
        public const byte Pause = 80;                  // 'P'

        public const ushort ProtocolVersion = 2;

        public static byte[] BuildRequestProtocolVersion()
        {
            byte[] p = NewPackage(RequestProtocolVersion, payloadLength: 2, out int at);
            BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(at), ProtocolVersion);
            return p;
        }

        /// <summary>출력 구독 요청 — 주기(Hz) + 쉼표로 이은 변수명 목록.</summary>
        public static byte[] BuildSetupOutputs(double frequencyHz, IReadOnlyList<string> variables)
        {
            byte[] names = Encoding.UTF8.GetBytes(string.Join(',', variables));
            byte[] p = NewPackage(SetupOutputs, payloadLength: 8 + names.Length, out int at);
            BinaryPrimitives.WriteDoubleBigEndian(p.AsSpan(at), frequencyHz);
            names.CopyTo(p, at + 8);
            return p;
        }

        public static byte[] BuildStart() => NewPackage(Start, payloadLength: 0, out _);
        public static byte[] BuildPause() => NewPackage(Pause, payloadLength: 0, out _);

        /// <summary>V(버전 협상)·S(시작)·P(정지) 응답의 수락 여부.</summary>
        public static bool ParseAccepted(ReadOnlySpan<byte> payload) => payload.Length >= 1 && payload[0] == 1;

        /// <summary>
        /// SETUP_OUTPUTS 응답 — 레시피 id + 변수별 타입 목록(요청 순서 그대로).
        /// 컨트롤러가 모르는 변수는 타입이 "NOT_FOUND"로 온다.
        /// </summary>
        public static (byte RecipeId, string[] Types) ParseSetupOutputsReply(ReadOnlySpan<byte> payload)
        {
            if (payload.Length < 1) throw new InvalidDataException("SETUP_OUTPUTS 응답이 비어 있습니다.");
            string types = Encoding.UTF8.GetString(payload[1..]);
            return (payload[0], types.Length == 0 ? Array.Empty<string>() : types.Split(','));
        }

        /// <summary>
        /// DATA_PACKAGE — 레시피 id + 구독 변수 값 나열. 본 드라이버 레시피는 전 변수가
        /// double 계열(DOUBLE/VECTOR6D)이라 8바이트 빅엔디언 double 연속으로 해석한다.
        /// </summary>
        public static (byte RecipeId, double[] Values) ParseDataPackage(ReadOnlySpan<byte> payload)
        {
            if (payload.Length < 1 || (payload.Length - 1) % 8 != 0)
                throw new InvalidDataException($"DATA_PACKAGE 길이 오류({payload.Length}바이트).");

            var values = new double[(payload.Length - 1) / 8];
            for (int i = 0; i < values.Length; i++)
                values[i] = BinaryPrimitives.ReadDoubleBigEndian(payload.Slice(1 + i * 8, 8));
            return (payload[0], values);
        }

        private static byte[] NewPackage(byte type, int payloadLength, out int payloadAt)
        {
            var p = new byte[3 + payloadLength];
            BinaryPrimitives.WriteUInt16BigEndian(p, (ushort)p.Length);
            p[2] = type;
            payloadAt = 3;
            return p;
        }
    }

    /// <summary>TCP 수신 → RTDE 패키지 경계 복원 (분할·병합 수신 대응, JakaStreamFramer 패턴).</summary>
    internal sealed class RtdeFramer
    {
        private byte[] _buf = new byte[8192];
        private int _len;

        public IReadOnlyList<(byte Type, byte[] Payload)> Append(ReadOnlySpan<byte> data)
        {
            if (_len + data.Length > _buf.Length)
                Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _len + data.Length));
            data.CopyTo(_buf.AsSpan(_len));
            _len += data.Length;

            var found = new List<(byte, byte[])>();
            int at = 0;
            while (_len - at >= 3)
            {
                int size = BinaryPrimitives.ReadUInt16BigEndian(_buf.AsSpan(at));
                if (size < 3)
                    throw new InvalidDataException($"RTDE 패키지 크기 오류({size}) — 스트림 비동기화.");
                if (_len - at < size) break;

                found.Add((_buf[at + 2], _buf.AsSpan(at + 3, size - 3).ToArray()));
                at += size;
            }

            if (at > 0)
            {
                Buffer.BlockCopy(_buf, at, _buf, 0, _len - at);
                _len -= at;
            }
            return found;
        }
    }
}
