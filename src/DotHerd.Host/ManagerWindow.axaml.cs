using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace DotHerd;

internal sealed partial class ManagerWindow : Window
{
    private enum Page { Applications, Mcp, Settings }
    private const string NoProblem = "—";
    private readonly DesktopClient client;
    private DesktopStatus[] apps = [];
    private AppListItem[] entries = [];
    private Page page;
    private string? monitorError, feedback;
    private bool busy;
    private Guid? actionOwner;
    private string? actionVersion;
    private bool? loginEnabled;
    private sealed record AppListItem(Guid Id, string Label) { public override string ToString() => Label; }
    private sealed record LoginState(bool Enabled);

    public ManagerWindow(DesktopClient client)
    {
        this.client = client;
        InitializeComponent();
        ShowPage(Page.Applications);
    }

    private DesktopStatus? Selected => AppList.SelectedItem is AppListItem item
        ? apps.FirstOrDefault(app => app.Id == item.Id)
        : null;

    public void ShowFailures()
    {
        FailuresOnly.IsChecked = true;
        ShowPage(Page.Applications);
    }

    public void Refresh(DesktopStatus[] next, string? error)
    {
        apps = next;
        monitorError = error;
        RefreshList();
        RefreshDetails();
        RefreshFeedback();
    }

    public void Report(string text)
    {
        feedback = text;
        RefreshFeedback();
    }

    public Task<bool> ConfirmExit(string preview) => Question("Exit and stop applications", preview, "Exit and stop");

    private void RefreshFeedback() => Feedback.Text = monitorError ?? feedback ?? "";

    private void ShowPage(Page next)
    {
        page = next;
        ApplicationsNav.Classes.Set("selected", page == Page.Applications);
        McpNav.Classes.Set("selected", page == Page.Mcp);
        SettingsNav.Classes.Set("selected", page == Page.Settings);
        PageTitle.Text = page switch { Page.Applications => "Applications", Page.Mcp => "MCP services", _ => "Settings" };
        InstallButton.IsVisible = page == Page.Applications;
        ApplicationPanel.IsVisible = page != Page.Settings;
        SettingsPanel.IsVisible = page == Page.Settings;
        FailuresOnly.IsVisible = page == Page.Applications;
        RefreshList();
        RefreshDetails();
    }

    private void RefreshList()
    {
        var selection = (AppList.SelectedItem as AppListItem)?.Id;
        var next = apps.Where(app => page == Page.Mcp ? app.Mcp is not null : FailuresOnly.IsChecked != true || app.Failed)
            .OrderBy(app => app.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(app => new AppListItem(app.Id, app.DisplayName == app.Name ? app.Name : $"{app.DisplayName} · {app.Name}")).ToArray();
        if (entries.SequenceEqual(next)) return;
        entries = next;
        AppList.ItemsSource = entries;
        AppList.SelectedItem = entries.FirstOrDefault(item => item.Id == selection) ?? entries.FirstOrDefault();
    }

    private void RefreshDetails()
    {
        var app = Selected;
        EmptyMessage.Text = page == Page.Mcp ? "No MCP services installed." :
            FailuresOnly.IsChecked == true ? "No application failures." : "No applications installed.";
        EmptyMessage.IsVisible = app is null;
        AppDetails.IsVisible = app is not null;
        InstallButton.IsEnabled = !busy;
        StartupEnabled.IsChecked = loginEnabled;
        StartupEnabled.IsEnabled = loginEnabled is not null && !busy;
        if (app is null) return;

        AppTitle.Text = app.DisplayName;
        AppVersion.Text = app.Package?.Version is { } version ? $"{app.Name}  ·  {version}" : "Local application";
        ProcessState.Text = app.Pid is int pid ? $"{StatusLabel(app.State)}  ·  PID {pid}" : StatusLabel(app.State);
        ProcessFailure.Text = string.Join("\n", new[] { app.LastFailure, app.UpdateFailure, app.RecoveryError }
            .OfType<string>().Distinct().DefaultIfEmpty(NoProblem));
        PackageSourceLabel.IsVisible = PackageSource.IsVisible = app.Package is not null;
        PackageSource.Text = app.Package?.Source;
        AppOperations.IsVisible = page == Page.Applications;
        McpDetails.IsVisible = page == Page.Mcp;
        bool canOperate = !busy && app.State != "uninstalling";
        StartButton.IsEnabled = canOperate && app.State is "stopped" or "failed";
        StopButton.IsEnabled = canOperate && app.WantRunning;
        RestartButton.IsEnabled = canOperate;
        UpdateButton.IsEnabled = canOperate && app.Package is not null;
        UninstallButton.IsEnabled = !busy;
        SeparateTray.IsEnabled = canOperate;
        SeparateTray.IsChecked = app.SeparateTray;
        if (actionOwner != app.Id || actionVersion != app.Package?.Version)
        {
            actionOwner = app.Id;
            actionVersion = app.Package?.Version;
            AuthorActions.Children.Clear();
            foreach (string id in app.Actions.Keys)
            {
                var button = new Button { Content = app.Label(id), Tag = id };
                button.Click += async (_, _) =>
                {
                    if (Selected is { } selected && selected.CanInvoke(id))
                        await Operate(() => client.Send(new("action", selected.Name, Action: id, Instance: selected.Instance)), "Action sent.");
                };
                AuthorActions.Children.Add(button);
            }
        }
        AuthorActions.IsVisible = AuthorActions.Children.Count != 0;
        foreach (var button in AuthorActions.Children.OfType<Button>())
            button.IsEnabled = canOperate && app.CanInvoke((string)button.Tag!);

        if (app.Mcp is not { } mcp) return;
        McpTransport.Text = mcp.OnConnection ? $"{mcp.Transport} · on connection" : mcp.Transport;
        McpPublication.Text = mcp.Revision is { } revision ? $"{StatusLabel(mcp.State)} · {revision}" : StatusLabel(mcp.State);
        McpError.Text = mcp.Error ?? NoProblem;
        McpTools.Text = mcp.Tools.Length == 0 ? NoProblem : string.Join("\n", mcp.Tools);
        GatewayEnabled.IsEnabled = canOperate;
        GatewayEnabled.IsChecked = mcp.Enabled;
    }

    private static string StatusLabel(string state) => state switch
    {
        "connectable" => "On demand",
        "registered" => "Registered",
        "backoff" => "Waiting to retry",
        _ => char.ToUpperInvariant(state[0]) + state[1..]
    };

    private void ApplicationsClicked(object? sender, RoutedEventArgs e)
    {
        FailuresOnly.IsChecked = false;
        ShowPage(Page.Applications);
    }

    private void McpClicked(object? sender, RoutedEventArgs e) => ShowPage(Page.Mcp);
    private async void SettingsClicked(object? sender, RoutedEventArgs e)
    {
        ShowPage(Page.Settings);
        try
        {
            loginEnabled = (await client.Read<LoginState>(new("startup", Action: "status"))).Enabled;
            RefreshDetails();
        }
        catch (Exception error) { Report(error.Message); }
    }

    private void FilterClicked(object? sender, RoutedEventArgs e) { RefreshList(); RefreshDetails(); }
    private void SelectionChanged(object? sender, SelectionChangedEventArgs e) => RefreshDetails();

    private async Task Operate(Func<Task<Reply>> operation, string? success = null)
    {
        if (busy) return;
        busy = true;
        RefreshDetails();
        Report("Working…");
        try
        {
            var reply = await operation();
            Report(success ?? reply.Output);
            Refresh(await client.Read<DesktopStatus[]>(new("status")), null);
        }
        catch (Exception e) { Report(e.Message); }
        finally { busy = false; RefreshDetails(); }
    }

    private Task AppOperation(string command, string? action = null, string? success = null) => Selected is { } app
        ? Operate(() => client.Send(new(command, app.Name, Action: action)), success)
        : Task.CompletedTask;

    private async void StartClicked(object? sender, RoutedEventArgs e) => await AppOperation("start", success: "Start request completed.");
    private async void StopClicked(object? sender, RoutedEventArgs e) => await AppOperation("stop", success: "Application stopped.");
    private async void RestartClicked(object? sender, RoutedEventArgs e) => await AppOperation("restart", success: "Restart request completed.");
    private async void TrayClicked(object? sender, RoutedEventArgs e) =>
        await AppOperation("tray", SeparateTray.IsChecked == true ? "on" : "off", "Tray preference saved.");
    private async void GatewayClicked(object? sender, RoutedEventArgs e) =>
        await AppOperation(GatewayEnabled.IsChecked == true ? "mcp-enable" : "mcp-disable", success: "Gateway preference saved.");

    private async void StartupClicked(object? sender, RoutedEventArgs e)
    {
        bool enable = StartupEnabled.IsChecked == true;
        await Operate(async () =>
        {
            var result = await client.Read<LoginState>(new("startup", Action: enable ? "on" : "off"));
            loginEnabled = result.Enabled;
            return new Reply(true, result.Enabled ? "Windows login startup enabled." : "Windows login startup disabled.");
        });
    }

    private async void LogsClicked(object? sender, RoutedEventArgs e)
    {
        if (Selected is not { } app) return;
        try
        {
            var log = (await client.Send(new("logs", app.Name))).Output;
            var text = new TextBox
            {
                Text = log.Length == 0 ? "No logs yet." : log,
                FontFamily = new FontFamily("Consolas"), IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap
            };
            var dialog = CreateDialog($"{app.DisplayName} logs", text, width: 780);
            dialog.Height = 520;
            dialog.SizeToContent = SizeToContent.Manual;
            await dialog.ShowDialog(this);
        }
        catch (Exception error) { Report(error.Message); }
    }

    private async void UninstallClicked(object? sender, RoutedEventArgs e)
    {
        if (Selected is not { } app ||
            !await Question("Uninstall application", $"Uninstall {app.DisplayName}? Its data and logs are retained.", "Uninstall")) return;
        await Operate(() => client.Send(new("uninstall", app.Name)));
    }

    private async void InstallClicked(object? sender, RoutedEventArgs e) => await PackageOperation(null);
    private async void UpdateClicked(object? sender, RoutedEventArgs e)
    {
        if (Selected is { Package: not null } app) await PackageOperation(app);
    }

    private async Task PackageOperation(DesktopStatus? app)
    {
        string operation = app is null ? "Install" : "Update";
        var package = new TextBox { Text = app?.Name, IsReadOnly = app is not null };
        var version = new TextBox { PlaceholderText = "Latest stable when empty" };
        var source = new TextBox { PlaceholderText = "Configured feed name, URL, or directory", Text = app?.Package?.Source };
        var inputs = new TextBox { PlaceholderText = "INPUT=ENVIRONMENT_VARIABLE (one per line)", AcceptsReturn = true };
        var fields = new StackPanel
        {
            Spacing = 8,
            Children = { new TextBlock { Text = "Package ID" }, package, new TextBlock { Text = "Exact version (optional)" },
                version, new TextBlock { Text = "Package source (optional)" }, source,
                new TextBlock { Text = "MCP input bindings (optional)" }, inputs }
        };
        var dialog = CreateDialog($"{operation} package", fields, operation);
        if (await dialog.ShowDialog<bool>(this))
            await Operate(async () =>
            {
                var bindings = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (string line in (inputs.Text ?? "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] pair = line.Split('=', 2, StringSplitOptions.TrimEntries);
                    if (pair.Length != 2 || pair.Any(string.IsNullOrWhiteSpace))
                        throw new ArgumentException("MCP inputs use INPUT=ENVIRONMENT_VARIABLE, one binding per line.");
                    if (!bindings.TryAdd(pair[0], pair[1])) throw new ArgumentException($"Duplicate MCP input: {pair[0]}");
                }
                var request = new InstallRequest(package.Text?.Trim() ?? "", Optional(version.Text),
                    Environment.CurrentDirectory, Optional(source.Text), false, null, [], bindings);
                var result = await client.Send(new(operation.ToLowerInvariant(), Install: request),
                    preview => Question("Confirm MCP registration", preview, "Confirm"));
                return result.Output.StartsWith('{') ? result with { Output = $"{operation} completed." } : result;
            });
    }

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private Task<bool> Question(string title, string message, string confirm)
    {
        var details = new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
        return CreateDialog(title, new ScrollViewer { MaxHeight = 330, Content = details }, confirm).ShowDialog<bool>(this);
    }

    private static Window CreateDialog(string title, Control content, string? confirm = null, double width = 485)
    {
        var dialog = new Window
        {
            Title = title, Width = width, SizeToContent = SizeToContent.Height, CanResize = confirm is null,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        var cancel = new Button { Content = confirm is null ? "Close" : "Cancel", IsCancel = true };
        cancel.Click += (_, _) => dialog.Close(false);
        buttons.Children.Add(cancel);
        if (confirm is not null)
        {
            var action = new Button { Content = confirm, IsDefault = true, Classes = { "accent" } };
            action.Click += (_, _) => dialog.Close(true);
            buttons.Children.Add(action);
        }
        var layout = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), RowSpacing = 16, Margin = new Thickness(18) };
        layout.Children.Add(content);
        Grid.SetRow(buttons, 1);
        layout.Children.Add(buttons);
        dialog.Content = layout;
        return dialog;
    }
}
