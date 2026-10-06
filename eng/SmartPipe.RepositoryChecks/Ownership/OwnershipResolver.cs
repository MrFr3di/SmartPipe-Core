namespace SmartPipe.RepositoryChecks.Ownership;

internal static class OwnershipResolver
{
    public static OwnershipAssignment Resolve(string type, IReadOnlyList<OwnershipAssignment> assignments)
    {
        var matches = assignments.Select(x => (Assignment: x, Exact: !x.TypePattern.EndsWith('*'), Prefix: x.TypePattern.TrimEnd('*')))
            .Where(x => x.Exact ? type.Equals(x.Prefix, StringComparison.Ordinal) : type.StartsWith(x.Prefix, StringComparison.Ordinal))
            .OrderByDescending(x => x.Exact).ThenByDescending(x => x.Prefix.Length).ToArray();
        if (matches.Length == 0) throw new OwnershipException("SPOWN001", $"Baseline type {type} has no ownership assignment.");
        var best = matches[0];
        if (matches.Skip(1).Any(x => x.Exact == best.Exact && x.Prefix.Length == best.Prefix.Length))
            throw new OwnershipException("SPOWN002", $"Baseline type {type} has ambiguous equal-specificity ownership assignments.");
        return best.Assignment;
    }
}
