// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentFlyout.Classes.Settings;

namespace FluentFlyoutWPF.Classes.Lyrics;

/// <summary>
/// Imports the YouTube Music login session (auth cookies) from the Pear/th-ch
/// desktop app's profile, so the lyrics provider can fetch account-gated lyrics.
/// Supports both unencrypted Electron cookie values and Chromium DPAPI / AES-GCM cookies.
/// </summary>
public static class YtmSession
{
    private static readonly string AppProfileDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YouTube Music");

    private static readonly string CookieDbPath =
        Path.Combine(AppProfileDir, "Network", "Cookies");

    private static readonly string LocalStatePath =
        Path.Combine(AppProfileDir, "Local State");

    private static readonly string[] AuthCookieNames =
    [
        "SID", "HSID", "SSID", "SAPISID", "APISID",
        "__Secure-1PSID", "__Secure-3PSID", "__Secure-1PAPISID", "__Secure-3PAPISID",
        "LOGIN_INFO", "VISITOR_INFO1_LIVE", "CONSENT",
    ];

    /// <summary>Cookie header of the imported session, or empty when none stored.</summary>
    public static string StoredCookieHeader =>
        SettingsManager.Current.LyricsYtmSession;

    /// <summary>
    /// Reads auth cookies from the desktop app's profile and returns a cookie header.
    /// Throws when the app is running (the DB is exclusively locked) or nothing was found.
    /// </summary>
    public static string ImportFromApp()
    {
        if (!File.Exists(CookieDbPath))
            throw new FileNotFoundException("YouTube Music profile not found (is the app installed?)");

        // the running app holds the DB exclusively — copy with shared read as a fallback
        string tmp = Path.Combine(Path.GetTempPath(), $"ff_ytm_cookies_{Guid.NewGuid():N}.db");
        string tmpWal = tmp + "-wal";
        string tmpShm = tmp + "-shm";
        try
        {
            CopyShared(CookieDbPath, tmp);
            if (File.Exists(CookieDbPath + "-wal"))
                CopyShared(CookieDbPath + "-wal", tmpWal);
            if (File.Exists(CookieDbPath + "-shm"))
                CopyShared(CookieDbPath + "-shm", tmpShm);

            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={tmp};Pooling=False");
            conn.Open();

            byte[]? masterKey = null;
            bool masterKeyLoaded = false;

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT name, value, encrypted_value, host_key FROM cookies WHERE host_key LIKE '%youtube.com'";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string name = reader.GetString(0);
                    if (!AuthCookieNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                        continue;

                    string plain = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    if (plain.Length == 0 && !reader.IsDBNull(2))
                    {
                        byte[] encrypted = reader.GetFieldValue<byte[]>(2);
                        string hostKey = reader.IsDBNull(3) ? "" : reader.GetString(3);
                        if (!masterKeyLoaded)
                        {
                            masterKey = TryLoadMasterKey();
                            masterKeyLoaded = true;
                        }
                        plain = TryDecryptCookie(encrypted, hostKey, masterKey) ?? "";
                    }

                    if (plain.Length > 0)
                        values[name] = plain;
                }
            }

            if (!values.ContainsKey("SAPISID") && !values.ContainsKey("__Secure-3PAPISID"))
                throw new InvalidOperationException("No logged-in session found in the YouTube Music app cookies.");

            string header = string.Join("; ", values.Select(kv => $"{kv.Key}={kv.Value}"));
            SettingsManager.Current.LyricsYtmSession = header;
            SettingsManager.SaveSettings();
            return header;
        }
        finally
        {
            TryDeleteTempFiles(tmp, tmpWal, tmpShm);
        }
    }

    private static void CopyShared(string sourcePath, string destPath)
    {
        using var src = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write);
        src.CopyTo(dst);
    }

    private static void TryDeleteTempFiles(params string[] paths)
    {
        foreach (string path in paths)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // best effort cleanup
            }
        }

        // Clean up any stale temp DB files from earlier runs
        try
        {
            foreach (string stale in Directory.EnumerateFiles(Path.GetTempPath(), "ff_ytm_cookies*.db*"))
            {
                try { File.Delete(stale); } catch { }
            }
        }
        catch
        {
            // ignore enumeration errors
        }
    }

    private static byte[]? TryLoadMasterKey()
    {
        try
        {
            if (!File.Exists(LocalStatePath))
                return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(LocalStatePath));
            if (!doc.RootElement.TryGetProperty("os_crypt", out var osCrypt) ||
                !osCrypt.TryGetProperty("encrypted_key", out var keyEl) ||
                keyEl.ValueKind != JsonValueKind.String)
                return null;

            string? b64 = keyEl.GetString();
            if (string.IsNullOrEmpty(b64))
                return null;

            byte[] raw = Convert.FromBase64String(b64);
            // Chromium prefixes the DPAPI-protected master key with ASCII "DPAPI"
            if (raw.Length <= 5 || !raw.AsSpan(0, 5).SequenceEqual("DPAPI"u8))
                return null;

            return ProtectedData.Unprotect(raw[5..], null, DataProtectionScope.CurrentUser);
        }
        catch
        {
            return null;
        }
    }

    private static string? TryDecryptCookie(byte[] encrypted, string hostKey, byte[]? masterKey)
    {
        if (encrypted.Length == 0)
            return null;

        try
        {
            // Chromium v10/v11 AES-256-GCM: "v10"/"v11" (3) + nonce (12) + ciphertext + tag (16)
            if (encrypted.Length >= 31 &&
                encrypted[0] == (byte)'v' && encrypted[1] == (byte)'1' &&
                (encrypted[2] == (byte)'0' || encrypted[2] == (byte)'1'))
            {
                if (masterKey == null)
                    return null;

                var nonce = encrypted.AsSpan(3, 12);
                var ciphertext = encrypted.AsSpan(15, encrypted.Length - 15 - 16);
                var tag = encrypted.AsSpan(encrypted.Length - 16, 16);
                byte[] plaintext = new byte[ciphertext.Length];

                using var aes = new AesGcm(masterKey, 16);
                aes.Decrypt(nonce, ciphertext, tag, plaintext);

                // Chromium DB v24+ prepends SHA-256(host_key) (32 bytes) to decrypted cookie value
                ReadOnlySpan<byte> valueBytes = plaintext;
                if (plaintext.Length >= 32 && hostKey.Length > 0)
                {
                    Span<byte> hostHash = stackalloc byte[32];
                    SHA256.HashData(Encoding.UTF8.GetBytes(hostKey), hostHash);
                    if (valueBytes[..32].SequenceEqual(hostHash))
                        valueBytes = valueBytes[32..];
                }
                return Encoding.UTF8.GetString(valueBytes);
            }

            // Legacy raw DPAPI blob (not v20 app-bound)
            if (encrypted.Length >= 3 && encrypted[0] == (byte)'v' && encrypted[1] == (byte)'2' && encrypted[2] == (byte)'0')
                return null;

            byte[] unprotected = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(unprotected);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Builds the SAPISIDHASH authorization header for Innertube calls.</summary>
    public static (string Cookie, string Authorization)? GetAuth()
    {
        string header = StoredCookieHeader;
        if (header.Length == 0)
            return null;

        string? sapisid = header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split('=', 2))
            .FirstOrDefault(p => p.Length == 2 &&
                (p[0].Equals("SAPISID", StringComparison.OrdinalIgnoreCase) ||
                 p[0].Equals("__Secure-3PAPISID", StringComparison.OrdinalIgnoreCase)))?[1].Trim();

        if (string.IsNullOrEmpty(sapisid))
            return (header, "");

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string origin = "https://music.youtube.com";
        string hash = Convert.ToHexString(
            SHA1.HashData(
                Encoding.UTF8.GetBytes($"{sapisid} {now} {origin}"))).ToLowerInvariant();
        return (header, $"SAPISIDHASH {now}_{hash}");
    }
}
