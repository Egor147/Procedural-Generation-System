using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Executes Unity API calls on the main thread from background tasks.
/// Thread-safe queue processed in MonoBehaviour.Update().
/// </summary>
public class MainThreadDispatcher : MonoBehaviour
{
    private static MainThreadDispatcher _instance;
    private readonly Queue<System.Action> _actions = new Queue<System.Action>();
    private readonly object _lock = new object();

    /// <summary>
    /// Singleton accessor. Initializes on first access.
    /// </summary>
    public static MainThreadDispatcher Instance
    {
        get
        {
            if (_instance == null)
            {
                var obj = new GameObject("MainThreadDispatcher");
                DontDestroyOnLoad(obj);
                _instance = obj.AddComponent<MainThreadDispatcher>();
            }
            return _instance;
        }
    }

    /// <summary>
    /// Queues an action for execution on the main thread.
    /// </summary>
    public static void Enqueue(System.Action action)
    {
        if (action == null) return;
        lock (Instance._lock)
        {
            Instance._actions.Enqueue(action);
        }
    }

    private void Update()
    {
        lock (_lock)
        {
            while (_actions.Count > 0)
            {
                _actions.Dequeue()?.Invoke();
            }
        }
    }
}