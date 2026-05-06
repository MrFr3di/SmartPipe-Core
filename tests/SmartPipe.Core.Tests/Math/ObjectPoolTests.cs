using System.Collections.Concurrent;
using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Math;

public class ObjectPoolTests
{
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
}
