using System.Runtime.CompilerServices;
using SmartPipe.Extensions.Selectors;
using SmartPipe.Extensions.Sinks;
using SmartPipe.Extensions.Transforms;

[assembly: TypeForwardedTo(typeof(CsvFileSource<>))]
#pragma warning disable RS0027 // Existing optional constructor is preserved by type forwarding.
[assembly: TypeForwardedTo(typeof(CsvFileSink<>))]
[assembly: TypeForwardedTo(typeof(CsvTransform<,>))]
#pragma warning restore RS0027
