using System.Windows;
using System.Windows.Media.Imaging;
using OB2.Core.Settings;

namespace OB2.App.Infrastructure;

/// <summary>
/// Swaps the palette dictionary in <see cref="Application.Resources"/>. Every window and style
/// reads its colours through the keys in Theme.Light.xaml / Theme.Dark.xaml as DynamicResource,
/// so replacing the dictionary repaints the open windows; nothing has to be reopened.
/// </summary>
internal static class ThemeManager
{
    private const string LightSource = "Resources/Theme.Light.xaml";
    private const string DarkSource = "Resources/Theme.Dark.xaml";

    /// <summary>The theme currently applied, for anything that has to branch on it.</summary>
    public static AppTheme Current { get; private set; } = AppTheme.Light;

    /// <summary>
    /// Paints every window the app opens from now on, including the dialogs, without each one
    /// having to ask. A class handler is used because an implicit Window style does not reach
    /// a subclass, and every window here is one.
    /// </summary>
    public static void HookNewWindows() =>
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => ApplyTo((Window)sender)));

    public static void Apply(AppTheme theme)
    {
        if (Application.Current is not { } app)
        {
            return;
        }

        var source = new Uri(theme == AppTheme.Dark ? DarkSource : LightSource, UriKind.Relative);
        var dictionaries = app.Resources.MergedDictionaries;

        // The palette is found by the keys it defines rather than by position, because
        // Strings.xaml and Styles.xaml are merged alongside it and the order is not ours to rely on.
        var existing = dictionaries.FirstOrDefault(d => d.Contains("WindowBrush"));
        var replacement = new ResourceDictionary { Source = source };

        if (existing is null)
        {
            // Before the styles, so a style's StaticResource lookups resolve against it.
            dictionaries.Insert(0, replacement);
        }
        else
        {
            dictionaries[dictionaries.IndexOf(existing)] = replacement;
        }

        // The wordmark is decoded here rather than declared as a BitmapImage in the dictionary:
        // a relative URI in a merged dictionary resolves against the dictionary, not the
        // application, so the image silently came out blank. Built from the application's own
        // pack URI, it loads whichever cut of the logo the theme named.
        if (replacement["LogoPath"] is string path)
        {
            app.Resources["LogoImage"] = new BitmapImage(
                new Uri($"pack://application:,,,/{typeof(ThemeManager).Assembly.GetName().Name};component/{path}", UriKind.Absolute));
        }

        Current = theme;
        RepaintOpenWindows();
    }

    /// <summary>
    /// Points every open window's background at the palette.
    ///
    /// An implicit <c>Style TargetType="Window"</c> only matches a Window itself, never a
    /// subclass, so SettingsWindow and the rest never picked it up: their backgrounds stayed
    /// the system's white, which showed as a white frame in the margin around their content.
    /// This covers every subclass without each window having to opt in.
    /// </summary>
    private static void RepaintOpenWindows()
    {
        if (Application.Current is not { } app)
        {
            return;
        }

        foreach (Window window in app.Windows)
        {
            ApplyTo(window);
        }
    }

    /// <summary>
    /// Points one window's background at the palette. A DynamicResource reference rather than
    /// a brush, so the window follows a later theme change on its own.
    /// </summary>
    public static void ApplyTo(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.SetResourceReference(Window.BackgroundProperty, "WindowBrush");
    }
}
