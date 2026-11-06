using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;

namespace RingView.Core.Tests.Golden;

/// <summary>
/// Replays a golden timeline the way vitest's fake timers ran it in the Electron app: one thread,
/// time advanced one millisecond at a time, and every posted continuation run before the clock
/// moves again (JavaScript's microtasks between timers).
/// </summary>
internal sealed class Timeline : SynchronizationContext, IDisposable
{
    public static readonly DateTimeOffset Base = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);

    private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = new();
    private readonly SynchronizationContext? _previous;

    public Timeline()
    {
        Time = new FakeTimeProvider(Base);
        _previous = Current;
        SetSynchronizationContext(this);
    }

    public FakeTimeProvider Time { get; }

    public JsonArray Trace { get; } = [];

    /// <summary>Milliseconds since the scenario started.</summary>
    public long T => (long)(Time.GetUtcNow() - Base).TotalMilliseconds;

    public long? Rel(DateTimeOffset? at) => at is { } value ? (long)(value - Base).TotalMilliseconds : null;

    public void Record(string ev, JsonObject? fields = null)
    {
        var entry = new JsonObject { ["t"] = T, ["ev"] = ev };
        if (fields is not null)
        {
            foreach ((string key, JsonNode? value) in fields.ToList())
            {
                fields.Remove(key);
                entry[key] = value;
            }
        }
        Trace.Add(entry);
    }

    public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));

    public override void Send(SendOrPostCallback d, object? state) => d(state);

    /// <summary>Runs everything queued, including work that queues more work.</summary>
    public void RunUntilIdle()
    {
        while (_queue.TryDequeue(out var item))
            item.Callback(item.State);
    }

    /// <summary>Moves the clock forward, firing timers and running continuations as JS would.</summary>
    public void AdvanceTo(long ms)
    {
        RunUntilIdle();
        while (T < ms)
        {
            Time.Advance(TimeSpan.FromMilliseconds(1));
            RunUntilIdle();
        }
    }

    /// <summary>Starts async work on this loop without awaiting it; failures are recorded.</summary>
    public void Fire(Func<Task> work)
    {
        Task task = work();
        task.ContinueWith(
            t => Record("action-failed", new JsonObject { ["message"] = t.Exception!.InnerException!.Message }),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Compares a recorded trace with the golden one and names the first difference.</summary>
    public static void ShouldMatch(JsonArray actual, JsonArray expected, string scenario)
    {
        int count = Math.Max(actual.Count, expected.Count);
        for (int i = 0; i < count; i++)
        {
            JsonNode? a = i < actual.Count ? actual[i] : null;
            JsonNode? e = i < expected.Count ? expected[i] : null;
            if (!JsonNode.DeepEquals(a, e))
            {
                Assert.Fail($"Scenario \"{scenario}\" differs at trace entry {i}:\n  expected {e?.ToJsonString() ?? "(end)"}\n  actual   {a?.ToJsonString() ?? "(end)"}");
            }
        }
    }

    public void Dispose() => SetSynchronizationContext(_previous);
}
