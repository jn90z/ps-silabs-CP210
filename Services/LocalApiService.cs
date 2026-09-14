using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SPPS2001D_Pro.Models;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SPPS2001D_Pro.Services
{
    public sealed class LocalApiService : IAsyncDisposable
    {
        private const string ApiKeyHeaderName = "X-API-Key";

        private readonly PowerSupplyController _controller;
        private readonly IRecipeProvider _recipeProvider;
        private readonly string? _apiKey;
        private WebApplication? _app;

        public string Url { get; }
        public bool AuthenticationEnabled => !string.IsNullOrWhiteSpace(_apiKey);

        public LocalApiService(
            PowerSupplyController controller,
            IRecipeProvider recipeProvider,
            string url = "http://127.0.0.1:5187",
            string? apiKey = null)
        {
            _controller = controller ?? throw new ArgumentNullException(nameof(controller));
            _recipeProvider = recipeProvider ?? throw new ArgumentNullException(nameof(recipeProvider));
            Url = string.IsNullOrWhiteSpace(url)
                ? throw new ArgumentException("API URL is required.", nameof(url))
                : url.Trim();
            _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();

            if (!IsLoopbackUrl(Url) && _apiKey is null)
            {
                throw new InvalidOperationException(
                    "SPPS_API_KEY must be configured before the Plex API can bind to a non-loopback address.");
            }
        }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (_app is not null) return;

            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls(Url);
            builder.Services.ConfigureHttpJsonOptions(options =>
            {
                options.SerializerOptions.WriteIndented = false;
            });

            var app = builder.Build();

            app.Use(async (context, next) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers.Pragma = "no-cache";

                bool isApiRequest = context.Request.Path.StartsWithSegments("/api");
                bool isHealthRequest = context.Request.Path.StartsWithSegments("/api/health");

                if (isApiRequest && !isHealthRequest && _apiKey is not null)
                {
                    if (!context.Request.Headers.TryGetValue(ApiKeyHeaderName, out var providedKey) ||
                        !ApiKeysEqual(_apiKey, providedKey.ToString()))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        await context.Response.WriteAsJsonAsync(new
                        {
                            error = "unauthorized",
                            message = $"Provide a valid {ApiKeyHeaderName} header."
                        });
                        return;
                    }
                }

                await next();
            });

            app.MapGet("/api/health", () => Results.Ok(new
            {
                status = "ok",
                service = "SPPS2001D_Pro",
                apiVersion = 1,
                authenticationEnabled = AuthenticationEnabled
            }));

            app.MapGet("/api/status", () => Results.Ok(CreateStatus()));

            app.MapPost("/api/job/load", async (LoadJobRequest request, CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(request.PartNumber))
                    return Results.BadRequest(new { error = "partNumber is required" });

                try
                {
                    bool loaded = await _controller.LoadJobAsync(request.PartNumber.Trim(), _recipeProvider, ct);
                    return loaded
                        ? Results.Ok(CreateStatus())
                        : Results.Conflict(CreateStatus());
                }
                catch (FileNotFoundException ex)
                {
                    return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
                }
                catch (InvalidDataException ex)
                {
                    return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
                }
                catch (Exception ex)
                {
                    return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
                }
            });

            app.MapPost("/api/job/unload", async (CancellationToken ct) =>
            {
                bool unloaded = await _controller.UnloadJobAsync(ct);
                return unloaded
                    ? Results.Ok(CreateStatus())
                    : Results.Conflict(CreateStatus());
            });

            app.MapPost("/api/output/off", async () =>
            {
                bool off = await _controller.OutputOffAsync();
                return off
                    ? Results.Ok(CreateStatus())
                    : Results.Conflict(CreateStatus());
            });

            await app.StartAsync(cancellationToken);
            _app = app;
        }

        private object CreateStatus()
        {
            PowerSupplyRecipe? recipe = _controller.ActiveRecipe;
            return new
            {
                connected = _controller.Serial.IsOpen,
                outputOn = _controller.IsOutputOn,
                productionState = _controller.ProductionState.ToString(),
                jobActive = _controller.IsJobActive,
                partNumber = recipe?.PartNumber,
                voltageSetpoint = recipe?.Voltage,
                currentSetpoint = recipe?.Current,
                voltageActual = _controller.VoltageReadback,
                currentActual = _controller.CurrentReadback,
                powerActual = _controller.VoltageReadback * _controller.CurrentReadback,
                mode = _controller.Mode,
                serialStatus = _controller.Serial.Status
            };
        }

        private static bool IsLoopbackUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
                throw new ArgumentException($"Invalid API URL: {url}", nameof(url));

            return string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(uri.Host, "::1", StringComparison.OrdinalIgnoreCase) ||
                   (IPAddress.TryParse(uri.Host, out IPAddress? address) && IPAddress.IsLoopback(address));
        }

        private static bool ApiKeysEqual(string expected, string provided)
        {
            byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);
            byte[] providedBytes = Encoding.UTF8.GetBytes(provided);

            return expectedBytes.Length == providedBytes.Length &&
                   CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
        }

        public async ValueTask DisposeAsync()
        {
            if (_app is null) return;

            try { await _app.StopAsync(); }
            finally
            {
                await _app.DisposeAsync();
                _app = null;
            }
        }

        public sealed record LoadJobRequest(string PartNumber);
    }
}
