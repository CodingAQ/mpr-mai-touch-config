using MaimaiTouchTool.Localization;
using MaimaiTouchTool.Theming;
using MaimaiTouchTool.ViewModels;
using Xunit;

namespace MaimaiTouchTool.Tests;

public sealed class ThemeManagerTests
{
    [Fact]
    public void ThemeManagerDefaultIsDarkOrConfigured()
    {
        var tempPref = Path.GetTempFileName();
        try
        {
            ThemeManager.Instance.Initialize(tempPref);
            // Default should be dark
            Assert.Contains(ThemeManager.Instance.Theme, new[] { "dark", "light", "system" });
            Assert.NotEmpty(ThemeManager.Instance.ThemeLabels);
            Assert.NotEmpty(ThemeManager.Instance.SelectedLabel);
        }
        finally
        {
            if (File.Exists(tempPref)) File.Delete(tempPref);
            ThemeManager.Instance.SetTheme("dark", persist: false);
        }
    }

    [Fact]
    public void ThemeManagerSwitchingUpdatesPropertiesAndFiresEvents()
    {
        var tempPref = Path.GetTempFileName();
        try
        {
            ThemeManager.Instance.Initialize(tempPref);
            var eventFired = false;
            ThemeManager.Instance.ThemeChanged += (_, _) => eventFired = true;

            ThemeManager.Instance.SetTheme("light", persist: true);
            Assert.Equal("light", ThemeManager.Instance.Theme);
            Assert.True(ThemeManager.Instance.IsLight);
            Assert.True(eventFired);

            // Verify persistence
            var loaded = UserPreferences.Load(tempPref);
            Assert.Equal("light", loaded.Theme);

            // Switch to dark
            eventFired = false;
            ThemeManager.Instance.SetTheme("dark", persist: true);
            Assert.Equal("dark", ThemeManager.Instance.Theme);
            Assert.False(ThemeManager.Instance.IsLight);
            Assert.True(eventFired);

            loaded = UserPreferences.Load(tempPref);
            Assert.Equal("dark", loaded.Theme);
        }
        finally
        {
            if (File.Exists(tempPref)) File.Delete(tempPref);
            ThemeManager.Instance.SetTheme("dark", persist: false);
        }
    }

    [Fact]
    public void ThemeLabelsAdaptToLanguageChanges()
    {
        try
        {
            Localizer.Instance.SetLanguage("en");
            ThemeManager.Instance.SetTheme("light", persist: false);
            Assert.Equal("Light", ThemeManager.Instance.SelectedLabel);
            Assert.Contains("Dark", ThemeManager.Instance.ThemeLabels);
            Assert.Contains("Light", ThemeManager.Instance.ThemeLabels);
            Assert.Contains("Follow system", ThemeManager.Instance.ThemeLabels);

            Localizer.Instance.SetLanguage("zh-Hans");
            Assert.Equal("浅色", ThemeManager.Instance.SelectedLabel);
            Assert.Contains("深色", ThemeManager.Instance.ThemeLabels);
            Assert.Contains("浅色", ThemeManager.Instance.ThemeLabels);
            Assert.Contains("跟随系统", ThemeManager.Instance.ThemeLabels);
        }
        finally
        {
            Localizer.Instance.SetLanguage("en");
            ThemeManager.Instance.SetTheme("dark", persist: false);
        }
    }

    [Fact]
    public void ElectrodeModelTintBrushAdaptsToTheme()
    {
        try
        {
            var model = new ElectrodeEditorModel(0, "A1", 0, 0, 0);

            ThemeManager.Instance.SetTheme("dark", persist: false);
            Assert.Equal("#1A1E24", model.TintBrush);

            var tintChanged = false;
            model.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ElectrodeEditorModel.TintBrush)) tintChanged = true;
            };

            ThemeManager.Instance.SetTheme("light", persist: false);
            Assert.True(tintChanged);
            Assert.Equal("#F1F5F9", model.TintBrush);

            // Test non-zero offset
            model.Offset = -8;
            var lightTint = model.TintBrush;

            ThemeManager.Instance.SetTheme("dark", persist: false);
            var darkTint = model.TintBrush;

            Assert.NotEqual(lightTint, darkTint);
        }
        finally
        {
            ThemeManager.Instance.SetTheme("dark", persist: false);
        }
    }
}
