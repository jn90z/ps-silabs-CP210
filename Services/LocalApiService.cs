using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SPPS2001D_Pro.Models;

namespace SPPS2001D_Pro.Services
{
    public sealed class LocalApiService : IAsyncDisposable
    {
        private readonly PowerSupplyController _controller;
        private readonly IRecipeProvider _recipeProvider;
        private WebApplication? _app;

        public string Url { get; }

        public LocalApiService(
            PowerSupplyController controller,
            IRecipeProvider recipeProvider,
            string url = "http://127.0.0.1:5187")
        {
            _controller = controller ?? throw new ArgumentNullException(nameof(controller));
            _recipeProvider = recipeProvider ?? throw new ArgumentNullException(nameof(recipeProvider));
            Url = url;
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

            app.MapGet("/api/health", () => Results.Ok(new
            {
                status = "ok",
                service = "SPPS2001D_Pro",
                apiVersion = 1
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
