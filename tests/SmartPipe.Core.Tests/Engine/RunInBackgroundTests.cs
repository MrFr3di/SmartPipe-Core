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
        
        reader.Should().NotBeNull();
        reader.Completion.IsCompleted.Should().BeFalse(); // Ещё работает
    }
}
