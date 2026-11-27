using System.IO.Compression;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;

namespace DotHerd;

internal sealed partial class DesktopApp : Application
{
    private readonly Profile profile;
    private readonly Action<DesktopApp> started;
    private readonly DesktopClient client;
    private readonly CancellationTokenSource exiting = new();
    private readonly Dictionary<Guid, AppTray> appTrays = new();
    private readonly TrayIcons icons = new();
    private DesktopStatus[] apps = [];
    private ManagerWindow? window;
    private WindowIcon defaultIcon = null!;
    private NativeMenuItem failuresItem = null!;
    private NativeMenuItem exitItem = null!;
    private string? refreshError;
    private string? trayError;

    public DesktopApp(Profile profile, Action<DesktopApp> started)
    {
        this.profile = profile;
        this.started = started;
        client = new DesktopClient(profile);
    }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        using (var image = AssetLoader.Open(new Uri("avares://DotHerd.Host/Assets/DotHerd.png")))
            defaultIcon = new WindowIcon(image);
        var main = new TrayIcon { Icon = defaultIcon, ToolTipText = "DotHerd" };
        main.Clicked += (_, _) => Show();
        failuresItem = Item("Failed applications", (_, _) => Show(failures: true));
        exitItem = Item("Exit and stop applications…", async (_, _) => await Exit());
        main.Menu = new NativeMenu();
        main.Menu.Items.Add(Item("Open DotHerd", (_, _) => Show()));
        main.Menu.Items.Add(failuresItem);
        main.Menu.Items.Add(new NativeMenuItemSeparator());
        main.Menu.Items.Add(exitItem);
        icons.Add(main);
        TrayIcon.SetIcons(this, icons);
        desktop.Exit += (_, _) =>
        {
            exiting.Cancel();
            foreach (var icon in icons) icon.Dispose();
        };
        started(this);
        _ = Monitor();
    }

    internal ManagerWindow Show(bool failures = false)
    {
        if (window is null)
        {
            window = new ManagerWindow(client) { Icon = defaultIcon };
            ((IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!).MainWindow = window;
            window.Closed += (_, _) => window = null;
        }
        if (failures) window.ShowFailures();
        window.Refresh(apps, refreshError ?? trayError);
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        return window;
    }

    private async Task Exit()
    {
        exitItem.IsEnabled = false;
        ManagerWindow? owner = null;
        bool declined = false;
        try
        {
            owner = Show();
            await client.Send(new("host-exit-preview"), async preview =>
            {
                declined = !await owner.ConfirmExit(preview);
                return !declined;
            }, exiting.Token);
        }
        catch (OperationCanceledException) when (exiting.IsCancellationRequested) { }
        catch (InvalidOperationException) when (declined) { }
        catch (Exception e) when (owner is not null) { owner.Report(e.Message); }
        finally { exitItem.IsEnabled = true; }
    }

    private async Task Monitor()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            do
            {
                try
                {
                    apps = await client.Read<DesktopStatus[]>(new("status"), exiting.Token);
                    refreshError = null;
                    SyncTrays();
                }
                catch (Exception e) when (e is not OperationCanceledException) { refreshError = e.Message; }
                window?.Refresh(apps, refreshError ?? trayError);
            } while (await timer.WaitForNextTickAsync(exiting.Token));
        }
        catch (OperationCanceledException) when (exiting.IsCancellationRequested) { }
    }

    private void SyncTrays()
    {
        failuresItem.Header = $"Failed applications ({apps.Count(app => app.Failed)})";
        var separate = apps.Where(app => app.SeparateTray).ToDictionary(app => app.Id);
        foreach (var entry in appTrays.ToArray())
        {
            if (!separate.TryGetValue(entry.Key, out var app) || !entry.Value.Matches(app))
            {
                appTrays.Remove(entry.Key);
                icons.Remove(entry.Value.Icon);
                entry.Value.Icon.Dispose();
            }
        }

        foreach (var app in separate.Values)
        {
            if (!appTrays.TryGetValue(app.Id, out var tray))
            {
                var image = defaultIcon;
                string? error = null;
                if (app.Package?.Tray?.Text("icon") is { } iconPath)
                {
                    try { image = LoadPackageIcon(app, iconPath); }
                    catch (Exception e) when (e is not OutOfMemoryException)
                    { error = $"Tray icon for {app.DisplayName}: {e.Message}"; }
                }
                tray = new AppTray(app, image, error, Invoke);
                appTrays.Add(app.Id, tray);
                icons.Add(tray.Icon);
            }
            tray.Update(app);
        }
        trayError = appTrays.Values.Select(tray => tray.Error).FirstOrDefault(error => error is not null);
    }

    private WindowIcon LoadPackageIcon(DesktopStatus app, string relative)
    {
        if (!ToolManifest.SafeRelative(relative)) throw ToolManifest.Error("tray.icon");
        var package = app.Package!;
        string id = package.Id.ToLowerInvariant();
        string version = PackageFeed.ValidateIdentity(id, package.Version)!.ToNormalizedString();
        string root = profile.Root;
        string[] parts = ["packages", app.Id.ToString("N"), version, ".store", id, version, id, version];
        foreach (string part in parts)
        {
            root = Path.Combine(root, part);
            if ((File.GetAttributes(root) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                throw new InvalidDataException("The icon archive must be inside the managed package.");
        }
        string archive = Path.Combine(root, $"{id}.{version}.nupkg");
        if ((File.GetAttributes(archive) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException("The icon archive must be a regular package file.");
        using var zip = ZipFile.OpenRead(archive);
        var asset = zip.GetEntry(relative) ?? throw ToolManifest.Error("tray.icon");
        using var icon = asset.Open();
        using var buffer = new MemoryStream();
        icon.CopyTo(buffer);
        buffer.Position = 0;
        return new WindowIcon(buffer);
    }

    private async Task Invoke(DesktopStatus app, string id)
    {
        if (!app.CanInvoke(id)) return;
        try { await client.Send(new("action", app.Name, Action: id, Instance: app.Instance), cancellation: exiting.Token); }
        catch (OperationCanceledException) when (exiting.IsCancellationRequested) { }
        catch (Exception e) { Show().Report(e.Message); }
    }

    private static NativeMenuItem Item(string label, EventHandler clicked)
    {
        var item = new NativeMenuItem(label);
        item.Click += clicked;
        return item;
    }

    private sealed class AppTray
    {
        private readonly DesktopStatus original;
        private DesktopStatus app;
        private readonly Dictionary<string, NativeMenuItem> items = new(StringComparer.Ordinal);
        public TrayIcon Icon { get; }
        public string? Error { get; }

        public AppTray(DesktopStatus app, WindowIcon image, string? error, Func<DesktopStatus, string, Task> invoke)
        {
            this.app = original = app;
            Error = error;
            var menu = new NativeMenu();
            foreach (var id in app.Actions.Keys)
            {
                var item = Item(app.Label(id), async (_, _) => await invoke(this.app, id));
                menu.Items.Add(item);
                items.Add(id, item);
            }
            Icon = new TrayIcon { Icon = image, Menu = menu };
            Icon.Clicked += async (_, _) =>
            {
                if (this.app.Activate is { } id) await invoke(this.app, id);
            };
        }

        public bool Matches(DesktopStatus next) => original.Package?.Version == next.Package?.Version &&
            original.Actions.Keys.SequenceEqual(next.Actions.Keys);

        public void Update(DesktopStatus next)
        {
            app = next;
            string tooltip = next.Tooltip ?? next.DisplayName;
            int length = Math.Min(tooltip.Length, 127);
            if (length > 0 && char.IsHighSurrogate(tooltip[length - 1])) length--;
            Icon.ToolTipText = tooltip[..length];
            foreach (var (id, item) in items) item.IsEnabled = next.CanInvoke(id);
        }
    }
}
