using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Types;

public class ProcessingContextLineageTests
{
    [Fact]
    public void LineageKeys_ShouldBeAvailable()
    {
        ProcessingContext<int>.LineageSource.Should().Be("lineage_source");
        ProcessingContext<int>.LineagePipeline.Should().Be("lineage_pipeline");
    }

    [Fact]
    public void Metadata_CanStoreLineage()
    {
        var ctx = new ProcessingContext<string>("test");
        ctx.Metadata[ProcessingContext<string>.LineageSource] = "orders_db";
        ctx.Metadata[ProcessingContext<string>.LineagePipeline] = "etl_main";
        
        ctx.Metadata["lineage_source"].Should().Be("orders_db");
        ctx.Metadata["lineage_pipeline"].Should().Be("etl_main");
    }
}
