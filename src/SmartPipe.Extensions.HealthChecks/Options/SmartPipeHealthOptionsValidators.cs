using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using SmartPipe.Core;
using SmartPipe.Extensions.DependencyInjection;

namespace SmartPipe.Extensions.HealthChecks;

internal sealed class SmartPipeLivenessOptionsValidator : IValidateOptions<SmartPipeLivenessOptions>
{
    public ValidateOptionsResult Validate(string? name, SmartPipeLivenessOptions options) =>
        ValidateLimit(options.MaximumReportedProblemRuns, nameof(options.MaximumReportedProblemRuns));

    internal static ValidateOptionsResult ValidateLimit(int value, string memberName) =>
        value is >= 1 and <= 100
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail($"{memberName} must be between 1 and 100.");
}

internal sealed class SmartPipeReadinessOptionsValidator : IValidateOptions<SmartPipeReadinessOptions>
{
    public ValidateOptionsResult Validate(string? name, SmartPipeReadinessOptions options)
    {
        var failures = Validate(options);
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    internal static List<string> Validate(SmartPipeReadinessOptions options)
    {
        var failures = new List<string>();
        if (!Enum.IsDefined(options.RunRequirement)) failures.Add($"{nameof(options.RunRequirement)} is invalid.");
        if (options.InitialActivityGracePeriod <= TimeSpan.Zero) failures.Add($"{nameof(options.InitialActivityGracePeriod)} must be positive.");
        if (options.StaleAfter is { } staleAfter && staleAfter <= TimeSpan.Zero) failures.Add($"{nameof(options.StaleAfter)} must be positive when set.");
        if (options.QueueUtilizationDegradedThreshold is { } threshold
            && (!double.IsFinite(threshold) || threshold <= 0 || threshold > 1))
            failures.Add($"{nameof(options.QueueUtilizationDegradedThreshold)} must be in (0, 1].");
        ValidateSoftStatus(options.InitialActivityStatus, nameof(options.InitialActivityStatus), failures);
        ValidateSoftStatus(options.StaleActivityStatus, nameof(options.StaleActivityStatus), failures);
        ValidateSoftStatus(options.QueuePressureStatus, nameof(options.QueuePressureStatus), failures);
        if (options.MaximumReportedProblemRuns is < 1 or > 100) failures.Add($"{nameof(options.MaximumReportedProblemRuns)} must be between 1 and 100.");
        if (options.RunRequirement == SmartPipeReadinessRunRequirement.RegistrationOnly && options.RequireInitialActivity)
            failures.Add($"{nameof(options.RequireInitialActivity)} cannot be used with {nameof(SmartPipeReadinessRunRequirement.RegistrationOnly)}.");
        return failures;
    }

    private static void ValidateSoftStatus(HealthStatus status, string memberName, List<string> failures)
    {
        if (status is not HealthStatus.Degraded and not HealthStatus.Unhealthy)
            failures.Add($"{memberName} must be Degraded or Unhealthy.");
    }
}

internal sealed class SmartPipeAggregateLivenessOptionsValidator(
    ISmartPipeRegistry registry) : IValidateOptions<SmartPipeAggregateLivenessOptions>
{
    public ValidateOptionsResult Validate(string? name, SmartPipeAggregateLivenessOptions options) =>
        AggregateValidation.Validate(
            options.MaximumReportedProblemKeys,
            options.IncludeAllRegisteredPipelines,
            options.IncludedPipelines,
            registry,
            SmartPipeLivenessOptionsValidator.ValidateLimit(
                options.Liveness.MaximumReportedProblemRuns,
                nameof(options.Liveness.MaximumReportedProblemRuns)));
}

internal sealed class SmartPipeAggregateReadinessOptionsValidator(
    ISmartPipeRegistry registry) : IValidateOptions<SmartPipeAggregateReadinessOptions>
{
    public ValidateOptionsResult Validate(string? name, SmartPipeAggregateReadinessOptions options)
    {
        var readinessFailures = SmartPipeReadinessOptionsValidator.Validate(options.Readiness);
        return AggregateValidation.Validate(
            options.MaximumReportedProblemKeys,
            options.IncludeAllRegisteredPipelines,
            options.IncludedPipelines,
            registry,
            readinessFailures.Count == 0
                ? ValidateOptionsResult.Success
                : ValidateOptionsResult.Fail(readinessFailures));
    }
}

internal static class AggregateValidation
{
    internal static ValidateOptionsResult Validate(
        int maximum,
        bool includeAll,
        IList<PipelineKey> included,
        ISmartPipeRegistry registry,
        ValidateOptionsResult nested)
    {
        var failures = nested.Failed ? nested.Failures.ToList() : [];
        if (maximum is < 1 or > 100) failures.Add("MaximumReportedProblemKeys must be between 1 and 100.");
        if (!includeAll && included.Count == 0) failures.Add("At least one pipeline must be included when IncludeAllRegisteredPipelines is false.");
        var seen = new HashSet<PipelineKey>();
        foreach (var key in included)
        {
            if (key.IsEmpty) failures.Add("IncludedPipelines contains an empty key.");
            else if (!seen.Add(key)) failures.Add($"IncludedPipelines contains duplicate key '{key.Value}'.");
            else if (!registry.TryGetRegistration(key, out _)) failures.Add($"IncludedPipelines contains unknown key '{key.Value}'.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
