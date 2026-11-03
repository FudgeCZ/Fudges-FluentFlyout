// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using NLog;

namespace FluentFlyoutWPF.Classes.Services;

/// <summary>
/// Custom updater for the fork's self-contained zip deployment: downloads the
/// release asset, validates it, and stages a detached PowerShell helper that
/// swaps the application files after this process exits and restarts the app.
/// Any failure falls back to opening the releases page in the browser.
/// </summary>
public static class UpdateInstallerService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };

    private const string ReleasesPage = "https://github.com/FudgeCZ/Fudges-FluentFlyout/releases/latest";

    private static string UpdateDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FudgesFluentFlyout", "update");

    /// <summary>
    /// Downloads the release asset and validates that it contains the app exe.
    /// Returns the staged zip path, or null on failure.
    /// </summary>
    public static async Task<string?> DownloadUpdateAsync(string downloadUrl, string version, IProgress<double>? progress, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrEmpty(downloadUrl))
                return null;

            Directory.CreateDirectory(UpdateDir);
            foreach (var stale in Directory.EnumerateFiles(UpdateDir, "*.zip"))
            {
                try { File.Delete(stale); } catch { /* best effort */ }
            }

            string zipPath = Path.Combine(UpdateDir, $"FluentFlyout_{version}_x64.zip");

            using var response = await _http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? -1;

            {
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var target = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

                var buffer = new byte[81920];
                long read = 0;
                int n;
                while ((n = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, n), ct);
                    read += n;
                    if (progress != null && total > 0)
                        progress.Report(read * 100.0 / total);
                }
            } // target disposed (and flushed) before validation opens the file again

                using (var archive = ZipFile.OpenRead(zipPath))
                {
                    bool hasExe = archive.Entries.Any(e =>
                        e.FullName == "FluentFlyout.exe" ||
                        e.FullName.EndsWith("/FluentFlyout.exe", StringComparison.OrdinalIgnoreCase) ||
                        e.FullName.EndsWith("\\FluentFlyout.exe", StringComparison.OrdinalIgnoreCase));
                    if (!hasExe)
                    {
                        Logger.Error("Staged update package does not contain FluentFlyout.exe");
                        return null;
                    }
                }

                Logger.Info($"Update package staged: {zipPath}");
                return zipPath;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to download the update package");
                return null;
            }
        }

    /// <summary>
    /// Writes the detached helper script and launches it. The caller must exit the
    /// app afterwards — the helper waits for this process to end before swapping.
    /// </summary>
    public static bool LaunchStagedUpdate(string zipPath)
    {
        try
        {
            string? exePath = Environment.ProcessPath;
            string appDir = AppContext.BaseDirectory;
            if (string.IsNullOrEmpty(exePath) || !File.Exists(zipPath))
                return false;

            const string template = """
$ErrorActionPreference = 'Stop'
try {
    $appDir   = <APPDIR>
    $exePath  = <EXE>
    $zipPath  = <ZIP>
    $ownerPid = <PID>
    Wait-Process -Id $ownerPid -Timeout 30 -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 800
    $staging = Join-Path $env:TEMP ('FFUpdate_' + [guid]::NewGuid().ToString('N'))
    Expand-Archive -Path $zipPath -DestinationPath $staging -Force
    if (-not (Test-Path (Join-Path $staging 'FluentFlyout.exe'))) { throw 'invalid package' }
    $backup = Join-Path $appDir ('_old_' + (Get-Date -Format 'yyyyMMddHHmmss'))
    New-Item -ItemType Directory -Path $backup -Force | Out-Null
    Get-ChildItem $appDir -Force | Where-Object { $_.Name -notlike '_old_*' } | ForEach-Object {
        Move-Item -LiteralPath $_.FullName -Destination (Join-Path $backup $_.Name) -Force -ErrorAction SilentlyContinue
    }
    Copy-Item (Join-Path $staging '*') $appDir -Recurse -Force
    Start-Process -FilePath $exePath -WorkingDirectory $appDir
    Start-Sleep -Seconds 4
    Remove-Item $backup -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
} catch {
    Start-Process '<RELEASES>'
}
""";

            string script = template
                .Replace("<APPDIR>", SingleQuote(appDir))
                .Replace("<EXE>", SingleQuote(exePath))
                .Replace("<ZIP>", SingleQuote(zipPath))
                .Replace("<PID>", Environment.ProcessId.ToString())
                .Replace("<RELEASES>", ReleasesPage);

            string scriptPath = Path.Combine(Path.GetTempPath(), "FudgesFluentFlyout_update.ps1");
            File.WriteAllText(scriptPath, script);

            // detached + UseShellExecute so the helper survives this process exiting
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
                UseShellExecute = true,
                CreateNoWindow = true,
            });
            Logger.Info("Update helper launched, app will restart into the new version");
            return p != null;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to launch the update helper");
            return false;
        }
    }

    private static string SingleQuote(string s) => "'" + s.Replace("'", "''") + "'";

    /// <summary>Removes leftover backup folders from previous updates (startup call).</summary>
    public static void CleanupStaleBackups()
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(AppContext.BaseDirectory, "_old_*"))
            {
                try { Directory.Delete(dir, true); } catch { /* locked or partial — retried next start */ }
            }
        }
        catch { /* best effort */ }
    }
}
