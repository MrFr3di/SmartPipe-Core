using System.Reflection;
using System.Diagnostics.CodeAnalysis;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Csv.Tests;

public sealed class CsvStrictContractTests
{
    [Fact]
    public void LeafAssembly_ExposesTheFinalRootNamespaceContracts()
    {
        var assembly = LoadLeafAssembly();

        AssertEnum(assembly, "CsvInvalidRecordBehavior", ("Throw", 0), ("SkipAndLog", 1));
        AssertEnum(assembly, "CsvMissingFieldBehavior", ("Throw", 0), ("UseDefault", 1));
        AssertEnum(assembly, "CsvHeaderValidationBehavior", ("Throw", 0), ("Ignore", 1));
        AssertEnum(assembly, "CsvFileOpenMode", ("Create", 0), ("Append", 1));
        AssertEnum(assembly, "CsvFormulaInjectionMode", ("None", 0), ("Escape", 1), ("Strip", 2), ("Throw", 3));

        AssertRecordProperties(
            assembly,
            "CsvSourceOptions",
            ("Delimiter", typeof(char)),
            ("Quote", typeof(char)),
            ("Culture", typeof(System.Globalization.CultureInfo)),
            ("Encoding", typeof(System.Text.Encoding)),
            ("DetectEncodingFromByteOrderMarks", typeof(bool)),
            ("HasHeaderRecord", typeof(bool)),
            ("InvalidRecordBehavior", "CsvInvalidRecordBehavior"),
            ("MissingFieldBehavior", "CsvMissingFieldBehavior"),
            ("HeaderValidationBehavior", "CsvHeaderValidationBehavior"),
            ("DetectColumnCountChanges", typeof(bool)),
            ("IgnoreBlankLines", typeof(bool)),
            ("MaxRecordSizeCharacters", typeof(int)),
            ("MaxFieldSizeCharacters", typeof(int)),
            ("MaxColumnCount", typeof(int)),
            ("BufferSize", typeof(int)));

        AssertRecordProperties(
            assembly,
            "CsvSinkOptions",
            ("Delimiter", typeof(char)),
            ("Quote", typeof(char)),
            ("Culture", typeof(System.Globalization.CultureInfo)),
            ("Encoding", typeof(System.Text.Encoding)),
            ("EmitByteOrderMark", typeof(bool)),
            ("HasHeaderRecord", typeof(bool)),
            ("OpenMode", "CsvFileOpenMode"),
            ("ValidateExistingHeaderOnAppend", typeof(bool)),
            ("WriteHeaderWhenFileIsEmpty", typeof(bool)),
            ("NewLine", typeof(string)),
            ("MaxRecordSizeCharacters", typeof(int)),
            ("FlushEveryRecords", typeof(int)),
            ("FormulaInjectionMode", "CsvFormulaInjectionMode"),
            ("BufferSize", typeof(int)));

        RequireType(assembly, "CsvMapRegistration`1");
        RequireType(assembly, "CsvPipelineComponents");
        RequireType(assembly, "CsvPipelineDefinitionBuilder");
        RequireType(assembly, "CsvPipelineDefinitionBuilderExtensions");
    }

    [Fact]
    public void LeafFactories_ExposeOnlyTheFinalFileDefinitionOverloads()
    {
        var assembly = LoadLeafAssembly();
        var components = RequireType(assembly, "CsvPipelineComponents");
        var builder = RequireType(assembly, "CsvPipelineDefinitionBuilder");
        var extensions = RequireType(assembly, "CsvPipelineDefinitionBuilderExtensions");
        var registration = RequireType(assembly, "CsvMapRegistration`1");

        AssertFactory(components, "FileSource", parameterCount: 4);
        AssertFactory(components, "FileSink", parameterCount: 3);
        AssertMethod(builder, "FromCsvFile", parameterCount: 5);
        Assert.Equal(
            2,
            extensions.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Count(method => method.Name == "ToCsvFile" && method.IsGenericMethodDefinition));

        var mapMethods = registration.GetMethods(BindingFlags.Public | BindingFlags.Static);
        Assert.Contains(mapMethods, method => method.Name == "From" && method.IsGenericMethodDefinition && method.GetParameters().Length == 0);
        Assert.Contains(mapMethods, method => method.Name == "FromFactory" && !method.IsGenericMethodDefinition && method.GetParameters().Length == 1);
        Assert.NotNull(registration.GetProperty("Auto", BindingFlags.Public | BindingFlags.Static));
    }

    [Fact]
    public void StrictExecutableEntryPoints_AdvertiseTrimAndDynamicCodeBoundaries()
    {
        var assembly = LoadLeafAssembly();
        foreach (var typeName in new[]
        {
            "CsvPipelineComponents",
            "CsvPipelineDefinitionBuilder",
            "CsvPipelineDefinitionBuilderExtensions",
        })
        {
            var methods = RequireType(assembly, typeName)
                .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
            Assert.NotEmpty(methods);
            Assert.All(methods, method =>
            {
                Assert.NotNull(method.GetCustomAttribute<RequiresUnreferencedCodeAttribute>());
                Assert.NotNull(method.GetCustomAttribute<RequiresDynamicCodeAttribute>());
            });
        }
    }

    [Fact]
    public void StrictOptions_UseTheFinalSafeDefaults()
    {
        var assembly = LoadLeafAssembly();
        var source = Activator.CreateInstance(RequireType(assembly, "CsvSourceOptions"))!;
        Assert.Equal(',', GetOption(source, "Delimiter"));
        Assert.Equal('"', GetOption(source, "Quote"));
        Assert.Equal(string.Empty, ((System.Globalization.CultureInfo)GetOption(source, "Culture")!).Name);
        var sourceEncoding = (System.Text.Encoding)GetOption(source, "Encoding")!;
        Assert.Empty(sourceEncoding.GetPreamble());
        Assert.Same(System.Text.EncoderFallback.ExceptionFallback, sourceEncoding.EncoderFallback);
        Assert.Same(System.Text.DecoderFallback.ExceptionFallback, sourceEncoding.DecoderFallback);
        Assert.Equal(true, GetOption(source, "DetectEncodingFromByteOrderMarks"));
        Assert.Equal(true, GetOption(source, "HasHeaderRecord"));
        Assert.Equal(0, Convert.ToInt32(GetOption(source, "InvalidRecordBehavior")));
        Assert.Equal(0, Convert.ToInt32(GetOption(source, "MissingFieldBehavior")));
        Assert.Equal(0, Convert.ToInt32(GetOption(source, "HeaderValidationBehavior")));
        Assert.Equal(true, GetOption(source, "DetectColumnCountChanges"));
        Assert.Equal(true, GetOption(source, "IgnoreBlankLines"));
        Assert.Equal(524_288, GetOption(source, "MaxRecordSizeCharacters"));
        Assert.Equal(262_144, GetOption(source, "MaxFieldSizeCharacters"));
        Assert.Equal(1_024, GetOption(source, "MaxColumnCount"));
        Assert.Equal(16_384, GetOption(source, "BufferSize"));

        var sink = Activator.CreateInstance(RequireType(assembly, "CsvSinkOptions"))!;
        Assert.Equal(',', GetOption(sink, "Delimiter"));
        Assert.Equal('"', GetOption(sink, "Quote"));
        Assert.Equal(string.Empty, ((System.Globalization.CultureInfo)GetOption(sink, "Culture")!).Name);
        var sinkEncoding = (System.Text.Encoding)GetOption(sink, "Encoding")!;
        Assert.Empty(sinkEncoding.GetPreamble());
        Assert.Same(System.Text.EncoderFallback.ExceptionFallback, sinkEncoding.EncoderFallback);
        Assert.Same(System.Text.DecoderFallback.ExceptionFallback, sinkEncoding.DecoderFallback);
        Assert.Equal(false, GetOption(sink, "EmitByteOrderMark"));
        Assert.Equal(true, GetOption(sink, "HasHeaderRecord"));
        Assert.Equal(0, Convert.ToInt32(GetOption(sink, "OpenMode")));
        Assert.Equal(true, GetOption(sink, "ValidateExistingHeaderOnAppend"));
        Assert.Equal(true, GetOption(sink, "WriteHeaderWhenFileIsEmpty"));
        Assert.Equal("\r\n", GetOption(sink, "NewLine"));
        Assert.Equal(524_288, GetOption(sink, "MaxRecordSizeCharacters"));
        Assert.Equal(1, GetOption(sink, "FlushEveryRecords"));
        Assert.Equal(0, Convert.ToInt32(GetOption(sink, "FormulaInjectionMode")));
        Assert.Equal(16_384, GetOption(sink, "BufferSize"));
    }

    [Fact]
    public void LeafPublicSurface_IsFileOnlyAndHasNoRevokedDraftMembers()
    {
        var assembly = LoadLeafAssembly();
        var publicTypes = assembly.GetExportedTypes()
            .Where(type => type.Namespace == "SmartPipe.Extensions.Csv")
            .ToArray();

        Assert.NotEmpty(publicTypes);
        foreach (var type in publicTypes)
        {
            foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                Assert.DoesNotContain("leaveOpen", member.Name, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Stream", member.Name, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("ByteLimit", member.Name, StringComparison.OrdinalIgnoreCase);

                if (member is MethodBase method)
                {
                    foreach (var parameter in method.GetParameters())
                    {
                        Assert.DoesNotContain("leaveOpen", parameter.Name, StringComparison.OrdinalIgnoreCase);
                        Assert.DoesNotContain("Stream", GetTypeName(parameter.ParameterType), StringComparison.OrdinalIgnoreCase);
                        Assert.DoesNotContain("PipeReader", GetTypeName(parameter.ParameterType), StringComparison.OrdinalIgnoreCase);
                    }
                }
            }

            Assert.DoesNotContain(
                type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly),
                property => property.Name.Contains("ByteLimit", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Contains("SizeBytes", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void LegacyFacadeCsvSurface_HasNoNewObsoleteWarningsAndKeepsCsvHelperQuarantine()
    {
        var facade = typeof(SmartPipe.Extensions.Selectors.CsvFileSource<>).Assembly;
        var legacyTypes = new[]
        {
            facade.GetType("SmartPipe.Extensions.Selectors.CsvFileSource`1"),
            facade.GetType("SmartPipe.Extensions.Sinks.CsvFileSink`1"),
            facade.GetType("SmartPipe.Extensions.Transforms.CsvTransform`2"),
        };

        Assert.All(legacyTypes, type =>
        {
            Assert.NotNull(type);
            Assert.Null(type!.GetCustomAttribute<ObsoleteAttribute>());
            Assert.All(type.GetConstructors(), constructor => Assert.Null(constructor.GetCustomAttribute<ObsoleteAttribute>()));
        });

        Assert.Contains(
            "CsvHelper",
            facade.GetReferencedAssemblies().Select(reference => reference.Name),
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void LeafDependencies_ContainOnlyCoreCsvHelperAndLoggingClosure()
    {
        var references = LoadLeafAssembly().GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => name is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("SmartPipe.Core", references);
        Assert.Contains("CsvHelper", references);
        Assert.Contains("Microsoft.Extensions.Logging.Abstractions", references);
        Assert.DoesNotContain("SmartPipe.Extensions", references);
        Assert.DoesNotContain("SmartPipe.Extensions.DependencyInjection", references);
        Assert.DoesNotContain("SmartPipe.Extensions.Json", references);
        Assert.DoesNotContain("SmartPipe.Extensions.Hosting", references);
        Assert.DoesNotContain("SmartPipe.Extensions.Http", references);
    }

    [Fact]
    public void CanonicalSourceSkipAndLog_RequiresBorrowedLoggerFactory()
    {
        var assembly = LoadLeafAssembly();
        var components = RequireType(assembly, "CsvPipelineComponents");
        var sourceOptions = RequireType(assembly, "CsvSourceOptions");
        var invalidBehavior = RequireType(assembly, "CsvInvalidRecordBehavior");
        var options = Activator.CreateInstance(sourceOptions)!;
        sourceOptions.GetProperty("InvalidRecordBehavior")!.SetValue(
            options,
            Enum.Parse(invalidBehavior, "SkipAndLog"));
        var method = components.GetMethod("FileSource", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);

        var exception = Assert.ThrowsAny<Exception>(() => method!.MakeGenericMethod(typeof(int)).Invoke(
            null,
            ["missing.csv", options, null, null]));
        Assert.IsType<ArgumentException>(Unwrap(exception));
    }

    [Fact]
    public async Task CanonicalFileSourceDefinitionBuild_IsPureAndRuntimeOwned()
    {
        var assembly = LoadLeafAssembly();
        var components = RequireType(assembly, "CsvPipelineComponents");
        var sourceOptions = Activator.CreateInstance(RequireType(assembly, "CsvSourceOptions"))!;
        var method = components.GetMethod("FileSource", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        var path = Path.Combine(Path.GetTempPath(), $"smartpipe-csv-definition-{Guid.NewGuid():N}.csv");
        try
        {
            Assert.False(File.Exists(path));
            var descriptor = method!.MakeGenericMethod(typeof(int)).Invoke(null, [path, sourceOptions, null, null]);
            Assert.NotNull(descriptor);
            Assert.Equal(PipelineComponentOwnership.RuntimeOwned, descriptor!.GetType().GetProperty("Ownership")!.GetValue(descriptor));
            Assert.True((bool)descriptor.GetType().GetProperty("Initialize")!.GetValue(descriptor)!);
            Assert.False(File.Exists(path));

            using var firstCancellation = new CancellationTokenSource();
            using var secondCancellation = new CancellationTokenSource();
            var first = await ActivateAsync(
                descriptor,
                new PipelineActivationContext(new PipelineKey("csv-source"), Guid.NewGuid()),
                firstCancellation.Token);
            var second = await ActivateAsync(
                descriptor,
                new PipelineActivationContext(new PipelineKey("csv-source"), Guid.NewGuid()),
                secondCancellation.Token);
            try
            {
                Assert.NotSame(first, second);
                Assert.False(File.Exists(path));
            }
            finally
            {
                await ((IAsyncDisposable)first).DisposeAsync();
                await ((IAsyncDisposable)second).DisposeAsync();
            }
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static Assembly LoadLeafAssembly() => Assembly.Load("SmartPipe.Extensions.Csv");

    private static Type RequireType(Assembly assembly, string name)
    {
        var type = assembly.GetType($"SmartPipe.Extensions.Csv.{name}");
        Assert.NotNull(type);
        return type!;
    }

    private static object? GetOption(object options, string propertyName) =>
        options.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)!.GetValue(options);

    private static async Task<object> ActivateAsync(
        object descriptor,
        PipelineActivationContext context,
        CancellationToken cancellationToken)
    {
        var activatorProperty = descriptor.GetType().GetProperty(
            "Activator",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(activatorProperty);
        var activator = Assert.IsAssignableFrom<Delegate>(activatorProperty!.GetValue(descriptor));
        var valueTask = activator.DynamicInvoke(context, cancellationToken);
        Assert.NotNull(valueTask);
        var asTask = valueTask!.GetType().GetMethod("AsTask", Type.EmptyTypes);
        Assert.NotNull(asTask);
        var task = Assert.IsAssignableFrom<Task>(asTask!.Invoke(valueTask, null));
        await task;
        return task.GetType().GetProperty("Result")!.GetValue(task)!;
    }

    private static void AssertEnum(Assembly assembly, string name, params (string Name, int Value)[] members)
    {
        var type = RequireType(assembly, name);
        Assert.True(type.IsEnum);
        Assert.Equal(members.Select(member => member.Name), Enum.GetNames(type));
        Assert.Equal(members.Select(member => member.Value), Enum.GetValues(type).Cast<object>().Select(Convert.ToInt32));
    }

    private static void AssertRecordProperties(Assembly assembly, string name, params (string Name, object Type)[] members)
    {
        var type = RequireType(assembly, name);
        Assert.True(type.IsClass && type.IsSealed);
        foreach (var member in members)
        {
            var property = type.GetProperty(member.Name, BindingFlags.Public | BindingFlags.Instance);
            Assert.NotNull(property);
            var expectedType = member.Type is Type typeValue ? typeValue : RequireType(assembly, (string)member.Type);
            Assert.Equal(expectedType, property!.PropertyType);
            Assert.True(property.SetMethod is not null);
        }
    }

    private static void AssertFactory(Type type, string name, int parameterCount)
    {
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name == name && method.IsGenericMethodDefinition)
            .ToArray();
        Assert.Single(methods);
        Assert.Equal(parameterCount, methods[0].GetParameters().Length);
    }

    private static void AssertMethod(Type type, string name, int parameterCount)
    {
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name == name && method.IsGenericMethodDefinition)
            .ToArray();
        Assert.Single(methods);
        Assert.Equal(parameterCount, methods[0].GetParameters().Length);
    }

    private static string GetTypeName(Type type) =>
        type.IsByRef ? GetTypeName(type.GetElementType()!) : type.FullName ?? type.Name;

    private static Exception Unwrap(Exception exception) =>
        exception is TargetInvocationException { InnerException: not null } invocation
            ? invocation.InnerException!
            : exception;
}
