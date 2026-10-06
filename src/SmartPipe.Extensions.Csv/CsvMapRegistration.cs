using CsvHelper;
using CsvHelper.Configuration;

namespace SmartPipe.Extensions.Csv;

/// <summary>Describes an automatic or per-run CsvHelper class-map factory.</summary>
/// <typeparam name="T">Mapped record type.</typeparam>
public sealed class CsvMapRegistration<T>
{
    private readonly Func<ClassMap<T>>? _mapFactory;

    private CsvMapRegistration(Func<ClassMap<T>>? mapFactory)
    {
        _mapFactory = mapFactory;
    }

    /// <summary>Gets the CsvHelper automatic-map registration.</summary>
    public static CsvMapRegistration<T> Auto { get; } = new(mapFactory: null);

    /// <summary>Creates a registration that constructs a new map for every activated run.</summary>
    /// <typeparam name="TMap">Map type to construct.</typeparam>
    public static CsvMapRegistration<T> From<TMap>()
        where TMap : ClassMap<T>, new() =>
        new(() => new TMap());

    /// <summary>Creates a registration backed by a fresh-map factory.</summary>
    /// <param name="mapFactory">Factory invoked once for each activated run.</param>
    public static CsvMapRegistration<T> FromFactory(Func<ClassMap<T>> mapFactory)
    {
        ArgumentNullException.ThrowIfNull(mapFactory);
        return new(mapFactory);
    }

    internal void Register(CsvContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_mapFactory is null)
            return;

        var map = _mapFactory();
        if (map is null)
            throw new InvalidOperationException("The CSV map factory returned null.");
        context.RegisterClassMap(map);
    }
}
