using System;
using System.Collections.Concurrent;
using UnityEngine;

public class UnityMainThreadDispatcher : MonoBehaviour
{
    private static UnityMainThreadDispatcher _instance;
    private static readonly ConcurrentQueue<Action> _queue = new ConcurrentQueue<Action>();

    public static void Initialize()
    {
        if (_instance != null) return;

        var obj = new GameObject("UnityMainThreadDispatcher");
        _instance = obj.AddComponent<UnityMainThreadDispatcher>();
        DontDestroyOnLoad(obj);
    }

    public static void Enqueue(Action action)
    {
        if (action == null) return;
        _queue.Enqueue(action);
    }

    private void Update()
    {
        while (_queue.TryDequeue(out var action))
        {
            try
            {
                action?.Invoke();
            }
            catch (Exception e)
            {
                Debug.LogError("Dispatcher exception: " + e);
            }
        }
    }
}
