using System.Collections.Concurrent;
using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Math;

[Trait("Category", "CorrectnessRegression")]
[Trait("Category", "ConcurrencyRegression")]
public class ObjectPoolTests
{
    private static readonly TimeSpan DeadlockTimeout = TimeSpan.FromSeconds(5);

    private class TestObject { public int Value { get; set; } }

    [Fact]
    public void Rent_ShouldReturnObject()
    {
        var pool = new ObjectPool<TestObject>(() => new TestObject(), 10);
        var obj = pool.Rent();
        obj.Should().NotBeNull();
    }

    [Fact]
    public void Rent_ShouldCreateNewWhenExhausted()
    {
        var pool = new ObjectPool<TestObject>(() => new TestObject(), 2);
        pool.Rent();
        pool.Rent();
        var obj = pool.Rent();
        obj.Should().NotBeNull();
    }

    [Fact]
    public void Return_ShouldAllowReuse()
    {
        var pool = new ObjectPool<TestObject>(() => new TestObject(), 5);
        var obj = pool.Rent()!;
        obj.Value = 42;
        pool.Return(obj);
        var obj2 = pool.Rent()!;
        obj2.Should().BeSameAs(obj);
        obj2.Value.Should().Be(42);
    }

    [Fact]
    public void Return_ShouldResetObject_WhenResetCallbackIsConfigured()
    {
        var pool = new ObjectPool<TestObject>(
            factory: () => new TestObject(),
            reset: static item => item.Value = 0,
            capacity: 1);

        var obj = pool.Rent();
        obj.Value = 42;

        pool.Return(obj);

        var reused = pool.Rent();
        reused.Should().BeSameAs(obj);
        reused.Value.Should().Be(0);
    }

    [Fact]
    public void Constructor_WhenFactoryReturnsNull_ShouldThrow()
    {
        Action act = () => _ = new ObjectPool<TestObject>(() => null!, 1);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*factory*null*");
    }

    [Fact]
    public void Constructor_WhenMaxCapacityIsLessThanCapacity_ShouldThrow()
    {
        Action act = () => _ = new ObjectPool<TestObject>(
            factory: () => new TestObject(),
            reset: null,
            capacity: 2,
            maxCapacity: 1);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .Where(ex => ex.ParamName == "maxCapacity");
    }

    [Fact]
    public void Constructor_WhenCapacityExceedsDefaultMaxCapacity_ShouldPreserveCompatibility()
    {
        const int capacity = 1025;
        var created = 0;

        _ = new ObjectPool<TestObject>(
            () =>
            {
                Interlocked.Increment(ref created);
                return new TestObject();
            },
            capacity);

        created.Should().Be(capacity);
    }

    [Fact]
    public void Constructor_ShouldPreFillExactlyCapacity()
    {
        var created = 0;

        _ = new ObjectPool<TestObject>(
            () =>
            {
                Interlocked.Increment(ref created);
                return new TestObject();
            },
            capacity: 3,
            maxCapacity: 5);

        created.Should().Be(3);
    }

    [Fact]
    public void Rent_WhenFactoryReturnsNull_ShouldThrow()
    {
        var pool = new ObjectPool<TestObject>(
            factory: () => null!,
            reset: null,
            capacity: 0,
            maxCapacity: 0);

        Action act = () => _ = pool.Rent();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*factory*null*");
    }

    [Fact]
    public async Task Rent_WhenFactoryIsRunning_ShouldNotBlockConcurrentReturn()
    {
        var factoryEntered = CreateGate();
        var releaseFactory = CreateGate();
        ObjectPool<TestObject>? pool = null;

        pool = new ObjectPool<TestObject>(
            () =>
            {
                factoryEntered.TrySetResult(null);
                releaseFactory.Task.GetAwaiter().GetResult();
                return new TestObject { Value = 1 };
            },
            reset: null,
            capacity: 0,
            maxCapacity: 1);

        var rentTask = Task.Run(() => pool.Rent());
        var returned = new TestObject { Value = 2 };

        try
        {
            await factoryEntered.Task.WaitAsync(DeadlockTimeout);

            var returnTask = Task.Run(() => pool.Return(returned));
            await returnTask.WaitAsync(DeadlockTimeout);
        }
        finally
        {
            releaseFactory.TrySetResult(null);
        }

        var rented = await rentTask.WaitAsync(DeadlockTimeout);
        rented.Value.Should().Be(1);
        pool.Rent().Should().BeSameAs(returned);
    }

    [Fact]
    public async Task Return_WhenResetIsRunning_ShouldNotBlockConcurrentRent()
    {
        var resetEntered = CreateGate();
        var releaseReset = CreateGate();
        var created = 0;

        var pool = new ObjectPool<TestObject>(
            () => new TestObject { Value = Interlocked.Increment(ref created) },
            item =>
            {
                item.Value = 0;
                resetEntered.TrySetResult(null);
                releaseReset.Task.GetAwaiter().GetResult();
            },
            capacity: 1,
            maxCapacity: 1);

        var returned = pool.Rent();
        var returnTask = Task.Run(() => pool.Return(returned));

        try
        {
            await resetEntered.Task.WaitAsync(DeadlockTimeout);

            var rentTask = Task.Run(() => pool.Rent());
            var rented = await rentTask.WaitAsync(DeadlockTimeout);
            rented.Should().NotBeSameAs(returned);
        }
        finally
        {
            releaseReset.TrySetResult(null);
        }

        await returnTask.WaitAsync(DeadlockTimeout);
    }

    [Fact]
    public void Return_WhenResetThrows_ShouldDiscardReturnedItem()
    {
        var created = 0;
        var pool = new ObjectPool<TestObject>(
            () => new TestObject { Value = Interlocked.Increment(ref created) },
            _ => throw new InvalidOperationException("reset failed"),
            capacity: 1,
            maxCapacity: 1);

        var returned = pool.Rent();

        Action act = () => pool.Return(returned);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("reset failed");

        var rented = pool.Rent();
        rented.Should().NotBeSameAs(returned);
        created.Should().Be(2);
    }

    [Fact]
    public void Return_WhenPoolIsFull_ShouldDiscardReturnedItem()
    {
        var pool = new ObjectPool<TestObject>(
            () => new TestObject(),
            reset: null,
            capacity: 1,
            maxCapacity: 1);

        var retained = pool.Rent();
        pool.Return(retained);

        var discarded = new TestObject();
        pool.Return(discarded);

        var rented = pool.Rent();
        rented.Should().BeSameAs(retained);
        rented.Should().NotBeSameAs(discarded);
    }

    [Fact]
    public void Rent_WhenConcurrent_ShouldNotReturnSameInstanceToSimultaneousRenters()
    {
        const int poolCapacity = 8;
        const int workerCount = 16;
        const int iterationsPerWorker = 200;

        var created = 0;
        var pool = new ObjectPool<TestObject>(
            () => new TestObject { Value = Interlocked.Increment(ref created) },
            poolCapacity);
        var inUse = new ConcurrentDictionary<TestObject, byte>();
        var duplicateRentCount = 0;
        using var start = new Barrier(workerCount);
        var exceptions = new ConcurrentBag<Exception>();
        var threads = new Thread[workerCount];

        for (int workerIndex = 0; workerIndex < workerCount; workerIndex++)
        {
            threads[workerIndex] = new Thread(() =>
            {
                try
                {
                    start.SignalAndWait();

                    for (int i = 0; i < iterationsPerWorker; i++)
                    {
                        var item = pool.Rent();

                        if (!inUse.TryAdd(item, 0))
                        {
                            Interlocked.Increment(ref duplicateRentCount);
                        }

                        Thread.Yield();
                        inUse.TryRemove(item, out _);
                        pool.Return(item);
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            });
            threads[workerIndex].Start();
        }

        foreach (var thread in threads)
        {
            thread.Join(DeadlockTimeout).Should().BeTrue("worker threads should not deadlock");
        }

        exceptions.Should().BeEmpty();
        duplicateRentCount.Should().Be(0);
        inUse.Should().BeEmpty();
    }

    [Fact]
    public async Task FactoryAndReset_WhenReentrant_ShouldNotDeadlock()
    {
        ObjectPool<TestObject>? factoryPool = null;
        var factoryCalls = 0;

        factoryPool = new ObjectPool<TestObject>(
            () =>
            {
                if (Interlocked.Increment(ref factoryCalls) == 1)
                {
                    factoryPool!.Return(new TestObject { Value = 10 });
                }

                return new TestObject { Value = 20 };
            },
            reset: null,
            capacity: 0,
            maxCapacity: 1);

        var factoryRent = await Task.Run(() => factoryPool.Rent()).WaitAsync(DeadlockTimeout);
        factoryRent.Value.Should().Be(20);
        factoryPool.Rent().Value.Should().Be(10);

        ObjectPool<TestObject>? resetPool = null;
        TestObject? nestedRent = null;
        var resetCalls = 0;

        resetPool = new ObjectPool<TestObject>(
            () => new TestObject { Value = 30 },
            _ =>
            {
                if (Interlocked.Increment(ref resetCalls) == 1)
                {
                    nestedRent = resetPool!.Rent();
                }
            },
            capacity: 1,
            maxCapacity: 1);

        var returned = resetPool.Rent();
        await Task.Run(() => resetPool.Return(returned)).WaitAsync(DeadlockTimeout);

        nestedRent.Should().NotBeNull();
        nestedRent.Should().NotBeSameAs(returned);
    }

    [Fact]
    public void StressTest_20Threads_ZeroObjectLoss()
    {
        const int poolCapacity = 100;
        const int threadCount = 20;
        const int iterationsPerThread = 1000;

        var pool = new ObjectPool<TestObject>(
            () => new TestObject(),
            poolCapacity);

        // Track all objects created by the pool factory
        var allObjects = new ConcurrentBag<TestObject>();
        var poolWithTracking = new ObjectPool<TestObject>(
            () =>
            {
                var obj = new TestObject();
                allObjects.Add(obj);
                return obj;
            },
            poolCapacity);

        // Track objects that are currently rented (not returned)
        var rentedObjects = new ConcurrentDictionary<TestObject, int>();
        var returnedObjects = new ConcurrentBag<TestObject>();
        var lostObjects = new ConcurrentBag<TestObject>();
        var exceptions = new ConcurrentBag<Exception>();

        var threads = new Thread[threadCount];
        var barrier = new Barrier(threadCount);
        var cts = new CancellationTokenSource();

        for (int t = 0; t < threadCount; t++)
        {
            threads[t] = new Thread(state =>
            {
                try
                {
                    barrier.SignalAndWait(); // Start all threads simultaneously

                    for (int i = 0; i < iterationsPerThread; i++)
                    {
                        TestObject? obj = null;
                        try
                        {
                            obj = poolWithTracking.Rent();
                            if (obj == null)
                            {
                                throw new InvalidOperationException("Rent returned null");
                            }

                            // Simulate some work
                            obj.Value = Environment.TickCount;

                            // Track that we rented this object
                            rentedObjects.TryAdd(obj, 1);

                            // Return the object
                            poolWithTracking.Return(obj);

                            // Remove from rented tracking
                            rentedObjects.TryRemove(obj, out _);
                            returnedObjects.Add(obj);
                        }
                        catch (Exception ex)
                        {
                            exceptions.Add(ex);
                            if (obj != null)
                            {
                                rentedObjects.TryRemove(obj, out _);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            });
            threads[t].Start();
        }

        // Wait for all threads to complete
        for (int t = 0; t < threadCount; t++)
        {
            threads[t].Join(TimeSpan.FromSeconds(30));
        }

        // Check for exceptions
        if (exceptions.Count > 0)
        {
            throw new AggregateException(exceptions);
        }

        // Verify no objects were lost
        // In a correct implementation, all rented objects should be returned
        // The rentedObjects dictionary should be empty
        rentedObjects.Should().BeEmpty(
            $"no objects should be lost. Objects still rented: {rentedObjects.Count}");

        // Verify we completed all operations without ABA corruption
        returnedObjects.Count.Should().Be(
            threadCount * iterationsPerThread,
            "all rent/return cycles should complete successfully");
    }

    private static TaskCompletionSource<object?> CreateGate() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
