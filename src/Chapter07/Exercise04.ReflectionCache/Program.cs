using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

// ============================================================================
// Exercise 04 — Production-Grade Reflection Cache Framework
// ============================================================================
// Reflection is powerful but slow: every call to PropertyInfo.GetValue or
// MethodInfo.Invoke goes through runtime type checks, security verification,
// and boxing. This exercise builds a two-level cache that compiles reflection
// metadata into strongly-typed delegates via expression trees.
//
// Architecture:
//   Level 1: Type  →  TypeCache          (one entry per reflected type)
//   Level 2: Name  →  Compiled Delegate  (one entry per property / method)
//
// The initial expression-tree compilation costs ~1 ms, but it only happens
// once per property or method. After that, the cached delegate executes at
// roughly virtual-call speed — 50-100x faster than raw reflection and only
// ~2-5x slower than a direct property access.
// ============================================================================

BenchmarkRunner.Run<ReflectionCacheBenchmarks>();

// ---------------------------------------------------------------------------
// Cache Statistics Record
// ---------------------------------------------------------------------------

/// <summary>Snapshot of current cache population counts.</summary>
record CacheStatistics(int TypesCached, int GettersCached, int SettersCached, int MethodsCached);

// ---------------------------------------------------------------------------
// Reflection Cache — the core framework
// ---------------------------------------------------------------------------

/// <summary>
/// Thread-safe, two-level reflection cache that compiles property accessors
/// and method invokers into delegates using expression trees.
/// </summary>
static class ReflectionCache
{
    // -----------------------------------------------------------------------
    // Level-1 cache: one TypeCache per reflected Type.
    // ConcurrentDictionary.GetOrAdd is used everywhere so that concurrent
    // readers never block. Under a race two threads may both compile the
    // same delegate, but that is harmless — the dictionary keeps whichever
    // one wins and the other is simply garbage-collected.
    // -----------------------------------------------------------------------
    private static readonly ConcurrentDictionary<Type, TypeCache> Cache = new();

    // -----------------------------------------------------------------------
    // TypeCache — Level-2 caches for a single Type
    // -----------------------------------------------------------------------

    /// <summary>
    /// Holds compiled delegates for all reflected members of a single type.
    /// Each dictionary is lazily populated on first access.
    /// </summary>
    private sealed class TypeCache
    {
        /// <summary>Property name → compiled getter delegate.</summary>
        public ConcurrentDictionary<string, Func<object, object?>> Getters { get; } = new();

        /// <summary>Property name → compiled setter delegate.</summary>
        public ConcurrentDictionary<string, Action<object, object?>> Setters { get; } = new();

        /// <summary>
        /// Method key → compiled invoker delegate.
        /// The key encodes both the method name and its parameter types so
        /// that overloads are cached independently.
        /// </summary>
        public ConcurrentDictionary<string, Func<object, object?[], object?>> MethodInvokers { get; } = new();
    }

    // -----------------------------------------------------------------------
    // Public API — Property Getter
    // -----------------------------------------------------------------------

    /// <summary>
    /// Returns a compiled getter delegate for the specified property.
    /// The first call compiles an expression tree like:
    ///   (object obj) => (object?)((TargetType)obj).PropertyName
    /// Subsequent calls return the cached delegate in O(1).
    /// </summary>
    public static Func<object, object?> GetPropertyGetter(Type type, string propertyName)
    {
        var typeCache = Cache.GetOrAdd(type, _ => new TypeCache());
        return typeCache.Getters.GetOrAdd(propertyName, name =>
        {
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
                ?? throw new ArgumentException($"Property '{name}' not found on {type.Name}.");

            // Expression tree:  (object obj) => (object?)((Type)obj).Property
            var param = Expression.Parameter(typeof(object), "obj");
            var cast = Expression.Convert(param, type);
            var access = Expression.Property(cast, property);
            var boxed = Expression.Convert(access, typeof(object));

            return Expression.Lambda<Func<object, object?>>(boxed, param).Compile();
        });
    }

    // -----------------------------------------------------------------------
    // Public API — Property Setter
    // -----------------------------------------------------------------------

    /// <summary>
    /// Returns a compiled setter delegate for the specified property.
    /// Compiles:  (object obj, object? val) => ((Type)obj).Prop = (PropType)val
    /// </summary>
    public static Action<object, object?> GetPropertySetter(Type type, string propertyName)
    {
        var typeCache = Cache.GetOrAdd(type, _ => new TypeCache());
        return typeCache.Setters.GetOrAdd(propertyName, name =>
        {
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
                ?? throw new ArgumentException($"Property '{name}' not found on {type.Name}.");

            if (!property.CanWrite)
                throw new InvalidOperationException($"Property '{name}' on {type.Name} is read-only.");

            // Expression tree:
            //   (object obj, object? value) => ((Type)obj).Prop = (PropType)value
            var objParam = Expression.Parameter(typeof(object), "obj");
            var valParam = Expression.Parameter(typeof(object), "value");
            var castObj = Expression.Convert(objParam, type);
            var castVal = Expression.Convert(valParam, property.PropertyType);
            var assign = Expression.Assign(Expression.Property(castObj, property), castVal);

            return Expression.Lambda<Action<object, object?>>(assign, objParam, valParam).Compile();
        });
    }

    // -----------------------------------------------------------------------
    // Public API — Method Invoker
    // -----------------------------------------------------------------------

    /// <summary>
    /// Returns a compiled invoker for the specified method and parameter signature.
    /// Compiles:
    ///   (object obj, object?[] args) =>
    ///       (object?)((Type)obj).Method((P0)args[0], (P1)args[1], ...)
    /// </summary>
    public static Func<object, object?[], object?> GetMethodInvoker(
        Type type, string methodName, Type[] parameterTypes)
    {
        var typeCache = Cache.GetOrAdd(type, _ => new TypeCache());

        // Build a unique key that distinguishes overloads.
        var key = $"{methodName}({string.Join(",", parameterTypes.Select(t => t.FullName))})";

        return typeCache.MethodInvokers.GetOrAdd(key, _ =>
        {
            var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance, parameterTypes)
                ?? throw new ArgumentException(
                    $"Method '{methodName}' with specified parameters not found on {type.Name}.");

            var objParam = Expression.Parameter(typeof(object), "obj");
            var argsParam = Expression.Parameter(typeof(object[]), "args");
            var castObj = Expression.Convert(objParam, type);

            // Unpack each element of the args array and cast to the correct type.
            var parameters = method.GetParameters();
            var argExpressions = new Expression[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
            {
                var index = Expression.ArrayIndex(argsParam, Expression.Constant(i));
                argExpressions[i] = Expression.Convert(index, parameters[i].ParameterType);
            }

            Expression call = Expression.Call(castObj, method, argExpressions);

            // If the method returns void, wrap it so the delegate returns null.
            if (method.ReturnType == typeof(void))
            {
                call = Expression.Block(call, Expression.Constant(null, typeof(object)));
            }
            else
            {
                call = Expression.Convert(call, typeof(object));
            }

            return Expression.Lambda<Func<object, object?[], object?>>(
                call, objParam, argsParam).Compile();
        });
    }

    // -----------------------------------------------------------------------
    // Public API — Statistics
    // -----------------------------------------------------------------------

    /// <summary>Returns a snapshot of how many items are currently cached.</summary>
    public static CacheStatistics GetStatistics()
    {
        int getters = 0, setters = 0, methods = 0;
        foreach (var tc in Cache.Values)
        {
            getters += tc.Getters.Count;
            setters += tc.Setters.Count;
            methods += tc.MethodInvokers.Count;
        }
        return new CacheStatistics(Cache.Count, getters, setters, methods);
    }

    // -----------------------------------------------------------------------
    // Public API — Warmup
    // -----------------------------------------------------------------------

    /// <summary>
    /// Pre-compiles getters and setters for every public instance property of
    /// the given type. Call this during application startup to eliminate the
    /// ~1 ms first-call compilation latency from hot paths in production.
    /// </summary>
    public static void Warmup(Type type)
    {
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        foreach (var prop in properties)
        {
            if (prop.CanRead) GetPropertyGetter(type, prop.Name);
            if (prop.CanWrite) GetPropertySetter(type, prop.Name);
        }
    }

    // -----------------------------------------------------------------------
    // Public API — Clear
    // -----------------------------------------------------------------------

    /// <summary>Clears all cached delegates. Useful for testing or hot-reload.</summary>
    public static void Clear()
    {
        Cache.Clear();
    }
}

// ---------------------------------------------------------------------------
// Test Domain Model
// ---------------------------------------------------------------------------

class Customer
{
    public string Name { get; set; } = string.Empty;
    public int Age { get; set; }
    public string Email { get; set; } = string.Empty;
    public int VipLevel { get; set; }

    /// <summary>Calculates a VIP discount on the given amount.</summary>
    public decimal CalculateDiscount(decimal amount)
        => amount * (VipLevel * 0.05m);
}

// ---------------------------------------------------------------------------
// Benchmarks
// ---------------------------------------------------------------------------
// Expected results (approximate, varies by hardware):
//
//   | Method                | Mean       | Ratio |
//   |-----------------------|------------|-------|
//   | DirectPropertyAccess  |   ~1-2 ns  |  1.00 |
//   | CachedExpressionGetter|   ~3-8 ns  |  ~2-5 |
//   | RawReflection         | ~100-300 ns| ~50+  |
//   | CachedMethodInvoke    |  ~10-30 ns |  ~5-15|
//   | RawMethodInvoke       | ~200-500 ns| ~100+ |
//
// The cached expression delegate closes the gap to within a small constant
// factor of direct access, while raw reflection is orders of magnitude slower.
// ---------------------------------------------------------------------------

[MemoryDiagnoser]
public class ReflectionCacheBenchmarks
{
    private Customer _customer = null!;
    private PropertyInfo _nameProperty = null!;
    private MethodInfo _discountMethod = null!;
    private Func<object, object?> _cachedGetter = null!;
    private Func<object, object?[], object?> _cachedInvoker = null!;

    [GlobalSetup]
    public void Setup()
    {
        _customer = new Customer
        {
            Name = "Alice",
            Age = 30,
            Email = "alice@example.com",
            VipLevel = 3
        };

        // Pre-warm the cache so compilation cost is excluded from benchmarks.
        ReflectionCache.Warmup(typeof(Customer));
        ReflectionCache.GetMethodInvoker(
            typeof(Customer), nameof(Customer.CalculateDiscount), [typeof(decimal)]);

        // Cache the delegates and reflection objects locally to isolate
        // the invocation cost from dictionary lookup cost.
        _cachedGetter = ReflectionCache.GetPropertyGetter(typeof(Customer), nameof(Customer.Name));
        _cachedInvoker = ReflectionCache.GetMethodInvoker(
            typeof(Customer), nameof(Customer.CalculateDiscount), [typeof(decimal)]);

        _nameProperty = typeof(Customer).GetProperty(nameof(Customer.Name))!;
        _discountMethod = typeof(Customer).GetMethod(nameof(Customer.CalculateDiscount))!;
    }

    /// <summary>Baseline: direct property access — the fastest possible path.</summary>
    [Benchmark(Baseline = true)]
    public object? DirectPropertyAccess()
        => _customer.Name;

    /// <summary>
    /// Cached expression-tree delegate. After the one-time compilation,
    /// this is essentially a virtual call through a Func delegate.
    /// </summary>
    [Benchmark]
    public object? CachedExpressionGetter()
        => _cachedGetter(_customer);

    /// <summary>
    /// Raw reflection via PropertyInfo.GetValue — performs runtime type
    /// checks and boxing on every call.
    /// </summary>
    [Benchmark]
    public object? RawReflection()
        => _nameProperty.GetValue(_customer);

    /// <summary>Cached compiled method invoker via expression tree.</summary>
    [Benchmark]
    public object? CachedMethodInvoke()
        => _cachedInvoker(_customer, [100m]);

    /// <summary>Raw MethodInfo.Invoke — slowest path with boxing and validation.</summary>
    [Benchmark]
    public object? RawMethodInvoke()
        => _discountMethod.Invoke(_customer, [100m]);
}
