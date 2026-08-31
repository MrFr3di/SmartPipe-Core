using System.Threading.Channels;
using SmartPipe.Extensions;

var first = Channel.CreateUnbounded<int>();
var second = Channel.CreateUnbounded<int>();
await first.Writer.WriteAsync(20);
await second.Writer.WriteAsync(22);
first.Writer.Complete();
second.Writer.Complete();

var values = new List<int>();
await foreach (var value in ChannelMerge.Merge(first.Reader, second.Reader).ReadAllAsync())
    values.Add(value);

if (values.Sum() != 42)
    return 1;

Console.WriteLine("CONSUMER_OK channels-direct");
return 0;
