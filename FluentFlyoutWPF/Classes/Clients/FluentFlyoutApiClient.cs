// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using System.Net.Http;
using System.Net.Http.Json;

namespace FluentFlyoutWPF.Classes.Clients;

public sealed class FluentFlyoutApiClient
{
    private sealed class ClientHolder(HttpClient client)
    {
        public HttpClient Client { get; } = client;
        private int _refCount = 1;
        private int _disposed;

        public bool TryAddRef()
        {
            while (true)
            {
                int current = Volatile.Read(ref _refCount);
                if (current <= 0) return false;
                if (Interlocked.CompareExchange(ref _refCount, current + 1, current) == current)
                    return true;
            }
        }

        public void Release()
        {
            if (Interlocked.Decrement(ref _refCount) == 0 &&
                Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Client.Dispose();
            }
        }
    }

    private static readonly Lock _lock = new();
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(2);
    private static readonly Uri _uri = new("https://fluentflyout.com/api/");
    private const int MaxConsecutiveTimeouts = 1;

    private static int _consecutiveTimeouts;
    private static ClientHolder _holder;

    static FluentFlyoutApiClient()
    {
        _holder = new ClientHolder(CreateClient());
    }

    private static ClientHolder AcquireClient()
    {
        lock (_lock)
        {
            if (!_holder.TryAddRef())
            {
                _holder = new ClientHolder(CreateClient());
                _holder.TryAddRef();
            }
            return _holder;
        }
    }

    public static async Task<string> GetStringAsync(string endpoint)
    {
        var holder = AcquireClient();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            AddUserAgentHeader(request);
            using var response = await holder.Client.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadAsStringAsync();
            OnRequestSucceeded();
            return result;
        }
        catch (Exception ex) when (IsTimeout(ex))
        {
            OnRequestTimedOut(holder);
            throw;
        }
        finally
        {
            holder.Release();
        }
    }

    public static async Task PostAsJsonAsync<T>(string endpoint, T content)
    {
        var holder = AcquireClient();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(content)
            };
            AddUserAgentHeader(request);
            using var response = await holder.Client.SendAsync(request);
            OnRequestSucceeded();
        }
        catch (Exception ex) when (IsTimeout(ex))
        {
            OnRequestTimedOut(holder);
            throw;
        }
        finally
        {
            holder.Release();
        }
    }

    private static bool IsTimeout(Exception ex)
    {
        return ex is TaskCanceledException { InnerException: TimeoutException }
            || ex is TimeoutException;
    }

    private static void OnRequestSucceeded()
    {
        lock (_lock)
        {
            _consecutiveTimeouts = 0;
        }
    }

    private static void OnRequestTimedOut(ClientHolder timedOutHolder)
    {
        ClientHolder? oldHolder = null;
        lock (_lock)
        {
            if (!ReferenceEquals(_holder, timedOutHolder))
                return;

            _consecutiveTimeouts++;
            if (_consecutiveTimeouts >= MaxConsecutiveTimeouts)
            {
                oldHolder = _holder;
                _holder = new ClientHolder(CreateClient());
                _consecutiveTimeouts = 0;
            }
        }
        oldHolder?.Release();
    }

    private static HttpClient CreateClient()
    {
        return new HttpClient
        {
            Timeout = _timeout,
            BaseAddress = _uri
        };
    }

    private static void AddUserAgentHeader(HttpRequestMessage request)
    {
        string appVersion = SettingsManager.Current.LastKnownVersion;
        string normalizedVersion = string.IsNullOrWhiteSpace(appVersion) ? "unknown" : appVersion.Trim();

        if (!request.Headers.UserAgent.TryParseAdd($"FluentFlyout/{normalizedVersion}"))
        {
            request.Headers.UserAgent.TryParseAdd("FluentFlyout/unknown");
        }
    }
}