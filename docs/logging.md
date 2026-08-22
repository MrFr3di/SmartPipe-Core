# Logging

Install `SmartPipe.Extensions.Logging` for `LoggerSink<T>`. The shipped one-argument
constructor remains non-obsolete and preserves raw structured payload logging for
source and binary compatibility.

New code should pass `LoggerSinkOptions<T>`. `PayloadMode.None` logs no payload;
`Formatted` requires an explicit formatter and enforces the configured length cap;
`UnsafeRaw` is an explicit opt-in to the legacy exposure. Trace identifiers can be
disabled independently. Disabled log levels do not invoke formatters. Never place
credentials, tokens, service providers, or exception graphs in payload formatters.
