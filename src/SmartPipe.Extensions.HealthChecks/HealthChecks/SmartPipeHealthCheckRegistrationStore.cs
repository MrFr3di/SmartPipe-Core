namespace SmartPipe.Extensions.HealthChecks;

internal sealed class SmartPipeHealthCheckRegistrationStore
{
    private readonly object _gate = new();
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);

    internal void Register(string name, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_gate)
        {
            if (!_names.Add(name))
                throw new InvalidOperationException($"A health check named '{name}' is already registered by SmartPipe.");

            try
            {
                callback();
            }
            catch
            {
                _names.Remove(name);
                throw;
            }
        }
    }
}
