using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace DotHerd;

internal sealed class DesktopSession : IDisposable
{
    private readonly TaskCompletionSource<DesktopApp> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread thread;

    public DesktopSession(Profile profile)
    {
        thread = new Thread(() =>
        {
            try
            {
                AppBuilder.Configure(() => new DesktopApp(profile, app => ready.TrySetResult(app)))
                    .UsePlatformDetect()
                    .StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown);
            }
            catch (Exception e) { ready.TrySetException(e); }
            finally { ready.TrySetException(new InvalidOperationException("The desktop session could not start.")); }
        }) { Name = "DotHerd desktop", IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public async Task Show()
    {
        var app = await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Dispatcher.UIThread.InvokeAsync(() => app.Show()).GetTask().WaitAsync(TimeSpan.FromSeconds(10));
    }

    public void Dispose()
    {
        if (ready.Task.IsCompletedSuccessfully && thread.IsAlive)
        {
            var app = ready.Task.Result;
            Dispatcher.UIThread.Post(() => ((IClassicDesktopStyleApplicationLifetime)app.ApplicationLifetime!).Shutdown());
        }
        thread.Join(TimeSpan.FromSeconds(5));
    }
}
