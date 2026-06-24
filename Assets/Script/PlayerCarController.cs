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
    public float FinalDriveRatio = 3.5f; // 최종 감속비 (★ 0→100km/h 가속 시간 튜닝용: 클수록 빨라짐)
    public float MaxEngineRPM = 10000.0f;
    public float MinEngineRPM = 1000.0f;
    public float RPMIncreaseRate = 500.0f; // RPM 증가 속도
    public float RPMDecreaseRate = 1000.0f; // RPM 감소 속도


    private float EngineRPM = 0.0f;
    private float EngineTorque = 0.0f;

    private Rigidbody rigidBody;
    private AudioSource audioSource;

     
    public float SteerAngle = 30.0f; // 최대 조향 각도
    public float MaxSpeed = 300.0f; // 최대 속도 (km/h)

    // 드리프트 관련 변수
    public float DriftGripMultiplier = 0.5f;
    public float NormalGripMultiplier = 1.0f;
    public float DriftHandbrakeForce = 5000.0f;

    // 브레이크 관련 변수 
    public float BrakeForce = 10000.0f;

    // 부스터 변수 (Left Ctrl)
    public float BoostForce = 12000.0f;       // 부스터 추진력
    public float BoostDuration = 3.0f;        // 최대 지속 시간(초)
    public float BoostRechargeRate = 0.5f;    // 미사용 시 초당 충전량
    public float BoostExtraSpeed = 60.0f;     // 부스터 중 최고속 추가 허용(km/h)
    private float boostRemaining;             // 남은 부스터(초)
    private bool isBoosting = false;          // 부스터 사용 중 여부

    // 0→100km/h 가속 측정 (튜닝 보조)
    private float accelTimer = 0.0f;
    private bool accelMeasuring = false;
    private float last0to100 = -1.0f;

    // 안정적인 승차감 관련 변수
    public float AntiRollForce = 5000.0f;

    // 랩/순위 관련 (★ Waypoints에 AI와 동일한 트랙 웨이포인트를 순서대로 할당)
    public Transform[] Waypoints;            // 트랙 웨이포인트 (0번 ≈ 출발/결승선)
    public int TotalLaps = 5;                // 총 랩 수
    public float WaypointThreshold = 25.0f;  // 웨이포인트 통과 인정 거리 (★ 트랙 크기에 맞게 조정)
    private LapTracker lapTracker;           // 랩/진행도 추적기

    public AudioClip driftSound; // 드리프트 소리
    private AudioSource driftAudioSource; // 드리프트 소리 재생 컴포넌트
    private bool isDrifting = false; // 드리프트 중 여부
    void Start()
    {
        rigidBody = GetComponent<Rigidbody>();
        audioSource = GetComponent<AudioSource>();

        rigidBody.centerOfMass = new Vector3(rigidBody.centerOfMass.x, -0.8f, rigidBody.centerOfMass.z);

        FrontLeftWheel.ConfigureVehicleSubsteps(5f, 10, 10);
        RearLeftWheel.ConfigureVehicleSubsteps(5f, 10, 10);

        driftAudioSource = gameObject.AddComponent<AudioSource>();
        driftAudioSource.clip = driftSound;
        driftAudioSource.loop = true;

        boostRemaining = BoostDuration;
        if (TorqueCurve == null || TorqueCurve.length == 0) TorqueCurve = EngineModel.DefaultTorqueCurve();
        // Waypoints를 직접 지정하지 않았으면 GameManager에서 공유 웨이포인트를 자동으로 가져옴
        Transform[] wp = (Waypoints != null && Waypoints.Length > 0) ? Waypoints : GameManager.Instance.GetWaypoints();
        lapTracker = new LapTracker(wp, TotalLaps, WaypointThreshold);
        GameManager.Instance.AddCar(gameObject);
    }

    // IRaceCar 구현 (등수 계산용)
    public int CurrentLap => lapTracker != null ? lapTracker.CurrentLap : 0;
    public float RaceProgress => lapTracker != null ? lapTracker.Progress(transform.position) : 0f;
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
        // (더 빨리 가려면 업시프트 필요) + 부스터 중에는 BoostExtraSpeed 만큼 추가 허용
        float currentSpeed = rigidBody.linearVelocity.magnitude * 3.6f;
        float topGearRatio = GearRatio[GearRatio.Length - 1];
        float gearMaxSpeed = MaxSpeed * (topGearRatio / GearRatio[CurrentGear]);
        float effectiveMaxSpeed = gearMaxSpeed + (isBoosting ? BoostExtraSpeed : 0f);
        if (currentSpeed > effectiveMaxSpeed)
        {
            rigidBody.linearVelocity = rigidBody.linearVelocity.normalized * (effectiveMaxSpeed / 3.6f);
        }

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
        FrontLeftWheel.motorTorque = torque;
        FrontRightWheel.motorTorque = torque;
        RearLeftWheel.motorTorque = torque;
        RearRightWheel.motorTorque = torque;

        // 조향
        float steerInput = Input.GetAxis("Horizontal");
        FrontLeftWheel.steerAngle = SteerAngle * steerInput;
        FrontRightWheel.steerAngle = SteerAngle * steerInput;

        // 드리프트
        if (Input.GetKey(KeyCode.LeftShift))
        {
            SetGrip(DriftGripMultiplier);
            RearLeftWheel.brakeTorque = DriftHandbrakeForce;
            RearRightWheel.brakeTorque = DriftHandbrakeForce;
        }
        else
        {
            SetGrip(NormalGripMultiplier);
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