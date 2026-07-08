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

        public OnnxAnomalyScorer(string onnxPath)
        {
            _session = new InferenceSession(onnxPath);
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
                using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results =
                    _session.Run(inputs, new[] { _scoreOutput });
                return results[0].AsEnumerable<float>().First();
            }
        }

        public void Dispose() => _session.Dispose();
    }
}
