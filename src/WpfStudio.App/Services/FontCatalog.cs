using System.Globalization;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using WpfStudio.Contracts;
using WpfStudio.Core;

namespace WpfStudio.App.Services;

/// <summary>
/// Installed font families, fixed-width ones first since code is aligned in columns. Measuring every installed font
/// takes a moment, so the list fills in from a background thread the first time it is shown.
/// </summary>
public sealed class FontCatalog : ObservableObject
{
    private IReadOnlyList<string>? _families;
    private bool _listingFamilies;
    public IReadOnlyList<string> Families
    {
        get
        {
            if (_families is null && !_listingFamilies)
            {
                _listingFamilies = true;
                LoadFamiliesAsync().Forget();
            }
            return _families ?? [];
        }
    }

    private async Task LoadFamiliesAsync()
    {
        IReadOnlyList<string> families;
        try { families = await Task.Run(ListFamilies).ConfigureAwait(false); }
        catch (Exception) { families = [EditorSettings.DefaultFontFamily]; } // The settings page only needs something to offer.
        _families = families;
        // Not through the first reader's dispatcher: that reader may be on a thread that never runs one.
        if (Application.Current?.Dispatcher is { } ui) _ = ui.BeginInvoke(() => OnPropertyChanged(nameof(Families)));
        else OnPropertyChanged(nameof(Families));
    }

    private static IReadOnlyList<string> ListFamilies()
    {
        var language = System.Windows.Markup.XmlLanguage.GetLanguage(CultureInfo.CurrentUICulture.IetfLanguageTag);
        var families = new List<(string Name, bool Monospace)>();
        foreach (var family in Fonts.SystemFontFamilies)
        {
            var name = family.FamilyNames.TryGetValue(language, out var localized) ? localized : family.Source;
            families.Add((name, IsMonospace(family)));
        }
        return families.DistinctBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item.Monospace ? 0 : 1).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).Select(item => item.Name).ToArray();
    }

    private static bool IsMonospace(FontFamily family)
    {
        try
        {
            var typeface = family.GetTypefaces().FirstOrDefault(face => face.Style == FontStyles.Normal && face.Weight == FontWeights.Normal) ?? family.GetTypefaces().FirstOrDefault();
            if (typeface is null || !typeface.TryGetGlyphTypeface(out var glyphs)) return false;
            double Width(char character) => glyphs.CharacterToGlyphMap.TryGetValue(character, out var glyph) ? glyphs.AdvanceWidths[glyph] : -1;
            var narrow = Width('i');
            return narrow > 0 && Math.Abs(narrow - Width('W')) < 0.001 && Math.Abs(narrow - Width('.')) < 0.001;
        }
        catch (Exception exception) when (exception is ArgumentException or System.IO.IOException or UnauthorizedAccessException) { return false; }
    }
}
