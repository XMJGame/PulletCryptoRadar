using System.Runtime.InteropServices;

namespace PulletCryptoRadar.Host;

public static class ConsoleInputPolicy
{
    public static string Status { get; private set; } = "NotInitialized";
    public static void Initialize()
    {
        if (!OperatingSystem.IsWindows()) { Status = "NotWindows"; return; }
        var input = GetStdHandle(-10);
        if (!GetConsoleMode(input,out var mode)) { Status = "NoConsoleInput"; return; }
        // This process's input buffer only; do not change the user's registry or other consoles.
        const uint extendedFlags = 0x0080,quickEdit = 0x0040;
        Status = SetConsoleMode(input,(mode|extendedFlags)&~quickEdit) &&
            GetConsoleMode(input,out var actual) && (actual&quickEdit) == 0 ? "QuickEditDisabled" : "QuickEditDisableFailed";
    }
    [DllImport("kernel32.dll",SetLastError = true)]
    private static extern IntPtr GetStdHandle(int handle);
    [DllImport("kernel32.dll",SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(IntPtr handle,out uint mode);
    [DllImport("kernel32.dll",SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleMode(IntPtr handle,uint mode);
}
