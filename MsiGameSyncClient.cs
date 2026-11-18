using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Jargb;

internal readonly record struct MsiGameSyncState(bool Enabled, string? ActiveGame);

internal static class MsiGameSyncClient
{
    private const string Address = "http://localhost:1462/";
    private const string LedRegistryKey = @"SOFTWARE\MSI\MSI Center\Component\Mystic Light\LED";
    private static readonly HttpClient Client = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
    {
        BaseAddress = new Uri(Address),
        Timeout = TimeSpan.FromSeconds(2)
    };

    internal static async Task<int> ReadProfileCountAsync()
    {
        string body = await SendAsync(HttpMethod.Get, "sdk/loader", null);
        using JsonDocument document = ParseJson(body, "sdk/loader");
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw Unexpected("sdk/loader", body);

        int count = 0;
        foreach (JsonProperty item in document.RootElement.EnumerateObject())
        {
            if (!Guid.TryParse(item.Name, out _) || item.Value.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(item.Value.GetString()))
                throw Unexpected("sdk/loader", body);
            count++;
        }
        if (count == 0)
            throw new IOException("Mystic Light не загрузил игровые профили (GET /sdk/loader вернул пустой список).");
        return count;
    }

    internal static async Task<MsiGameSyncState> ReadStateAsync()
    {
        string body = await SendAsync(HttpMethod.Get, "debug", null);
        string? enabled = null;
        string? game = null;
        foreach (string line in body.Split('\n', StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("IsEnableSDK=", StringComparison.Ordinal))
                enabled = line["IsEnableSDK=".Length..];
            else if (line.StartsWith("ApplicationName=", StringComparison.Ordinal))
                game = line["ApplicationName=".Length..];
        }

        if (!bool.TryParse(enabled, out bool state) || game is null)
            throw Unexpected("debug", body);

        return new(state, state && game.Length > 0 ? game : null);
    }

    internal static async Task<MsiGameSyncState> SetEnabledAsync(bool enabled)
    {
        string payload = enabled ? """{"isenablesdk":true}""" : """{"isenablesdk":false}""";
        string response = await SendAsync(HttpMethod.Post, "sdk/setting", payload);
        if (response.Trim().Length > 0)
            throw Unexpected("sdk/setting", response);

        MsiGameSyncState actual = await ReadStateAsync();
        if (actual.Enabled != enabled)
            throw new IOException($"Mystic Light не подтвердил Game Sync. POST /sdk/setting {payload}. Получено: {actual.Enabled}.");
        return actual;
    }

    internal static void VerifyGameModeConfigured()
    {
        using RegistryKey registry = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
        using RegistryKey? led = registry.OpenSubKey(LedRegistryKey);
        if (led?.GetValue("Ambi_AllSwitch") is not string state ||
            !string.Equals(state, "True", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Фоновый модуль MSI не подготовлен. Один раз включите Game Sync в MSI Center → Mystic Light.");
    }

    private static async Task<string> SendAsync(HttpMethod method, string route, string? payload)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using HttpRequestMessage request = new(method, route);
                if (payload is not null)
                    request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

                using HttpResponseMessage response = await Client.SendAsync(request);
                string body = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"HTTP {(int)response.StatusCode}, ответ: {Excerpt(body)}",
                        null, response.StatusCode);
                return body;
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
            {
                if (attempt == 2)
                    throw new IOException($"Mystic Light · {method} {Address}{route} {payload} · {error.Message}", error);

                Trace.TraceWarning("Mystic Light request failed. Method={0} Route={1} Attempt={2} Error={3}",
                    method, route, attempt, error.Message);
                await Task.Delay(120);
            }
        }
    }

    private static JsonDocument ParseJson(string body, string route)
    {
        try { return JsonDocument.Parse(body); }
        catch (JsonException error)
        {
            throw new IOException($"Некорректный JSON от Mystic Light на /{route}. Ответ: {Excerpt(body)}", error);
        }
    }

    private static IOException Unexpected(string route, string body) =>
        new($"Неожиданный ответ Mystic Light на /{route}: {Excerpt(body)}");

    private static string Excerpt(string body) => body.Length <= 400 ? body : body[..400] + "…";
}
