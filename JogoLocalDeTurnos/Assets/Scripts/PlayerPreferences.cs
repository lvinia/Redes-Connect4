using UnityEngine;

public class PlayerPreferences : MonoBehaviour
{
    public static PlayerPreferences Instance { get; private set; }

    public bool IsPlayerRed { get; private set; } = true;

    void Awake()
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

    public void SetPlayerAsRed()
    {
        IsPlayerRed = true;
    }

    public void SetPlayerAsGreen()
    {
        IsPlayerRed = false;
    }
}