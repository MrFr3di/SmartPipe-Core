using SmartPipe.Extensions.Sinks;
using SmartPipe.Extensions.Transforms;

namespace SmartPipe.Extensions.Tests;

public sealed class Sp22007OwnershipContractTests
{
    [Fact]
    public void MovedTypesArePhysicallyOwnedByTheirLeafPackages()
    {
        Assert.Equal("SmartPipe.Extensions.Channels", typeof(ChannelMerge).Assembly.GetName().Name);
        Assert.Equal("SmartPipe.Extensions.Transforms", typeof(CompositeTransform<>).Assembly.GetName().Name);
        Assert.Equal("SmartPipe.Extensions.Transforms", typeof(FilterTransform<>).Assembly.GetName().Name);
        Assert.Equal("SmartPipe.Extensions.Logging", typeof(LoggerSink<>).Assembly.GetName().Name);
        Assert.Equal("SmartPipe.Extensions.DataAnnotations", typeof(ValidationTransform<>).Assembly.GetName().Name);
    }
}
