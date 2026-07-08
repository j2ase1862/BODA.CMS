using System.Text;

namespace BODA.CMS.Drivers.Jaka
{
    /// <summary>
    /// TCP 스트림에서 완전한 최상위 JSON 오브젝트를 잘라내는 프레이머.
    /// JAKA 모니터 스트림은 구분자 보장이 없고 TCP 특성상 패킷이 임의 지점에서 쪼개지거나
    /// 붙어 오므로, 문자열/이스케이프를 인식하는 중괄호 깊이 추적으로 경계를 찾는다.
    /// 첫 '{' 이전의 쓰레기 바이트(길이 프리픽스 등 펌웨어 변형)는 버린다.
    /// </summary>
    public sealed class JakaStreamFramer
    {
        private const int MaxBuffer = 1 << 20; // 1MB — 브레이스 불균형 스트림 폭주 방지

        private readonly StringBuilder _buffer = new();
        private int _depth;
        private int _startIndex = -1;
        private bool _inString;
        private bool _escaped;

        /// <summary>수신 청크를 누적하고, 완성된 JSON 오브젝트 문자열들을 반환한다.</summary>
        public IReadOnlyList<string> Append(string chunk)
        {
            var complete = new List<string>();

            foreach (char c in chunk)
            {
                if (_startIndex < 0)
                {
                    if (c != '{') continue; // 오브젝트 시작 전 쓰레기 무시
                    _startIndex = _buffer.Length;
                    _depth = 0;
                    _inString = false;
                    _escaped = false;
                }

                _buffer.Append(c);

                if (_inString)
                {
                    if (_escaped) _escaped = false;
                    else if (c == '\\') _escaped = true;
                    else if (c == '"') _inString = false;
                    continue;
                }

                switch (c)
                {
                    case '"': _inString = true; break;
                    case '{': _depth++; break;
                    case '}':
                        if (--_depth == 0)
                        {
                            complete.Add(_buffer.ToString(_startIndex, _buffer.Length - _startIndex));
                            _buffer.Clear();
                            _startIndex = -1;
                        }
                        break;
                }
            }

            if (_buffer.Length > MaxBuffer)
            {
                _buffer.Clear();
                _startIndex = -1;
            }

            return complete;
        }
    }
}
