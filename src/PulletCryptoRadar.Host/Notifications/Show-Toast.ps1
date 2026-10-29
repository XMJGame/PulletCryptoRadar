$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
try {
    $payload = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($env:PULLET_TOAST_PAYLOAD)) | ConvertFrom-Json
    $appId = 'Pullet.PulletCryptoRadar'
    $shortcutPath = Join-Path ([Environment]::GetFolderPath('Programs')) 'PulletCryptoRadar.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($shortcutPath)
    $link.TargetPath = $payload.Executable
    $link.WorkingDirectory = Split-Path -Parent $payload.Executable
    $link.Description = 'PulletCryptoRadar local market research'
    $link.Save()
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($link) | Out-Null
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class PulletToastIdentity {
    [StructLayout(LayoutKind.Sequential, Pack=4)] public struct PropertyKey { public Guid Format; public uint Id; }
    [StructLayout(LayoutKind.Explicit, Size=24)] public struct PropVariant {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Value;
    }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPropertyStore {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }
    [DllImport("shell32.dll", CharSet=CharSet.Unicode, PreserveSig=false)]
    static extern void SHGetPropertyStoreFromParsingName(string path, IntPtr context, uint flags, ref Guid iid, out IPropertyStore store);
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant value);
    public static void Set(string path, string appId) {
        var iid = typeof(IPropertyStore).GUID;
        IPropertyStore store;
        SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 2, ref iid, out store);
        var key = new PropertyKey { Format = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = 5 };
        var value = new PropVariant { Type = 31, Value = Marshal.StringToCoTaskMemUni(appId) };
        try { store.SetValue(ref key, ref value); store.Commit(); }
        finally { PropVariantClear(ref value); Marshal.FinalReleaseComObject(store); }
    }
}
'@
    [PulletToastIdentity]::Set($shortcutPath, $appId)
    $identityKey = 'HKCU:\Software\Classes\AppUserModelId\' + $appId
    New-Item -Path $identityKey -Force | Out-Null
    New-ItemProperty -Path $identityKey -Name DisplayName -Value 'PulletCryptoRadar' -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $identityKey -Name IconUri -Value $payload.Executable -PropertyType String -Force | Out-Null
    [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
    [Windows.UI.Notifications.ToastNotification, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
    [Windows.UI.Notifications.NotificationSetting, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
    [Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] | Out-Null
    $notifier = [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($appId)
    # Some unpackaged desktop identities cannot query Setting until their first submission.
    $setting = $null
    try { $setting = $notifier.get_Setting() } catch {
        if ($_.Exception.ToString() -notmatch '0x80070490') { throw }
    }
    if ($null -ne $setting -and $setting.ToString() -ne 'Enabled') {
        @{ Status = 'Disabled'; Error = 'Windows notification setting: ' + $setting.ToString() } | ConvertTo-Json -Compress
        exit 0
    }
    $document = [Windows.Data.Xml.Dom.XmlDocument]::new()
    $document.LoadXml('<toast activationType="protocol" launch="http://localhost:5218/alerts"><visual><binding template="ToastGeneric"><text></text><text></text></binding></visual><audio silent="true" /></toast>')
    $nodes = $document.GetElementsByTagName('text')
    $nodes.Item(0).InnerText = [string]$payload.Title
    $nodes.Item(1).InnerText = [string]$payload.Body
    $toast = [Windows.UI.Notifications.ToastNotification]::new($document)
    $toast.Tag = $payload.Tag
    $toast.Group = 'PulletRadar'
    $toast.ExpirationTime = [DateTimeOffset]::Parse($payload.ExpiresAt)
    $notifier.Show($toast)
    @{ Status = 'Submitted' } | ConvertTo-Json -Compress
} catch {
    @{ Status = 'Failed'; Error = $_.Exception.Message + ' (line ' + $_.InvocationInfo.ScriptLineNumber + ')' } | ConvertTo-Json -Compress
    exit 1
}
