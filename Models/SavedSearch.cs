namespace FindHistory.Models;

public sealed record SavedSearch(
    Guid Id,
    string Name,
    string SearchText,
    string Extension,
    bool? Exists,
    string Folder,
    int? CalendarDayCount,
    DateTime? SpecificDate,
    bool IsSpecificDate)
{
    public override string ToString() => Name;
}

public static class SavedSearchDateRangeResolver
{
    public static DateRangeOption Resolve(SavedSearch savedSearch, IReadOnlyList<DateRangeOption> options)
    {
        ArgumentNullException.ThrowIfNull(savedSearch);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Count == 0)
        {
            throw new ArgumentException("At least one date range option is required.", nameof(options));
        }

        if (savedSearch.IsSpecificDate)
        {
            return options.FirstOrDefault(option => option.IsSpecificDate) ?? options[0];
        }

        return savedSearch.CalendarDayCount is null
            ? options[0]
            : options.FirstOrDefault(option => option.CalendarDayCount == savedSearch.CalendarDayCount)
              ?? options[0];
    }
}
