using System.Windows;
using System.Windows.Input;
using Win11ProSuite.ViewModels;

namespace Win11ProSuite;

public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    public MainWindow()
    {
        InitializeComponent();
        ViewModel = new MainViewModel(Dispatcher);
        DataContext = ViewModel;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch (InvalidOperationException) { }
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        // Keep the pill / dock styling alive in the background; launching the exe again reopens this window.
        if (ViewModel.KeepsRunningInBackground)
        {
            Hide();
            ViewModel.SetStatus("Running in the background. Launch Win11ProSuite again to reopen this window.");
        }
        else
        {
            Quit();
        }
    }

    private void Quit_Click(object sender, RoutedEventArgs e) => Quit();

    private void Quit()
    {
        ViewModel.Dispose(); // closes the pill and restores the taskbar
        Application.Current.Shutdown();
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        RestoreButton.IsEnabled = false;
        try
        {
            if (!ViewModel.BackupExists)
            {
                bool created = await ViewModel.CreateBackupAsync();
                MessageBox.Show(
                    this,
                    created
                        ? $"Your current Windows settings were saved to:\n\n{ViewModel.BackupPath}\n\nUse this button any time to restore them."
                        : "The backup couldn't be created. See the status line for details.",
                    "Win11ProSuite",
                    MessageBoxButton.OK,
                    created ? MessageBoxImage.Information : MessageBoxImage.Warning);
                return;
            }

            MessageBoxResult answer = MessageBox.Show(
                this,
                "Restore your original Windows 10 settings?\n\n" +
                "This turns off Turbo Animations, the Media Island and the Dock styling, and puts back the " +
                "animation values saved in your backup.",
                "Win11ProSuite",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (answer == MessageBoxResult.Yes)
            {
                await ViewModel.RestoreDefaultsAsync();
            }
        }
        finally
        {
            RestoreButton.IsEnabled = true;
        }
    }
}
