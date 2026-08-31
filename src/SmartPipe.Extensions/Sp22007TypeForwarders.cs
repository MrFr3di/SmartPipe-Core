using System.Runtime.CompilerServices;
using SmartPipe.Extensions;
using SmartPipe.Extensions.Sinks;
using SmartPipe.Extensions.Transforms;

#pragma warning disable RS0027 // Forwarded optional overloads preserve the shipped facade contract.
[assembly: TypeForwardedTo(typeof(ChannelMerge))]
[assembly: TypeForwardedTo(typeof(CompositeTransform<>))]
[assembly: TypeForwardedTo(typeof(CompressionAlgorithm))]
[assembly: TypeForwardedTo(typeof(CompressionTransform))]
[assembly: TypeForwardedTo(typeof(ConditionalTransform<>))]
[assembly: TypeForwardedTo(typeof(FilterTransform<>))]
[assembly: TypeForwardedTo(typeof(FilterValidationExtensions))]
[assembly: TypeForwardedTo(typeof(ValidationTransform<>))]
[assembly: TypeForwardedTo(typeof(LoggerSink<>))]
#pragma warning restore RS0027
