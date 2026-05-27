using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Engine;

public class RunInBackgroundTests
{
    [Fact]
    public async Task RunInBackground_ShouldReturnReader()
    {
        var source = new SimpleSource<int>(1, 2, 3);
        var transformer = new PassthroughTransformer<int>();

        var pipe = new SmartPipeChannel<int, int>();
        pipe.AddSource(source);
        pipe.AddTransformer(transformer);
        pipe.AddSink(new CollectionSink<int>()); // Sink нужен для Validate()

        var reader = pipe.RunInBackground();

        var results = new List<int>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var result in reader.ReadAllAsync(cts.Token))
        {
            if (result.IsSuccess)
                results.Add(result.Value);
        }

        results.Should().Equal(1, 2, 3);
        reader.Completion.IsCompletedSuccessfully.Should().BeTrue();
    }
}
