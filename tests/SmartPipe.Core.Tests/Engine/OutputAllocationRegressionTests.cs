#pragma warning disable CS0618 // All legacy policies are characterization controls.
using System.Threading.Channels;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Engine;

[Trait("Category", "CorrectnessRegression")]
public sealed class OutputAllocationRegressionTests
{
    public static IEnumerable<object[]> Policies()
    {
        foreach (var sink in new[] { false, true })
        foreach (var kind in Enum.GetValues<PipelineResultKind>())
        foreach (var policy in Enum.GetValues<PipelineOutputPolicy>())
            yield return [sink, kind, (int)policy, false];
        foreach (var sink in new[] { false, true })
        foreach (var kind in Enum.GetValues<PipelineResultKind>())
        foreach (var mode in Enum.GetValues<PipelineOutputMode>())
            yield return [sink, kind, (int)mode, true];
    }

    [Theory]
    [MemberData(nameof(Policies))]
    public void KindGate_PreservesEveryModernAndLegacyPolicy(bool sink, PipelineResultKind kind, int policy, bool legacy)
    {
        var options = legacy ? new PipelineRuntimeOptions { OutputMode = (PipelineOutputMode)policy }
            : new PipelineRuntimeOptions { OutputPolicy = (PipelineOutputPolicy)policy };
        var emitter = new PipelineOutputEmitter<int>(Channel.CreateUnbounded<PipelineOutput<int>>().Writer, options, sink);
        var result = kind switch
        {
            PipelineResultKind.Success => PipelineResult<int>.Success(7, 42),
            PipelineResultKind.Failure => PipelineResult<int>.Failure(new SmartPipeError("error", ErrorType.Permanent), 42),
            PipelineResultKind.Filtered => PipelineResult<int>.Filtered(42),
            PipelineResultKind.Skipped => PipelineResult<int>.Skipped(42),
            _ => throw new InvalidOperationException(),
        };
        var expected = legacy ? policy switch
        {
            0 => true, 1 => !sink || kind == PipelineResultKind.Failure, 2 => !sink, 3 => false,
            _ => throw new InvalidOperationException(),
        } : policy switch
        {
            0 => true, 1 => kind == PipelineResultKind.Failure, 2 => !sink || kind != PipelineResultKind.Success, 3 => !sink,
            _ => throw new InvalidOperationException(),
        };
        Assert.Equal(expected, emitter.ShouldEmit(result));
        Assert.Equal(expected, emitter.ShouldEmit(kind));
    }

    [Fact]
    public void DefaultResult_KeepsBooleanClassification()
    {
        var emitter = new PipelineOutputEmitter<int>(Channel.CreateUnbounded<PipelineOutput<int>>().Writer,
            new PipelineRuntimeOptions(), hasSink: true);
        Assert.True(emitter.ShouldEmit(default(PipelineResult<int>)));
        Assert.False(emitter.ShouldEmit(PipelineResultKind.Success));
    }
}
