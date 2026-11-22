using System.ComponentModel;
using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;
using MaimaiTouchTool.Localization;

namespace MaimaiTouchTool.Theming;

/// <summary>
/// 管理界面主题（深色、日间浅色、跟随系统）。
/// 联动 Application.Current.RequestedThemeVariant，支持环境覆盖与偏好持久化。
/// </summary>
public sealed class ThemeManager : INotifyPropertyChanged
{
    public static readonly (string Code, string Key)[] Available =
    [
        ("dark", "Theme.Dark"),
        ("light", "Theme.Light"),
        ("system", "Theme.System")
    ];

    public static ThemeManager Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? ThemeChanged;

    private string theme = "dark";
    private string? prefFilePath;

    private ThemeManager()
    {
        Localizer.Instance.LanguageChanged += (_, _) =>
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThemeLabels)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedLabel)));
        };
    }

    public string Theme => theme;

    public string SelectedTheme
    {
        get => theme;
        set => SetTheme(value);
    }

    public string SelectedLabel
    {
        get
        {
            var match = Available.FirstOrDefault(x => x.Code == theme);
            return match != default ? Localizer.Instance[match.Key] : Localizer.Instance["Theme.Dark"];
        }
        set
        {
            var match = Available.FirstOrDefault(x => Localizer.Instance[x.Key] == value);
            if (match != default) SetTheme(match.Code);
        }
    }

    public string[] ThemeLabels => Available.Select(x => Localizer.Instance[x.Key]).ToArray();

    public bool IsLight
    {
        get
        {
            if (theme == "light") return true;
            if (theme == "dark") return false;
            return Application.Current?.ActualThemeVariant == ThemeVariant.Light;
        }
    }

    public void Initialize(string? customPrefPath = null)
    {
        prefFilePath = customPrefPath;

        // 1. 检查环境变量优先覆盖
        var envTheme = Environment.GetEnvironmentVariable("MAIMAI_THEME");
        if (!string.IsNullOrEmpty(envTheme))
        {
            SetTheme(envTheme, persist: false);
            return;
        }

        // 2. 从偏好文件加载
        var prefs = UserPreferences.Load(prefFilePath);
        SetTheme(prefs.Theme, persist: false);
    }

    public void SetTheme(string themeCode, bool persist = true)
    {
        var normalized = (themeCode ?? "dark").Trim().ToLowerInvariant();
        if (normalized is not ("dark" or "light" or "system")) normalized = "dark";

        var changed = normalized != theme;
        theme = normalized;

        ApplyToApplication(normalized);

        if (persist)
        {
            var prefs = UserPreferences.Load(prefFilePath);
            prefs.Theme = normalized;
            prefs.Save(prefFilePath);
        }

        if (changed)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Theme)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedTheme)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedLabel)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLight)));
            ThemeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static void ApplyToApplication(string themeCode)
    {
        var variant = themeCode switch
        {
            "light" => ThemeVariant.Light,
            "system" => ThemeVariant.Default,
            _ => ThemeVariant.Dark
        };

        void UpdateVariant()
        {
            if (Application.Current is not null)
            {
                Application.Current.RequestedThemeVariant = variant;
            }
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            UpdateVariant();
        }
        else
        {
            Dispatcher.UIThread.Post(UpdateVariant);
        }
    }
}
