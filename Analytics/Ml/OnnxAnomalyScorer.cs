using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace BODA.CMS.Analytics.Ml
{
    /// <summary>
    /// ONNX Runtime 추론 — Python(sklearn IsolationForest → skl2onnx)에서 export한 모델을
    /// .NET 안에서 실행한다 (ROADMAP §2: 학습만 Python, 추론은 C# 영역).
    /// </summary>
    public sealed class OnnxAnomalyScorer : IAnomalyScorer, IDisposable
    {
        private readonly InferenceSession _session;
        private readonly string _inputName;
        private readonly string _scoreOutput;
        private readonly object _gate = new(); // InferenceSession.Run은 스레드 안전하지만 보수적으로 직렬화
        private bool _disposed;

        public OnnxAnomalyScorer(string onnxPath)
        {
            // 저사양(2~4코어) 보호: 기본 세션은 코어 수만큼 스레드를 만들고 스핀 대기해
            // 유휴 중에도 CPU를 점유한다. 이 모델(소형 IsolationForest, 입력 1×6)은
            // 단일 스레드로도 밀리초 미만이라 1개로 고정한다.
            using var opts = new SessionOptions { IntraOpNumThreads = 1, InterOpNumThreads = 1 };
            _session = new InferenceSession(onnxPath, opts);
            _inputName = _session.InputMetadata.Keys.First();
            // skl2onnx IsolationForest 출력: label(int64), scores(float) — float 쪽이 decision_function.
            _scoreOutput = _session.OutputMetadata
                .First(kv => kv.Value.ElementType == typeof(float)).Key;
        }

        public double Score(float[] features)
        {
            var tensor = new DenseTensor<float>(features, new[] { 1, features.Length });
            var inputs = new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) };

            lock (_gate)
            {
                // 모델 교체(재학습) 중 드라이버 스레드의 마지막 호출과 Dispose 가 겹칠 수 있다
                // — 세션이 닫혔으면 "이상 아님" 점수로 조용히 빠진다.
                if (_disposed) return double.MaxValue;
                using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results =
                    _session.Run(inputs, new[] { _scoreOutput });
                return results[0].AsEnumerable<float>().First();
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _session.Dispose();
            }
        }
    }
}
