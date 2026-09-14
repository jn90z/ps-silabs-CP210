using SPPS2001D_Pro.Models;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace SPPS2001D_Pro.Services
{
    public enum ProductionState { NoJob, LoadingRecipe, JobActive, EndingJob, Fault }

    public sealed class PowerSupplyController : INotifyPropertyChanged, IDisposable
    {
        public SerialService Serial { get; }
        private double _voltageReadback, _currentReadback;
        private string _mode = "(n/a)";
        private bool _isOutputOn;
        private ProductionState _productionState = ProductionState.NoJob;
        private PowerSupplyRecipe? _activeRecipe;
        private CancellationTokenSource? _autoCts;
        private DateTime _lastReadbackUtc = DateTime.MinValue, _lastVoltageReadbackUtc = DateTime.MinValue, _lastCurrentReadbackUtc = DateTime.MinValue;
        private bool _readbackWarningShown;
        private readonly SemaphoreSlim _queryLock = new(1, 1), _jobLock = new(1, 1);
        private const int ReadbackResponseTimeoutMs = 100, AutoQueryIntervalMs = 300;
        private const int VerificationAttempts = 4, VerificationDelayMs = 125;
        private const double VerificationPercentTolerance = 0.02, VerificationVoltageFloor = 0.10, VerificationCurrentFloor = 0.010;

        public double VoltageReadback { get => _voltageReadback; private set => SetField(ref _voltageReadback, value); }
        public double CurrentReadback { get => _currentReadback; private set => SetField(ref _currentReadback, value); }
        public string Mode { get => _mode; private set => SetField(ref _mode, value); }
        public bool IsOutputOn { get => _isOutputOn; private set => SetField(ref _isOutputOn, value); }
        public ProductionState ProductionState { get => _productionState; private set => SetField(ref _productionState, value); }
        public PowerSupplyRecipe? ActiveRecipe { get => _activeRecipe; private set => SetField(ref _activeRecipe, value); }
        public bool IsJobActive => ProductionState == ProductionState.JobActive && ActiveRecipe is not null;
        public event Action<string>? RawFrameReceived;
        public event Action<string>? Message;

        public PowerSupplyController(SerialService? serial = null) { Serial = serial ?? new SerialService(); Serial.FrameReceived += OnFrame; }
        public Task RefreshPortsAsync() => Serial.RefreshPortsAsync();

        public async Task<bool> ConnectAsync()
        {
            if (Serial.IsOpen) return true;
            if (!await Serial.OpenAsync()) return false;
            _lastReadbackUtc = _lastVoltageReadbackUtc = _lastCurrentReadbackUtc = DateTime.MinValue;
            _readbackWarningShown = false;
            _autoCts?.Cancel(); _autoCts?.Dispose(); _autoCts = new CancellationTokenSource();
            _ = AutoQueryLoopAsync(_autoCts.Token);
            Message?.Invoke("Connected. Response-paced auto-query started.");
            return true;
        }

        public async Task DisconnectAsync()
        {
            _autoCts?.Cancel(); await Serial.SafeDisconnectAsync(); IsOutputOn = false; ActiveRecipe = null; ProductionState = ProductionState.NoJob; OnPropertyChanged(nameof(IsJobActive));
        }

        public Task<bool> SetVoltageAsync(double volts) { if (volts < 0) throw new ArgumentOutOfRangeException(nameof(volts)); return Serial.SendSetVoltageAsync(Protocol.Encode6(volts)); }
        public Task<bool> SetCurrentAsync(double amps) { if (amps < 0) throw new ArgumentOutOfRangeException(nameof(amps)); return Serial.SendSetCurrentAsync(Protocol.Encode6(amps)); }
        public Task<bool> SetRemoteModeAsync() => Serial.SendRemoteAsync();
        public Task<bool> SetLocalModeAsync() => Serial.SendLocalAsync();
        public async Task<bool> OutputOnAsync() { bool sent = await Serial.OutputOnAsync(); if (sent) IsOutputOn = true; return sent; }
        public async Task<bool> OutputOffAsync() { bool sent = await Serial.OutputOffAsync(); if (sent) IsOutputOn = false; return sent; }

        public async Task<bool> LoadJobAsync(PowerSupplyRecipe recipe, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(recipe); ValidateRecipe(recipe); await _jobLock.WaitAsync(ct);
            try
            {
                ProductionState = ProductionState.LoadingRecipe; ActiveRecipe = null; OnPropertyChanged(nameof(IsJobActive));
                if (!Serial.IsOpen && !await ConnectAsync()) return SetJobFault("Unable to connect to the power supply while loading the job.");
                if (!await OutputOffAsync()) return SetJobFault("Unable to turn the power supply output off before loading the recipe.");
                if (!await SetRemoteModeAsync()) return SetJobFault("Unable to place the power supply in remote mode.");
                if (!await SetVoltageAsync(recipe.Voltage)) return SetJobFault("Unable to send the recipe voltage.");
                if (!await SetCurrentAsync(recipe.Current)) return SetJobFault("Unable to send the recipe current.");
                if (!await OutputOnAsync()) return SetJobFault("Recipe loaded, but the power supply output could not be enabled.");

                if (!await VerifyRecipeReadbackAsync(recipe, ct))
                {
                    try { await OutputOffAsync(); } catch { }
                    ActiveRecipe = null;
                    return SetJobFault($"Recipe verification failed for {recipe.PartNumber}. Output was turned off.");
                }

                ActiveRecipe = recipe;
                ProductionState = ProductionState.JobActive; OnPropertyChanged(nameof(IsJobActive));
                Message?.Invoke($"Job recipe verified and active: {recipe.PartNumber} - {recipe.Voltage:0.000} V / {recipe.Current:0.000} A. Output will remain on until the job is unloaded or changed.");
                return true;
            }
            catch (OperationCanceledException) { try { await OutputOffAsync(); } catch { } ActiveRecipe = null; ProductionState = ProductionState.NoJob; OnPropertyChanged(nameof(IsJobActive)); throw; }
            catch (Exception ex) { try { await OutputOffAsync(); } catch { } ActiveRecipe = null; return SetJobFault("Job load failed: " + ex.Message); }
            finally { _jobLock.Release(); }
        }

        public async Task<bool> LoadJobAsync(string partNumber, IRecipeProvider recipeProvider, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(recipeProvider);
            var recipe = await recipeProvider.GetRecipeAsync(partNumber, ct);
            if (recipe is null) { if (Serial.IsOpen) await OutputOffAsync(); ActiveRecipe = null; return SetJobFault($"No power-supply recipe found for part number {partNumber}."); }
            return await LoadJobAsync(recipe, ct);
        }

        public async Task<bool> UnloadJobAsync(CancellationToken ct = default)
        {
            await _jobLock.WaitAsync(ct);
            try { ProductionState = ProductionState.EndingJob; OnPropertyChanged(nameof(IsJobActive)); bool off = await OutputOffAsync(); ActiveRecipe = null; ProductionState = off ? ProductionState.NoJob : ProductionState.Fault; OnPropertyChanged(nameof(IsJobActive)); Message?.Invoke(off ? "Job unloaded. Power supply output is off." : "Job ended, but output-off command failed."); return off; }
            finally { _jobLock.Release(); }
        }

        private async Task<bool> VerifyRecipeReadbackAsync(PowerSupplyRecipe recipe, CancellationToken ct)
        {
            for (int attempt = 1; attempt <= VerificationAttempts; attempt++)
            {
                DateTime voltageBefore = _lastVoltageReadbackUtc;
                DateTime currentBefore = _lastCurrentReadbackUtc;
                await QueryReadbackAsync(ct);

                bool freshVoltage = _lastVoltageReadbackUtc > voltageBefore;
                bool freshCurrent = _lastCurrentReadbackUtc > currentBefore;
                if (freshVoltage && freshCurrent)
                {
                    double voltageTolerance = Math.Max(VerificationVoltageFloor, Math.Abs(recipe.Voltage) * VerificationPercentTolerance);
                    double currentTolerance = Math.Max(VerificationCurrentFloor, Math.Abs(recipe.Current) * VerificationPercentTolerance);

                    if (Mode == "CV" && Math.Abs(VoltageReadback - recipe.Voltage) <= voltageTolerance)
                    {
                        Message?.Invoke($"Recipe verification passed in CV mode: {VoltageReadback:0.000} V, {CurrentReadback:0.000} A.");
                        return true;
                    }

                    if (Mode == "CC" && Math.Abs(CurrentReadback - recipe.Current) <= currentTolerance)
                    {
                        Message?.Invoke($"Recipe verification passed in CC mode: {VoltageReadback:0.000} V, {CurrentReadback:0.000} A.");
                        return true;
                    }
                }

                if (attempt < VerificationAttempts)
                    await Task.Delay(VerificationDelayMs, ct);
            }

            Message?.Invoke($"Recipe readback did not verify. Expected {recipe.Voltage:0.000} V / {recipe.Current:0.000} A; read {VoltageReadback:0.000} V / {CurrentReadback:0.000} A, mode {Mode}.");
            return false;
        }

        private static void ValidateRecipe(PowerSupplyRecipe recipe)
        {
            if (string.IsNullOrWhiteSpace(recipe.PartNumber)) throw new ArgumentException("Recipe part number is required.", nameof(recipe));
            if (recipe.Voltage < 0 || recipe.Current < 0) throw new ArgumentOutOfRangeException(nameof(recipe), "Recipe voltage/current cannot be negative.");
            if (recipe.MaxVoltage.HasValue && recipe.Voltage > recipe.MaxVoltage.Value) throw new ArgumentOutOfRangeException(nameof(recipe), "Recipe voltage exceeds MaxVoltage.");
            if (recipe.MaxCurrent.HasValue && recipe.Current > recipe.MaxCurrent.Value) throw new ArgumentOutOfRangeException(nameof(recipe), "Recipe current exceeds MaxCurrent.");
        }
        private bool SetJobFault(string message) { ActiveRecipe = null; ProductionState = ProductionState.Fault; OnPropertyChanged(nameof(IsJobActive)); Message?.Invoke(message); return false; }

        public async Task QueryReadbackAsync(CancellationToken ct = default)
        {
            await _queryLock.WaitAsync(ct);
            try { bool gotCurrent = await QueryAndWaitAsync(Serial.QueryCurrent033Async, () => _lastCurrentReadbackUtc, ct); if (!gotCurrent && !ct.IsCancellationRequested) await QueryAndWaitAsync(Serial.QueryCurrent000Async, () => _lastCurrentReadbackUtc, ct); bool gotVoltage = await QueryAndWaitAsync(Serial.QueryVoltage122Async, () => _lastVoltageReadbackUtc, ct); if (!gotVoltage && !ct.IsCancellationRequested) await QueryAndWaitAsync(Serial.QueryVoltage000Async, () => _lastVoltageReadbackUtc, ct); }
            finally { _queryLock.Release(); }
        }
        private async Task AutoQueryLoopAsync(CancellationToken ct)
        {
            DateTime started = DateTime.UtcNow;
            try { while (!ct.IsCancellationRequested && Serial.IsOpen) { await QueryReadbackAsync(ct); if (!_readbackWarningShown && _lastReadbackUtc == DateTime.MinValue && (DateTime.UtcNow - started).TotalSeconds > 3) { _readbackWarningShown = true; Message?.Invoke("No readback frames received yet. Check PSU remote/lock state and selected COM port."); } await Task.Delay(AutoQueryIntervalMs, ct); } }
            catch (OperationCanceledException) { } catch (Exception ex) { Message?.Invoke("AutoQuery error: " + ex.Message); }
        }
        private static async Task<bool> QueryAndWaitAsync(Func<Task<bool>> sendQuery, Func<DateTime> lastResponseUtc, CancellationToken ct)
        {
            DateTime before = lastResponseUtc(); if (!await sendQuery()) return false; DateTime deadline = DateTime.UtcNow.AddMilliseconds(ReadbackResponseTimeoutMs);
            while (DateTime.UtcNow < deadline) { ct.ThrowIfCancellationRequested(); if (lastResponseUtc() > before) return true; await Task.Delay(5, ct); }
            return lastResponseUtc() > before;
        }
        private void OnFrame(string frame)
        {
            try { if (frame.StartsWith("<12")) { VoltageReadback = Protocol.ParseVoltage(frame); DateTime now = DateTime.UtcNow; _lastReadbackUtc = _lastVoltageReadbackUtc = now; } else if (frame.StartsWith("<14") || frame.StartsWith("<C4")) { var parsed = Protocol.ParseCurrent(frame); Mode = parsed.Mode; CurrentReadback = parsed.A; DateTime now = DateTime.UtcNow; _lastReadbackUtc = _lastCurrentReadbackUtc = now; } RawFrameReceived?.Invoke(frame); }
            catch (Exception ex) { Message?.Invoke("Parse error: " + ex.Message); }
        }
        public void Dispose() { _autoCts?.Cancel(); _autoCts?.Dispose(); Serial.FrameReceived -= OnFrame; Serial.Dispose(); _queryLock.Dispose(); _jobLock.Dispose(); }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return; field = value; OnPropertyChanged(name); }
    }
}
