using System.Collections.Immutable;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Hosting;

internal sealed class SmartPipeHostedRegistrationStore
{
    private readonly object _gate = new();
    private readonly Dictionary<PipelineKey, HostedRegistrationReservation> _registrations = [];

    internal HostedRegistrationReservation Reserve(
        PipelineKey key,
        Type inputType,
        Type outputType)
    {
        if (key.IsEmpty)
            throw new ArgumentException("Pipeline key must be initialized.", nameof(key));

        ArgumentNullException.ThrowIfNull(inputType);
        ArgumentNullException.ThrowIfNull(outputType);

        lock (_gate)
        {
            if (_registrations.TryGetValue(key, out var existing))
            {
                throw new InvalidOperationException(
                    $"A hosted pipeline with key '{key.Value}' already uses "
                    + $"'{existing.InputType.FullName} -> {existing.OutputType.FullName}'; "
                    + $"cannot register '{inputType.FullName} -> {outputType.FullName}'.");
            }

            var reservation = new HostedRegistrationReservation(
                this,
                key,
                inputType,
                outputType);
            _registrations.Add(key, reservation);
            return reservation;
        }
    }

    internal void Commit(
        HostedRegistrationReservation reservation,
        HostedPipelineDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        ArgumentNullException.ThrowIfNull(descriptor);

        lock (_gate)
        {
            if (!ReferenceEquals(reservation.Store, this)
                || reservation.IsCompleted
                || !_registrations.TryGetValue(reservation.Key, out var current)
                || !ReferenceEquals(current, reservation))
            {
                throw new InvalidOperationException("Hosted registration reservation is not active.");
            }

            if (descriptor.Key != reservation.Key
                || descriptor.InputType != reservation.InputType
                || descriptor.OutputType != reservation.OutputType)
            {
                throw new InvalidOperationException(
                    "Hosted registration metadata does not match its reservation.");
            }

            reservation.Descriptor = descriptor;
            reservation.IsCompleted = true;
        }
    }

    internal void Rollback(HostedRegistrationReservation reservation)
    {
        ArgumentNullException.ThrowIfNull(reservation);

        lock (_gate)
        {
            if (!ReferenceEquals(reservation.Store, this)
                || reservation.IsCompleted
                || !_registrations.TryGetValue(reservation.Key, out var current)
                || !ReferenceEquals(current, reservation))
            {
                return;
            }

            _registrations.Remove(reservation.Key);
            reservation.IsCompleted = true;
        }
    }

    internal ImmutableArray<HostedPipelineDescriptor> Snapshot()
    {
        lock (_gate)
        {
            return _registrations.Values
                .Select(static reservation => reservation.Descriptor)
                .Where(static descriptor => descriptor is not null)
                .Select(static descriptor => descriptor!)
                .OrderBy(static descriptor => descriptor.Key.Value, StringComparer.Ordinal)
                .ToImmutableArray();
        }
    }
}
