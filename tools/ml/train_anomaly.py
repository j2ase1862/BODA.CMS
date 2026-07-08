"""BODA.CMS P3 — 이상탐지 부트스트랩 모델 학습 (ROADMAP §4 P3).

정상 거동의 z-정규화 집계 시퀀스를 합성해 6차원 윈도 피처(mean/std/rms/min/max/slope)를 만들고,
IsolationForest(비지도)를 학습한 뒤 ONNX + 임계값 사이드카(JSON)를 models/ 에 export 한다.

- 입력 공간이 CBM 기준선 z-점수라 신호·벤더·샘플링 주기와 무관 — 단일 모델로 전 채널 커버.
- ⚠️ 피처 정의는 C# Analytics/Ml/AnomalyFeatures.cs 와 정확히 일치해야 한다.
- 이 모델은 실 데이터 확보 전의 **부트스트랩**이다. 현장 정상 데이터가 TimescaleDB에 쌓이면
  같은 피처로 재학습해 models/ 만 교체한다 (C# 코드 수정 불필요).

사용: python tools/ml/train_anomaly.py   (저장소 루트에서)
"""
from __future__ import annotations

import json
import datetime as dt
from pathlib import Path

import numpy as np
from sklearn.ensemble import IsolationForest
from skl2onnx import convert_sklearn
from skl2onnx.common.data_types import FloatTensorType

WINDOW = 10          # 집계(초) 단위 슬라이딩 윈도 — C# MlModelInfo.Window 와 동일
N_SEQ = 4000         # 합성 정상 시퀀스 수
SEQ_LEN = 64         # 시퀀스 길이(집계 수)
SEED = 20260707

FEATURE_NAMES = ["mean", "std", "rms", "min", "max", "slope"]


def synth_normal_sequences(rng: np.random.Generator) -> np.ndarray:
    """정상 거동 z-시퀀스 합성: AR(1) (ρ 0~0.7) + 완만한 주기 성분 — 정상 범위의 다양성 확보."""
    seqs = np.empty((N_SEQ, SEQ_LEN), dtype=np.float64)
    for i in range(N_SEQ):
        rho = rng.uniform(0.0, 0.7)
        eps = rng.normal(0.0, np.sqrt(1 - rho**2), SEQ_LEN)
        z = np.empty(SEQ_LEN)
        z[0] = rng.normal()
        for t in range(1, SEQ_LEN):
            z[t] = rho * z[t - 1] + eps[t]
        # 완만한 주기 성분(±0.5σ 이내) — 사이클성 부하 변동을 정상으로 학습
        amp = rng.uniform(0.0, 0.5)
        phase = rng.uniform(0, 2 * np.pi)
        period = rng.uniform(20, 120)
        z += amp * np.sin(2 * np.pi * np.arange(SEQ_LEN) / period + phase)
        seqs[i] = z
    return seqs


def window_features(seqs: np.ndarray) -> np.ndarray:
    """C# AnomalyFeatures.Compute 와 동일한 피처 (표본 std, RMS, 최소제곱 slope)."""
    feats = []
    x = np.arange(WINDOW)
    x_center = x - x.mean()
    den = float((x_center**2).sum())
    for z in seqs:
        for s in range(0, SEQ_LEN - WINDOW + 1, 3):  # stride 3 — 중복 완화
            w = z[s : s + WINDOW]
            mean = w.mean()
            feats.append([
                mean,
                w.std(ddof=1),
                np.sqrt((w**2).mean()),
                w.min(),
                w.max(),
                float((x_center * (w - mean)).sum()) / den,
            ])
    return np.asarray(feats, dtype=np.float32)


def main() -> None:
    root = Path(__file__).resolve().parents[2]
    out_dir = root / "models"
    out_dir.mkdir(exist_ok=True)

    rng = np.random.default_rng(SEED)
    X = window_features(synth_normal_sequences(rng))
    rng.shuffle(X)
    split = int(len(X) * 0.8)
    X_train, X_val = X[:split], X[split:]
    print(f"train={len(X_train)} val={len(X_val)} features={X.shape[1]}")

    model = IsolationForest(n_estimators=200, max_samples=1024, random_state=SEED, n_jobs=-1)
    model.fit(X_train)

    # 임계값: 검증(정상) 점수의 0.5퍼센타일 — 정상 오탐 ≈ 0.5%/윈도 수준으로 캘리브레이션.
    val_scores = model.decision_function(X_val)
    threshold = float(np.quantile(val_scores, 0.005))
    print(f"validation decision_function: min={val_scores.min():.4f} "
          f"p0.5={threshold:.4f} median={np.median(val_scores):.4f}")

    onnx_model = convert_sklearn(
        model,
        initial_types=[("input", FloatTensorType([None, X.shape[1]]))],
        # ai.onnx.ml v4는 skl2onnx/onnxruntime 조합에 따라 미지원 — v3로 고정.
        target_opset={"": 15, "ai.onnx.ml": 3},
    )
    onnx_path = out_dir / "anomaly_iforest.onnx"
    onnx_path.write_bytes(onnx_model.SerializeToString())

    sidecar = {
        "window": WINDOW,
        "threshold": threshold,
        "featureNames": FEATURE_NAMES,
        "trainedAtUtc": dt.datetime.now(dt.timezone.utc).isoformat(),
        "note": "부트스트랩 모델 — 합성 정상(z-공간) 학습. 실 데이터 축적 후 재학습해 교체할 것.",
    }
    (out_dir / "anomaly_iforest.json").write_text(
        json.dumps(sidecar, ensure_ascii=False, indent=2), encoding="utf-8")

    # (선택) onnxruntime이 있으면 sklearn ↔ ONNX 점수 정합 검증
    try:
        import onnxruntime as ort

        sess = ort.InferenceSession(str(onnx_path))
        score_name = next(o.name for o in sess.get_outputs() if "score" in o.name.lower())
        onnx_scores = sess.run([score_name], {"input": X_val[:256]})[0].ravel()
        skl_scores = model.decision_function(X_val[:256])
        max_diff = float(np.abs(onnx_scores - skl_scores).max())
        print(f"ONNX↔sklearn 점수 최대 오차: {max_diff:.6f}")
        assert max_diff < 1e-3, "ONNX 변환 점수 불일치"
    except ImportError:
        print("onnxruntime 미설치 — 정합 검증 생략 (pip install onnxruntime)")

    print(f"saved: {onnx_path}")
    print(f"saved: {out_dir / 'anomaly_iforest.json'}")


if __name__ == "__main__":
    main()
