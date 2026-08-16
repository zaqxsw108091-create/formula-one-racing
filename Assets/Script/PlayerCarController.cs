using UnityEngine;
using System.Collections;

public class PlayerCarController : MonoBehaviour, IRaceCar
{
    public WheelCollider FrontLeftWheel;
    public WheelCollider FrontRightWheel;
    public WheelCollider RearLeftWheel;
    public WheelCollider RearRightWheel;

    public float[] GearRatio = { 2.66f, 1.91f, 1.39f, 1.00f, 0.77f, 0.63f }; // 6단 기어비
    public int CurrentGear = 0;

    public float PeakTorque = 600.0f; // 피크 엔진 토크 (N·m) — 가속 세기 튜닝
    public AnimationCurve TorqueCurve; // RPM-토크 곡선 (비우면 기본 곡선 사용)
    public float FinalDriveRatio = 2.0f; // 최종 감속비 (★ 0→100km/h 가속 시간 튜닝용: 클수록 빨라짐)

    // 트랙션 컨트롤 — 구동 바퀴가 헛돌면 토크를 줄여 접지력을 회복
    public bool TractionControl = true;
    public float TractionSlipLimit = 0.35f; // 허용 슬립량 (작을수록 개입이 빠름)
    public float MaxEngineRPM = 10000.0f;
    public float MinEngineRPM = 1000.0f;
    public float RPMIncreaseRate = 500.0f; // RPM 증가 속도
    public float RPMDecreaseRate = 1000.0f; // RPM 감소 속도


    private float EngineRPM = 0.0f;
    private float EngineTorque = 0.0f;

    private Rigidbody rigidBody;
    private AudioSource audioSource;

     
    public float SteerAngle = 30.0f; // 저속에서의 최대 조향 각도
    public float MaxSpeed = 300.0f; // 최대 속도 (km/h)

    // 속도 감응 조향 — 빠를수록 조향각을 줄여 고속 안정성 확보
    public float HighSpeedSteerAngle = 8.0f; // 고속에서의 조향 각도
    public float SteerFalloffSpeed = 180.0f; // 이 속도(km/h)에서 조향각이 최소가 됨

    // 드리프트 관련 변수 (뒷바퀴 그립만 낮춤 → 핸들은 살아있는 채로 미끄러짐)
    public float DriftGripMultiplier = 0.5f;
    public float NormalGripMultiplier = 1.0f;
    public float DriftHandbrakeForce = 5000.0f;

    // 브레이크 관련 변수 
    public float BrakeForce = 10000.0f;

    // 접지력 / 다운포스 — 고속 코너링 안정성
    public float GripStiffness = 2.2f;        // 타이어 접지력 배수 (클수록 덜 미끄러짐)
    public float DownforceCoefficient = 25.0f; // 속도에 비례해 차를 눌러주는 힘

    // 감속 특성 — 최고속을 넘었을 때 뚝 끊기지 않고 서서히 줄도록
    public float SpeedDecayRate = 25.0f;      // 초당 감속량(km/h)

    // 부스터 변수 (Left Ctrl)
    public float BoostForce = 12000.0f;       // 부스터 추진력
    public float BoostDuration = 3.0f;        // 최대 지속 시간(초)
    public float BoostRechargeRate = 0.5f;    // 미사용 시 초당 충전량
    public float BoostExtraSpeed = 60.0f;     // 부스터 중 최고속 추가 허용(km/h)
    public float BoostFadeRate = 12.0f;       // 부스터 종료 후 추가속도가 사라지는 속도(km/h per sec)
    private float boostRemaining;             // 남은 부스터(초)
    private bool isBoosting = false;          // 부스터 사용 중 여부
    private float boostSpeedBonus = 0.0f;     // 현재 적용 중인 부스터 추가 최고속 (서서히 감소)

    // 0→100km/h 가속 측정 (튜닝 보조)
    private float accelTimer = 0.0f;
    private bool accelMeasuring = false;
    private float last0to100 = -1.0f;

    // 안정적인 승차감 관련 변수
    public float AntiRollForce = 5000.0f;

    // 랩/순위 관련 (★ Waypoints에 AI와 동일한 트랙 웨이포인트를 순서대로 할당)
    public Transform[] Waypoints;            // 트랙 웨이포인트 (0번 ≈ 출발/결승선)
    public int TotalLaps = 5;                // 총 랩 수
    private LapTracker lapTracker;           // 랩/진행도 추적기

    public AudioClip driftSound; // 드리프트 소리
    private AudioSource driftAudioSource; // 드리프트 소리 재생 컴포넌트
    private bool isDrifting = false; // 드리프트 중 여부
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

        driftAudioSource = gameObject.AddComponent<AudioSource>();
        driftAudioSource.clip = driftSound;
        driftAudioSource.loop = true;

        boostRemaining = BoostDuration;
        if (TorqueCurve == null || TorqueCurve.length == 0) TorqueCurve = EngineModel.DefaultTorqueCurve();
        // Waypoints를 직접 지정하지 않았으면 GameManager에서 공유 웨이포인트를 자동으로 가져옴
        Transform[] wp = (Waypoints != null && Waypoints.Length > 0) ? Waypoints : GameManager.Instance.GetWaypoints();
        lapTracker = new LapTracker(wp, TotalLaps);
        GameManager.Instance.AddCar(gameObject);
    }

    // IRaceCar 구현 (등수 계산용)
    public int CurrentLap => lapTracker != null ? lapTracker.CurrentLap : 0;
    public float RaceProgress => lapTracker != null ? lapTracker.TotalProgress : 0f;
    public bool Finished => lapTracker != null && lapTracker.Finished;
    public float FinishTime => lapTracker != null ? lapTracker.FinishTime : -1f;

    void Update()
    {
        // 기어 변속 입력 (★ GetKeyDown은 반드시 Update에서 — FixedUpdate에서는 입력이 씹힘)
        // (1) 순차 변속: E = 업시프트, Q = 다운시프트
        if (Input.GetKeyDown(KeyCode.E)) CurrentGear++;
        if (Input.GetKeyDown(KeyCode.Q)) CurrentGear--;
        // (2) 직접 선택: 숫자키 1~6
        if (Input.GetKeyDown(KeyCode.Alpha1)) CurrentGear = 0;
        if (Input.GetKeyDown(KeyCode.Alpha2)) CurrentGear = 1;
        if (Input.GetKeyDown(KeyCode.Alpha3)) CurrentGear = 2;
        if (Input.GetKeyDown(KeyCode.Alpha4)) CurrentGear = 3;
        if (Input.GetKeyDown(KeyCode.Alpha5)) CurrentGear = 4;
        if (Input.GetKeyDown(KeyCode.Alpha6)) CurrentGear = 5;
        CurrentGear = Mathf.Clamp(CurrentGear, 0, GearRatio.Length - 1);

        // 코스를 벗어나거나 뒤집혔을 때 트랙으로 복귀
        if (Input.GetKeyDown(KeyCode.R)) RespawnOnTrack();
    }

    // 트랙 위(다음 웨이포인트)로 복귀 — 진행 방향을 보게 세운다
    void RespawnOnTrack()
    {
        if (lapTracker == null || !lapTracker.HasWaypoints) return;

        transform.position = lapTracker.GetRespawnPosition() + Vector3.up * 1.5f;
        transform.rotation = lapTracker.GetRespawnRotation();
        rigidBody.linearVelocity = Vector3.zero;
        rigidBody.angularVelocity = Vector3.zero;
        lapTracker.ResyncAfterTeleport(); // 순간이동을 결승선 통과로 오인하지 않도록
        CurrentGear = 0;
    }

    void FixedUpdate()
    {
        // 랩/진행도 갱신 (웨이포인트 순서 통과 → 한 바퀴 돌면 랩 +1)
        if (lapTracker != null)
        {
            lapTracker.Tick(transform.position, GameManager.Instance != null ? GameManager.Instance.gameTime : Time.time);
        }

        // 속도 제한 (km/h)
        // 기어별 최고속: 기어비에 반비례 → 최고 기어가 MaxSpeed, 낮은 기어는 더 낮음.
        float currentSpeed = rigidBody.linearVelocity.magnitude * 3.6f;
        float topGearRatio = GearRatio[GearRatio.Length - 1];
        float gearMaxSpeed = MaxSpeed * (topGearRatio / GearRatio[CurrentGear]);

        // 부스터 추가 최고속은 부스터가 끝나면 '서서히' 사라짐 (속도가 뚝 끊기지 않게)
        if (isBoosting) boostSpeedBonus = BoostExtraSpeed;
        else boostSpeedBonus = Mathf.MoveTowards(boostSpeedBonus, 0f, BoostFadeRate * Time.fixedDeltaTime);

        float effectiveMaxSpeed = gearMaxSpeed + boostSpeedBonus;
        if (currentSpeed > effectiveMaxSpeed)
        {
            // 즉시 잘라내지 않고 서서히 줄여 자연스럽게 감속
            float newSpeed = Mathf.MoveTowards(currentSpeed, effectiveMaxSpeed, SpeedDecayRate * Time.fixedDeltaTime);
            rigidBody.linearVelocity = rigidBody.linearVelocity.normalized * (newSpeed / 3.6f);
            currentSpeed = newSpeed;
        }

        // 다운포스 — 속도가 붙을수록 차를 아래로 눌러 접지력 확보 (고속 코너 안정)
        rigidBody.AddForce(-transform.up * DownforceCoefficient * currentSpeed);

        // 0→100km/h 가속 시간 측정 (튜닝 보조)
        if (currentSpeed < 1.0f && Input.GetAxis("Vertical") > 0.1f && !accelMeasuring)
        {
            accelMeasuring = true;
            accelTimer = 0.0f;
        }
        if (accelMeasuring)
        {
            accelTimer += Time.deltaTime;
            if (currentSpeed >= 100.0f)
            {
                last0to100 = accelTimer;
                accelMeasuring = false;
            }
            else if (Input.GetAxis("Vertical") <= 0.1f)
            {
                accelMeasuring = false; // 가속을 멈추면 측정 취소
            }
        }

        // 엔진 RPM 계산
        float averageWheelRPM = (FrontLeftWheel.rpm + FrontRightWheel.rpm + RearLeftWheel.rpm + RearRightWheel.rpm) / 4;
        EngineRPM = averageWheelRPM * GearRatio[CurrentGear];

        // 엔진 RPM 증가/감소
        if (Input.GetAxis("Vertical") > 0)
        {
            EngineRPM += RPMIncreaseRate * Time.deltaTime;
        }
        else
        {
            EngineRPM -= RPMDecreaseRate * Time.deltaTime;
        }
        EngineRPM = Mathf.Clamp(EngineRPM, MinEngineRPM, MaxEngineRPM);

        // 엔진 토크 계산 (실제 RPM-토크 곡선 반영: 중회전 피크, 저/고회전 하락)
        EngineTorque = EngineModel.Torque(TorqueCurve, PeakTorque, EngineRPM, MinEngineRPM, MaxEngineRPM);

        // (기어 변속 입력은 Update()에서 처리 — GetKeyDown은 FixedUpdate에서 누락될 수 있음)

        // 바퀴 구동 — 바퀴 토크 = 엔진 토크 × 기어비 × 최종 감속비
        // (낮은 기어일수록 기어비가 커서 가속력↑, 높은 기어일수록 최고속↑)
        float torque = EngineTorque * GearRatio[CurrentGear] * FinalDriveRatio * Input.GetAxis("Vertical");
        torque = ApplyTractionControl(torque); // 헛도는 만큼 토크를 깎아 접지력 확보
        FrontLeftWheel.motorTorque = torque;
        FrontRightWheel.motorTorque = torque;
        RearLeftWheel.motorTorque = torque;
        RearRightWheel.motorTorque = torque;

        // 조향 — 속도가 붙을수록 조향각을 줄여 고속에서 안정적으로
        float steerInput = Input.GetAxis("Horizontal");
        float speedFactor = Mathf.Clamp01(currentSpeed / SteerFalloffSpeed);
        float effectiveSteerAngle = Mathf.Lerp(SteerAngle, HighSpeedSteerAngle, speedFactor);
        FrontLeftWheel.steerAngle = effectiveSteerAngle * steerInput;
        FrontRightWheel.steerAngle = effectiveSteerAngle * steerInput;

        // 드리프트 — 앞바퀴 그립은 그대로 두고 뒷바퀴만 낮춤 (핸들이 살아있는 채로 뒤가 미끄러짐)
        // GripStiffness를 곱해 평상시 접지력을 높임 → 코너에서 덜 미끄러짐
        if (Input.GetKey(KeyCode.LeftShift))
        {
            SetGrip(NormalGripMultiplier * GripStiffness, DriftGripMultiplier * GripStiffness);
            RearLeftWheel.brakeTorque = DriftHandbrakeForce;
            RearRightWheel.brakeTorque = DriftHandbrakeForce;
        }
        else
        {
            SetGrip(NormalGripMultiplier * GripStiffness, NormalGripMultiplier * GripStiffness);
            RearLeftWheel.brakeTorque = 0;
            RearRightWheel.brakeTorque = 0;
        }


        // 브레이크
        if (Input.GetKey(KeyCode.Space))
        {
            FrontLeftWheel.brakeTorque = BrakeForce;
            FrontRightWheel.brakeTorque = BrakeForce;
            RearLeftWheel.brakeTorque = BrakeForce;
            RearRightWheel.brakeTorque = BrakeForce;
        }
        else
        {
            FrontLeftWheel.brakeTorque = 0;
            FrontRightWheel.brakeTorque = 0;
            RearLeftWheel.brakeTorque = 0;
            RearRightWheel.brakeTorque = 0;
        }

        // 부스터 (Left Ctrl) — 강력한 전방 추진, 사용 시 소모 / 미사용 시 충전
        isBoosting = Input.GetKey(KeyCode.LeftControl) && boostRemaining > 0f;
        if (isBoosting)
        {
            rigidBody.AddForce(transform.forward * BoostForce);
            boostRemaining = Mathf.Max(0f, boostRemaining - Time.deltaTime);
        }
        else
        {
            boostRemaining = Mathf.Min(BoostDuration, boostRemaining + BoostRechargeRate * Time.deltaTime);
        }

        AntiRoll();
        // 엔진 소리
        audioSource.pitch = Mathf.Clamp(EngineRPM / MaxEngineRPM + 0.5f, 0.5f, 2.0f);
        if (!audioSource.isPlaying && currentSpeed > 1)
        {
            audioSource.Play();
        }
        else if (audioSource.isPlaying && currentSpeed <= 1)
        {
            audioSource.Stop();
        }

        CheckDrift();
        void CheckDrift()
        {
            // 드리프트 (currentSpeed, steerInput은 FixedUpdate의 값을 그대로 사용)
            if (Input.GetKey(KeyCode.LeftShift) && currentSpeed > 30.0f && Mathf.Abs(steerInput) > 0.5f)
            {
                if (!isDrifting)
                {
                    driftAudioSource.Play();
                    isDrifting = true;
                }
            }
            else
            {
                if (isDrifting)
                {
                    driftAudioSource.Stop();
                    isDrifting = false;
                }
            }
        }

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
    void OnGUI()
    {

        // 실시간 플레이어 차량 시스템
        float currentSpeed = rigidBody.linearVelocity.magnitude * 3.6f;
        GUI.Label(new Rect(10, 10, 200, 20), "Speed: " + currentSpeed.ToString("F1") + " km/h");
        float gearMaxSpeed = MaxSpeed * (GearRatio[GearRatio.Length - 1] / GearRatio[CurrentGear]);
        GUI.Label(new Rect(10, 90, 250, 20), "Gear: " + (CurrentGear + 1) + " (max " + gearMaxSpeed.ToString("F0") + "km/h, E/Q·1~6)");
        GUI.Label(new Rect(10, 110, 220, 20), "Boost: " + (boostRemaining / BoostDuration * 100f).ToString("F0") + "%  (Left Ctrl)");
        if (last0to100 > 0f)
        {
            GUI.Label(new Rect(10, 130, 220, 20), "0→100: " + last0to100.ToString("F2") + " s");
        }
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