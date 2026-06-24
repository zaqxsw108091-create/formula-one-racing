using UnityEngine;
using UnityEngine.SceneManagement;
using System.Collections.Generic;
using System.Linq; // Linq 사용을 위한 추가

public class GameManager : MonoBehaviour
{
    public static GameManager Instance;

    public enum GameState { Start, Playing, Paused, Finished }
    public GameState currentState = GameState.Start;

    public float gameTime = 0f;
    public int totalLaps = 5;

    public List<GameObject> cars = new List<GameObject>();
    public Transform finishLine; // 현재 미사용(호환용). 랩 판정은 각 차의 웨이포인트 통과로 처리.

    // 공유 트랙 웨이포인트. 비워두면 AI 차의 targetPositions 중 가장 많은 것을 자동으로 사용.
    public Transform[] waypoints;

    private List<GameObject> ranking = new List<GameObject>();      // 실시간 등수
    private List<GameObject> finalRanking = new List<GameObject>(); // 최종 순위 스냅샷
    private GameObject playerCar;                                   // 플레이어 차 캐시

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }
        else
        {
            Destroy(gameObject);
        }
    }

    void Start()
    {
        currentState = GameState.Playing;
    }

    void Update()
    {
        if (currentState == GameState.Playing)
        {
            gameTime += Time.deltaTime;
            UpdateRanking();      // 실시간 순위 갱신
            CheckRaceFinished();  // 플레이어 완주 시 레이스 종료
        }

        if (Input.GetKeyDown(KeyCode.Escape))
        {
            TogglePause();
        }
    }

    public void AddCar(GameObject car)
    {
        cars.Add(car);
        if (playerCar == null && car.GetComponent<PlayerCarController>() != null)
        {
            playerCar = car;
        }
    }

    private IRaceCar GetRaceCar(GameObject car)
    {
        return car != null ? car.GetComponent<IRaceCar>() : null;
    }

    // 공유 웨이포인트 반환 (우선순위):
    //   1) 인스펙터의 waypoints 필드  2) 씬의 WaypointPath  3) AI 차들의 targetPositions
    public Transform[] GetWaypoints()
    {
        if (waypoints != null && waypoints.Length > 0) return waypoints;

        var path = FindFirstObjectByType<WaypointPath>();
        if (path != null)
        {
            Transform[] p = path.GetWaypoints();
            if (p != null && p.Length > 0) return p;
        }

        Transform[] best = null;
        foreach (var ai in FindObjectsByType<ComputerCarController>(FindObjectsSortMode.None))
        {
            if (ai.targetPositions != null && (best == null || ai.targetPositions.Length > best.Length))
            {
                best = ai.targetPositions;
            }
        }
        return best;
    }

    // 정렬 기준: 완주 차량 우선(완주 시간 빠른 순) → 미완주는 진행도 높은 순
    private int CompareCars(GameObject x, GameObject y)
    {
        IRaceCar a = GetRaceCar(x);
        IRaceCar b = GetRaceCar(y);
        if (a.Finished && b.Finished) return a.FinishTime.CompareTo(b.FinishTime);
        if (a.Finished) return -1;
        if (b.Finished) return 1;
        return b.RaceProgress.CompareTo(a.RaceProgress);
    }

    void UpdateRanking()
    {
        ranking = cars.Where(c => c != null && GetRaceCar(c) != null).ToList();
        ranking.Sort(CompareCars);
    }

    // 플레이어가 5바퀴 완주하면 레이스 종료 후 최종 순위 확정
    void CheckRaceFinished()
    {
        IRaceCar player = GetRaceCar(playerCar);
        if (player != null && player.Finished)
        {
            currentState = GameState.Finished;
            finalRanking = new List<GameObject>(ranking);
            Debug.Log("Race Finished!");
        }
    }

    public void TogglePause()
    {
        if (currentState == GameState.Playing)
        {
            currentState = GameState.Paused;
            Time.timeScale = 0f;
        }
        else if (currentState == GameState.Paused)
        {
            currentState = GameState.Playing;
            Time.timeScale = 1f;
        }
    }

    public void RestartGame()
    {
        Time.timeScale = 1f;
        SceneManager.LoadScene(SceneManager.GetActiveScene().name);
    }

    public void EndGame()
    {
        Application.Quit();
    }

    // GUI 표시
    void OnGUI()
    {
        if (currentState == GameState.Playing || currentState == GameState.Paused)
        {
            // 화면 오른쪽 열에 표시 (플레이어 차의 좌측 HUD와 겹치지 않도록)
            float x = Screen.width - 240;
            IRaceCar player = GetRaceCar(playerCar);
            int playerLap = player != null ? Mathf.Clamp(player.CurrentLap + 1, 1, totalLaps) : 1;
            GUI.Label(new Rect(x, 10, 230, 20), "Lap: " + playerLap + " / " + totalLaps);
            GUI.Label(new Rect(x, 30, 230, 20), "Time: " + gameTime.ToString("F2"));

            // 실시간 등수
            GUI.Label(new Rect(x, 55, 230, 20), "Ranking:");
            for (int i = 0; i < ranking.Count; i++)
            {
                IRaceCar rc = GetRaceCar(ranking[i]);
                int lap = Mathf.Clamp(rc.CurrentLap + 1, 1, totalLaps);
                GUI.Label(new Rect(x, 75 + i * 20, 230, 20), (i + 1) + ". " + ranking[i].name + " (Lap " + lap + "/" + totalLaps + ")");
            }
        }
        else if (currentState == GameState.Finished)
        {
            GUI.Label(new Rect(Screen.width / 2 - 100, Screen.height / 2 - 120, 220, 20), "=== Final Ranking ===");
            for (int i = 0; i < finalRanking.Count; i++)
            {
                IRaceCar rc = GetRaceCar(finalRanking[i]);
                string result = rc.Finished ? rc.FinishTime.ToString("F2") + "s" : "Lap " + Mathf.Clamp(rc.CurrentLap + 1, 1, totalLaps);
                GUI.Label(new Rect(Screen.width / 2 - 100, Screen.height / 2 - 100 + i * 20, 320, 20),
                          (i + 1) + ". " + finalRanking[i].name + " - " + result);
            }
        }
    }
}
