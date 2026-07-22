namespace SmartPipe.RepositoryChecks.PackageGraph;

internal static class TopologicalPackageSorter
{
    public static IReadOnlyList<string> Sort(IReadOnlyDictionary<string, IReadOnlyList<string>> dependencies)
    {
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        var stack = new List<string>();
        foreach (var id in dependencies.Keys.Order(StringComparer.Ordinal)) Visit(id);
        return result;

        void Visit(string id)
        {
            if (state.TryGetValue(id, out var current))
            {
                if (current == 2) return;
                var index = stack.FindIndex(x => x.Equals(id, StringComparison.OrdinalIgnoreCase));
                var cycle = stack.Skip(index).Append(id);
                throw new PackageGraphException("SPGRAPH020", $"Package dependency cycle: {string.Join(" -> ", cycle)}.");
            }
            state[id] = 1; stack.Add(id);
            if (dependencies.TryGetValue(id, out var edges))
                foreach (var dependency in edges.Order(StringComparer.Ordinal))
                    if (dependencies.ContainsKey(dependency)) Visit(dependency);
            stack.RemoveAt(stack.Count - 1); state[id] = 2; result.Add(id);
        }
    }
}
