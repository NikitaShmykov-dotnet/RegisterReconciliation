namespace RegisterReconciliation;

internal sealed record Outcome(string Category, Operation? Internal, Operation? External,
    string? Method = null, int CandidateCount = 0);

internal static class Matcher
{
    internal const int Version = 1;
    internal static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    internal static IEnumerable<Outcome> Compare(List<Operation> internalRows, List<Operation> externalRows,
        CancellationToken ct)
    {
        var internalIds = ById(internalRows);
        var externalIds = ById(externalRows);
        var used = new HashSet<long>();

        foreach (var (id, left) in internalIds.OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            externalIds.TryGetValue(id, out var right);
            if (left.Length > 1 || right is { Length: > 1 })
            {
                foreach (var row in left)
                {
                    used.Add(row.Id);
                    yield return new Outcome("duplicate", row, null);
                }

                foreach (var row in right ?? [])
                {
                    used.Add(row.Id);
                    yield return new Outcome("duplicate", null, row);
                }
            }
            else if (right is not null)
            {
                var first = left[0];
                var second = right[0];
                used.Add(first.Id);
                used.Add(second.Id);
                var sameMoney = first.Amount == second.Amount && first.Currency == second.Currency;
                yield return new Outcome(sameMoney ? "matched" : "amount-mismatch", first, second, "exact-id");
            }
        }

        foreach (var (id, right) in externalIds.OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            if (right.Length < 2 || internalIds.ContainsKey(id))
                continue;

            foreach (var row in right)
            {
                used.Add(row.Id);
                yield return new Outcome("duplicate", null, row);
            }
        }

        var remainingInternal = internalRows.Where(row => !used.Contains(row.Id)).ToArray();
        var remainingExternal = externalRows.Where(row => !used.Contains(row.Id)).ToArray();
        var internalIndex = new CandidateIndex(remainingInternal);
        var externalIndex = new CandidateIndex(remainingExternal);

        // Degrees come from the same graph, before consuming any fallback pair.
        foreach (var row in remainingInternal)
        {
            ct.ThrowIfCancellationRequested();
            var (count, only) = externalIndex.Find(row);
            if (only is not null && internalIndex.Find(only).Count == 1)
            {
                used.Add(only.Id);
                yield return new Outcome("matched", row, only, "fallback", count);
            }
            else
            {
                yield return new Outcome(count == 0 ? "missing-external" : "ambiguous", row, null, CandidateCount: count);
            }
        }

        foreach (var row in remainingExternal)
        {
            ct.ThrowIfCancellationRequested();
            if (used.Contains(row.Id))
                continue;

            var (count, _) = internalIndex.Find(row);
            yield return new Outcome(count == 0 ? "missing-internal" : "ambiguous", null, row, CandidateCount: count);
        }
    }

    private static Dictionary<string, Operation[]> ById(IEnumerable<Operation> rows) =>
        rows.Where(row => row.ExternalId is not null)
            .GroupBy(row => row.ExternalId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

    private sealed class CandidateIndex(IEnumerable<Operation> rows)
    {
        private readonly Dictionary<(decimal, string, string), Operation[]> all = Index(rows);
        private readonly Dictionary<(decimal, string, string), Operation[]> withoutId =
            Index(rows.Where(row => row.ExternalId is null));

        internal (int Count, Operation? Only) Find(Operation row)
        {
            var index = row.ExternalId is null ? all : withoutId;
            if (!index.TryGetValue(Key(row), out var group))
                return (0, null);

            var first = LowerBound(group, row.OccurredAt - Window);
            var end = LowerBound(group, (row.OccurredAt + Window).AddTicks(1));
            return (end - first, end - first == 1 ? group[first] : null);
        }

        private static Dictionary<(decimal, string, string), Operation[]> Index(IEnumerable<Operation> operations) =>
            operations.GroupBy(Key).ToDictionary(group => group.Key,
                group => group.OrderBy(row => row.OccurredAt).ToArray());

        private static (decimal, string, string) Key(Operation row) =>
            (row.Amount, row.Currency, row.OrderReference);

        private static int LowerBound(Operation[] rows, DateTime time)
        {
            var low = 0;
            var high = rows.Length;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (rows[middle].OccurredAt < time)
                    low = middle + 1;
                else
                    high = middle;
            }

            return low;
        }
    }
}
