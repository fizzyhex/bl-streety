using System.Collections.Concurrent;

using Il2CppInterop.Runtime;

namespace Streety.Render;

/// <summary>
/// a background thread attached to the il2cpp domain, for thread-safe unity calls such as Physics.BakeMesh.
/// </summary>
public sealed class Il2CppWorker : IDisposable
{
    private readonly BlockingCollection<Action> _work = new();
    private readonly ConcurrentQueue<Action> _completions = new();
    private readonly Thread _thread;

    public Il2CppWorker(string name)
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = name,
            Priority = ThreadPriority.BelowNormal,
        };

        _thread.Start();
    }

    /// <summary>
    /// runs <paramref name="work"/> on the worker, then <paramref name="onMainThread"/> during <see cref="Pump"/>.
    /// </summary>
    public void Enqueue(Action work, Action onMainThread)
    {
        _work.Add(() =>
        {
            try
            {
                work();
            }
            catch (Exception e)
            {
                StreetyMod.Log.Warning($"worker task failed: {e.Message}");
                return;
            }

            _completions.Enqueue(onMainThread);
        });
    }

    public void Pump()
    {
        while (_completions.TryDequeue(out var completion))
        {
            try
            {
                completion();
            }
            catch (Exception e)
            {
                StreetyMod.Log.Error(e);
            }
        }
    }

    private void Run()
    {
        var attached = IL2CPP.il2cpp_thread_attach(IL2CPP.il2cpp_domain_get());

        try
        {
            foreach (var work in _work.GetConsumingEnumerable())
                work();
        }
        finally
        {
            IL2CPP.il2cpp_thread_detach(attached);
        }
    }

    public void Dispose()
    {
        _work.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(2));
        _work.Dispose();
    }
}
