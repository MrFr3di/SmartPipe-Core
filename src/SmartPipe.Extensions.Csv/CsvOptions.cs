#nullable enable

using System.Globalization;
using System.Text;

namespace SmartPipe.Extensions.Csv;

/// <summary>Controls how malformed or structurally invalid CSV records are handled.</summary>
public enum CsvInvalidRecordBehavior
{
    /// <summary>Stop reading and throw.</summary>
    Throw = 0,
    /// <summary>Log a bounded diagnostic and continue at a proven record boundary.</summary>
    SkipAndLog = 1,
}

/// <summary>Controls missing-field handling for strict CSV reads.</summary>
public enum CsvMissingFieldBehavior
{
    /// <summary>Stop reading and throw.</summary>
    Throw = 0,
    /// <summary>Allow CsvHelper to assign the type default.</summary>
    UseDefault = 1,
}

/// <summary>Controls header validation for strict CSV reads.</summary>
public enum CsvHeaderValidationBehavior
{
    /// <summary>Stop reading when the header does not match the map.</summary>
    Throw = 0,
    /// <summary>Do not validate header names.</summary>
    Ignore = 1,
}

/// <summary>Controls how a strict CSV sink opens its file.</summary>
public enum CsvFileOpenMode
{
    /// <summary>Create or replace the output file.</summary>
    Create = 0,
    /// <summary>Append after strict existing-file preflight.</summary>
    Append = 1,
}

/// <summary>Controls formula-like cell handling for spreadsheet consumers.</summary>
public enum CsvFormulaInjectionMode
{
    /// <summary>Use CsvHelper's normal output behavior.</summary>
    None = 0,
    /// <summary>Prefix formula-like cells with the configured escape character.</summary>
    Escape = 1,
    /// <summary>Strip the leading formula marker.</summary>
    Strip = 2,
    /// <summary>Reject formula-like cells.</summary>
    Throw = 3,
}

/// <summary>Options for the strict file-oriented CSV source.</summary>
public sealed record CsvSourceOptions
{
    /// <summary>Gets the single-character field delimiter.</summary>
    public char Delimiter { get; init; } = ',';
    /// <summary>Gets the field quote character.</summary>
    public char Quote { get; init; } = '"';
    /// <summary>Gets the culture used by CsvHelper conversions.</summary>
    public CultureInfo Culture { get; init; } = CultureInfo.InvariantCulture;
    /// <summary>Gets the configured input encoding.</summary>
    public Encoding Encoding { get; init; } = new UTF8Encoding(false, true);
    /// <summary>Gets whether a supported byte-order mark overrides the configured encoding.</summary>
    public bool DetectEncodingFromByteOrderMarks { get; init; } = true;
    /// <summary>Gets whether the first accepted record is a header.</summary>
    public bool HasHeaderRecord { get; init; } = true;
    /// <summary>Gets invalid-record handling.</summary>
    public CsvInvalidRecordBehavior InvalidRecordBehavior { get; init; } = CsvInvalidRecordBehavior.Throw;
    /// <summary>Gets missing-field handling.</summary>
    public CsvMissingFieldBehavior MissingFieldBehavior { get; init; } = CsvMissingFieldBehavior.Throw;
    /// <summary>Gets header-validation handling.</summary>
    public CsvHeaderValidationBehavior HeaderValidationBehavior { get; init; } = CsvHeaderValidationBehavior.Throw;
    /// <summary>Gets whether changing column counts are detected.</summary>
    public bool DetectColumnCountChanges { get; init; } = true;
    /// <summary>Gets whether blank lines are ignored.</summary>
    public bool IgnoreBlankLines { get; init; } = true;
    /// <summary>Gets the maximum retained logical-record character count.</summary>
    public int MaxRecordSizeCharacters { get; init; } = 524_288;
    /// <summary>Gets the maximum retained field character count.</summary>
    public int MaxFieldSizeCharacters { get; init; } = 262_144;
    /// <summary>Gets the maximum logical column count.</summary>
    public int MaxColumnCount { get; init; } = 1_024;
    /// <summary>Gets the asynchronous reader buffer size.</summary>
    public int BufferSize { get; init; } = 16_384;
}

/// <summary>Options for the strict file-oriented CSV sink.</summary>
public sealed record CsvSinkOptions
{
    /// <summary>Gets the single-character field delimiter.</summary>
    public char Delimiter { get; init; } = ',';
    /// <summary>Gets the field quote character.</summary>
    public char Quote { get; init; } = '"';
    /// <summary>Gets the culture used by CsvHelper conversions.</summary>
    public CultureInfo Culture { get; init; } = CultureInfo.InvariantCulture;
    /// <summary>Gets the configured output encoding.</summary>
    public Encoding Encoding { get; init; } = new UTF8Encoding(false, true);
    /// <summary>Gets whether a BOM is emitted for a newly created file.</summary>
    public bool EmitByteOrderMark { get; init; }
    /// <summary>Gets whether the sink writes a header.</summary>
    public bool HasHeaderRecord { get; init; } = true;
    /// <summary>Gets the output open mode.</summary>
    public CsvFileOpenMode OpenMode { get; init; }
    /// <summary>Gets whether an existing append header is validated.</summary>
    public bool ValidateExistingHeaderOnAppend { get; init; } = true;
    /// <summary>Gets whether a header is written to an empty file.</summary>
    public bool WriteHeaderWhenFileIsEmpty { get; init; } = true;
    /// <summary>Gets the configured output newline.</summary>
    public string NewLine { get; init; } = "\r\n";
    /// <summary>Gets the maximum serialized record character count.</summary>
    public int MaxRecordSizeCharacters { get; init; } = 524_288;
    /// <summary>Gets the number of records between flushes.</summary>
    public int FlushEveryRecords { get; init; } = 1;
    /// <summary>Gets spreadsheet formula-like cell handling.</summary>
    public CsvFormulaInjectionMode FormulaInjectionMode { get; init; }
    /// <summary>Gets the asynchronous writer buffer size.</summary>
    public int BufferSize { get; init; } = 16_384;
}
