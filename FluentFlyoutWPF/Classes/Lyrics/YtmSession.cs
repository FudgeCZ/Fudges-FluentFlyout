// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO;
using FluentFlyout.Classes.Settings;

namespace FluentFlyoutWPF.Classes.Lyrics;

/// <summary>
/// Imports the YouTube Music login session (auth cookies) from the Pear/th-ch
/// desktop app's profile, so the lyrics provider can fetch account-gated lyrics.
/// The Electron build stores cookie values unencrypted in its Chromium cookie DB.
/// </summary>
public static class YtmSession
{
    private static readonly string CookieDbPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "YouTube Music", "Network", "Cookies");

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
        string tmp = Path.Combine(Path.GetTempPath(), "ff_ytm_cookies.db");
        using (var src = new FileStream(CookieDbPath, FileMode.Open, FileAccess.Read,
                   FileShare.ReadWrite | FileShare.Delete))
        using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            src.CopyTo(dst);

        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={tmp}");
        conn.Open();

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name, value, encrypted_value FROM cookies WHERE host_key LIKE '%youtube.com'";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string name = reader.GetString(0);
                if (!AuthCookieNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                    continue;

                string plain = reader.IsDBNull(1) ? "" : reader.GetString(1);
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

    /// <summary>Builds the SAPISIDHASH authorization header for Innertube calls.</summary>
    public static (string Cookie, string Authorization)? GetAuth()
    {
        string header = StoredCookieHeader;
        if (header.Length == 0)
            return null;

        string? sapisid = header.Split("; ")
            .Select(p => p.Split('=', 2))
            .FirstOrDefault(p => p[0] == "SAPISID" || p[0] == "__Secure-3PAPISID")?[1];

        if (string.IsNullOrEmpty(sapisid))
            return (header, "");

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string origin = "https://music.youtube.com";
        string hash = Convert.ToHexString(
            System.Security.Cryptography.SHA1.HashData(
                System.Text.Encoding.UTF8.GetBytes($"{sapisid} {now} {origin}"))).ToLowerInvariant();
        return (header, $"SAPISIDHASH {now}_{hash}");
    }
}
