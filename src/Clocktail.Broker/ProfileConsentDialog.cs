using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Clocktail.Domain.Broker;

namespace Clocktail.Broker;

/// <summary>
/// High-integrity Windows confirmation prompt owned by the elevated broker for client-initiated profile mutations.
/// Fails closed whenever no interactive input desktop is available, on non-Windows platforms, or when the dialog is
/// declined, dismissed, or times out.
/// </summary>
public static partial class ProfileConsentDialog
{
    public static readonly TimeSpan DialogTimeout = TimeSpan.FromSeconds(60);

    private const uint MbYesNo = 0x4;
    private const uint MbIconWarning = 0x30;
    private const uint MbDefButton2 = 0x100;
    private const uint MbSetForeground = 0x10000;
    private const uint MbTopMost = 0x40000;
    private const int IdYes = 6;
    private const int UoiFlags = 1;
    private const uint WsfVisible = 0x0001;
    private const uint DesktopReadObjects = 0x0001;
    private const string DialogCaption = "Clocktail Broker: Confirm GPU Profile Change";
    private const int MaxFieldLength = 80;
    private const int MaxSummaryLength = 200;
    private const int MaxValueLength = 120;

    public static bool Confirm(
        ProfileConsentRequest request,
        Func<string, string, int>? showMessageBox = null,
        bool? isInteractive = null,
        Func<bool>? hasInputDesktop = null,
        TimeSpan? timeout = null)
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (!(isInteractive ?? Environment.UserInteractive)) return false;
        try
        {
            if (!(hasInputDesktop ?? HasInteractiveInputDesktop)()) return false;
        }
        catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException)
        {
            return false;
        }

        TimeSpan effectiveTimeout = timeout is { } t && t > TimeSpan.Zero && t < DialogTimeout ? t : DialogTimeout;
        var text = FormatPrompt(request);
        try
        {
            if (showMessageBox != null)
            {
                var task = Task.Run(() => showMessageBox(text, DialogCaption));
                _ = task.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return task.Wait(effectiveTimeout) && task.Result == IdYes;
            }
            int timeoutMs = Math.Clamp((int)Math.Ceiling(effectiveTimeout.TotalMilliseconds), 1, int.MaxValue);
            return ShowNativeDialog(text, DialogCaption, timeoutMs) == IdYes;
        }
        catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException)
        {
            return false;
        }
    }

    internal static string FormatPrompt(ProfileConsentRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Operation: {SanitizeSingleLine(request.Kind, MaxFieldLength)}");
        sb.AppendLine($"GPU: {SanitizeSingleLine(request.GpuName, MaxFieldLength)} ({SanitizeSingleLine(request.GpuId, MaxFieldLength)})");
        sb.AppendLine();
        sb.AppendLine(SanitizeSingleLine(request.Summary, MaxSummaryLength));
        if (!string.IsNullOrWhiteSpace(request.Value))
            sb.AppendLine($"Client detail (untrusted): \"{SanitizeSingleLine(request.Value, MaxValueLength)}\"");
        if (request.Writes is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("Profile settings:");
            foreach (var w in request.Writes.Take(8))
                sb.AppendLine($"  - {SanitizeSingleLine(w.Kind, MaxFieldLength)}/{SanitizeSingleLine(w.Key, MaxFieldLength)} = {SanitizeSingleLine(w.Value, MaxFieldLength)}");
            if (request.Writes.Count > 8)
                sb.AppendLine($"  ... and {request.Writes.Count - 8} more");
        }
        sb.AppendLine();
        sb.Append("Allow this GPU profile change?");
        return sb.ToString();
    }

    internal static string SanitizeSingleLine(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var sb = new StringBuilder(Math.Min(value.Length, maxLength));
        bool lastWasSpace = true;
        foreach (char c in value)
        {
            if (char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format || char.IsWhiteSpace(c))
            {
                if (!lastWasSpace && sb.Length < maxLength) { sb.Append(' '); lastWasSpace = true; }
                continue;
            }
            if (sb.Length >= maxLength) break;
            sb.Append(c);
            lastWasSpace = false;
        }
        string cleaned = sb.ToString().Trim();
        if (cleaned.Length >= maxLength && value.Trim().Length > maxLength && maxLength > 3)
            return string.Concat(cleaned.AsSpan(0, maxLength - 3), "...");
        return cleaned;
    }

    [SupportedOSPlatform("windows")]
    internal static bool HasInteractiveInputDesktop()
    {
        try
        {
            IntPtr winsta = GetProcessWindowStation();
            if (winsta == IntPtr.Zero) return false;
            var flags = default(UserObjectFlags);
            if (!GetUserObjectInformation(winsta, UoiFlags, out flags, Marshal.SizeOf<UserObjectFlags>(), out _))
                return false;
            if ((flags.Flags & WsfVisible) == 0)
                return false;
            IntPtr desktop = OpenInputDesktop(0, false, DesktopReadObjects);
            if (desktop == IntPtr.Zero) return false;
            CloseDesktop(desktop);
            return true;
        }
        catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static int ShowNativeDialog(string text, string caption, int timeoutMs)
    {
        try
        {
            return MessageBoxTimeout(
                IntPtr.Zero,
                text,
                caption,
                MbYesNo | MbIconWarning | MbDefButton2 | MbSetForeground | MbTopMost,
                0,
                (uint)timeoutMs);
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException)
        {
            return 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UserObjectFlags
    {
        public int Inherit;
        public int Reserved;
        public uint Flags;
    }

    [DllImport("user32.dll", EntryPoint = "MessageBoxTimeoutW", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxTimeout(IntPtr hWnd, string text, string caption, uint type, ushort language, uint milliseconds);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetProcessWindowStation();

    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", SetLastError = true)]
    private static extern bool GetUserObjectInformation(IntPtr hObj, int nIndex, out UserObjectFlags pvInfo, int nLength, out uint lpnLengthNeeded);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr hDesktop);
}
