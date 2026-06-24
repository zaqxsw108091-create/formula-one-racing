using UnityEngine;

// 엔진 토크 곡선 모델 (플레이어/AI 공유)
// 실제 엔진처럼 RPM에 따라 토크가 변함: 저회전 낮음 → 중회전 피크 → 레드존 하락
public static class EngineModel
{
    // 기본 토크 곡선: x = 정규화 RPM(0~1), y = 피크 토크 대비 비율(0~1)
    public static AnimationCurve DefaultTorqueCurve()
    {
        return new AnimationCurve(
            new Keyframe(0.0f, 0.55f),  // 공회전 부근: 토크 낮음
            new Keyframe(0.3f, 0.85f),
            new Keyframe(0.6f, 1.0f),   // 중회전: 피크 토크
            new Keyframe(0.85f, 0.95f),
            new Keyframe(1.0f, 0.72f)   // 레드존: 다시 하락
        );
    }

    // 현재 RPM에서의 엔진 토크(N·m) = 피크 토크 × 곡선값
    public static float Torque(AnimationCurve curve, float peakTorque, float rpm, float minRpm, float maxRpm)
    {
        float n = Mathf.InverseLerp(minRpm, maxRpm, rpm); // 0~1로 정규화
        return peakTorque * curve.Evaluate(n);
    }
}

// 모든 레이스 차량(플레이어/AI)이 구현하는 공통 인터페이스 — 등수 계산에 사용
public interface IRaceCar
{
    int CurrentLap { get; }      // 완주한 바퀴 수 (0 ~ totalLaps)
    float RaceProgress { get; }  // 등수 정렬용 진행도 (클수록 앞섬)
    bool Finished { get; }       // 목표 바퀴 수 완주 여부
    float FinishTime { get; }    // 완주 시각(초), 미완주 시 -1
}

// 웨이포인트 순서 통과 기반 랩/진행도 추적기 (MonoBehaviour 아님 — 각 차량 스크립트가 보유)
public class LapTracker
{
    private readonly Transform[] waypoints;
    private readonly int totalLaps;
    private readonly float threshold;

    public int NextWaypointIndex { get; private set; }
    public int CurrentLap { get; private set; }
    public bool Finished { get; private set; }
    public float FinishTime { get; private set; }

    public LapTracker(Transform[] waypoints, int totalLaps, float threshold)
    {
        this.waypoints = waypoints;
        this.totalLaps = totalLaps;
        this.threshold = threshold;
        NextWaypointIndex = 0;
        CurrentLap = 0;
        Finished = false;
        FinishTime = -1f;
    }

    public bool HasWaypoints => waypoints != null && waypoints.Length > 0;

    public Transform CurrentTarget => HasWaypoints ? waypoints[NextWaypointIndex] : null;

    // 현재 목표에서 offset칸 뒤의 웨이포인트(순환). offset 0 = 현재 목표, 1 = 그 다음.
    public Transform GetUpcomingWaypoint(int offset)
    {
        if (!HasWaypoints) return null;
        int idx = (NextWaypointIndex + offset) % waypoints.Length;
        return waypoints[idx];
    }

    // 매 프레임 호출: 현재 목표 웨이포인트에 근접하면 다음으로 전환,
    // 마지막 → 0번으로 돌아오면(= 출발선 통과) 한 바퀴 완료
    public void Tick(Vector3 position, float time)
    {
        if (Finished || !HasWaypoints) return;

        float dist = Vector3.Distance(position, waypoints[NextWaypointIndex].position);
        if (dist <= threshold)
        {
            NextWaypointIndex++;
            if (NextWaypointIndex >= waypoints.Length)
            {
                NextWaypointIndex = 0;
                CurrentLap++;
                if (CurrentLap >= totalLaps)
                {
                    Finished = true;
                    FinishTime = time;
                }
            }
        }
    }

    // 등수 정렬용 진행도 = (랩 × 웨이포인트수) + 통과한 웨이포인트 수 + 다음 지점 근접도(0~1)
    public float Progress(Vector3 position)
    {
        if (!HasWaypoints) return 0f;
        int n = waypoints.Length;
        float baseProgress = CurrentLap * n + NextWaypointIndex;
        float dist = Vector3.Distance(position, waypoints[NextWaypointIndex].position);
        float frac = 1f / (1f + dist); // 가까울수록 1에 근접
        return baseProgress + frac;
    }
}
