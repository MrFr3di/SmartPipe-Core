using System.Runtime.CompilerServices;
using SmartPipe.Extensions.Selectors;
using SmartPipe.Extensions.Sinks;

#pragma warning disable RS0027 // Existing optional constructors are preserved by type forwarding.
[assembly: TypeForwardedTo(typeof(DapperSelector<>))]
[assembly: TypeForwardedTo(typeof(DbSink<>))]
#pragma warning restore RS0027
