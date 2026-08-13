namespace SmartPipe.Extensions.HealthChecks;

internal sealed class SmartPipeHealthCheckRegistrationStore
{
    private readonly object _gate = new();
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);

    internal Reservation Reserve(string name)
    {
        Monitor.Enter(_gate);
        if (_names.Contains(name))
        {
            Monitor.Exit(_gate);
            throw new InvalidOperationException($"A health check named '{name}' is already registered by SmartPipe.");
        }

        return new(this, name);
    }

    internal sealed class Reservation
    {
        private readonly SmartPipeHealthCheckRegistrationStore _store;
        private readonly string _name;
        private bool _completed;

        internal Reservation(SmartPipeHealthCheckRegistrationStore store, string name)
        {
            _store = store;
            _name = name;
        }

        internal void Commit()
        {
            if (_completed) throw new InvalidOperationException("Registration reservation is already completed.");
            _store._names.Add(_name);
            Complete();
        }

        internal void Rollback()
        {
            if (!_completed) Complete();
        }

        private void Complete()
        {
            _completed = true;
            Monitor.Exit(_store._gate);
        }
    }
}
