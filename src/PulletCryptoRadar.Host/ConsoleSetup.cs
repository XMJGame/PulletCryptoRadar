using System.Runtime.InteropServices;

namespace PulletCryptoRadar.Host;
internal static class ConsoleSetup
{
    // Only changes this process's input buffer. Legacy console text selection otherwise
    // suspends log writes and can delay shutdown even after the pipe acknowledges it.
    public static void Initialize()
    {
        if(!OperatingSystem.IsWindows()||Console.IsInputRedirected)return;
        var input=GetStdHandle(-10);
        if(GetConsoleMode(input,out var mode))_=SetConsoleMode(input,(mode|0x80u)&~0x40u);
    }
    [DllImport("kernel32.dll",SetLastError=true)]private static extern IntPtr GetStdHandle(int handle);
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetConsoleMode(IntPtr handle,out uint mode);
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]private static extern bool SetConsoleMode(IntPtr handle,uint mode);
}
