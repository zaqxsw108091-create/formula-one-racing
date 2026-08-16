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
    public float FinalDriveRatio = 2.0f; // 최종 감속비 (플레이어와 동일 — 성능 차이가 아니라 주행 실력으로 승부)

    // 접지력 / 다운포스 — 고속 코너링 안정성 (플레이어와 동일 설정)
    public float GripStiffness = 2.2f;
    public float DownforceCoefficient = 25.0f;
    public float SpeedDecayRate = 25.0f; // 최고속 초과 시 초당 감속량(km/h)

    // 코스 이탈 / 끼임 복구
    public float StuckSpeedThreshold = 5.0f;   // 이 속도(km/h) 밑이면 멈춘 것으로 간주
    public float StuckTimeLimit = 3.0f;        // 이만큼 멈춰 있으면 트랙으로 복귀
    public float MaxPathDistance = 60.0f;      // 경로에서 이만큼 벗어나면 복귀
    public float StartGracePeriod = 5.0f;      // 출발 직후 이 시간 동안은 끼임 판정 안 함
    private float stuckTimer = 0.0f;
    private float aliveTime = 0.0f;

    // 러버밴딩 — 플레이어와 너무 벌어지지 않게 (같이 달리는 레이스가 되도록)
    public bool RubberBanding = true;
    public float RubberBandStrength = 0.15f;   // 최대 ±15% 속도 보정

    // 트랙션 컨트롤 — 구동 바퀴가 헛돌면 토크를 줄여 접지력을 회복
    public bool TractionControl = true;
    public float TractionSlipLimit = 0.35f; // 허용 슬립량 (작을수록 개입이 빠름)

    // 속도 감응 조향 — 빠를수록 조향각을 줄여 고속 안정성 확보
    public float HighSpeedSteerAngle = 8.0f; // 고속에서의 조향 각도
    public float SteerFalloffSpeed = 180.0f; // 이 속도(km/h)에서 조향각이 최소가 됨
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

    public Transform[] targetPositions; // 트랙 웨이포인트(순서대로). 0번 ≈ 출발/결승선
                                        // (GameManager에 공유 경로가 있으면 그쪽이 우선)

    public int totalLaps = 5; // 총 랩 수
    private LapTracker lapTracker; // 랩/진행도 추적기

    // AI 뭉침(기차 현상) 방지
    public float LaneSpread = 4.0f;     // 좌우 차선 분산 폭 (★ 트랙 폭 절반 정도로 조정)
    public float SpeedVariance = 0.04f; // AI 간 최고속 편차 (0~1, 클수록 속도 차이 큼)
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

    public float NormalGripMultiplier = 1.0f; // 기본 접지 배수 (GripStiffness와 곱해져 적용)
    private float steerInput;

    void Start()
    {
        rigidBody = GetComponent<Rigidbody>();
        audioSource = GetComponent<AudioSource>();

        rigidBody.centerOfMass = new Vector3(rigidBody.centerOfMass.x, -0.8f, rigidBody.centerOfMass.z);
        // 고속에서 지형을 뚫고 빠지는 현상 방지 + 움직임 보간
        rigidBody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        rigidBody.interpolation = RigidbodyInterpolation.Interpolate;

        // 모든 바퀴에 서브스텝 적용 (고속 물리 정확도)
        FrontLeftWheel.ConfigureVehicleSubsteps(5f, 12, 12);
        FrontRightWheel.ConfigureVehicleSubsteps(5f, 12, 12);
        RearLeftWheel.ConfigureVehicleSubsteps(5f, 12, 12);
        RearRightWheel.ConfigureVehicleSubsteps(5f, 12, 12);

        // 모든 차가 같은 공유 경로를 쓰도록 GameManager에서 가져옴 (없으면 자기 targetPositions 사용)
        Transform[] wp = GameManager.Instance.GetWaypoints();
        if (wp == null || wp.Length == 0) wp = targetPositions;
        lapTracker = new LapTracker(wp, totalLaps);
        if (TorqueCurve == null || TorqueCurve.length == 0) TorqueCurve = EngineModel.DefaultTorqueCurve();

        // 차마다 다른 차선·속도 → 같은 라인에 줄지어 붙는 현상 방지
        laneOffset = Random.Range(-LaneSpread, LaneSpread);
        speedMultiplier = Random.Range(1f - SpeedVariance, 1f);

        GameManager.Instance.AddCar(gameObject);
    }

    // IRaceCar 구현 (등수 계산용)
    public int CurrentLap => lapTracker != null ? lapTracker.CurrentLap : 0;
    public float RaceProgress => lapTracker != null ? lapTracker.TotalProgress : 0f;
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

        // 랩/진행도 갱신 (경로 투영 방식 → 어떤 라인으로 달려도 정확)
        lapTracker.Tick(transform.position, GameManager.Instance != null ? GameManager.Instance.gameTime : Time.time);

        float currentSpeed = rigidBody.linearVelocity.magnitude * 3.6f;

        // 트랙에 끼이거나 코스를 크게 벗어나면 경로 위로 복귀
        if (CheckStuckAndRecover(currentSpeed)) return;

        // 다운포스 — 속도가 붙을수록 차를 아래로 눌러 접지력 확보
        rigidBody.AddForce(-transform.up * DownforceCoefficient * currentSpeed);

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

        // 속도 감응 조향 — 빠를수록 조향각을 줄여 고속에서 안정적으로
        float speedFactor = Mathf.Clamp01(currentSpeed / SteerFalloffSpeed);
        float effectiveSteerAngle = Mathf.Lerp(SteerAngle, HighSpeedSteerAngle, speedFactor);
        FrontLeftWheel.steerAngle = effectiveSteerAngle * steerInput;
        FrontRightWheel.steerAngle = effectiveSteerAngle * steerInput;

        // ===== RPM / 자동 변속 / 기어별 최고속 =====
        float averageWheelRPM = (FrontLeftWheel.rpm + FrontRightWheel.rpm + RearLeftWheel.rpm + RearRightWheel.rpm) / 4;
        EngineRPM = averageWheelRPM * GearRatio[CurrentGear];

        // 러버밴딩: 플레이어보다 앞서면 살짝 느리게, 뒤처지면 살짝 빠르게 → 항상 붙어서 달리는 레이스
        float carMaxSpeed = MaxSpeed * speedMultiplier * GetRubberBandFactor();
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
            // 즉시 잘라내지 않고 서서히 줄여 자연스럽게 감속
            float newSpeed = Mathf.MoveTowards(currentSpeed, gearMaxSpeed, SpeedDecayRate * Time.fixedDeltaTime);
            rigidBody.linearVelocity = rigidBody.linearVelocity.normalized * (newSpeed / 3.6f);
            currentSpeed = newSpeed;
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

        torque = ApplyTractionControl(torque); // 헛도는 만큼 토크를 깎아 접지력 확보
        FrontLeftWheel.motorTorque = torque;
        FrontRightWheel.motorTorque = torque;
        RearLeftWheel.motorTorque = torque;
        RearRightWheel.motorTorque = torque;

        // ===== 브레이크 적용 =====
        // 레이싱 AI는 접지를 유지하는 편이 빠르고 안정적이므로 핸드브레이크 드리프트를 쓰지 않는다.
        // (예전에는 코너마다 뒷바퀴를 미끄러뜨려 코스를 이탈하고 트랙에 끼이는 원인이 되었다.)
        SetGrip(NormalGripMultiplier * GripStiffness, NormalGripMultiplier * GripStiffness);
        FrontLeftWheel.brakeTorque = brake;
        FrontRightWheel.brakeTorque = brake;
        RearLeftWheel.brakeTorque = brake;
        RearRightWheel.brakeTorque = brake;

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

    // 트랙에 끼이거나(반쯤 파묻힘) 코스를 크게 벗어났을 때 경로 위로 되돌림.
    // 복구했으면 true를 반환해 이번 프레임의 주행 처리를 건너뛴다.
    bool CheckStuckAndRecover(float currentSpeed)
    {
        // 출발 직후에는 정지 상태가 정상이므로 판정하지 않음
        aliveTime += Time.fixedDeltaTime;
        if (aliveTime < StartGracePeriod) return false;

        bool tooFarOff = lapTracker.DistanceFromPath > MaxPathDistance;

        if (currentSpeed < StuckSpeedThreshold || tooFarOff)
        {
            stuckTimer += Time.fixedDeltaTime;
        }
        else
        {
            stuckTimer = 0f;
        }

        if (stuckTimer < StuckTimeLimit) return false;

        // 다음 웨이포인트 위치로, 진행 방향을 보게 복귀 (살짝 띄워서 지면에 박히지 않게)
        transform.position = lapTracker.GetRespawnPosition() + Vector3.up * 1.5f;
        transform.rotation = lapTracker.GetRespawnRotation();
        rigidBody.linearVelocity = Vector3.zero;
        rigidBody.angularVelocity = Vector3.zero;
        lapTracker.ResyncAfterTeleport(); // 순간이동을 결승선 통과로 오인하지 않도록
        CurrentGear = 0;
        stuckTimer = 0f;
        return true;
    }

    // 플레이어와의 진행도 차이에 따라 최고속을 ±RubberBandStrength 만큼 보정
    float GetRubberBandFactor()
    {
        if (!RubberBanding || GameManager.Instance == null) return 1f;

        float playerProgress = GameManager.Instance.PlayerProgress;
        if (playerProgress < 0f) return 1f; // 플레이어 정보 없음

        // 진행도 차이(랩 단위) — 양수면 AI가 앞섬
        float diff = RaceProgress - playerProgress;
        float t = Mathf.Clamp(diff / 0.25f, -1f, 1f); // 4분의 1바퀴 차이에서 최대 보정
        return 1f - t * RubberBandStrength;
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

    // 앞/뒤 그립을 따로 설정 (드리프트 시 뒤만 낮추기 위함)
    void SetGrip(float frontMultiplier, float rearMultiplier)
    {
        ApplyGrip(FrontLeftWheel, frontMultiplier);
        ApplyGrip(FrontRightWheel, frontMultiplier);
        ApplyGrip(RearLeftWheel, rearMultiplier);
        ApplyGrip(RearRightWheel, rearMultiplier);
    }

    void ApplyGrip(WheelCollider wheel, float multiplier)
    {
        WheelFrictionCurve forwardFriction = wheel.forwardFriction;
        WheelFrictionCurve sidewaysFriction = wheel.sidewaysFriction;

        forwardFriction.stiffness = multiplier;
        sidewaysFriction.stiffness = multiplier;

        wheel.forwardFriction = forwardFriction;
        wheel.sidewaysFriction = sidewaysFriction;
    }

    // 트랙션 컨트롤 — 바퀴가 헛도는 정도(슬립)를 보고 토크를 줄여 접지력을 회복
    float ApplyTractionControl(float torque)
    {
        if (!TractionControl || Mathf.Approximately(torque, 0f)) return torque;

        float maxSlip = 0f;
        WheelHit hit;
        if (FrontLeftWheel.GetGroundHit(out hit)) maxSlip = Mathf.Max(maxSlip, Mathf.Abs(hit.forwardSlip));
        if (FrontRightWheel.GetGroundHit(out hit)) maxSlip = Mathf.Max(maxSlip, Mathf.Abs(hit.forwardSlip));
        if (RearLeftWheel.GetGroundHit(out hit)) maxSlip = Mathf.Max(maxSlip, Mathf.Abs(hit.forwardSlip));
        if (RearRightWheel.GetGroundHit(out hit)) maxSlip = Mathf.Max(maxSlip, Mathf.Abs(hit.forwardSlip));

        if (maxSlip > TractionSlipLimit)
        {
            float excess = (maxSlip - TractionSlipLimit) / TractionSlipLimit;
            torque *= Mathf.Clamp01(1f - excess); // 슬립이 클수록 토크를 크게 감소
        }
        return torque;
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
        AntiRollAxle(FrontLeftWheel, FrontRightWheel);
        AntiRollAxle(RearLeftWheel, RearRightWheel);
    }

    // 한 차축의 좌우 서스펜션 압축 차이만큼 반대 방향 힘을 주어 차체 롤을 억제.
    // 양쪽 바퀴가 모두 접지했을 때만 적용한다 — 한쪽이 떠 있을 때 적용하면
    // 큰 힘이 한쪽으로 몰려 차가 지면으로 박히거나 튕겨나갈 수 있다.
    void AntiRollAxle(WheelCollider left, WheelCollider right)
    {
        WheelHit hit;
        bool groundedL = left.GetGroundHit(out hit);
        float travelL = groundedL
            ? Mathf.Clamp01((-left.transform.InverseTransformPoint(hit.point).y - left.radius) / left.suspensionDistance)
            : 1.0f;

        bool groundedR = right.GetGroundHit(out hit);
        float travelR = groundedR
            ? Mathf.Clamp01((-right.transform.InverseTransformPoint(hit.point).y - right.radius) / right.suspensionDistance)
            : 1.0f;

        if (!groundedL || !groundedR) return; // 한쪽이라도 떠 있으면 적용하지 않음

        float force = (travelL - travelR) * AntiRollForce;
        rigidBody.AddForceAtPosition(left.transform.up * -force, left.transform.position);
        rigidBody.AddForceAtPosition(right.transform.up * force, right.transform.position);
    }
}