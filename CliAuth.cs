using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace ClaudeUsageWidget;

/// <summary>
/// Keeps the Claude Code CLI token fresh by letting the official CLI do the
/// refresh (<c>claude auth status</c>), so the widget never rotates tokens itself
/// and the CLI's own login stays valid.
/// </summary>
public static class CliAuth
{
    /// <summary>Refresh when the token expires within this window.</summary>
    public static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(15);

    public static DateTimeOffset? ReadExpiry()
    {
        try
        {
            var path = Path.Combine(UsageService.CredentialsDir, UsageService.CredentialsFileName);
            if (!File.Exists(path)) return null;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth)) return null;
            if (!oauth.TryGetProperty("refreshToken", out var rt) || rt.GetString() is not { Length: > 0 }) return null;
            if (!oauth.TryGetProperty("expiresAt", out var exp) || exp.ValueKind != JsonValueKind.Number) return null;
            return DateTimeOffset.FromUnixTimeMilliseconds(exp.GetInt64());
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static bool NeedsRefresh()
        => ReadExpiry() is { } exp && exp - DateTimeOffset.UtcNow < RefreshMargin;

    /// <summary>Runs <c>claude auth status</c> hidden; the CLI refreshes an expiring token as a side effect.</summary>
    public static async Task RunStatusAsync()
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo("cmd.exe", "/c claude auth status")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (proc is null) return;

            var drainOut = proc.StandardOutput.ReadToEndAsync();
            var drainErr = proc.StandardError.ReadToEndAsync();
            var exited = proc.WaitForExitAsync();
            if (await Task.WhenAny(exited, Task.Delay(TimeSpan.FromSeconds(45))) != exited)
                proc.Kill(entireProcessTree: true);
            await Task.WhenAll(drainOut, drainErr);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // CLI missing or failed to start: the fetch will report the expired token.
        }
    }

    private static bool _loginRunning;

    /// <summary>
    /// Runs <c>claude auth login</c> without a console window. The CLI opens the
    /// browser itself and receives the result on a localhost callback, so no
    /// terminal input is needed. Shows the CLI output only if login fails.
    /// </summary>
    public static async Task LoginInBackgroundAsync()
    {
        if (_loginRunning) return;
        _loginRunning = true;
        try
        {
            using var proc = Process.Start(new ProcessStartInfo("cmd.exe", "/c claude auth login")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (proc is null) return;
            proc.StandardInput.Close();

            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();
            var exited = proc.WaitForExitAsync();
            bool timedOut = await Task.WhenAny(exited, Task.Delay(TimeSpan.FromMinutes(5))) != exited;
            if (timedOut)
                proc.Kill(entireProcessTree: true);
            var output = (await stdout + await stderr).Trim();

            if (timedOut || proc.ExitCode != 0)
            {
                var tail = output.Length > 600 ? output[^600..] : output;
                System.Windows.MessageBox.Show(
                    (timedOut ? "5분 안에 로그인이 끝나지 않았습니다." : "로그인에 실패했습니다.") + "\n\n" + tail,
                    "Claude Usage Widget");
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            System.Windows.MessageBox.Show($"로그인을 시작하지 못했습니다: {ex.Message}", "Claude Usage Widget");
        }
        finally
        {
            _loginRunning = false;
        }
    }
}
