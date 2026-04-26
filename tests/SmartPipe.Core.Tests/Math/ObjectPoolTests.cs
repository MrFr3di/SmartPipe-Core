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
}
