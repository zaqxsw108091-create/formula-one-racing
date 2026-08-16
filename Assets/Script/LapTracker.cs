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

// 트랙 경로 위로 차량 위치를 '투영'해서 랩과 진행도를 계산한다.
//
// 예전 방식(웨이포인트 반경 안에 들어와야 인정)은 웨이포인트를 향해 달리는 AI에게만
// 맞고, 자기 라인으로 달리는 플레이어는 반경을 벗어나 랩이 전혀 오르지 않았다.
// 이 방식은 어떤 주행 라인이든 경로 상의 위치를 구하므로 사람이 몰아도 정확하다.
public class LapTracker
{
    private readonly Transform[] waypoints;
    private readonly int totalLaps;

    private float lastT = 0f;      // 직전 프레임의 랩 내 위치 (0~1)
    private bool initialized = false;

    public int CurrentLap { get; private set; }
    public bool Finished { get; private set; }
    public float FinishTime { get; private set; }

    // 현재 랩에서의 진행 위치 (0=출발선, 1=한 바퀴)
    public float LapProgress01 { get; private set; }
    // AI가 조향해야 할 다음 웨이포인트 인덱스
    public int NextWaypointIndex { get; private set; }
    // 경로에서 얼마나 벗어나 있는지 (m) — 코스 이탈/끼임 판정에 사용
    public float DistanceFromPath { get; private set; }

    public LapTracker(Transform[] waypoints, int totalLaps)
    {
        this.waypoints = waypoints;
        this.totalLaps = Mathf.Max(1, totalLaps);
        CurrentLap = 0;
        Finished = false;
        FinishTime = -1f;
        NextWaypointIndex = 0;
    }

    public bool HasWaypoints => waypoints != null && waypoints.Length >= 2;

    public Transform CurrentTarget => HasWaypoints ? waypoints[NextWaypointIndex] : null;

    // 현재 목표에서 offset칸 뒤의 웨이포인트(순환). offset 0 = 현재 목표, 1 = 그 다음.
    public Transform GetUpcomingWaypoint(int offset)
    {
        if (!HasWaypoints) return null;
        int idx = (NextWaypointIndex + offset) % waypoints.Length;
        return waypoints[idx];
    }

    // 등수 정렬용 총 진행도 = 완주한 바퀴 수 + 현재 랩 내 진행도
    public float TotalProgress => CurrentLap + LapProgress01;

    // 매 프레임 호출 — 차량 위치를 경로에 투영해 랩/진행도를 갱신
    public void Tick(Vector3 position, float time)
    {
        if (Finished || !HasWaypoints) return;

        int n = waypoints.Length;

        // 1) 경로 전체 길이와 각 웨이포인트까지의 누적 거리
        float totalLength = 0f;
        for (int i = 0; i < n; i++)
        {
            totalLength += Flat(waypoints[(i + 1) % n].position - waypoints[i].position).magnitude;
        }
        if (totalLength < 0.01f) return;

        // 2) 모든 구간 중 차량과 가장 가까운 지점을 찾아 누적 거리(s)를 구함
        float best = float.MaxValue;
        float bestS = 0f;
        int bestSegment = 0;
        float running = 0f;
        Vector3 flatPos = Flat(position);

        for (int i = 0; i < n; i++)
        {
            Vector3 a = Flat(waypoints[i].position);
            Vector3 b = Flat(waypoints[(i + 1) % n].position);
            Vector3 ab = b - a;
            float len = ab.magnitude;

            float t = 0f;
            if (len > 0.001f) t = Mathf.Clamp01(Vector3.Dot(flatPos - a, ab) / (len * len));

            Vector3 closest = a + ab * t;
            float d = (flatPos - closest).magnitude;

            if (d < best)
            {
                best = d;
                bestS = running + len * t;
                bestSegment = i;
            }
            running += len;
        }

        DistanceFromPath = best;
        float currentT = Mathf.Clamp01(bestS / totalLength);

        // 다음에 향할 웨이포인트 = 가장 가까운 구간의 끝점
        NextWaypointIndex = (bestSegment + 1) % n;

        // 3) 출발선 통과 판정 — 경로 끝(1.0)에서 시작(0.0)으로 넘어가면 한 바퀴 완료
        if (!initialized)
        {
            lastT = currentT;
            initialized = true;
        }
        else
        {
            if (lastT > 0.7f && currentT < 0.3f)
            {
                CurrentLap++; // 정방향으로 결승선 통과
                if (CurrentLap >= totalLaps)
                {
                    Finished = true;
                    FinishTime = time;
                }
            }
            else if (lastT < 0.3f && currentT > 0.7f)
            {
                CurrentLap = Mathf.Max(0, CurrentLap - 1); // 역주행으로 되돌아감
            }
            lastT = currentT;
        }

        LapProgress01 = currentT;
    }

    // 차량을 순간이동시킨 뒤 호출 — 위치가 갑자기 바뀐 것을 결승선 통과로
    // 잘못 인식해 랩이 오르는 일을 막는다.
    public void ResyncAfterTeleport()
    {
        initialized = false;
    }

    // 코스에서 심하게 벗어나거나 끼었을 때 되돌릴 위치/방향
    public Vector3 GetRespawnPosition()
    {
        if (!HasWaypoints) return Vector3.zero;
        return waypoints[NextWaypointIndex].position;
    }

    public Quaternion GetRespawnRotation()
    {
        if (!HasWaypoints) return Quaternion.identity;
        int n = waypoints.Length;
        Vector3 from = waypoints[NextWaypointIndex].position;
        Vector3 to = waypoints[(NextWaypointIndex + 1) % n].position;
        Vector3 dir = Flat(to - from);
        if (dir.sqrMagnitude < 0.001f) return Quaternion.identity;
        return Quaternion.LookRotation(dir.normalized, Vector3.up);
    }

    private static Vector3 Flat(Vector3 v)
    {
        v.y = 0f;
        return v;
    }
}
