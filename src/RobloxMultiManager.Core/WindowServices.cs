using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace RobloxMultiManager.Core;

internal static class NativeWindows
{
    internal delegate bool EnumWindowsProc(nint hWnd, nint lParam);
    internal delegate bool MonitorEnumProc(
        nint hMonitor,
        nint hdcMonitor,
        ref NativeRect lprcMonitor,
        nint dwData);

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly Rectangle ToRectangle() =>
            Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MonitorInfoEx
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Device;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr64(nint hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(nint hWnd, int index);

    internal static nint GetWindowLongPtr(nint hWnd, int index) =>
        nint.Size == 8 ? GetWindowLongPtr64(hWnd, index) : (nint)GetWindowLong32(hWnd, index);

    [DllImport("user32.dll")]
    internal static extern nint GetWindow(nint hWnd, uint command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(nint hWnd, out NativeRect rect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint window, int attribute, out NativeRect bounds, int size);

    internal static bool GetVisibleRect(nint window, out NativeRect rect)
    {
        if (!GetWindowRect(window, out rect)) return false;
        if (DwmGetWindowAttribute(window, 9, out var visible, Marshal.SizeOf<NativeRect>()) == 0
            && visible.Right > visible.Left && visible.Bottom > visible.Top
            && rect.ToRectangle().Contains(visible.ToRectangle())) rect = visible;
        return true;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowTextLengthW(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowTextW(nint hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        nint hWnd,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindowAsync(nint hWnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsZoomed(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessageW(nint hWnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SendMessageTimeoutW(
        nint hWnd,
        uint message,
        nint wParam,
        nint lParam,
        uint flags,
        uint timeout,
        out nint result);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplayMonitors(
        nint hdc,
        nint clipRect,
        MonitorEnumProc callback,
        nint data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfoEx info);

    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080L;
    private const uint GwOwner = 4;
    private const uint WmNull = 0x0000;
    private const uint SmtoAbortIfHung = 0x0002;
    private const uint SmtoBlock = 0x0001;

    internal static nint FindBestWindowForProcess(int processId)
    {
        nint best = nint.Zero;
        long bestArea = -1;

        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window))
            {
                return true;
            }

            GetWindowThreadProcessId(window, out var candidateProcessId);
            if (candidateProcessId != (uint)processId)
            {
                return true;
            }

            if (GetWindow(window, GwOwner) != nint.Zero)
            {
                return true;
            }

            var extendedStyle = GetWindowLongPtr(window, GwlExStyle).ToInt64();
            if ((extendedStyle & WsExToolWindow) != 0)
            {
                return true;
            }

            if (!GetWindowRect(window, out var rect))
            {
                return true;
            }

            var width = Math.Max(0, rect.Right - rect.Left);
            var height = Math.Max(0, rect.Bottom - rect.Top);
            var area = (long)width * height;
            if (area > bestArea)
            {
                best = window;
                bestArea = area;
            }

            return true;
        }, nint.Zero);

        return best;
    }

    internal static string GetTitle(nint window)
    {
        var length = GetWindowTextLengthW(window);
        if (length <= 0)
        {
            return "Roblox";
        }

        var text = new StringBuilder(length + 1);
        _ = GetWindowTextW(window, text, text.Capacity);
        return text.ToString();
    }

    internal static bool IsResponsive(nint window)
    {
        if (window == nint.Zero || !IsWindow(window))
        {
            return false;
        }

        return SendMessageTimeoutW(
                   window,
                   WmNull,
                   nint.Zero,
                   nint.Zero,
                   SmtoAbortIfHung | SmtoBlock,
                   250,
                   out _) != nint.Zero;
    }
}

public sealed class RobloxProcessMonitor : IAsyncDisposable
{
    private readonly string _processName;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ConcurrentDictionary<string, RobloxInstanceInfo> _instances = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private Task? _worker;
    private string _lastFingerprint = string.Empty;
    private readonly ProcessResourceSampler _resources = new();
    public event EventHandler<IReadOnlyDictionary<string, ProcessResourceUsage>>? ResourcesChanged;

    public RobloxProcessMonitor(string processName = "RobloxPlayerBeta")
    {
        _processName = processName;
    }

    public event EventHandler<IReadOnlyList<RobloxInstanceInfo>>? SnapshotChanged;

    public IReadOnlyList<RobloxInstanceInfo> Current =>
        _instances.Values
            .DistinctBy(instance => instance.WindowHandle)
            .OrderBy(instance => instance.StartTimeUtc)
            .ThenBy(instance => instance.ProcessId)
            .ToArray();

    public void Start()
    {
        _worker ??= Task.Run(() => MonitorLoopAsync(_cancellation.Token));
    }

    public static int CountPlayerProcesses()
    {
        var processes = Process.GetProcessesByName("RobloxPlayerBeta");
        try
        {
            return processes.Length;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    public async Task<IReadOnlyList<RobloxInstanceInfo>> RefreshNowAsync()
    {
        await _refreshGate.WaitAsync(_cancellation.Token).ConfigureAwait(false);
        try
        {
            var snapshot = await Task.Run(CaptureSnapshot).ConfigureAwait(false);
            ApplySnapshot(snapshot);
            return snapshot;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task MonitorLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            do
            {
                await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    ApplySnapshot(CaptureSnapshot());
                }
                finally
                {
                    _refreshGate.Release();
                }
            }
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
    }

    private IReadOnlyList<RobloxInstanceInfo> CaptureSnapshot()
    {
        var found = new List<RobloxInstanceInfo>();
        var processes = Process.GetProcessesByName(_processName);
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    if (process.HasExited)
                    {
                        continue;
                    }

                    var started = process.StartTime.ToUniversalTime();

                    // The raw path is read first and kept even when the strict
                    // inspector rejects it.  A client launched by another
                    // bootstrapper runs from that program's own Versions folder,
                    // and recording nothing for it made the row read "Não
                    // verificado" and made EnsureRunningClientsMatch treat it as a
                    // version mismatch, which refused every launch.
                    var executablePath = ReadMainModulePath(process);
                    RobloxClientExecutableMetadata? client = null;
                    string? deploymentId = null;
                    string? fileVersion = null;
                    if (executablePath is not null)
                    {
                        try
                        {
                            if (RobloxClientExecutableInspector.TryInspect(
                                    executablePath,
                                    out var inspected))
                            {
                                client = inspected;
                            }
                        }
                        catch (System.ComponentModel.Win32Exception)
                        {
                            // Metadata remains unavailable at another integrity level.
                        }

                        deploymentId = ReadDeploymentFolder(executablePath);
                        fileVersion = client?.FileVersion
                                      ?? ReadFileVersion(executablePath);
                    }

                    var window = NativeWindows.FindBestWindowForProcess(process.Id);
                    var responsive = window != nint.Zero && NativeWindows.IsResponsive(window);
                    var age = DateTime.UtcNow - started;
                    var state = window == nint.Zero
                        ? RobloxInstanceState.Starting
                        : !responsive
                            ? RobloxInstanceState.NotResponding
                            : age < TimeSpan.FromSeconds(2)
                                ? RobloxInstanceState.WindowReady
                                : RobloxInstanceState.Running;

                    found.Add(new RobloxInstanceInfo(
                        process.Id,
                        started,
                        window,
                        window == nint.Zero
                            ? RobloxMultiManager.Core.L.T("Abrindo...")
                            : NativeWindows.GetTitle(window),
                        state,
                        responsive,
                        client?.ExecutablePath ?? executablePath,
                        client?.VersionId,
                        fileVersion,
                        DeploymentId: deploymentId));
                }
                catch (InvalidOperationException)
                {
                    // Process exited between enumeration and inspection.
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // A process at another integrity level is not controllable.
                }
            }
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }

        return found
            .OrderBy(instance => instance.StartTimeUtc)
            .ThenBy(instance => instance.ProcessId)
            .ToArray();
    }

    private static string? ReadMainModulePath(Process process)
    {
        try
        {
            var path = process.MainModule?.FileName;
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The <c>version-*</c> folder a client runs from, whichever launcher owns it.
    /// </summary>
    private static string? ReadDeploymentFolder(string executablePath)
    {
        try
        {
            var folder = Path.GetFileName(Path.GetDirectoryName(executablePath));
            return folder is not null
                   && RobloxInstallationLocator.IsValidVersionId(folder)
                ? folder
                : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (PathTooLongException)
        {
            return null;
        }
    }

    private static string? ReadFileVersion(string executablePath)
    {
        try
        {
            var value = FileVersionInfo
                .GetVersionInfo(executablePath)
                .FileVersion?
                .Replace(", ", ".", StringComparison.Ordinal)
                .Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private void ApplySnapshot(IReadOnlyList<RobloxInstanceInfo> snapshot)
    {
        if (_resources.Sample(snapshot) is { } resources) ResourcesChanged?.Invoke(this, resources);
        var keys = snapshot.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var stale in _instances.Keys.Where(key => !keys.Contains(key)))
        {
            _instances.TryRemove(stale, out _);
        }

        foreach (var item in snapshot)
        {
            _instances[item.Key] = item;
        }

        // The window title is deliberately excluded.  Nothing displays it, and
        // Roblox retitles its window whenever the player joins or leaves a place,
        // so including it rebuilt the whole instance list and re-triggered
        // auto-arrange during normal play.
        var fingerprint = string.Join(
            "|",
            snapshot.Select(item =>
                $"{item.Key}:{item.WindowHandle}:{item.State}:{item.IsResponsive}:"
                + $"{item.VersionId}:{item.DeploymentId}:{item.FileVersion}"));
        if (fingerprint == _lastFingerprint)
        {
            return;
        }

        _lastFingerprint = fingerprint;
        SnapshotChanged?.Invoke(this, snapshot);
    }

    public async ValueTask DisposeAsync()
    {
        _cancellation.Cancel();
        if (_worker is not null)
        {
            try
            {
                await _worker.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
        }

        _resources.Dispose();
        _cancellation.Dispose();
        _refreshGate.Dispose();
    }
}

public static class GridLayoutCalculator
{
    /// <summary>
    /// Preferred cell dimensions. Spacing yields first when the monitor is crowded.
    /// </summary>
    /// <remarks>
    /// The previous version clamped the available space with
    /// <c>Math.Max(rows, height - gap * (rows + 1))</c>, which collapsed to
    /// <c>rows</c> once the gaps exceeded the work area and then produced
    /// one-pixel-tall cells.  That is literally the "not all of them are visible"
    /// complaint: every window except the last row became an invisible sliver.
    /// </remarks>
    public const int MinimumCellWidth = 320;
    public const int MinimumCellHeight = 240;

    /// <summary>
    /// Deterministic tiling of <paramref name="count"/> windows inside
    /// <paramref name="workingArea"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The truncation remainder is spread one pixel at a time across the leading
    /// columns within each row and across row heights. Incomplete rows use the
    /// whole width; sizes within a row differ by at most one pixel.
    /// </para>
    /// <para>
    /// Every rectangle returned is inside <paramref name="workingArea"/>.  That
    /// used to hold only algebraically, and two degenerate paths broke it: the
    /// <c>Math.Max(columns, ...)</c> below and the <c>Math.Max(1, ...)</c> in
    /// <see cref="Distribute"/> both raise the summed extent above the area when
    /// the area is narrower in pixels than the number of columns or rows, and the
    /// surplus left the last cell hanging past the edge.  The final intersection
    /// makes containment unconditional instead of incidental.
    /// </para>
    /// <para>
    /// <see cref="MinimumCellWidth"/> and <see cref="MinimumCellHeight"/> are a
    /// floor for the <em>gap</em>, not for the cell: the gap is given up first, and
    /// past that point cells do keep shrinking, because a fixed number of windows
    /// cannot be tiled without overlap below some size.  Keeping the real windows
    /// on screen when a client refuses to shrink into its cell is
    /// <see cref="Contain"/>'s job, not this method's.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<Rectangle> Calculate(
        Rectangle workingArea,
        int count,
        int gap = 8,
        int minimumWidth = MinimumCellWidth,
        int minimumHeight = MinimumCellHeight)
    {
        if (count <= 0)
        {
            return [];
        }

        gap = Math.Max(0, gap);
        if (workingArea.Width <= 0 || workingArea.Height <= 0)
        {
            return Enumerable.Repeat(workingArea, count).ToArray();
        }

        minimumWidth = Math.Max(1, minimumWidth);
        minimumHeight = Math.Max(1, minimumHeight);
        var rows = ChooseRows(workingArea, count, minimumWidth, minimumHeight);
        var rowCounts = Distribute(count, rows);
        var verticalGap = FitGap(gap, workingArea.Height, rows, minimumHeight);
        var rowHeights = Distribute(Math.Max(rows, workingArea.Height - verticalGap * (rows + 1)), rows);
        var rowOffsets = Offsets(workingArea.Top, rowHeights, verticalGap);
        var rectangles = new List<Rectangle>(count);
        for (var row = 0; row < rows; row++)
        {
            // Balance an incomplete row and give it the whole available width.
            // E.g. five clients use 3 + 2 cells, never 3 + 2 + a blank slot.
            var columns = rowCounts[row];
            var horizontalGap = FitGap(gap, workingArea.Width, columns, minimumWidth);
            var widths = Distribute(Math.Max(columns, workingArea.Width - horizontalGap * (columns + 1)), columns);
            var offsets = Offsets(workingArea.Left, widths, horizontalGap);
            for (var column = 0; column < columns; column++)
                rectangles.Add(Rectangle.Intersect(new Rectangle(offsets[column], rowOffsets[row], widths[column], rowHeights[row]), workingArea));
        }

        return rectangles;
    }

    /// <summary>
    /// The rectangle to request so that a window of <paramref name="measured"/>
    /// size stays completely inside <paramref name="area"/>, staying as close to
    /// <paramref name="desired"/> as containment allows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A Roblox client can refuse to shrink into its cell: the window manager asks
    /// the window first, and a WM_GETMINMAXINFO handler is free to answer with a
    /// larger minimum.  The old code knew this and chose to accept it, verifying
    /// only the origin, which is exactly how a window ends up hanging over the edge
    /// of the screen while being reported as organised.
    /// </para>
    /// <para>
    /// No minimum size is assumed here.  The size is whatever the window actually
    /// became, read back from the live window, so this holds for any client and any
    /// future change to its minimum.  When the window genuinely cannot fit, the
    /// size is capped at the area and the origin is pinned to the area's corner:
    /// the caller can then report honestly instead of silently overflowing.
    /// </para>
    /// </remarks>
    public static Rectangle Contain(Rectangle desired, Size measured, Rectangle area)
    {
        if (area.Width <= 0 || area.Height <= 0)
        {
            return desired;
        }

        var width = Math.Clamp(measured.Width, 1, area.Width);
        var height = Math.Clamp(measured.Height, 1, area.Height);

        // Pull the origin back so the far edge lands inside, then make sure pulling
        // back did not push the near edge out on a small area.
        var x = Math.Max(area.Left, Math.Min(desired.X, area.Right - width));
        var y = Math.Max(area.Top, Math.Min(desired.Y, area.Bottom - height));
        return new Rectangle(x, y, width, height);
    }

    /// <summary>
    /// The largest gap, up to the requested one, that still leaves every cell at
    /// or above the minimum size.  Returns zero when even that does not fit.
    /// </summary>
    private static int FitGap(int gap, int extent, int parts, int minimumCell)
    {
        var affordable = (extent - parts * minimumCell) / (parts + 1);
        return Math.Clamp(affordable, 0, gap);
    }

    // Prefer useful landscape viewports rather than square grid cells. Evaluate
    // balanced rows, with a strong penalty for dropping below usable dimensions.
    private static int ChooseRows(Rectangle area, int count, int minimumWidth, int minimumHeight)
    {
        var bestRows = 1;
        var bestScore = double.PositiveInfinity;
        for (var rows = 1; rows <= count; rows++)
        {
            var height = (double)area.Height / rows;
            var score = 0.0;
            var counts = Distribute(count, rows);
            foreach (var columns in counts)
            {
                var width = (double)area.Width / columns;
                var aspectCost = Math.Pow(Math.Log((width / height) / (16.0 / 9.0)), 2);
                var sizeCost = 4 * (Math.Pow(Math.Max(0, minimumWidth / width - 1), 2)
                    + Math.Pow(Math.Max(0, minimumHeight / height - 1), 2));
                score += columns * (aspectCost + sizeCost);
            }
            score /= count;
            if (score < bestScore)
            {
                bestScore = score;
                bestRows = rows;
            }
        }
        return bestRows;
    }

    /// <summary>
    /// Splits <paramref name="total"/> into <paramref name="parts"/> sizes that
    /// differ by at most one pixel, largest first.
    /// </summary>
    private static int[] Distribute(int total, int parts)
    {
        var sizes = new int[parts];
        var basis = total / parts;
        var remainder = total % parts;
        for (var index = 0; index < parts; index++)
        {
            sizes[index] = Math.Max(1, basis + (index < remainder ? 1 : 0));
        }

        return sizes;
    }

    private static int[] Offsets(int start, int[] sizes, int gap)
    {
        var offsets = new int[sizes.Length];
        var cursor = start + gap;
        for (var index = 0; index < sizes.Length; index++)
        {
            offsets[index] = cursor;
            cursor += sizes[index] + gap;
        }

        return offsets;
    }
}

/// <summary>
/// Outcome of one arrange pass, measured from the live windows.
/// </summary>
/// <param name="Moved">Windows that accepted a placement request.</param>
/// <param name="Contained">
/// Windows whose real rectangle was confirmed to be completely inside the
/// monitor's work area.
/// </param>
/// <param name="Overflowing">
/// Windows still crossing an edge after the containment pass, which only happens
/// when the client refuses to fit in the monitor at all.
/// </param>
/// <remarks>
/// <see cref="Moved"/> can exceed <see cref="Contained"/> plus
/// <see cref="Overflowing"/>: a window whose geometry cannot be read is counted as
/// moved but is not claimed either way.
/// </remarks>
public readonly record struct ArrangeReport(int Moved, int Contained, int Overflowing, int Overlapping = 0)
{
    public static ArrangeReport Empty => default;

    public static ArrangeReport operator +(ArrangeReport left, ArrangeReport right) =>
        new(
            left.Moved + right.Moved,
            left.Contained + right.Contained,
            left.Overflowing + right.Overflowing,
            left.Overlapping + right.Overlapping);
}

public sealed class WindowLayoutService
{
    private const int SwRestore = 9;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;

    /// <summary>
    /// Queues the request on the target thread instead of waiting for it.
    /// </summary>
    /// <remarks>
    /// These are cross-process windows, so a synchronous SetWindowPos against a
    /// hung Roblox client blocks the caller. Arrange runs on the UI thread, so
    /// that froze the whole manager.
    /// </remarks>
    private const uint SwpAsyncWindowPos = 0x4000;
    private const uint WmClose = 0x0010;

    public IReadOnlyList<MonitorDescriptor> GetMonitors()
    {
        var monitors = new List<MonitorDescriptor>();
        NativeWindows.MonitorEnumProc callback = (
            nint handle,
            nint hdc,
            ref NativeWindows.NativeRect monitorRect,
            nint data) =>
        {
            var info = new NativeWindows.MonitorInfoEx
            {
                Size = Marshal.SizeOf<NativeWindows.MonitorInfoEx>(),
                Device = string.Empty
            };
            if (NativeWindows.GetMonitorInfoW(handle, ref info))
            {
                monitors.Add(new MonitorDescriptor(
                    handle,
                    string.IsNullOrWhiteSpace(info.Device)
                        ? $"Monitor {monitors.Count + 1}"
                        : info.Device,
                    info.Work.ToRectangle(),
                    (info.Flags & 1) != 0));
            }

            return true;
        };
        NativeWindows.EnumDisplayMonitors(
            nint.Zero,
            nint.Zero,
            callback,
            nint.Zero);

        return monitors
            .OrderByDescending(monitor => monitor.IsPrimary)
            .ThenBy(monitor => monitor.WorkingArea.Left)
            .ThenBy(monitor => monitor.WorkingArea.Top)
            .ToArray();
    }

    public int Arrange(
        IReadOnlyList<RobloxInstanceInfo> instances,
        int selectedMonitor = -1,
        int gap = 8) =>
        ArrangeWithReport(instances, selectedMonitor, gap).Moved;

    /// <summary>
    /// Arranges every controllable window and reports what actually happened.
    /// </summary>
    /// <remarks>
    /// The report exists because "organised" and "on screen" are not the same
    /// claim.  A client that refuses to shrink below its own minimum is pinned to
    /// the screen edge by <see cref="GridLayoutCalculator.Contain"/>, and when even
    /// that is not enough the caller has to be able to say so instead of reporting
    /// a clean success.
    /// </remarks>
    public ArrangeReport ArrangeWithReport(
        IReadOnlyList<RobloxInstanceInfo> instances,
        int selectedMonitor = -1,
        int gap = 8)
    {
        var windows = instances
            .Where(instance =>
                instance.WindowHandle != nint.Zero
                && NativeWindows.IsWindow(instance.WindowHandle))
            .OrderBy(instance => instance.StartTimeUtc)
            .ThenBy(instance => instance.ProcessId)
            .ToArray();
        if (windows.Length == 0)
        {
            return ArrangeReport.Empty;
        }

        var monitors = GetMonitors();
        if (monitors.Count == 0)
        {
            return ArrangeReport.Empty;
        }

        if (selectedMonitor >= 0 && selectedMonitor < monitors.Count)
        {
            return ArrangeOnMonitor(windows, monitors[selectedMonitor], gap);
        }

        var report = ArrangeReport.Empty;
        var monitorCount = Math.Min(monitors.Count, windows.Length);
        var offset = 0;
        for (var monitorIndex = 0; monitorIndex < monitorCount; monitorIndex++)
        {
            var remainingWindows = windows.Length - offset;
            var remainingMonitors = monitorCount - monitorIndex;
            var take = (int)Math.Ceiling((double)remainingWindows / remainingMonitors);
            report += ArrangeOnMonitor(
                windows.Skip(offset).Take(take).ToArray(),
                monitors[monitorIndex],
                gap);
            offset += take;
        }

        return report;
    }

    private static ArrangeReport ArrangeOnMonitor(
        IReadOnlyList<RobloxInstanceInfo> windows,
        MonitorDescriptor monitor,
        int gap)
    {
        var area = monitor.WorkingArea;

        // Mutable on purpose: the containment pass rewrites the entries it had to
        // pull back inside, so the follow-up wait checks the corrected origin.
        var rectangles = GridLayoutCalculator.Calculate(
            area,
            windows.Count,
            gap).ToArray();

        // Restore every minimised or maximised window first, as its own pass.
        // ShowWindowAsync only POSTS the restore to the owning thread, so doing it
        // immediately before SetWindowPos meant the window was still minimised
        // when it was positioned, and the restore then re-applied the window's own
        // saved rectangle afterwards. That is the "windows end up in random
        // places" report.
        var needsSettle = false;
        foreach (var instance in windows)
        {
            var window = instance.WindowHandle;
            if (!NativeWindows.IsWindow(window))
            {
                continue;
            }

            if (NativeWindows.IsIconic(window) || NativeWindows.IsZoomed(window))
            {
                _ = NativeWindows.ShowWindowAsync(window, SwRestore);
                needsSettle = true;
            }
        }

        if (needsSettle)
        {
            WaitForRestore(windows);
        }

        // Pass one asks for the grid cell. Nothing is verified yet: SetWindowPos is
        // posted to the owning thread, so reading the rectangle back right away
        // reports the old one.
        var handles = new nint[windows.Count];
        for (var index = 0; index < windows.Count; index++)
        {
            var window = windows[index].WindowHandle;
            if (!NativeWindows.IsWindow(window))
            {
                continue;
            }

            if (RequestPlacement(window, rectangles[index]))
            {
                handles[index] = window;
            }
        }

        // One bounded wait for the whole batch. The app invokes arrangement on
        // a worker thread, so target-window message pumping cannot freeze WPF.
        WaitForPlacement(handles, rectangles);

        // A measured refusal is more useful than a guessed Roblox minimum. Give
        // up spacing or choose a different row count before accepting overlap.
        var minimumWidth = GridLayoutCalculator.MinimumCellWidth;
        var minimumHeight = GridLayoutCalculator.MinimumCellHeight;
        for (var index = 0; index < handles.Length; index++)
        {
            if (handles[index] == nint.Zero || !NativeWindows.GetVisibleRect(handles[index], out var measured)) continue;
            if (measured.Right - measured.Left > rectangles[index].Width + 2)
                minimumWidth = Math.Max(minimumWidth, measured.Right - measured.Left);
            if (measured.Bottom - measured.Top > rectangles[index].Height + 2)
                minimumHeight = Math.Max(minimumHeight, measured.Bottom - measured.Top);
        }
        if (minimumWidth > GridLayoutCalculator.MinimumCellWidth || minimumHeight > GridLayoutCalculator.MinimumCellHeight)
        {
            rectangles = GridLayoutCalculator.Calculate(area, windows.Count, gap, minimumWidth, minimumHeight).ToArray();
            for (var index = 0; index < handles.Length; index++)
                if (handles[index] != nint.Zero) RequestPlacement(handles[index], rectangles[index]);
            WaitForPlacement(handles, rectangles);
        }

        // Pass two measures what the windows really became and pulls anything that
        // hangs over an edge back inside the monitor.
        var corrected = false;
        for (var index = 0; index < handles.Length; index++)
        {
            var window = handles[index];
            if (window == nint.Zero || !NativeWindows.IsWindow(window))
            {
                continue;
            }

            if (!NativeWindows.GetVisibleRect(window, out var actual))
            {
                continue;
            }

            var current = actual.ToRectangle();
            if (area.Contains(current))
            {
                continue;
            }

            var containedRectangle = GridLayoutCalculator.Contain(
                rectangles[index],
                current.Size,
                area);
            if (containedRectangle == current)
            {
                continue;
            }

            rectangles[index] = containedRectangle;
            if (RequestPlacement(window, containedRectangle))
            {
                corrected = true;
            }
        }

        if (corrected)
        {
            WaitForPlacement(handles, rectangles);
        }

        // Only now is the outcome known, so only now is it counted.
        var moved = 0;
        var contained = 0;
        var overflowing = 0;
        for (var index = 0; index < handles.Length; index++)
        {
            var window = handles[index];
            if (window == nint.Zero || !NativeWindows.IsWindow(window))
            {
                continue;
            }

            moved++;
            if (!NativeWindows.GetVisibleRect(window, out var actual))
            {
                // Unverifiable: the placement calls succeeded, but this window's
                // geometry cannot be read, so it is not claimed as contained.
                continue;
            }

            if (area.Contains(actual.ToRectangle()))
            {
                contained++;
            }
            else
            {
                overflowing++;
            }
        }

        var actualCells = handles.Where(h => h != nint.Zero)
            .Select(h => NativeWindows.GetVisibleRect(h, out var bounds) ? bounds.ToRectangle() : Rectangle.Empty)
            .Where(bounds => !bounds.IsEmpty).ToArray();
        var overlapping = actualCells.Where((cell, index) =>
            actualCells.Where((_, otherIndex) => otherIndex != index).Any(cell.IntersectsWith)).Count();
        return new ArrangeReport(moved, contained, overflowing, overlapping);
    }

    /// <summary>
    /// Waits briefly for posted restores to be processed by their owning threads.
    /// </summary>
    private static void WaitForRestore(IReadOnlyList<RobloxInstanceInfo> windows)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var pending = windows.Any(instance =>
                NativeWindows.IsWindow(instance.WindowHandle)
                && (NativeWindows.IsIconic(instance.WindowHandle)
                    || NativeWindows.IsZoomed(instance.WindowHandle)));
            if (!pending)
            {
                return;
            }

            Thread.Sleep(25);
        }
    }

    /// <summary>
    /// Posts one placement request. Whether it took effect is decided later.
    /// </summary>
    /// <remarks>
    /// SWP_ASYNCWINDOWPOS is used because these are cross-process windows: without
    /// it a hung Roblox client blocks the caller, which is the UI thread, and
    /// freezes the manager. The flag is also why the result cannot be read back
    /// here: the request is queued on the owning thread, so the rectangle only
    /// becomes true after that thread pumps messages.
    /// </remarks>
    private static bool RequestPlacement(nint window, Rectangle rectangle)
    {
        if (NativeWindows.GetWindowRect(window, out var outer)
            && NativeWindows.GetVisibleRect(window, out var visible))
            rectangle = VisibleFrameLayout.ToOuter(rectangle, outer.ToRectangle(), visible.ToRectangle());
        return NativeWindows.SetWindowPos(
            window,
            nint.Zero,
            rectangle.X,
            rectangle.Y,
            rectangle.Width,
            rectangle.Height,
            SwpNoActivate | SwpNoZOrder | SwpNoOwnerZOrder | SwpAsyncWindowPos);
    }

    /// <summary>
    /// Waits, briefly and for the whole batch at once, until the posted placements
    /// have been applied by their owning threads.
    /// </summary>
    /// <remarks>
    /// Wait for matching geometry, or a stable size when a client enforces its
    /// own minimum. Checking only the origin could accept a pending resize.
    /// </remarks>
    private static void WaitForPlacement(nint[] windows, IReadOnlyList<Rectangle> targets)
    {
        var lastSizes = new Size[windows.Length];
        var stableSizes = new int[windows.Length];
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var pending = false;
            for (var index = 0; index < windows.Length; index++)
            {
                var window = windows[index];
                if (window == nint.Zero
                    || !NativeWindows.IsWindow(window)
                    || !NativeWindows.GetVisibleRect(window, out var actual))
                {
                    continue;
                }

                var size = new Size(actual.Right - actual.Left, actual.Bottom - actual.Top);
                stableSizes[index] = size == lastSizes[index] ? stableSizes[index] + 1 : 0;
                lastSizes[index] = size;
                var sizeAccepted = Math.Abs(size.Width - targets[index].Width) <= 2
                    && Math.Abs(size.Height - targets[index].Height) <= 2;
                pending |= Math.Abs(actual.Left - targets[index].X) > 2
                    || Math.Abs(actual.Top - targets[index].Y) > 2
                    || (!sizeAccepted && stableSizes[index] < 2);
            }

            if (!pending)
            {
                return;
            }

            Thread.Sleep(20);
        }
    }

    public bool Focus(RobloxInstanceInfo instance)
    {
        if (!NativeWindows.IsWindow(instance.WindowHandle))
        {
            return false;
        }

        _ = NativeWindows.ShowWindowAsync(instance.WindowHandle, SwRestore);
        return NativeWindows.SetForegroundWindow(instance.WindowHandle);
    }

    public bool RequestClose(RobloxInstanceInfo instance) =>
        NativeWindows.IsWindow(instance.WindowHandle)
        && NativeWindows.GetWindowThreadProcessId(
               instance.WindowHandle,
               out var ownerProcessId) != 0
        && ownerProcessId == (uint)instance.ProcessId
        && NativeWindows.PostMessageW(
               instance.WindowHandle,
               WmClose,
               nint.Zero,
               nint.Zero);

    public ProcessTerminationOutcome ForceTerminate(
        RobloxInstanceInfo instance,
        int waitMilliseconds = 2500)
    {
        if (instance.ProcessId <= 0 || instance.StartTimeUtc == default)
        {
            return ProcessTerminationOutcome.Rejected(
                "invalid-identity",
                RobloxMultiManager.Core.L.T("A identidade da instância selecionada é inválida."));
        }

        try
        {
            using var process = Process.GetProcessById(instance.ProcessId);
            if (process.HasExited)
            {
                return ProcessTerminationOutcome.Rejected(
                    "already-exited",
                    RobloxMultiManager.Core.L.T("O processo já foi encerrado."));
            }

            var actualStartTime = process.StartTime.ToUniversalTime();
            if (actualStartTime != instance.StartTimeUtc)
            {
                return ProcessTerminationOutcome.Rejected(
                    "identity-mismatch",
                    RobloxMultiManager.Core.L.T("O PID foi reutilizado por outro processo; nada foi encerrado."));
            }

            if (!process.ProcessName.Equals(
                    "RobloxPlayerBeta",
                    StringComparison.OrdinalIgnoreCase))
            {
                return ProcessTerminationOutcome.Rejected(
                    "process-mismatch",
                    RobloxMultiManager.Core.L.T("O PID selecionado não é um cliente Roblox."));
            }

            if (instance.ExecutablePath is not null)
            {
                string? actualPath = null;
                try
                {
                    actualPath = process.MainModule?.FileName;
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // The PID and creation time are still checked below. The
                    // monitor may have captured this process at another
                    // integrity level where MainModule is unavailable.
                }

                if (actualPath is not null
                    && !Path.GetFullPath(actualPath).Equals(
                        Path.GetFullPath(instance.ExecutablePath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return ProcessTerminationOutcome.Rejected(
                        "path-mismatch",
                        RobloxMultiManager.Core.L.T("O executável do PID mudou; nada foi encerrado."));
                }
            }

            process.Kill(entireProcessTree: false);
            var confirmed = process.WaitForExit(Math.Clamp(
                waitMilliseconds,
                250,
                10_000));
            return confirmed
                ? ProcessTerminationOutcome.Success(
                    "terminated",
                    RobloxMultiManager.Core.L.T("O processo selecionado foi finalizado."))
                : ProcessTerminationOutcome.Requested(
                    "termination-requested",
                    RobloxMultiManager.Core.L.T("O término foi solicitado, mas o processo ainda não confirmou a saída."));
        }
        catch (ArgumentException)
        {
            return ProcessTerminationOutcome.Rejected(
                "not-found",
                RobloxMultiManager.Core.L.T("O processo selecionado não foi encontrado."));
        }
        catch (InvalidOperationException)
        {
            return ProcessTerminationOutcome.Rejected(
                "already-exited",
                RobloxMultiManager.Core.L.T("O processo já foi encerrado."));
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            return ProcessTerminationOutcome.Rejected(
                exception.NativeErrorCode == 5 ? "access-denied" : "termination-failed",
                exception.NativeErrorCode == 5
                    ? RobloxMultiManager.Core.L.T("O Windows negou acesso para finalizar este processo.")
                    : RobloxMultiManager.Core.L.T("O Windows não conseguiu finalizar este processo."));
        }
    }
}

public sealed record ProcessTerminationOutcome(
    bool Succeeded,
    bool TerminationRequested,
    string Code,
    string Message)
{
    public static ProcessTerminationOutcome Success(string code, string message) =>
        new(true, true, code, message);

    public static ProcessTerminationOutcome Requested(string code, string message) =>
        new(false, true, code, message);

    public static ProcessTerminationOutcome Rejected(string code, string message) =>
        new(false, false, code, message);
}
