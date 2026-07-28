using SmartPipe.Core;
var breaker = new CircuitBreaker();
if (!breaker.AllowRequest()) return 1;
Console.WriteLine("CONSUMER_OK core-trim");
return 0;
