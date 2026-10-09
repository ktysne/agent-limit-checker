using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using AgentLimitChecker.App.ViewModels;

namespace AgentLimitChecker.App.Views;
public partial class UpdateAvailableWindow : Window
{
    private bool _closeRequestedByViewModel;

    public UpdateAvailableViewModel ViewModel { get; }

    public UpdateAvailableWindow(UpdateAvailableViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = ViewModel;
        ViewModel.CloseRequested += OnCloseRequested;
        Closing += OnClosing;
    }
    public UpdateDialogChoice Choice { get; private set; } = UpdateDialogChoice.RemindLater;

    private void OnCloseRequested(object? sender, UpdateDialogChoice choice)
    {
        Choice = choice;
        _closeRequestedByViewModel = true;
        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closeRequestedByViewModel || ViewModel.TryClose())
        {
            return;
        }

        e.Cancel = true;
    }

    private async void OnPrimaryClick(object sender, RoutedEventArgs e) => await ViewModel.ExecutePrimaryAsync();

    private void OnSecondaryClick(object sender, RoutedEventArgs e) => ViewModel.ExecuteSecondary();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        if (ViewModel.IsSecondaryEnabled)
        {
            ViewModel.ExecuteSecondary();
        }
    }

    private void OnSkipClick(object sender, RoutedEventArgs e) => ViewModel.ExecuteSkip();

    private void OnOpenBrowserClick(object sender, RoutedEventArgs e) => ViewModel.ExecuteOpenBrowser();
}
