using System.Globalization;
using System.Text;
using SmartPipe.Extensions.Csv;

namespace SmartPipe.Extensions.Csv.Internal;

internal sealed record CsvSourceOptionsSnapshot(
    char Delimiter,
    char Quote,
    CultureInfo Culture,
    Encoding Encoding,
    bool DetectEncodingFromByteOrderMarks,
    bool HasHeaderRecord,
    CsvInvalidRecordBehavior InvalidRecordBehavior,
    CsvMissingFieldBehavior MissingFieldBehavior,
    CsvHeaderValidationBehavior HeaderValidationBehavior,
    bool DetectColumnCountChanges,
    bool IgnoreBlankLines,
    int MaxRecordSizeCharacters,
    int MaxFieldSizeCharacters,
    int MaxColumnCount,
    int BufferSize)
{
    public static CsvSourceOptionsSnapshot Create(CsvSourceOptions? options, bool loggerAvailable)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateCommon(
            options.Delimiter,
            options.Quote,
            options.Culture,
            options.Encoding,
            options.MaxRecordSizeCharacters,
            options.MaxFieldSizeCharacters,
            options.MaxColumnCount,
            options.BufferSize);
        ValidateEnum(options.InvalidRecordBehavior, nameof(options.InvalidRecordBehavior));
        ValidateEnum(options.MissingFieldBehavior, nameof(options.MissingFieldBehavior));
        ValidateEnum(options.HeaderValidationBehavior, nameof(options.HeaderValidationBehavior));
        if (options.InvalidRecordBehavior == CsvInvalidRecordBehavior.SkipAndLog && !loggerAvailable)
            throw new ArgumentException(
                "SkipAndLog requires a borrowed ILoggerFactory.",
                nameof(loggerAvailable));

        return new(
            options.Delimiter,
            options.Quote,
            CloneCulture(options.Culture),
            CloneEncoding(options.Encoding),
            options.DetectEncodingFromByteOrderMarks,
            options.HasHeaderRecord,
            options.InvalidRecordBehavior,
            options.MissingFieldBehavior,
            options.HeaderValidationBehavior,
            options.DetectColumnCountChanges,
            options.IgnoreBlankLines,
            options.MaxRecordSizeCharacters,
            options.MaxFieldSizeCharacters,
            options.MaxColumnCount,
            options.BufferSize);
    }

    private static void ValidateCommon(
        char delimiter,
        char quote,
        CultureInfo? culture,
        Encoding? encoding,
        int maxRecordSizeCharacters,
        int maxFieldSizeCharacters,
        int maxColumnCount,
        int bufferSize)
    {
        if (delimiter == quote || delimiter is '\r' or '\n' or '\0' || quote is '\r' or '\n' or '\0')
            throw new ArgumentException("CSV delimiter and quote must be distinct printable characters.");
        ArgumentNullException.ThrowIfNull(culture);
        ArgumentNullException.ThrowIfNull(encoding);
        if (maxRecordSizeCharacters <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxRecordSizeCharacters));
        if (maxFieldSizeCharacters <= 0 || maxFieldSizeCharacters > maxRecordSizeCharacters)
            throw new ArgumentOutOfRangeException(nameof(maxFieldSizeCharacters));
        if (maxColumnCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxColumnCount));
        if (bufferSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(bufferSize));
    }

    private static void ValidateEnum<TEnum>(TEnum value, string parameterName)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
            throw new ArgumentOutOfRangeException(parameterName);
    }

    internal static CultureInfo CloneCulture(CultureInfo culture)
    {
        var clone = (CultureInfo)culture.Clone();
        return CultureInfo.ReadOnly(clone);
    }

    internal static void ValidateForSink(
        char delimiter,
        char quote,
        CultureInfo? culture,
        Encoding? encoding,
        int maxRecordSizeCharacters,
        int bufferSize)
    {
        if (delimiter == quote || delimiter is '\r' or '\n' or '\0' || quote is '\r' or '\n' or '\0')
            throw new ArgumentException("CSV delimiter and quote must be distinct printable characters.");
        ArgumentNullException.ThrowIfNull(culture);
        ArgumentNullException.ThrowIfNull(encoding);
        if (maxRecordSizeCharacters <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxRecordSizeCharacters));
        if (bufferSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(bufferSize));
    }

    internal static Encoding CloneEncoding(Encoding encoding)
    {
        var clone = (Encoding)encoding.Clone();
        try
        {
            clone.EncoderFallback = EncoderFallback.ExceptionFallback;
            clone.DecoderFallback = DecoderFallback.ExceptionFallback;
            return clone;
        }
        catch (InvalidOperationException exception)
        {
            throw new ArgumentException("The configured CSV encoding cannot use strict exception fallbacks.", nameof(encoding), exception);
        }
    }
}

internal sealed record CsvSinkOptionsSnapshot(
    char Delimiter,
    char Quote,
    CultureInfo Culture,
    Encoding Encoding,
    bool EmitByteOrderMark,
    bool HasHeaderRecord,
    CsvFileOpenMode OpenMode,
    bool ValidateExistingHeaderOnAppend,
    bool WriteHeaderWhenFileIsEmpty,
    string NewLine,
    int MaxRecordSizeCharacters,
    int FlushEveryRecords,
    CsvFormulaInjectionMode FormulaInjectionMode,
    int BufferSize)
{
    public static CsvSinkOptionsSnapshot Create(CsvSinkOptions? options)
    {
        ArgumentNullException.ThrowIfNull(options);
        CsvSourceOptionsSnapshot.ValidateForSink(
            options.Delimiter,
            options.Quote,
            options.Culture,
            options.Encoding,
            options.MaxRecordSizeCharacters,
            options.BufferSize);
        if (!Enum.IsDefined(options.OpenMode))
            throw new ArgumentOutOfRangeException(nameof(options.OpenMode));
        if (!Enum.IsDefined(options.FormulaInjectionMode))
            throw new ArgumentOutOfRangeException(nameof(options.FormulaInjectionMode));
        ArgumentNullException.ThrowIfNull(options.NewLine);
        if (options.NewLine.Length == 0)
            throw new ArgumentException("NewLine cannot be empty.", nameof(options));
        if (options.FlushEveryRecords <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.FlushEveryRecords));

        return new(
            options.Delimiter,
            options.Quote,
            CsvSourceOptionsSnapshot.CloneCulture(options.Culture),
            CsvSourceOptionsSnapshot.CloneEncoding(options.Encoding),
            options.EmitByteOrderMark,
            options.HasHeaderRecord,
            options.OpenMode,
            options.ValidateExistingHeaderOnAppend,
            options.WriteHeaderWhenFileIsEmpty,
            options.NewLine,
            options.MaxRecordSizeCharacters,
            options.FlushEveryRecords,
            options.FormulaInjectionMode,
            options.BufferSize);
    }
}
