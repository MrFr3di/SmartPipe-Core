using System.Runtime.CompilerServices;
using SmartPipe.Extensions.Selectors;
using SmartPipe.Extensions.Sinks;
using SmartPipe.Extensions.Transforms;

[assembly: TypeForwardedTo(typeof(JsonFileSource<>))]
[assembly: TypeForwardedTo(typeof(DeadLetterSource<>))]
#pragma warning disable RS0027 // Existing optional constructors are preserved by type forwarding.
[assembly: TypeForwardedTo(typeof(JsonFileSink<>))]
[assembly: TypeForwardedTo(typeof(DeadLetterSink<>))]
[assembly: TypeForwardedTo(typeof(DeadLetterWriteFailureMode))]
[assembly: TypeForwardedTo(typeof(DeadLetterWriteException))]
[assembly: TypeForwardedTo(typeof(JsonTransform<,>))]
#pragma warning restore RS0027
