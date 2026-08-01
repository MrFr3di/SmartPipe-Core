using SmartPipe.Core;
using SmartPipe.Extensions.DependencyInjection;

namespace SmartPipe.Extensions.Hosting.Tests.Registration;

public sealed class SmartPipeHostedRegistrationStoreTests
{
    [Fact]
    public void EmptyStore_ReturnsEmptyImmutableSnapshot()
    {
        var snapshot = new SmartPipeHostedRegistrationStore().SnapshotOrdered();

        Assert.True(snapshot.IsEmpty);
    }

    [Fact]
    public void FirstReservation_CommitsSuccessfully()
    {
        var store = new SmartPipeHostedRegistrationStore();
        var reservation = store.Reserve(new PipelineKey("orders"), typeof(int), typeof(string));
        var descriptor = CreateDescriptor(reservation, order: 0);

        store.Commit(reservation, descriptor);

        Assert.Same(descriptor, Assert.Single(store.SnapshotOrdered()));
    }

    [Fact]
    public void DuplicateReservation_IncludesKeyAndBothTypePairs()
    {
        var store = new SmartPipeHostedRegistrationStore();
        var first = store.Reserve(new PipelineKey("shared"), typeof(int), typeof(string));
        store.Commit(first, CreateDescriptor(first, order: 0));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            store.Reserve(new PipelineKey("shared"), typeof(Guid), typeof(decimal)));

        Assert.Contains("shared", exception.Message, StringComparison.Ordinal);
        Assert.Contains(typeof(int).FullName!, exception.Message, StringComparison.Ordinal);
        Assert.Contains(typeof(string).FullName!, exception.Message, StringComparison.Ordinal);
        Assert.Contains(typeof(Guid).FullName!, exception.Message, StringComparison.Ordinal);
        Assert.Contains(typeof(decimal).FullName!, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateReservation_IsIndependentOfGenericTypes()
    {
        var store = new SmartPipeHostedRegistrationStore();
        var reservation = store.Reserve(new PipelineKey("shared"), typeof(int), typeof(int));
        store.Commit(reservation, CreateDescriptor(reservation, order: 0));

        Assert.Throws<InvalidOperationException>(() =>
            store.Reserve(new PipelineKey("shared"), typeof(string), typeof(string)));
        Assert.Single(store.SnapshotOrdered());
    }

    [Fact]
    public void Rollback_AllowsRetry()
    {
        var store = new SmartPipeHostedRegistrationStore();
        var failed = store.Reserve(new PipelineKey("retry"), typeof(int), typeof(string));

        store.Rollback(failed);
        var retry = store.Reserve(new PipelineKey("retry"), typeof(int), typeof(string));
        var descriptor = CreateDescriptor(retry, order: 0);
        store.Commit(retry, descriptor);

        Assert.Same(descriptor, Assert.Single(store.SnapshotOrdered()));
    }

    [Fact]
    public void Rollback_WithForeignOrCommittedToken_RemovesNothing()
    {
        var store = new SmartPipeHostedRegistrationStore();
        var otherStore = new SmartPipeHostedRegistrationStore();
        var committed = store.Reserve(new PipelineKey("committed"), typeof(int), typeof(string));
        var pending = store.Reserve(new PipelineKey("pending"), typeof(Guid), typeof(decimal));
        store.Commit(committed, CreateDescriptor(committed, order: 0));
        var foreign = otherStore.Reserve(new PipelineKey("pending"), typeof(Guid), typeof(decimal));

        store.Rollback(committed);
        store.Rollback(foreign);

        Assert.Single(store.SnapshotOrdered());
        Assert.Throws<InvalidOperationException>(() =>
            store.Reserve(new PipelineKey("committed"), typeof(int), typeof(string)));
        Assert.Throws<InvalidOperationException>(() =>
            store.Reserve(new PipelineKey("pending"), typeof(Guid), typeof(decimal)));

        store.Rollback(pending);
        otherStore.Rollback(foreign);
    }

    [Fact]
    public void Snapshot_OrdersByOrderThenRegistrationOrderThenOrdinalKey()
    {
        var store = new SmartPipeHostedRegistrationStore();
        Commit(store, "z-last", order: 2);
        Commit(store, "first-registered", order: 1);
        Commit(store, "second-registered", order: 1);

        Assert.Equal(
            ["first-registered", "second-registered", "z-last"],
            store.SnapshotOrdered().Select(item => item.Key.Value));

        var tieStore = new SmartPipeHostedRegistrationStore();
        var z = tieStore.Reserve(new PipelineKey("z"), typeof(int), typeof(int));
        var a = tieStore.Reserve(new PipelineKey("a"), typeof(int), typeof(int));
        tieStore.Commit(z, CreateDescriptor(z, order: 0, registrationOrder: 7));
        tieStore.Commit(a, CreateDescriptor(a, order: 0, registrationOrder: 7));

        Assert.Equal(
            ["a", "z"],
            tieStore.SnapshotOrdered().Select(item => item.Key.Value));
    }

    [Fact]
    public void Snapshot_RemainsUnchangedAfterLaterCommit()
    {
        var store = new SmartPipeHostedRegistrationStore();
        Commit(store, "first", order: 0);
        var snapshot = store.SnapshotOrdered();

        Commit(store, "second", order: 0);

        Assert.Equal(["first"], snapshot.Select(item => item.Key.Value));
        Assert.Equal(["first", "second"], store.SnapshotOrdered().Select(item => item.Key.Value));
    }

    [Fact]
    public void Descriptor_ContainsOnlyImmutableMetadata()
    {
        var memberTypes = typeof(HostedPipelineDescriptor)
            .GetProperties()
            .Select(property => property.PropertyType)
            .Concat(typeof(HostedPipelineDescriptor).GetFields(
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic).Select(field => field.FieldType));

        Assert.DoesNotContain(memberTypes, type =>
            type == typeof(IServiceProvider)
            || typeof(Delegate).IsAssignableFrom(type)
            || (type.IsGenericType
                && (type.GetGenericTypeDefinition() == typeof(ISmartPipeRunFactory<,>)
                    || type.GetGenericTypeDefinition() == typeof(PipelineRun<>))));
        Assert.DoesNotContain(
            typeof(SmartPipeHostedPipelineOptions).Assembly.GetExportedTypes(),
            type => type.Name.Contains("Registry", StringComparison.Ordinal)
                || type.Name.Contains("Catalog", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Snapshot_IsConsistentDuringConcurrentCommits()
    {
        var store = new SmartPipeHostedRegistrationStore();
        var cancellationToken = TestContext.Current.CancellationToken;
        using var start = new ManualResetEventSlim();
        var writer = Task.Run(() =>
        {
            start.Wait(cancellationToken);
            for (var index = 0; index < 100; index++)
                Commit(store, $"pipeline-{index:D3}", order: index % 3);
        }, cancellationToken);
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            start.Wait(cancellationToken);
            do
            {
                AssertOrderedAndUnique(store.SnapshotOrdered());
            }
            while (!writer.IsCompleted);

            AssertOrderedAndUnique(store.SnapshotOrdered());
        }, cancellationToken)).ToArray();

        start.Set();
        await Task.WhenAll(readers.Append(writer));

        Assert.Equal(100, store.SnapshotOrdered().Length);
    }

    [Fact]
    public void Reserve_RejectsDefaultKey()
    {
        var store = new SmartPipeHostedRegistrationStore();

        Assert.Throws<ArgumentException>(() =>
            store.Reserve(default, typeof(int), typeof(string)));
    }

    private static void Commit(SmartPipeHostedRegistrationStore store, string key, int order)
    {
        var reservation = store.Reserve(new PipelineKey(key), typeof(int), typeof(int));
        store.Commit(reservation, CreateDescriptor(reservation, order));
    }

    private static HostedPipelineDescriptor CreateDescriptor(
        HostedRegistrationReservation reservation,
        int order,
        int? registrationOrder = null) =>
        new()
        {
            Key = reservation.Key,
            InputType = reservation.InputType,
            OutputType = reservation.OutputType,
            Order = order,
            RegistrationOrder = registrationOrder ?? reservation.RegistrationOrder,
            DrainTimeout = TimeSpan.FromSeconds(30),
            FailureBehavior = SmartPipeHostedPipelineFailureBehavior.StopApplication,
            CompletionBehavior = SmartPipeHostedCompletionBehavior.KeepHostAlive,
        };

    private static void AssertOrderedAndUnique(
        IReadOnlyList<HostedPipelineDescriptor> snapshot)
    {
        Assert.Equal(snapshot.Count, snapshot.Select(item => item.Key).Distinct().Count());
        Assert.Equal(
            snapshot.Select(item => item.Key.Value),
            snapshot
                .OrderBy(item => item.Order)
                .ThenBy(item => item.RegistrationOrder)
                .ThenBy(item => item.Key.Value, StringComparer.Ordinal)
                .Select(item => item.Key.Value));
    }
}
