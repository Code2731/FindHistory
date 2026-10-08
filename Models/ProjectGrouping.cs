using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using FindHistory.Localization;
using FindHistory.Services;

namespace FindHistory.Models;

public static class ProjectGrouping
{
    public static string GetGroupName(string targetPath, IReadOnlyList<ProjectDefinition> projects)
    {
        var project = projects.OrderByDescending(project => project.Folder.Length)
            .FirstOrDefault(project => ExcludedFolderPathRules.IsExcluded(targetPath, [project.Folder]));
        return project is null
            ? LocalizationManager.Instance.Translate("프로젝트 없음")
            : LocalizationManager.Instance.Translate("프로젝트") + ": " + project.Name;
    }

    public static ICollectionView CreateView(IReadOnlyList<RecentItem> items,
        IReadOnlyList<ProjectDefinition> projects)
    {
        var view = new ListCollectionView(items.ToList());
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(RecentItem.TargetPath),
            new ProjectConverter(projects)));
        return view;
    }

    private sealed class ProjectConverter(IReadOnlyList<ProjectDefinition> projects) : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            GetGroupName((string)value, projects);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            System.Windows.Data.Binding.DoNothing;
    }
}
