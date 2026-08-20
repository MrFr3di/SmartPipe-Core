using SmartPipe.Core;

namespace SmartPipe.Extensions.DependencyInjection;

internal sealed class SmartPipeRegistrationStore
{
    private readonly object _gate = new();
    private readonly Dictionary<PipelineKey, SmartPipeRegistrationDescriptor> _registrations = [];

    internal RegistrationReservation Reserve(PipelineKey key)
    {
        ThrowIfInvalid(key);
#pragma warning disable S2222 // Reservation ownership is completed synchronously by AddPipeline on this thread.
        Monitor.Enter(_gate);
#pragma warning restore S2222
        try
        {
            if (_registrations.ContainsKey(key))
            {
                throw new InvalidOperationException(
                    $"A pipeline with key '{key.Value}' is already registered.");
            }

            return new(this, key, _registrations.Count);
        }
        catch
        {
            Monitor.Exit(_gate);
            throw;
        }
    }

    internal IReadOnlyList<SmartPipeRegistrationDescriptor> GetRegistrations()
    {
        lock (_gate)
        {
            return Array.AsReadOnly(
                _registrations.Values
                    .OrderBy(static registration => registration.RegistrationOrder)
                    .ToArray());
        }
    }

    internal SmartPipeRegistrationDescriptor GetRegistration(PipelineKey key)
    {
        ThrowIfInvalid(key);
        lock (_gate)
        {
            return _registrations.TryGetValue(key, out var registration)
                ? registration
                : throw new KeyNotFoundException(
                    $"No pipeline with key '{key.Value}' is registered.");
        }
    }

    internal bool TryGetRegistration(
        PipelineKey key,
        out SmartPipeRegistrationDescriptor? registration)
    {
        ThrowIfInvalid(key);
        lock (_gate)
        {
            return _registrations.TryGetValue(key, out registration);
        }
    }

    private static void ThrowIfInvalid(PipelineKey key)
    {
        if (key.IsEmpty)
        {
            throw new ArgumentException("Pipeline key must be initialized.", nameof(key));
        }
    }

    internal sealed class RegistrationReservation
    {
        private readonly SmartPipeRegistrationStore _store;
        private readonly PipelineKey _key;
        private bool _completed;

        internal RegistrationReservation(
            SmartPipeRegistrationStore store,
            PipelineKey key,
            int registrationOrder)
        {
            _store = store;
            _key = key;
            RegistrationOrder = registrationOrder;
        }

        internal int RegistrationOrder { get; }

        internal void Commit(SmartPipeRegistrationDescriptor registration)
        {
            ArgumentNullException.ThrowIfNull(registration);
            if (_completed)
            {
                throw new InvalidOperationException("The registration reservation is already completed.");
            }

            if (registration.Key != _key || registration.RegistrationOrder != RegistrationOrder)
            {
                throw new InvalidOperationException("Registration metadata does not match its reservation.");
            }

            _store._registrations.Add(_key, registration);
            Complete();
        }

        internal void Rollback()
        {
            if (!_completed)
            {
                Complete();
            }
        }

        private void Complete()
        {
            _completed = true;
            Monitor.Exit(_store._gate);
        }
    }
}
