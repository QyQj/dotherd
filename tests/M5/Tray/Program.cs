using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;

if (args is not [var cli, var hostPath, var profile, var sample]) return 2;
Environment.SetEnvironmentVariable("DOTHERD_HOME", profile);
using var host = Process.Start(new ProcessStartInfo(hostPath)
{
    UseShellExecute = false, ArgumentList = { "--home", profile }
}) ?? throw new InvalidOperationException("Could not start the test host.");

void Command(params string[] arguments) => Read(arguments);
string Read(params string[] arguments)
{
    var start = new ProcessStartInfo(cli) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (string argument in arguments) start.ArgumentList.Add(argument);
    using var child = Process.Start(start)!;
    var output = child.StandardOutput.ReadToEndAsync();
    var error = child.StandardError.ReadToEndAsync();
    if (!child.WaitForExit(20000)) throw new TimeoutException($"CLI timed out: {string.Join(' ', arguments)}.");
    if (child.ExitCode != 0) throw new InvalidOperationException(error.Result.Trim());
    return output.Result;
}

T Wait<T>(Func<T> read, Func<T, bool> completed, string failure)
{
    var clock = Stopwatch.StartNew();
    do
    {
        if (host.HasExited) throw new InvalidOperationException($"Test host exited with code {host.ExitCode}.");
        var result = read();
        if (completed(result)) return result;
        Thread.Sleep(70);
    } while (clock.Elapsed < TimeSpan.FromSeconds(10));
    throw new InvalidOperationException(failure);
}

nint Find(Func<nint, bool> match) => Native.Windows(host.Id).FirstOrDefault(match);
nint Manager() => Find(handle => Native.IsWindowVisible(handle) != 0 &&
    AutomationElement.FromHandle(handle)?.Current.Name == "DotHerd");
int Actions() => Read("logs", "tray-test").Split('\n').Count(line => line.TrimEnd().EndsWith("[stdout] action=open", StringComparison.Ordinal));

try
{
    // The callback message and per-process IDs come from the pinned Avalonia Win32 implementation.
    const uint mainId = 1, appId = 2;
    nint sink = Wait(() => Find(handle => Native.ClassName(handle).StartsWith("AvaloniaMessageWindow ", StringComparison.Ordinal)),
        handle => handle != 0, "The test host has no Avalonia message window.");
    var main = Wait(() => Native.TrayRect(sink, mainId), rect => rect is not null, "Windows Shell did not register the manager tray icon.");
    Console.WriteLine($"PASS manager icon registered with Windows Shell ({main}).");

    Command();
    var window = Wait(Manager, handle => handle != 0, "The manager did not open.");
    Native.Post(window, host.Id, 0x0010); // WM_CLOSE
    Wait(Manager, handle => handle == 0, "Closing the manager did not dismiss its window.");
    if (Native.TrayRect(sink, mainId) is null) throw new InvalidOperationException("Closing the window removed the manager tray icon.");
    Native.TrayCallback(sink, host.Id, mainId, rightClick: false);
    Wait(Manager, handle => handle != 0, "The manager tray callback did not reopen the window.");
    Console.WriteLine("PASS closing the manager retained the Shell icon; the Shell-shaped activation callback reopened it.");

    Command("add", "tray-test", "--readiness", "notify", "--action", "open", "--", sample);
    Command("start", "tray-test");
    Command("tray", "tray-test", "on");
    var independent = Wait(() => Native.TrayRect(sink, appId), rect => rect is not null, "Windows Shell did not register the independent tray icon.");
    Console.WriteLine($"PASS independent application icon registered with Windows Shell ({independent}).");

    int baseline = Actions();
    Native.TrayCallback(sink, host.Id, appId, rightClick: false);
    Wait(Actions, count => count == baseline + 1, "The independent icon activation did not reach the application.");
    Console.WriteLine("PASS independent tray activation reached the author's open action.");

    Native.TrayCallback(sink, host.Id, appId, rightClick: true);
    var actionName = new PropertyCondition(AutomationElement.NameProperty, "open");
    AutomationElement? MenuItem()
    {
        foreach (var handle in Native.Windows(host.Id).Where(handle => Native.IsWindowVisible(handle) != 0))
        {
            var root = AutomationElement.FromHandle(handle);
            if (root?.FindFirst(TreeScope.Descendants, actionName) is { } item &&
                item.Current.ControlType == ControlType.MenuItem) return item;
        }
        return null;
    }
    var action = Wait(MenuItem, item => item is not null, "The independent tray menu did not expose its open action.");
    ((InvokePattern)action!.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
    Wait(Actions, count => count == baseline + 2, "Invoking the real tray menu item did not reach the application.");
    Console.WriteLine("PASS the actual tray menu item's UI Automation Invoke routed the author's open action.");

    Command("tray", "tray-test", "off");
    Wait(() => Native.TrayRect(sink, appId), rect => rect is null, "Windows Shell retained the disabled independent tray icon.");
    if (Native.TrayRect(sink, mainId) is null) throw new InvalidOperationException("Removing the independent icon removed the manager tray icon.");
    Console.WriteLine("PASS disabling the independent icon withdrew only that application icon from Windows Shell.");
    return 0;
}
finally
{
    Command("disable");
}

internal static partial class Native
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier
    {
        public uint Size;
        public nint Window;
        public uint Id;
        public Guid Guid;
    }

    [StructLayout(LayoutKind.Sequential)]
    public readonly record struct Rect(int Left, int Top, int Right, int Bottom);

    [LibraryImport("shell32.dll")]
    private static partial int Shell_NotifyIconGetRect(in NotifyIconIdentifier id, out Rect rect);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindowExW(nint parent, nint after, string? className, string? caption);
    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);
    [LibraryImport("user32.dll")]
    private static unsafe partial int GetClassNameW(nint window, char* name, int length);
    [LibraryImport("user32.dll")]
    public static partial int IsWindowVisible(nint window);
    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int PostMessageW(nint window, uint message, nuint id, nint data);

    public static IEnumerable<nint> Windows(int processId)
    {
        for (nint window = FindWindowExW(0, 0, null, null); window != 0; window = FindWindowExW(0, window, null, null))
            if (GetWindowThreadProcessId(window, out uint owner) != 0 && owner == processId) yield return window;
    }

    public static unsafe string ClassName(nint window)
    {
        const int capacity = 256;
        char* name = stackalloc char[capacity];
        return new string(name, 0, GetClassNameW(window, name, capacity));
    }

    public static Rect? TrayRect(nint window, uint id)
    {
        int size = Marshal.SizeOf<NotifyIconIdentifier>();
        if (size != 40 || Marshal.SizeOf<Rect>() != 16) throw new InvalidOperationException("Unexpected Win32 ABI for Windows x64.");
        var identifier = new NotifyIconIdentifier { Size = (uint)size, Window = window, Id = id };
        return Shell_NotifyIconGetRect(in identifier, out var rect) == 0 && rect.Right > rect.Left && rect.Bottom > rect.Top
            ? rect : null;
    }

    public static void TrayCallback(nint window, int processId, uint id, bool rightClick)
    {
        if (!ClassName(window).StartsWith("AvaloniaMessageWindow ", StringComparison.Ordinal) || TrayRect(window, id) is null)
            throw new InvalidOperationException("The test tray icon is missing.");
        Post(window, processId, 0x0800, id, rightClick ? 0x0205 : 0x0202); // WM_USER + 1024, WM_[R/L]BUTTONUP
    }

    public static void Post(nint window, int processId, uint message, nuint id = 0, nint data = 0)
    {
        if (GetWindowThreadProcessId(window, out uint owner) == 0 || owner != processId)
            throw new InvalidOperationException("Refusing to post to a window outside the test host.");
        if (PostMessageW(window, message, id, data) == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
    }
}
