using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Infrastructure;

public sealed class WindowsToastSender(IProviderHealthStore health) : INotificationSender
{
    public async Task<ToastResult> SendAsync(NotificationRecord notification, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return new(NotificationStatus.Failed, "Windows Toast 仅支持 Windows 桌面会话");
        var helperPath = Path.Combine(AppContext.BaseDirectory, "Notifications", "Show-Toast.ps1");
        if (!File.Exists(helperPath)) return new(NotificationStatus.Failed, "Toast helper 文件缺失");
        var script = await File.ReadAllTextAsync(helperPath, ct);
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) start.ArgumentList.Add(argument);
        start.Environment["PULLET_TOAST_PAYLOAD"] = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new
        {
            notification.Title, notification.Body, Tag = notification.Id.ToString("N")[..16],
            ExpiresAt = notification.ExpiresAt.ToUniversalTime().ToString("O"),
            Executable = Path.Combine(AppContext.BaseDirectory, "PulletCryptoRadar.exe")
        }));
        using var process = new Process { StartInfo = start };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(ct); var stderr = process.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true); health.Failure("Windows Toast", "通知进程超时，是否接收未知");
            return new(NotificationStatus.Indeterminate, "通知进程超时，不自动重发");
        }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        var output = await stdout; var error = await stderr;
        ToastResult result;
        try
        {
            using var document = JsonDocument.Parse(output.Trim());
            var root = document.RootElement;
            var status = root.GetProperty("Status").GetString() switch
            { "Submitted" => NotificationStatus.Submitted, "Disabled" => NotificationStatus.Disabled, _ => NotificationStatus.Failed };
            result = new(status, root.TryGetProperty("Error", out var detail) ? detail.GetString() : null);
        }
        catch (JsonException) { result = new(NotificationStatus.Indeterminate, $"无法确认 Toast 返回结果：{error[..Math.Min(300, error.Length)]}"); }
        if (result.Status == NotificationStatus.Submitted) health.Success("Windows Toast", freshnessSeconds: 86400);
        else health.Failure("Windows Toast", result.Error ?? result.Status.ToString());
        return result;
    }
}
