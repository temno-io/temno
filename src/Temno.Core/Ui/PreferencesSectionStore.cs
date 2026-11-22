using Temno.Core.IO;

namespace Temno.Core.Ui;

/// <summary>
/// Persists <see cref="SectionExpandState"/> in <c>user-preferences.json</c> so editor sections
/// remember expand/collapse across app restarts (WP-128).
/// </summary>
public sealed class PreferencesSectionStore : SectionExpandState.IStore
{
    public bool? Get(string sectionId)
    {
        if (string.IsNullOrWhiteSpace(sectionId)) return null;
        var sections = UserPreferences.Load().EditorSections;
        return sections.TryGetValue(sectionId, out var expanded) ? expanded : null;
    }

    public void Set(string sectionId, bool expanded)
    {
        if (string.IsNullOrWhiteSpace(sectionId)) return;
        var prefs = UserPreferences.Load();
        prefs.EditorSections[sectionId] = expanded;
        UserPreferences.Save(prefs);
    }
}
