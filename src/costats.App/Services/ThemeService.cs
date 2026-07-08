using System.Windows;
using costats.Application.Settings;
using Microsoft.Win32;

namespace costats.App.Services
{
    public enum AppThemeMode
    {
        Auto,
        Light,
        Dark
    }

    /// <summary>
    /// Applies the Light/Dark resource dictionary to the Application, either following
    /// the Windows "app mode" setting (Auto) or a user-forced Light/Dark preference.
    /// </summary>
    public sealed class ThemeService : IDisposable
    {
        private const string PersonalizeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        private const string LightThemeUri = "pack://application:,,,/costats.App;component/Themes/LightTheme.xaml";
        private const string DarkThemeUri = "pack://application:,,,/costats.App;component/Themes/DarkTheme.xaml";

        private ResourceDictionary? _activeDictionary;
        private AppThemeMode _mode = AppThemeMode.Auto;

        public AppThemeMode Mode => _mode;

        public bool IsDarkActive { get; private set; }

        public void Initialize(AppSettings settings)
        {
            _mode = ParseMode(settings.ThemeMode);
            ApplyCurrentMode();

            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }

        public void SetMode(AppThemeMode mode)
        {
            if (_mode == mode)
            {
                return;
            }

            _mode = mode;
            ApplyCurrentMode();
        }

        public static AppThemeMode ParseMode(string? value) =>
            Enum.TryParse<AppThemeMode>(value, ignoreCase: true, out var mode) ? mode : AppThemeMode.Auto;

        public static bool IsSystemDarkTheme()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath, writable: false);
                return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
            }
            catch
            {
                return false;
            }
        }

        private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (_mode != AppThemeMode.Auto)
            {
                return;
            }

            if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color))
            {
                return;
            }

            System.Windows.Application.Current?.Dispatcher.BeginInvoke(ApplyCurrentMode);
        }

        private void ApplyCurrentMode()
        {
            var useDark = _mode switch
            {
                AppThemeMode.Dark => true,
                AppThemeMode.Light => false,
                _ => IsSystemDarkTheme()
            };

            IsDarkActive = useDark;

            var app = System.Windows.Application.Current;
            if (app is null)
            {
                return;
            }

            var newDictionary = new ResourceDictionary
            {
                Source = new Uri(useDark ? DarkThemeUri : LightThemeUri, UriKind.Absolute)
            };

            var dictionaries = app.Resources.MergedDictionaries;
            dictionaries.Add(newDictionary);

            if (_activeDictionary is not null)
            {
                dictionaries.Remove(_activeDictionary);
            }

            _activeDictionary = newDictionary;
        }

        public void Dispose()
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        }
    }
}
