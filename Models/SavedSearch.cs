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
