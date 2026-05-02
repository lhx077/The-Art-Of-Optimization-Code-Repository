// ============================================================================
// Exercise 02 — Event Leak Detector and Weak Event Wrapper
// ============================================================================
// Demonstrates two things:
//   1. Detecting suspicious subscriber growth on an event source.
//   2. Using a weak-event wrapper to prevent leaks caused by long-lived
//      publishers holding strong references to short-lived subscribers.
// ============================================================================

using System.Reflection;

Console.WriteLine("=== Event Leak Detector Demo ===");

var publisher = new Publisher();
var detector = new EventLeakDetector(publisher, nameof(Publisher.Tick));

for (int i = 0; i < 20; i++)
{
    var subscriber = new LeakySubscriber($"Leaky-{i}");
    publisher.Tick += subscriber.OnTick;
    detector.Sample();
}

Console.WriteLine();
Console.WriteLine("Switching to weak event wrapper...");

var weakPublisher = new WeakPublisher();
for (int i = 0; i < 20; i++)
{
    var subscriber = new WeakSubscriber($"Weak-{i}");
    weakPublisher.Subscribe(subscriber, static (target, sender, args) => target.OnTick(sender, args));
}

GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();
weakPublisher.Raise();
Console.WriteLine("Weak event raise completed.");

// Publisher represents the long-lived event source whose invocation list keeps
// strong references to every subscribed target.
public sealed class Publisher
{
    public event EventHandler? Tick;
    public void Raise() => Tick?.Invoke(this, EventArgs.Empty);
}

// EventLeakDetector inspects the event backing field through reflection so the
// sample can flag suspicious subscriber growth without modifying Publisher.
public sealed class EventLeakDetector
{
    private readonly object _target;
    private readonly FieldInfo _eventField;
    private readonly Queue<int> _samples = new();

    public EventLeakDetector(object target, string eventName)
    {
        _target = target;
        _eventField = target.GetType().GetField(eventName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Backing field for event '{eventName}' not found.");
    }

    public void Sample()
    {
        // A short rolling window is enough for the demo to detect monotonically
        // increasing counts, which is a common leak smell for long-lived events.
        int count = GetSubscriberCount();
        _samples.Enqueue(count);
        if (_samples.Count > 5)
            _samples.Dequeue();

        Console.WriteLine($"Subscriber count: {count}");

        if (_samples.Count == 5 && IsStrictlyGrowing())
        {
            // The warning includes target types so the investigation starts with real subscribers.
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("Potential event leak detected: subscriber count is monotonically increasing.");
            PrintSubscriberTypes();
            Console.ResetColor();
        }
    }

    private int GetSubscriberCount()
    {
        return (_eventField.GetValue(_target) as MulticastDelegate)?.GetInvocationList().Length ?? 0;
    }

    private bool IsStrictlyGrowing()
    {
        int[] values = _samples.ToArray();
        for (int i = 1; i < values.Length; i++)
        {
            if (values[i] <= values[i - 1])
                return false;
        }
        return true;
    }

    private void PrintSubscriberTypes()
    {
        var handlers = (_eventField.GetValue(_target) as MulticastDelegate)?.GetInvocationList();
        if (handlers is null)
            return;

        foreach (var handler in handlers)
        {
            Console.WriteLine($"  Subscriber type: {handler.Target?.GetType().FullName ?? "<static>"}");
        }
    }
}

// WeakPublisher keeps weak subscriptions so dead subscribers can disappear after
// a GC instead of being kept alive by the publisher's invocation list.
public sealed class WeakPublisher
{
    private readonly List<IWeakSubscription> _subscriptions = [];

    public void Subscribe<TTarget>(TTarget target, Action<TTarget, object?, EventArgs> callback)
        where TTarget : class
    {
        _subscriptions.Add(new WeakSubscription<TTarget>(target, callback));
    }

    public void Raise()
    {
        for (int i = _subscriptions.Count - 1; i >= 0; i--)
        {
            if (!_subscriptions[i].Invoke(this, EventArgs.Empty))
            {
                _subscriptions.RemoveAt(i);
            }
        }
    }
}

public interface IWeakSubscription
{
    bool Invoke(object? sender, EventArgs args);
}

// WeakSubscription stores both a weak reference and a strongly typed callback.
// If the target is gone, Invoke returns false so the publisher can prune the slot.
public sealed class WeakSubscription<TTarget> : IWeakSubscription where TTarget : class
{
    private readonly WeakReference<TTarget> _target;
    private readonly Action<TTarget, object?, EventArgs> _callback;

    public WeakSubscription(TTarget target, Action<TTarget, object?, EventArgs> callback)
    {
        _target = new WeakReference<TTarget>(target);
        _callback = callback;
    }

    public bool Invoke(object? sender, EventArgs args)
    {
        if (!_target.TryGetTarget(out TTarget? target))
            return false;

        _callback(target, sender, args);
        return true;
    }
}

// Core implementation used by the demo and benchmark code below.
public sealed class LeakySubscriber
{
    private readonly string _name;
    public LeakySubscriber(string name) => _name = name;
    public void OnTick(object? sender, EventArgs args) { }
    public override string ToString() => _name;
}

// Core implementation used by the demo and benchmark code below.
public sealed class WeakSubscriber
{
    private readonly string _name;
    public WeakSubscriber(string name) => _name = name;
    public void OnTick(object? sender, EventArgs args) { }
    public override string ToString() => _name;
}
