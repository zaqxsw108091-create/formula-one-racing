using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI; // UI 요소 사용을 위한 추가

public class GameMenuManager : MonoBehaviour
{
    public GameObject mainMenuUI;
    public GameObject gameUI;
    public GameObject exitConfirmationUI;
    public GameObject gameOverUI;

    public Button startGameButton; // 게임 시작 버튼
    public Button exitGameButton; // 게임 종료 버튼
    public Button confirmExitButton; // 종료 확인 버튼
    public Button cancelExitButton; // 종료 취소 버튼

    public Text gameOverText; // 게임 종료 텍스트

    private bool isExiting = false;

    void Start()
    {
        mainMenuUI.SetActive(true);
        gameUI.SetActive(false);
        exitConfirmationUI.SetActive(false);
        gameOverUI.SetActive(false);
        Time.timeScale = 0f;

        // 버튼 클릭 이벤트 연결
        startGameButton.onClick.AddListener(StartGame);
        exitGameButton.onClick.AddListener(ExitGame);
        confirmExitButton.onClick.AddListener(ConfirmExit);
        cancelExitButton.onClick.AddListener(CancelExit);
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            if (SceneManager.GetActiveScene().name != "MainMenuScene")
            {
                if (isExiting)
                {
                    mainMenuUI.SetActive(true);
                    gameUI.SetActive(false);
                    exitConfirmationUI.SetActive(false);
                    Time.timeScale = 0f;
                    isExiting = false;
                }
                else
                {
                    exitConfirmationUI.SetActive(true);
                    isExiting = true;
                }
            }
        }
    }

    public void StartGame()
    {
        mainMenuUI.SetActive(false);
        gameUI.SetActive(true);
        Time.timeScale = 1f;
        SceneManager.LoadScene("GameScene");
    }

    public void ConfirmExit()
    {
        mainMenuUI.SetActive(true);
        gameUI.SetActive(false);
        exitConfirmationUI.SetActive(false);
        Time.timeScale = 0f;
        isExiting = false;
    }

    public void CancelExit()
    {
        exitConfirmationUI.SetActive(false);
        isExiting = false;
    }

    public void ExitGame()
    {
        Application.Quit();
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#endif
        gameOverUI.SetActive(true);
        gameOverText.text = "Game Over!"; // 게임 종료 텍스트 설정
    }

    public void GameOver(string message)
    {
        gameOverUI.SetActive(true);
        gameUI.SetActive(false);
        gameOverText.text = message; // 게임 종료 텍스트 설정
    }
}