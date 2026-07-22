using SmartPipe.Core;
var breaker = new CircuitBreaker();
breaker.RecordSuccess();
Console.WriteLine("CONSUMER_OK core-nativeaot");
