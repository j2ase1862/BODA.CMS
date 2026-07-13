"""BODA.CMS P3 — 이상탐지 모델 재학습 (TimescaleDB 실 데이터 기반).

부트스트랩(train_anomaly.py, 합성 z-공간)을 대체하는 현장 재학습 스크립트.
telemetry_frames 에 축적된 **정상 운전** 데이터에서 런타임(CbmMonitor)과 동일한
z-파이프라인을 재현해 윈도 피처를 뽑고, IsolationForest를 학습해 models/ 를 교체한다.

런타임 재현 (Analytics/CbmMonitor.cs 와 동일해야 함):
  1초 버킷 평균 집계 → 세그먼트별 첫 60집계로 기준선(평균/σ) 학습
  → 실효 σ = max(σ, 1e-3, 0.01·|μ|) → 이후 집계만 z-정규화 → 10집계 슬라이딩 윈도.
  (기준선은 프로세스 수명 단위지만, 여기서는 수집 공백(--gap-seconds)으로 세그먼트를
   근사한다 — 재접속마다 기준선을 다시 잡는 보수적 근사.)

- ⚠️ 피처 정의는 C# Analytics/Ml/AnomalyFeatures.cs 와 정확히 일치해야 한다.
- ⚠️ 학습 구간(--since/--until)은 **로봇이 정상 운전 중이던 기간**만 지정할 것.
  정지(STANDBY)·이상 구간이 섞이면 그 패턴도 '정상'으로 학습된다.
- 합성 정상(부트스트랩과 동일 생성기)을 일부 섞어(--synthetic-frac) 실 데이터가
  못 본 정상 형태에 대한 과잉 탐지를 완화한다.

사용 (저장소 루트에서, 데이터가 하루 이상 쌓인 뒤):
  python tools/ml/retrain_anomaly.py --since 2026-07-09T00:00:00+09:00
필요 패키지: numpy scikit-learn skl2onnx psycopg2-binary (검증용 onnxruntime 권장)
"""
from __future__ import annotations

import argparse
import json
import datetime as dt
from pathlib import Path

import numpy as np
from sklearn.ensemble import IsolationForest
from skl2onnx import convert_sklearn
from skl2onnx.common.data_types import FloatTensorType

WINDOW = 10            # C# MlModelInfo.Window / CbmMonitor 집계 단위와 동일
LEARNING_AGGREGATES = 60   # CbmOptions.LearningAggregates 기본값 — Collector:Cbm 설정으로 바꿨다면
                           # --learning-aggregates 로 같은 값을 넘겨야 런타임 z-공간과 정합된다.
ABSOLUTE_MIN_STD = 1e-3    # CbmOptions.AbsoluteMinStd
RELATIVE_MIN_STD = 0.01    # CbmOptions.RelativeMinStd
SEED = 20260708

FEATURE_NAMES = ["mean", "std", "rms", "min", "max", "slope"]

# CBM 감시 대상 신호 컬럼 (위치/속도는 CbmOptions.ExcludedSignals — 동작 의존이라 제외)
SIGNAL_COLUMNS = ["torque_nm", "model_torque_nm", "external_torque_nm", "current_a", "temperature_c"]


def parse_args() -> argparse.Namespace:
    p = argparse.ArgumentParser(description="실 데이터 기반 이상탐지 모델 재학습")
    p.add_argument("--dsn", default="host=localhost port=5432 dbname=boda_cms user=postgres password=postgres",
                   help="PostgreSQL DSN (기본: Collector appsettings와 동일)")
    p.add_argument("--since", help="학습 시작 시각(ISO8601) — 정상 운전 구간의 시작")
    p.add_argument("--until", help="학습 종료 시각(ISO8601)")
    p.add_argument("--robot", help="특정 robot_id만 사용")
    p.add_argument("--channel", help="특정 channel만 사용 (drfl/modbus)")
    p.add_argument("--gap-seconds", type=int, default=30,
                   help="이 이상 수집 공백이면 세그먼트 분리·기준선 재학습 (기본 30)")
    p.add_argument("--stride", type=int, default=2, help="윈도 슬라이딩 간격 (기본 2 — 중복 완화)")
    p.add_argument("--synthetic-frac", type=float, default=0.25,
                   help="학습셋 중 합성 정상 비율 0~0.9 (기본 0.25, 0이면 실 데이터만)")
    p.add_argument("--min-windows", type=int, default=5000,
                   help="실 데이터 윈도 최소 개수 — 미달 시 중단 (기본 5000 ≈ 신호당 수 시간)")
    p.add_argument("--learning-aggregates", type=int, default=LEARNING_AGGREGATES,
                   help="기준선 학습 집계 수(초) — 런타임 Collector:Cbm 학습창과 같아야 함 (기본 60)")
    p.add_argument("--out", default=None, help="출력 디렉터리 (기본: 저장소 models/)")
    return p.parse_args()


def connect(dsn: str):
    try:
        import psycopg2
    except ImportError:
        raise SystemExit("psycopg2 미설치 — pip install psycopg2-binary")
    return psycopg2.connect(dsn)


def _where(args: argparse.Namespace) -> tuple[str, list]:
    cond, params = ["TRUE"], []
    if args.since:
        cond.append("time >= %s"); params.append(args.since)
    if args.until:
        cond.append("time < %s"); params.append(args.until)
    if args.robot:
        cond.append("robot_id = %s"); params.append(args.robot)
    if args.channel:
        cond.append("channel = %s"); params.append(args.channel)
    return " AND ".join(cond), params


def load_series(conn, args: argparse.Namespace) -> dict[tuple, list[tuple[float, float]]]:
    """(robot, channel, signal, axis) → [(epoch_sec, 1초 평균)] — 런타임 집계와 동일."""
    where, params = _where(args)
    series: dict[tuple, list[tuple[float, float]]] = {}

    with conn.cursor() as cur:
        # 정형 컬럼 (drfl 등 네이티브 채널)
        for col in SIGNAL_COLUMNS:
            cur.execute(f"""
                SELECT robot_id, channel, ax.axis, extract(epoch FROM date_trunc('second', time))::bigint, avg(ax.val)
                FROM telemetry_frames, LATERAL unnest({col}) WITH ORDINALITY AS ax(val, axis)
                WHERE {where} AND {col} IS NOT NULL
                GROUP BY 1, 2, 3, 4 ORDER BY 1, 2, 3, 4
                """, params)
            for robot, channel, axis, epoch, val in cur:
                series.setdefault((robot, channel, col, int(axis)), []).append((float(epoch), float(val)))

        # vendor_raw jsonb 숫자 배열 (modbus cur_raw/temp_raw 등) — 런타임도 이 키들을 감시한다
        cur.execute(f"""
            SELECT DISTINCT robot_id, channel, k.key
            FROM telemetry_frames, LATERAL jsonb_object_keys(vendor_raw) AS k(key)
            WHERE {where} AND vendor_raw IS NOT NULL
              AND jsonb_typeof(vendor_raw->k.key) = 'array'
            """, params)
        raw_keys = cur.fetchall()
        for robot, channel, key in raw_keys:
            cur.execute(f"""
                SELECT ax.ordinality, extract(epoch FROM date_trunc('second', time))::bigint, avg(ax.val::float8)
                FROM telemetry_frames,
                     LATERAL jsonb_array_elements_text(vendor_raw->%s) WITH ORDINALITY AS ax(val, ordinality)
                WHERE {where} AND robot_id = %s AND channel = %s AND vendor_raw ? %s
                GROUP BY 1, 2 ORDER BY 1, 2
                """, [key, *params, robot, channel, key])
            for axis, epoch, val in cur:
                series.setdefault((robot, channel, key, int(axis)), []).append((float(epoch), float(val)))

    return series


def z_windows(series: dict[tuple, list[tuple[float, float]]], gap_seconds: int, stride: int,
              learning_aggregates: int = LEARNING_AGGREGATES) -> np.ndarray:
    """세그먼트별 기준선 학습 → z → 슬라이딩 윈도 피처 (런타임 CbmMonitor/MlAnomalyMonitor 재현)."""
    feats: list[list[float]] = []
    x = np.arange(WINDOW)
    x_center = x - x.mean()
    den = float((x_center**2).sum())
    skipped_short = 0

    for key, points in series.items():
        # 세그먼트 분리: 수집 공백 기준
        segments: list[list[float]] = [[]]
        prev_t = None
        for t, v in points:  # load_series가 시간순 정렬 보장
            if prev_t is not None and t - prev_t > gap_seconds:
                segments.append([])
            segments[-1].append(v)
            prev_t = t

        for seg in segments:
            if len(seg) < learning_aggregates + WINDOW:
                skipped_short += 1
                continue
            base = np.asarray(seg[:learning_aggregates])
            mean = float(base.mean())
            std = max(float(base.std(ddof=1)), ABSOLUTE_MIN_STD, RELATIVE_MIN_STD * abs(mean))
            z = (np.asarray(seg[learning_aggregates:]) - mean) / std

            for s in range(0, len(z) - WINDOW + 1, stride):
                w = z[s:s + WINDOW]
                m = w.mean()
                feats.append([
                    m,
                    w.std(ddof=1),
                    float(np.sqrt((w**2).mean())),
                    float(w.min()),
                    float(w.max()),
                    float((x_center * (w - m)).sum()) / den,
                ])

    if skipped_short:
        print(f"짧은 세그먼트 {skipped_short}개 제외 (기준선 {learning_aggregates} + 윈도 {WINDOW} 집계 미달)")
    return np.asarray(feats, dtype=np.float32)


def synth_windows(n: int, rng: np.random.Generator) -> np.ndarray:
    """부트스트랩(train_anomaly.py)과 동일한 합성 정상 z-시퀀스에서 윈도 n개."""
    seq_len = 64
    feats: list[list[float]] = []
    x = np.arange(WINDOW)
    x_center = x - x.mean()
    den = float((x_center**2).sum())
    while len(feats) < n:
        rho = rng.uniform(0.0, 0.7)
        eps = rng.normal(0.0, np.sqrt(1 - rho**2), seq_len)
        z = np.empty(seq_len)
        z[0] = rng.normal()
        for t in range(1, seq_len):
            z[t] = rho * z[t - 1] + eps[t]
        amp = rng.uniform(0.0, 0.5)
        z += amp * np.sin(2 * np.pi * np.arange(seq_len) / rng.uniform(20, 120) + rng.uniform(0, 2 * np.pi))
        for s in range(0, seq_len - WINDOW + 1, 3):
            w = z[s:s + WINDOW]
            m = w.mean()
            feats.append([m, w.std(ddof=1), float(np.sqrt((w**2).mean())),
                          float(w.min()), float(w.max()), float((x_center * (w - m)).sum()) / den])
    return np.asarray(feats[:n], dtype=np.float32)


def main() -> None:
    args = parse_args()
    root = Path(__file__).resolve().parents[2]
    out_dir = Path(args.out) if args.out else root / "models"
    out_dir.mkdir(parents=True, exist_ok=True)

    conn = connect(args.dsn)
    try:
        series = load_series(conn, args)
    finally:
        conn.close()
    if not series:
        raise SystemExit("조건에 맞는 데이터가 없습니다 — --since/--robot/--channel 확인.")

    X_real = z_windows(series, args.gap_seconds, args.stride, args.learning_aggregates)
    n_series = len(series)
    print(f"시리즈 {n_series}개(신호×축), 실 데이터 윈도 {len(X_real)}개")
    if len(X_real) < args.min_windows:
        raise SystemExit(f"실 데이터 윈도 {len(X_real)} < 최소 {args.min_windows} — "
                         "데이터를 더 축적하거나 --min-windows 를 낮추세요.")

    rng = np.random.default_rng(SEED)
    frac = min(max(args.synthetic_frac, 0.0), 0.9)
    n_syn = int(len(X_real) * frac / (1 - frac)) if frac > 0 else 0
    X = np.vstack([X_real, synth_windows(n_syn, rng)]) if n_syn else X_real
    print(f"합성 정상 blend: {n_syn}개 ({frac:.0%}) → 총 {len(X)}개")

    rng.shuffle(X)
    split = int(len(X) * 0.8)
    X_train, X_val = X[:split], X[split:]
    print(f"train={len(X_train)} val={len(X_val)} features={X.shape[1]}")

    model = IsolationForest(n_estimators=200, max_samples=min(1024, len(X_train)),
                            random_state=SEED, n_jobs=-1)
    model.fit(X_train)

    # 임계값: 검증(정상) 점수의 0.5퍼센타일 — 부트스트랩과 동일 캘리브레이션.
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
        "learningAggregates": args.learning_aggregates,  # 추적용 — 런타임은 Collector:Cbm 설정을 따른다
        "trainedAtUtc": dt.datetime.now(dt.timezone.utc).isoformat(),
        "note": f"실 데이터 재학습 — 시리즈 {n_series}개, 실 윈도 {len(X_real)}개"
                f"{f' + 합성 {n_syn}개' if n_syn else ''}"
                f" (구간 {args.since or '전체'} ~ {args.until or '현재'}, 학습창 {args.learning_aggregates}초).",
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
    if not args.out:
        print("\n적용: 실행 중인 exe 옆 models/ 를 교체해야 반영된다 (재빌드 또는 수동 복사):")
        print(r"  - Collector\bin\<config>\net8.0\models\ ")
        print(r"  - bin\<config>\net8.0-windows\models\  (WPF 앱)")
        print("교체 후 Collector/앱 재시작.")


if __name__ == "__main__":
    main()
