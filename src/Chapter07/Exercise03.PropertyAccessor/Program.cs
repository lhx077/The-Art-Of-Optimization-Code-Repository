using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

// ============================================================================
// Exercise 03 — High-Performance Property Accessor via Expression Trees
// ============================================================================
// Reflection gives us runtime access to any property, but PropertyInfo.GetValue
// and SetValue are expensive: each call performs parameter validation, security
// checks, and boxes value types. For hot paths this overhead is unacceptable.
//
// Expression trees solve this by representing code as data — an abstract syntax
// tree (AST) that can be inspected, transformed, and compiled to IL at runtime.
// Expression.Compile() produces a delegate whose invocation cost is virtually
// identical to a direct property access, because the JIT sees the same IL it
// would generate for hand-written code.
//
// Cost model:
//   - One-time compilation:  ~0.5-2 ms per property (amortised away by caching)
//   - Subsequent calls:      ~1-3 ns  (same as direct access)
//   - Reflection per call:   ~100-300 ns  (50-100x slower)
//
// This exercise builds a generic PropertyAccessor<T> that caches compiled
// getters and setters in a ConcurrentDictionary, ensuring thread-safe,
// one-time compilation per property.
//
// Expected benchmark ranking:
//   Direct ≈ ExpressionTree >> CachedDelegate (DynamicInvoke) >> Reflection
// ============================================================================

BenchmarkRunner.Run<PropertyAccessBenchmarks>();

// ---------------------------------------------------------------------------
// Test class — a simple POCO with mixed property types
// ---------------------------------------------------------------------------

class Person
{
    public string Name { get; set; } = string.Empty;
    public int Age { get; set; }
    public string Email { get; set; } = string.Empty;
}

// ---------------------------------------------------------------------------
// PropertyAccessor<T> — Generic, cached, expression-tree-based accessor
// ---------------------------------------------------------------------------
/// <summary>
/// Thread-safe, expression-tree-based property accessor with one-time
/// compilation and concurrent caching.
/// </summary>
/// <remarks>
/// ConcurrentDictionary.GetOrAdd is lock-free for reads and uses fine-grained
/// locking for writes. Under a race two threads may both compile the same
/// delegate, but only one wins insertion — the other is harmlessly GC'd.
/// </remarks>
static class PropertyAccessor<T>
{
    // Cache key: "Get:PropertyName" or "Set:PropertyName"
    // Cache value: the compiled delegate (Func or Action)
    private static readonly ConcurrentDictionary<string, Delegate> DelegateCache = new();

    // -----------------------------------------------------------------------
    // Strongly-typed getter: (T obj) => obj.PropertyName
    // -----------------------------------------------------------------------
    // The expression tree we build is equivalent to writing:
    //     string GetName(Person p) => p.Name;
    // Expression.Compile() emits IL that the JIT can inline, so the resulting
    // delegate runs at near-native speed — no reflection overhead per call.
    // -----------------------------------------------------------------------

    public static Func<T, TValue> CreateGetter<TValue>(string propertyName)
    {
        return (Func<T, TValue>)DelegateCache.GetOrAdd($"Get:{propertyName}:{typeof(TValue).Name}", _ =>
        {
            // Parameter: (T obj)
            var param = Expression.Parameter(typeof(T), "obj");

            // Body: obj.PropertyName
            var propertyAccess = Expression.Property(param, propertyName);

            // Lambda: (T obj) => obj.PropertyName
            var lambda = Expression.Lambda<Func<T, TValue>>(propertyAccess, param);

            // Compile to IL — this is the expensive step (~0.5-2 ms), but it
            // only happens once thanks to the ConcurrentDictionary cache.
            return lambda.Compile();
        });
    }

    // -----------------------------------------------------------------------
    // Strongly-typed setter: (T obj, TValue val) => obj.PropertyName = val
    // -----------------------------------------------------------------------

    public static Action<T, TValue> CreateSetter<TValue>(string propertyName)
    {
        return (Action<T, TValue>)DelegateCache.GetOrAdd($"Set:{propertyName}:{typeof(TValue).Name}", _ =>
        {
            // Parameters: (T obj, TValue val)
            var objParam = Expression.Parameter(typeof(T), "obj");
            var valParam = Expression.Parameter(typeof(TValue), "val");
            // Body: obj.PropertyName = val
            var property = Expression.Property(objParam, propertyName);
            var assign = Expression.Assign(property, valParam);

            // Lambda: (T obj, TValue val) => obj.PropertyName = val
            var lambda = Expression.Lambda<Action<T, TValue>>(assign, objParam, valParam);

            return lambda.Compile();
        });
    }

    // -----------------------------------------------------------------------
    // Boxed getter: (T obj) => (object?)obj.PropertyName
    // -----------------------------------------------------------------------
    // When the property type is unknown at compile time (e.g., iterating all
    // properties via reflection metadata), we fall back to returning object.
    // For value-type properties (int, bool, etc.) this incurs a boxing
    // allocation, but the invocation is still far faster than reflection.
    // -----------------------------------------------------------------------

    public static Func<T, object?> CreateBoxedGetter(string propertyName)
    {
        return (Func<T, object?>)DelegateCache.GetOrAdd($"BoxedGet:{propertyName}", _ =>
        {
            var param = Expression.Parameter(typeof(T), "obj");
            var propertyAccess = Expression.Property(param, propertyName);

            // Convert to object (boxes value types, no-op for reference types)
            var boxed = Expression.Convert(propertyAccess, typeof(object));

            var lambda = Expression.Lambda<Func<T, object?>>(boxed, param);
            return lambda.Compile();
        });
    }
}

// ---------------------------------------------------------------------------
// Benchmarks — Direct vs Expression Tree vs Reflection vs DynamicInvoke
// ---------------------------------------------------------------------------
// PropertyInfo.GetValue / SetValue is slow because each call must:
//   1. Validate the target object type
//   2. Check security permissions (CAS in older runtimes, metadata checks)
//   3. Look up the getter/setter MethodInfo
//   4. Box value-type return values into object
//   5. Unbox / convert arguments from object[]
//
// Delegate.DynamicInvoke is similarly slow — it wraps the delegate invocation
// in reflection-style parameter validation and boxing, defeating the purpose
// of having a compiled delegate in the first place.
//
// The expression-tree approach avoids all of this: after the one-time
// compilation, the cached Func<T, TValue> is a direct IL call with no
// intermediate boxing or validation.
// ---------------------------------------------------------------------------

[MemoryDiagnoser]
public class PropertyAccessBenchmarks
{
    private Person person = null!;
    private PropertyInfo namePropertyInfo = null!;
    private Func<Person, string> compiledGetter = null!;
    private Action<Person, string> compiledSetter = null!;
    private Delegate cachedDelegate = null!;

    [GlobalSetup]
    public void Setup()
    {
        person = new Person { Name = "Alice", Age = 30, Email = "alice@example.com" };

        // Cache the PropertyInfo so reflection benchmarks don't include the
        // metadata lookup cost — we want to isolate the GetValue/SetValue overhead.
        namePropertyInfo = typeof(Person).GetProperty(nameof(Person.Name))!;

        // Pre-compile expression tree accessors so benchmarks measure only
        // the invocation cost, not the one-time compilation.
        compiledGetter = PropertyAccessor<Person>.CreateGetter<string>(nameof(Person.Name));
        compiledSetter = PropertyAccessor<Person>.CreateSetter<string>(nameof(Person.Name));

        // Store the getter as a raw Delegate to benchmark DynamicInvoke
        cachedDelegate = compiledGetter;
    }
    // ===================================================================
    // GET benchmarks
    // ===================================================================

    /// <summary>
    /// Baseline: direct property access. The JIT inlines this to a single
    /// field load instruction — as fast as it gets.
    /// </summary>
    [Benchmark(Description = "Get: Direct access", Baseline = true)]
    public string DirectAccess() => person.Name;

    /// <summary>
    /// Expression-tree compiled getter. After one-time compilation, the
    /// delegate body is identical IL to the direct access above. The only
    /// extra cost is the delegate invocation overhead (~1 ns).
    /// </summary>
    [Benchmark(Description = "Get: Expression tree")]
    public string ExpressionTreeGetter() => compiledGetter(person);

    /// <summary>
    /// Raw reflection. Every call goes through:
    ///   PropertyInfo.GetValue → MethodInfo.Invoke → parameter checks → boxing
    /// Expect ~50-100x slower than direct access.
    /// </summary>
    [Benchmark(Description = "Get: Reflection GetValue")]
    public object? ReflectionGetValue() => namePropertyInfo.GetValue(person);

    /// <summary>
    /// Delegate.DynamicInvoke wraps the call in reflection-style argument
    /// validation and boxing. Even though the underlying delegate is compiled,
    /// DynamicInvoke negates the performance benefit — it's nearly as slow as
    /// raw reflection. This benchmark exists to show why you should always
    /// cast to the concrete Func/Action type instead of using DynamicInvoke.
    /// </summary>
    [Benchmark(Description = "Get: Cached delegate DynamicInvoke")]
    public object? CachedDelegateGetter() => cachedDelegate.DynamicInvoke(person);

    // ===================================================================
    // SET benchmarks
    // ===================================================================

    /// <summary>Direct property assignment — baseline for setters.</summary>
    [Benchmark(Description = "Set: Direct access")]
    public void DirectSet() => person.Name = "test";

    /// <summary>
    /// Expression-tree compiled setter. Same near-native performance as
    /// direct assignment after the one-time compilation cost.
    /// </summary>
    [Benchmark(Description = "Set: Expression tree")]
    public void ExpressionTreeSetter() => compiledSetter(person, "test");

    /// <summary>
    /// Reflection SetValue. Same overhead as GetValue: type checks, security
    /// validation, and argument unboxing on every call.
    /// </summary>
    [Benchmark(Description = "Set: Reflection SetValue")]
    public void ReflectionSetValue() => namePropertyInfo.SetValue(person, "test");
}