using System.Windows;
using System.Windows.Input;
using costats.App.ViewModels;
using costats.Application.Shell;

namespace costats.App
{
    public partial class SettingsWindow : Window
    {
        private readonly IGlassBackdropService _backdropService;

        public SettingsWindow(SettingsViewModel viewModel, IGlassBackdropService backdropService)
        {
            InitializeComponent();
            DataContext = viewModel;
            _backdropService = backdropService;
            SourceInitialized += OnSourceInitialized;
            MouseLeftButtonDown += OnMouseLeftButtonDown;
        }

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            // Skip backdrop - we use AllowsTransparency with custom Border for rounded corners.
            // Applying DWM backdrop creates a conflicting layer with a different corner radius
            // that shows through as an opaque patch in the corners (same issue as GlassWidgetWindow).
        }

        private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            Hide();
        }

        private async void OnSaveCopilotTokenClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is SettingsViewModel viewModel)
            {
                var token = CopilotTokenBox.Password;
                await viewModel.SaveCopilotTokenAsync(token);
            }
        }
    }
}
