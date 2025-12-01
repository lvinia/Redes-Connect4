using System;
using System.Collections.Generic;
using UnityEngine;

public class UnityMainThreadDispatcher : MonoBehaviour
{
    private static readonly Queue<Action> _executionQueue = new Queue<Action>();
    private static UnityMainThreadDispatcher _instance = null;

    // Verifica existência
    public static bool Exists()
    {
        return _instance != null;
    }

    // Retorna instância (Método para obter o Singleton)
    public static UnityMainThreadDispatcher Instance()
    {
        if (!Exists())
            throw new Exception("UnityMainThreadDispatcher não existe na cena. Adicione este script em algum GameObject.");
        return _instance;
    }

    void Awake()
    {
        if (_instance == null)
        {
            _instance = this;
            DontDestroyOnLoad(gameObject);
        }
        else
        {
            Destroy(gameObject);
        }
    }

    void Update()
    {
        lock (_executionQueue)
        {
            while (_executionQueue.Count > 0)
            {
                try
                {
                    _executionQueue.Dequeue().Invoke();
                }
                catch (Exception ex)
                {
                    Debug.LogError("[UnityMainThreadDispatcher] Erro ao executar ação: " + ex);
                }
            }
        }
    }

    // Enfileira ação para execução na main thread
    public void Enqueue(Action action)
    {
        if (action == null) throw new ArgumentNullException(nameof(action));
        lock (_executionQueue)
        {
            _executionQueue.Enqueue(action);
        }
    }
}