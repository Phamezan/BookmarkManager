using System.Text.RegularExpressions;

namespace BookmarkManager.Api.Services.Suwayomi;

/// <summary>
/// The filter changes needed to list a source's "latest Action" feed, or the reason the source was
/// skipped. A source without a resolvable latest-sort or Action filter is skipped rather than
/// listed unfiltered.
/// </summary>
public sealed record DiscoverFilterPlan(IReadOnlyList<SuwayomiFilterChange> Changes, string? SkipReason)
{
    public bool CanRun => SkipReason is null;
}

/// <summary>
/// Resolves a source's filter tree to the filter changes that produce "latest updates in Action".
/// Everything is matched by name at runtime (positions differ per source and can change), never by
/// hardcoded index. Verified rule set:
/// sort = Sort/Select named <c>sort|order</c> with a "Latest Updates"/"Chapter uploaded at" value
/// (SortFilter changes MUST use sortState); genre = a Group named <c>genre|tag|categor</c>
/// containing an Action child, or a Select named <c>genre|categor</c> with an Action value;
/// adult = a Select named <c>adult</c> set to None/False; direction = a "Sort direction" select set
/// to Descending.
/// </summary>
public static partial class DiscoverFilterResolver
{
    public static DiscoverFilterPlan Resolve(IReadOnlyList<SuwayomiFilter> filters)
    {
        var changes = new List<SuwayomiFilterChange>();
        var missing = new List<string>();

        var sort = ResolveSort(filters, changes);
        if (!sort)
        {
            missing.Add("latest sort");
        }

        ResolveDirection(filters, changes);

        var genre = ResolveGenre(filters, changes);
        if (!genre)
        {
            missing.Add("Action filter");
        }

        ResolveAdult(filters, changes);

        return missing.Count > 0
            ? new DiscoverFilterPlan(changes, $"no resolvable {string.Join(" or ", missing)}")
            : new DiscoverFilterPlan(changes, null);
    }

    private static bool ResolveSort(IReadOnlyList<SuwayomiFilter> filters, List<SuwayomiFilterChange> changes)
    {
        foreach (var filter in filters.Where(f => f.Kind is SuwayomiFilterKind.Sort or SuwayomiFilterKind.Select
            && SortNameRegex().IsMatch(f.Name)))
        {
            var index = IndexOfMatch(filter.Values, LatestValueRegex());
            if (index < 0)
            {
                continue;
            }

            // A SortFilter change must use sortState — selectState throws "Expected sort state change".
            changes.Add(filter.Kind == SuwayomiFilterKind.Sort
                ? new SuwayomiFilterChange(filter.Position, SortState: new SuwayomiSortState(index, Ascending: false))
                : new SuwayomiFilterChange(filter.Position, SelectState: index));
            return true;
        }

        return false;
    }

    private static void ResolveDirection(IReadOnlyList<SuwayomiFilter> filters, List<SuwayomiFilterChange> changes)
    {
        var filter = filters.FirstOrDefault(f => f.Kind == SuwayomiFilterKind.Select
            && SortDirectionRegex().IsMatch(f.Name));
        if (filter is null)
        {
            return;
        }

        var index = IndexOfMatch(filter.Values, DescendingValueRegex());
        if (index >= 0)
        {
            changes.Add(new SuwayomiFilterChange(filter.Position, SelectState: index));
        }
    }

    private static bool ResolveGenre(IReadOnlyList<SuwayomiFilter> filters, List<SuwayomiFilterChange> changes)
    {
        // Several groups can match the genre/tag name pattern (MangaDex has both "Tags mode" and
        // "Genre"); pick the one that actually carries an Action child.
        foreach (var group in filters.Where(f => f.Kind == SuwayomiFilterKind.Group && GenreNameRegex().IsMatch(f.Name)))
        {
            var child = group.Filters.FirstOrDefault(c =>
                (c.Kind is SuwayomiFilterKind.CheckBox or SuwayomiFilterKind.TriState)
                && string.Equals(c.Name, "Action", StringComparison.OrdinalIgnoreCase));
            if (child is null)
            {
                continue;
            }

            changes.Add(child.Kind == SuwayomiFilterKind.TriState
                ? new SuwayomiFilterChange(group.Position,
                    GroupChange: new SuwayomiGroupChange(child.Position, TriState: "INCLUDE"))
                : new SuwayomiFilterChange(group.Position,
                    GroupChange: new SuwayomiGroupChange(child.Position, CheckBoxState: true)));
            return true;
        }

        foreach (var select in filters.Where(f => f.Kind == SuwayomiFilterKind.Select && GenreNameRegex().IsMatch(f.Name)))
        {
            var index = IndexOfMatch(select.Values, ActionValueRegex());
            if (index >= 0)
            {
                changes.Add(new SuwayomiFilterChange(select.Position, SelectState: index));
                return true;
            }
        }

        return false;
    }

    private static void ResolveAdult(IReadOnlyList<SuwayomiFilter> filters, List<SuwayomiFilterChange> changes)
    {
        var filter = filters.FirstOrDefault(f => f.Kind == SuwayomiFilterKind.Select && AdultNameRegex().IsMatch(f.Name));
        if (filter is null)
        {
            return;
        }

        var index = IndexOfMatch(filter.Values, AdultSafeValueRegex());
        if (index >= 0)
        {
            changes.Add(new SuwayomiFilterChange(filter.Position, SelectState: index));
        }
    }

    private static int IndexOfMatch(IReadOnlyList<string> values, Regex pattern)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (pattern.IsMatch(values[i]))
            {
                return i;
            }
        }

        return -1;
    }

    [GeneratedRegex(@"sort|order", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SortNameRegex();

    [GeneratedRegex(@"^latest( update(s)?)?$|chapter uploaded at", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LatestValueRegex();

    [GeneratedRegex(@"sort\s*direction|direction", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SortDirectionRegex();

    [GeneratedRegex(@"^descending$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DescendingValueRegex();

    [GeneratedRegex(@"genre|tag|categor", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GenreNameRegex();

    [GeneratedRegex(@"adult", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AdultNameRegex();

    [GeneratedRegex(@"^action$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ActionValueRegex();

    [GeneratedRegex(@"^(none|false)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AdultSafeValueRegex();
}
