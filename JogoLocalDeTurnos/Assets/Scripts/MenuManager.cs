using UnityEngine;
using UnityEngine.SceneManagement;

public class MenuManager : MonoBehaviour
{
    public void EscolherVermelho()
    {
        PlayerPreferences.Instance.SetPlayerAsRed();
        SceneManager.LoadScene("Connect4");
    }

    public void EscolherVerde()
    {
        PlayerPreferences.Instance.SetPlayerAsGreen();
        SceneManager.LoadScene("Connect4");
    }
}