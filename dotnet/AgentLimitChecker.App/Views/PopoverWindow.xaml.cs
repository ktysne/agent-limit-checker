using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AgentLimitChecker.App.ViewModels;
using AgentLimitChecker.Core.Shell;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace AgentLimitChecker.App.Views;

public partial class PopoverWindow : Window
{
    private readonly ShellController? controller;
    private readonly Action quit;
    private readonly PopoverViewModel model = new();
    private readonly List<(string Label, TextBox Input)> nameInputs = [];
    private ShellSnapshot? snapshot;
    private bool updating;
    public bool HideOnDeactivate { get; set; } = true;
    public event Action? HideRequested;

    public PopoverWindow(ShellController? controller, Action quit)
    {
        this.controller = controller;
        this.quit = quit;
        InitializeComponent();
        DataContext = model;
    }

    public void UpdateSnapshot(ShellSnapshot value, double? now = null)
    {
        Dispatcher.VerifyAccess();
        snapshot = value;
        updating = true;
        try
        {
            ApplyTheme(value.Theme);
            model.Update(value, now ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            Interval.SelectedValue = value.Settings.PollingIntervalSec.ToString(System.Globalization.CultureInfo.InvariantCulture);
            AutoLaunch.IsChecked = value.AutoLaunchEnabled;
            var config = value.Settings.Ntfy;
            if (!TopicUrl.IsKeyboardFocusWithin) TopicUrl.Text = config.TopicUrl;
            if (!AccessToken.IsKeyboardFocusWithin) AccessToken.Password = config.AccessToken;
            NotifyFiveHour.IsChecked = config.NotifyFiveHour;
            NotifyWeekly.IsChecked = config.NotifyWeekly;
            NotifyExpiry.IsChecked = config.NotifyResetCreditsExpiry;
            NtfyStatus.Text = PopoverPresentation.NtfyStatus(config);
            UpdateNames(value);
        }
        finally { updating = false; }
    }

    public void OpenSettings(bool open)
    {
        model.SettingsOpen = open;
        SettingsButton.Background = open ? (Brush)Resources["Hover"] : Brushes.Transparent;
    }

    private void ApplyTheme(string theme)
    {
        var light = theme == "light";
        var colors = new Dictionary<string, string>
        {
            ["Bg"] = light ? "#f7f7f7" : "#1e1e1e", ["Panel"] = light ? "#ffffff" : "#262626",
            ["Text"] = light ? "#202124" : "#e6e6e6", ["Muted"] = light ? "#6f7378" : "#8a8a8a",
            ["Label"] = light ? "#4f5358" : "#b0b0b0", ["Button"] = light ? "#3c4043" : "#cfcfcf",
            ["Hover"] = light ? "#143c4043" : "#14ffffff", ["Field"] = light ? "#ffffff" : "#1e1e1e",
            ["FieldBorder"] = light ? "#d7dce0" : "#3a3a3a", ["Track"] = light ? "#1f3c4043" : "#14ffffff",
            ["ErrorBg"] = light ? "#1ff57c00" : "#1aff9800", ["ErrorBorder"] = light ? "#f57c00" : "#ff9800",
            ["ErrorTitle"] = light ? "#b85d00" : "#ffb74d", ["ErrorText"] = light ? "#3c4043" : "#d0d0d0"
        };
        foreach (var (key, color) in colors) Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        if (model.SettingsOpen) SettingsButton.Background = (Brush)Resources["Hover"];
    }

    private void UpdateNames(ShellSnapshot value)
    {
        var accounts = value.CodexAccounts.Where(a => !string.IsNullOrEmpty(a.Account.Label)).ToArray();
        var labels = accounts.Select(a => a.Account.Label).ToArray();
        if (!labels.SequenceEqual(nameInputs.Select(field => field.Label)))
        {
            nameInputs.Clear(); NameFields.Children.Clear();
            foreach (var account in accounts)
            {
                var field = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
                var label = new TextBlock { Text = account.Account.Label, FontSize = 11, Margin = new Thickness(0, 0, 0, 4) };
                label.SetResourceReference(TextBlock.ForegroundProperty, "Label");
                var input = new TextBox { MaxLength = 40, Tag = account.Account.Label, ToolTip = account.DefaultName };
                System.Windows.Automation.AutomationProperties.SetName(input, account.Account.Label);
                input.LostKeyboardFocus += OnAccountName;
                input.KeyDown += (_, e) => { if (e.Key == Key.Enter) SaveName(input); };
                field.Children.Add(label); field.Children.Add(input);
                NameFields.Children.Add(field); nameInputs.Add((account.Account.Label, input));
            }
        }
        for (var i = 0; i < accounts.Length; i++)
        {
            var input = nameInputs[i].Input;
            if (!input.IsKeyboardFocusWithin) input.Text = accounts[i].CustomName ?? "";
        }
        NamesGroup.Visibility = accounts.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnSettings(object sender, RoutedEventArgs e) => OpenSettings(!model.SettingsOpen);
    private async void OnRefresh(object sender, RoutedEventArgs e) { if (controller is not null) await controller.RefreshNowAsync(); }
    private async void OnLogin(object sender, RoutedEventArgs e)
    {
        if (controller is not null && ((FrameworkElement)sender).DataContext is ServiceViewModel service)
            await controller.OpenLoginAsync(service.Target, service.AccountId);
    }
    private async void OnDefaultLogin(object sender, RoutedEventArgs e)
    {
        if (controller is not null && snapshot is not null) await controller.OpenLoginAsync("codex", snapshot.CodexDefaultAccount.Id);
    }
    private void OnInterval(object sender, SelectionChangedEventArgs e)
    {
        if (!updating && Interval.SelectedValue is string seconds && int.TryParse(seconds, out var value)) controller?.SetPollingInterval(value);
    }
    private void OnAutoLaunch(object sender, RoutedEventArgs e) { if (!updating) controller?.SetAutoLaunch(AutoLaunch.IsChecked == true); }
    private void OnAccountName(object sender, KeyboardFocusChangedEventArgs e) => SaveName((TextBox)sender);
    private void SaveName(TextBox input)
    {
        if (!updating && input.Tag is string label && snapshot?.CodexAccounts.FirstOrDefault(a => a.Account.Label == label)?.CustomName != input.Text)
            controller?.SetAccountName(label, input.Text);
    }
    private void OnNtfyText(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!updating && TopicUrl.Text != snapshot?.Settings.Ntfy.TopicUrl)
            controller?.SetNtfySettings(new JsonObject { ["topicUrl"] = TopicUrl.Text });
    }
    private void OnToken(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!updating && AccessToken.Password != snapshot?.Settings.Ntfy.AccessToken)
            controller?.SetNtfySettings(new JsonObject { ["accessToken"] = AccessToken.Password });
    }
    private void OnNtfySwitch(object sender, RoutedEventArgs e)
    {
        if (!updating && sender is CheckBox { Tag: string key } check)
            controller?.SetNtfySettings(new JsonObject { [key] = check.IsChecked == true });
    }
    private void OnQuit(object sender, RoutedEventArgs e) => quit();
    private void OnDeactivated(object? sender, EventArgs e) { if (HideOnDeactivate) HideRequested?.Invoke(); }
    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; HideRequested?.Invoke(); }
    }
    private void OnBarSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var grid = (Grid)sender;
        if (grid.Children[1] is Border fill && grid.Tag is double fraction) fill.Width = grid.ActualWidth * fraction;
    }
    private void OnMetersLoaded(object sender, RoutedEventArgs e)
    {
        var items = (ItemsControl)sender;
        if (items.Items.Count == 0 || items.DataContext is ServiceViewModel { Credits.Count: > 0 }) return;
        var container = items.ItemContainerGenerator.ContainerFromIndex(items.Items.Count - 1);
        if (container is null) return;
        var panel = FindVisual<StackPanel>(container);
        if (panel is not null) panel.Margin = new Thickness(0);
    }
    private static T? FindVisual<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindVisual<T>(child) is { } nested) return nested;
        }
        return null;
    }
}
