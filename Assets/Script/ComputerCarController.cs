using UnityEngine;
using System.Collections;

public class ComputerCarController : MonoBehaviour, IRaceCar
{
    public WheelCollider FrontLeftWheel;
    public WheelCollider FrontRightWheel;
    public WheelCollider RearLeftWheel;
    public WheelCollider RearRightWheel;

    public float[] GearRatio = { 2.66f, 1.91f, 1.39f, 1.00f, 0.77f, 0.63f };  // 6단 기어비
    public int CurrentGear = 0;

    public float PeakTorque = 600.0f; // 피크 엔진 토크 (N·m) — 가속 세기 튜닝
    public AnimationCurve TorqueCurve; // RPM-토크 곡선 (비우면 기본 곡선 사용)
    public float FinalDriveRatio = 2.8f; // 최종 감속비 (★ 0→100km/h 가속 시간 튜닝용: 클수록 빨라짐, AI는 플레이어보다 약간 느리게)
    public float MaxEngineRPM = 10000.0f;
    public float MinEngineRPM = 1000.0f;
    public float RPMIncreaseRate = 500.0f; // RPM 증가 속도
    public float RPMDecreaseRate = 1000.0f; // RPM 감소 속도

    private float EngineRPM = 0.0f;
    private float EngineTorque = 0.0f;

    private Rigidbody rigidBody;
    private AudioSource audioSource;

    public float SteerAngle = 30.0f;
    public float MaxSpeed = 300.0f;

    public float AntiRollForce = 5000.0f;

    public Transform[] targetPositions; // 트랙 웨이포인트(순서대로). 모든 차에 동일하게, 0번 ≈ 출발/결승선
    public Transform finishLine; // 결승선 (현재 미사용, 호환용)
    public Transform startPosition; // 시작 지점

    public int totalLaps = 5; // 총 랩 수
    public float waypointThreshold = 20.0f; // 웨이포인트 통과 인정 거리 (★ 트랙 크기에 맞게 조정)
    private LapTracker lapTracker; // 랩/진행도 추적기

    // AI 뭉침(기차 현상) 방지
    public float LaneSpread = 4.0f;     // 좌우 차선 분산 폭 (★ 트랙 폭 절반 정도로 조정)
    public float SpeedVariance = 0.08f; // AI 간 최고속 편차 (0~1, 클수록 속도 차이 큼)
    private float laneOffset = 0f;      // 이 차의 좌우 오프셋
    private float speedMultiplier = 1f; // 이 차의 최고속 배수

    // 코너 감속
    public float CornerBrakeDistance = 40.0f; // 이 거리 안에 들어오면 다가오는 코너에 맞춰 감속 시작
    public float CornerMinSpeed = 40.0f;       // 급코너 최소 통과 속도 (km/h)
    public float BrakeForcePerWheel = 3000.0f; // 감속 시 바퀴당 브레이크 토크
    public float SteerSensitivity = 45.0f;     // 이 각도(도) 이상 벗어나면 풀 스티어

    // 충돌 회피
    public float AvoidDistance = 12.0f; // 전방 차량 감지 거리
    public float AvoidRadius = 2.0f;    // 감지 반경

    public float DriftGripMultiplier = 0.5f;
    public float NormalGripMultiplier = 1.0f;
    public float DriftHandbrakeForce = 5000.0f;
    private float steerInput;

    void Start()
    {
        rigidBody = GetComponent<Rigidbody>();
        audioSource = GetComponent<AudioSource>();

        rigidBody.centerOfMass = new Vector3(rigidBody.centerOfMass.x, -0.8f, rigidBody.centerOfMass.z);

        FrontLeftWheel.ConfigureVehicleSubsteps(5f, 10, 10);
        RearLeftWheel.ConfigureVehicleSubsteps(5f, 10, 10);

        // 모든 차가 같은 공유 경로를 쓰도록 GameManager에서 가져옴 (없으면 자기 targetPositions 사용)
        Transform[] wp = GameManager.Instance.GetWaypoints();
        if (wp == null || wp.Length == 0) wp = targetPositions;
        lapTracker = new LapTracker(wp, totalLaps, waypointThreshold);
        if (TorqueCurve == null || TorqueCurve.length == 0) TorqueCurve = EngineModel.DefaultTorqueCurve();

        // 차마다 다른 차선·속도 → 같은 라인에 줄지어 붙는 현상 방지
        laneOffset = Random.Range(-LaneSpread, LaneSpread);
        speedMultiplier = Random.Range(1f - SpeedVariance, 1f);

        GameManager.Instance.AddCar(gameObject);
    }

    // IRaceCar 구현 (등수 계산용)
    public int CurrentLap => lapTracker != null ? lapTracker.CurrentLap : 0;
    public float RaceProgress => lapTracker != null ? lapTracker.Progress(transform.position) : 0f;
    public bool Finished => lapTracker != null && lapTracker.Finished;
    public float FinishTime => lapTracker != null ? lapTracker.FinishTime : -1f;

    void FixedUpdate()
    {
        if (lapTracker == null || lapTracker.CurrentTarget == null) return;

        if (lapTracker.Finished)
        {
            StopCar();
            return;
        }

        // 랩/진행도 갱신 (목표 웨이포인트 통과 → 다음으로, 한 바퀴 돌면 랩 +1)
        lapTracker.Tick(transform.position, GameManager.Instance != null ? GameManager.Instance.gameTime : Time.time);

        float currentSpeed = rigidBody.linearVelocity.magnitude * 3.6f;

        // ===== 조향: 목표 웨이포인트(차선 오프셋 적용) 방향 =====
        Vector3 targetPos = lapTracker.CurrentTarget.position;
        Vector3 toTarget = targetPos - transform.position;
        Vector3 lateral = Vector3.Cross(Vector3.up, toTarget).normalized;
        targetPos += lateral * laneOffset;

        Vector3 targetDirection = targetPos - transform.position;
        float angle = GetAngle(transform.forward, targetDirection);
        float direction = GetDirection(transform.forward, targetDirection, transform.up);
        steerInput = Mathf.Clamp(angle * direction / SteerSensitivity, -1.0f, 1.0f);

        // 충돌 회피: 전방 차량 감지 → 회피 조향 + 속도 제한
        float avoidSteer;
        float collisionSpeedLimit;
        DetectCarAhead(currentSpeed, out avoidSteer, out collisionSpeedLimit);
        steerInput = Mathf.Clamp(steerInput + avoidSteer, -1.0f, 1.0f);

        FrontLeftWheel.steerAngle = SteerAngle * steerInput;
        FrontRightWheel.steerAngle = SteerAngle * steerInput;

        // ===== RPM / 자동 변속 / 기어별 최고속 =====
        float averageWheelRPM = (FrontLeftWheel.rpm + FrontRightWheel.rpm + RearLeftWheel.rpm + RearRightWheel.rpm) / 4;
        EngineRPM = averageWheelRPM * GearRatio[CurrentGear];

        float carMaxSpeed = MaxSpeed * speedMultiplier;
        float topGearRatio = GearRatio[GearRatio.Length - 1];
        float gearMaxSpeed = carMaxSpeed * (topGearRatio / GearRatio[CurrentGear]);

        if (currentSpeed >= gearMaxSpeed * 0.95f && CurrentGear < GearRatio.Length - 1)
        {
            CurrentGear++; // 현재 기어 한계 속도 근처 → 업시프트
        }
        else if (CurrentGear > 0)
        {
            float lowerGearMax = carMaxSpeed * (topGearRatio / GearRatio[CurrentGear - 1]);
            if (currentSpeed < lowerGearMax * 0.7f) CurrentGear--; // 너무 느려지면 다운시프트
        }

        EngineRPM = Mathf.Clamp(EngineRPM, MinEngineRPM, MaxEngineRPM);
        gearMaxSpeed = carMaxSpeed * (topGearRatio / GearRatio[CurrentGear]);
        if (currentSpeed > gearMaxSpeed)
        {
            rigidBody.linearVelocity = rigidBody.linearVelocity.normalized * (gearMaxSpeed / 3.6f);
        }

        EngineTorque = EngineModel.Torque(TorqueCurve, PeakTorque, EngineRPM, MinEngineRPM, MaxEngineRPM);

        // ===== 목표 속도 = min(기어 최고속, 코너 통과 속도, 충돌 제한) =====
        float cornerSpeed = GetCornerSpeedLimit(carMaxSpeed);
        float desiredSpeed = Mathf.Min(gearMaxSpeed, cornerSpeed);
        desiredSpeed = Mathf.Min(desiredSpeed, collisionSpeedLimit);

        // ===== 스로틀 / 브레이크 결정 =====
        float torque = EngineTorque * GearRatio[CurrentGear] * FinalDriveRatio;
        float brake = 0f;
        if (currentSpeed > desiredSpeed + 2.0f)
        {
            // 목표 속도보다 빠름 → 미리 감속 (브레이크 + 스로틀 차단)
            torque = 0f;
            brake = BrakeForcePerWheel;
        }
        else
        {
            // 코너 각도가 클수록 스로틀을 약간 줄여 부드럽게
            float moveInput = Mathf.Clamp(1.0f - Mathf.Abs(angle) / 180.0f, 0.3f, 1.0f);
            torque *= moveInput;
        }

        FrontLeftWheel.motorTorque = torque;
        FrontRightWheel.motorTorque = torque;
        RearLeftWheel.motorTorque = torque;
        RearRightWheel.motorTorque = torque;

        // ===== 드리프트 + 브레이크 적용 =====
        float frontBrake = brake;
        float rearBrake = brake;
        if (currentSpeed > 50.0f && Mathf.Abs(steerInput) > 0.7f)
        {
            SetGrip(DriftGripMultiplier);
            rearBrake = Mathf.Max(rearBrake, DriftHandbrakeForce); // 고속 급조향 시 뒤 핸드브레이크
        }
        else
        {
            SetGrip(NormalGripMultiplier);
        }
        FrontLeftWheel.brakeTorque = frontBrake;
        FrontRightWheel.brakeTorque = frontBrake;
        RearLeftWheel.brakeTorque = rearBrake;
        RearRightWheel.brakeTorque = rearBrake;

        AntiRoll();

        audioSource.pitch = Mathf.Clamp(EngineRPM / MaxEngineRPM + 0.5f, 0.5f, 2.0f);
        if (!audioSource.isPlaying && currentSpeed > 1)
        {
            audioSource.Play();
        }
        else if (audioSource.isPlaying && currentSpeed <= 1)
        {
            audioSource.Stop();
        }
    }

    // 다가오는 코너의 꺾임 각도·거리를 보고 통과 가능 속도(km/h)를 계산
    float GetCornerSpeedLimit(float carMaxSpeed)
    {
        Transform t0 = lapTracker.CurrentTarget;
        Transform t1 = lapTracker.GetUpcomingWaypoint(1);
        if (t0 == null || t1 == null) return carMaxSpeed;

        Vector3 inDir = t0.position - transform.position;
        Vector3 outDir = t1.position - t0.position;
        inDir.y = 0f; outDir.y = 0f;

        float cornerAngle = Vector3.Angle(inDir, outDir); // 0=직선, 클수록 급코너
        float sharpness = Mathf.Clamp01(cornerAngle / 90f);
        float cornerSpeed = Mathf.Lerp(carMaxSpeed, CornerMinSpeed, sharpness);

        // 코너에서 멀면 제한 완화, CornerBrakeDistance 안에서 점점 cornerSpeed로
        float distToCorner = inDir.magnitude;
        float approach = Mathf.Clamp01(distToCorner / CornerBrakeDistance); // 0(코너)~1(멀리)
        return Mathf.Lerp(cornerSpeed, carMaxSpeed, approach);
    }

    // 전방 차량 감지 → 회피 조향(avoidSteer)과 속도 제한(speedLimit) 산출
    void DetectCarAhead(float currentSpeed, out float avoidSteer, out float speedLimit)
    {
        avoidSteer = 0f;
        speedLimit = Mathf.Infinity;

        Vector3 origin = transform.position + transform.up * 0.5f;
        RaycastHit[] hits = Physics.SphereCastAll(origin, AvoidRadius, transform.forward, AvoidDistance);

        float nearest = AvoidDistance;
        Transform aheadCar = null;
        foreach (RaycastHit h in hits)
        {
            if (h.collider.transform.IsChildOf(transform)) continue;          // 자기 자신 무시
            if (h.collider.GetComponentInParent<IRaceCar>() == null) continue; // 차량만 대상
            if (h.distance < nearest)
            {
                nearest = h.distance;
                aheadCar = h.collider.transform;
            }
        }

        if (aheadCar == null) return;

        float factor = Mathf.Clamp01(nearest / AvoidDistance); // 0(코앞)~1(멀리)
        speedLimit = Mathf.Lerp(CornerMinSpeed, currentSpeed, factor); // 가까울수록 강하게 감속
        // 앞차가 오른쪽이면 왼쪽으로(반대), 가까울수록 크게 회피
        float side = Vector3.Dot(transform.right, aheadCar.position - transform.position);
        avoidSteer = (side > 0f ? -1f : 1f) * (1f - factor);
    }

    void SetGrip(float multiplier)
    {
        WheelFrictionCurve forwardFriction = FrontLeftWheel.forwardFriction;
        WheelFrictionCurve sidewaysFriction = FrontLeftWheel.sidewaysFriction;

        forwardFriction.stiffness = multiplier;
        sidewaysFriction.stiffness = multiplier;

        FrontLeftWheel.forwardFriction = forwardFriction;
        FrontLeftWheel.sidewaysFriction = sidewaysFriction;
        FrontRightWheel.forwardFriction = forwardFriction;
        FrontRightWheel.sidewaysFriction = sidewaysFriction;
        RearLeftWheel.forwardFriction = forwardFriction;
        RearLeftWheel.sidewaysFriction = sidewaysFriction;
        RearRightWheel.forwardFriction = forwardFriction;
        RearRightWheel.sidewaysFriction = sidewaysFriction;
    }

    float GetAngle(Vector3 v1, Vector3 v2)
    {
        return Vector3.Angle(v1, v2);
    }

    float GetDirection(Vector3 fwd, Vector3 targetDir, Vector3 up)
    {
        Vector3 perp = Vector3.Cross(fwd, targetDir);
        float dir = Vector3.Dot(perp, up);

        if (dir > 0.0f)
        {
            return 1.0f;
        }
        else if (dir < 0.0f)
        {
            return -1.0f;
        }
        else
        {
            return 0.0f;
        }
    }

    void StopCar()
    {
        FrontLeftWheel.motorTorque = 0.0f;
        FrontRightWheel.motorTorque = 0.0f;
        RearLeftWheel.motorTorque = 0.0f;
        RearRightWheel.motorTorque = 0.0f;
        FrontLeftWheel.brakeTorque = 10000.0f;
        FrontRightWheel.brakeTorque = 10000.0f;
        RearLeftWheel.brakeTorque = 10000.0f;
        RearRightWheel.brakeTorque = 10000.0f;
    }

    void AntiRoll()
    {
        WheelHit hit;
        float travelL = 1.0f;
        float travelR = 1.0f;

        // 앞 차축 안티롤
        bool groundedL = FrontLeftWheel.GetGroundHit(out hit);
        if (groundedL)
        {
            travelL = (-FrontLeftWheel.transform.InverseTransformPoint(hit.point).y - FrontLeftWheel.radius) / FrontLeftWheel.suspensionDistance;
        }

        bool groundedR = FrontRightWheel.GetGroundHit(out hit);
        if (groundedR)
        {
            travelR = (-FrontRightWheel.transform.InverseTransformPoint(hit.point).y - FrontRightWheel.radius) / FrontRightWheel.suspensionDistance;
        }

        float antiRollForce = (travelL - travelR) * AntiRollForce;

        if (groundedL)
        {
            rigidBody.AddForceAtPosition(FrontLeftWheel.transform.up * -antiRollForce, FrontLeftWheel.transform.position);
        }

        if (groundedR)
        {
            rigidBody.AddForceAtPosition(FrontRightWheel.transform.up * antiRollForce, FrontRightWheel.transform.position);
        }

        // 뒤 차축 안티롤
        travelL = 1.0f;
        travelR = 1.0f;

        groundedL = RearLeftWheel.GetGroundHit(out hit);
        if (groundedL)
        {
            travelL = (-RearLeftWheel.transform.InverseTransformPoint(hit.point).y - RearLeftWheel.radius) / RearLeftWheel.suspensionDistance;
        }

        groundedR = RearRightWheel.GetGroundHit(out hit);
        if (groundedR)
        {
            travelR = (-RearRightWheel.transform.InverseTransformPoint(hit.point).y - RearRightWheel.radius) / RearRightWheel.suspensionDistance;
        }

        antiRollForce = (travelL - travelR) * AntiRollForce;

        if (groundedL)
        {
            rigidBody.AddForceAtPosition(RearLeftWheel.transform.up * -antiRollForce, RearLeftWheel.transform.position);
        }

        if (groundedR)
        {
            rigidBody.AddForceAtPosition(RearRightWheel.transform.up * antiRollForce, RearRightWheel.transform.position);
        }
    }
}