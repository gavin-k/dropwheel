using Microsoft.Win32;
using Windows.ApplicationModel;

namespace DropWheel.App.Settings;

/// <summary>
/// 登录 Windows 时启动 for a DropWheel without a package (the MSI, a copied folder), which has no startup
/// task: a value under the user's Run key that starts this DropWheel.exe with <see cref="Switch"/>. The state
/// uses the startup task's words, so the settings window shows both the same way.
/// </summary>
internal static class SignInRun
{
    /// <summary>On the command line Windows runs at sign-in: stay in the tray.</summary>
    public const string Switch = "--at-sign-in";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string Name = "DropWheel";

    /// <summary>
    /// Enabled while the value is there, DisabledByUser once Task Manager's 启动应用 has turned it off (the
    /// first byte of its StartupApproved entry is odd then), otherwise Disabled.
    /// </summary>
    public static StartupTaskState State
    {
        get
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (run?.GetValue(Name) is not string)
            {
                return StartupTaskState.Disabled;
            }

            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            return approved?.GetValue(Name) is byte[] { Length: > 0 } flags && (flags[0] & 1) == 1 ? StartupTaskState.DisabledByUser : StartupTaskState.Enabled;
        }
    }

    public static void Set(bool on)
    {
        using var run = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on)
        {
            run.SetValue(Name, $"\"{Environment.ProcessPath}\" {Switch}");
        }
        else
        {
            run.DeleteValue(Name, throwOnMissingValue: false);
        }
    }
}
