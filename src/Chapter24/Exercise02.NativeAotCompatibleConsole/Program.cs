// ============================================================================
// Exercise 02 — Native AOT Compatible Console
// ============================================================================
// Demonstrates an AOT-friendly console app that uses source-generated JSON,
// explicit HttpClient registration, and simple logging without reflection-heavy
// runtime discovery.
// ============================================================================

using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

await Demo.RunAsync();

public static class Demo
{
    public static async Task RunAsync()
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();

        builder.Services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddSimpleConsole(options => options.SingleLine = true);
        });

        // Explicit service registration is easier for trimming and AOT than assembly scanning.
        builder.Services.AddHttpClient<IWeatherClient, WeatherClient>(client =>
        {
            client.BaseAddress = new Uri("https://example.invalid/");
            client.Timeout = TimeSpan.FromSeconds(2);
        });

        // Keep JSON metadata explicit and local to the call sites so the demo stays analyzable under AOT.

        using IHost host = builder.Build();
        ILogger logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Demo");
        IWeatherClient client = host.Services.GetRequiredService<IWeatherClient>();

        // The serialization context is known at compile time, so Native AOT does not need runtime contract discovery.
        WeatherSnapshot snapshot = new("Shanghai", 26, "Sunny");
        string json = System.Text.Json.JsonSerializer.Serialize(snapshot, AppJsonContext.Default.WeatherSnapshot);

        logger.LogInformation("Serialized payload: {Json}", json);

        string summary = await client.GetSummaryAsync(snapshot.City);
        logger.LogInformation("HTTP summary: {Summary}", summary);
    }
}

public interface IWeatherClient
{
    Task<string> GetSummaryAsync(string city);
}

public sealed class WeatherClient : IWeatherClient
{
    private readonly HttpClient _httpClient;

    public WeatherClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<string> GetSummaryAsync(string city)
    {
        // The endpoint is intentionally illustrative; the sample falls back to deterministic local data.
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, $"weather/{Uri.EscapeDataString(city)}");
            using HttpResponseMessage response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            WeatherApiResponse? payload = await response.Content.ReadFromJsonAsync(AppJsonContext.Default.WeatherApiResponse);
            if (payload is WeatherApiResponse data)
            {
                return $"{data.City}: {data.Condition}, {data.TemperatureC}C";
            }

            return $"No data for {city}.";
        }
        catch
        {
            // The fallback keeps the demo runnable even without a real endpoint.
            return $"{city}: offline demo response, 25C";
        }
    }
}

public readonly record struct WeatherSnapshot(string City, int TemperatureC, string Condition);
public readonly record struct WeatherApiResponse(string City, int TemperatureC, string Condition);

[JsonSerializable(typeof(WeatherSnapshot))]
[JsonSerializable(typeof(WeatherApiResponse))]
public partial class AppJsonContext : JsonSerializerContext
{
}
