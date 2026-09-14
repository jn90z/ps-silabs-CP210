using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace SPPS2001D_Pro.Services
{
    /// <summary>
    /// Application-facing control layer for the power supply.
    /// UI code and future automation integrations (for example Plex) should use
    /// this class instead of talking directly to the serial protocol.
    /// </summary>
    public sealed class PowerSupplyController : INotifyPropertyChanged, IDisposable
    {
        public SerialService Serial { get; }

        private double _voltageReadback;
        private double _currentReadback;
        private string _mode = "(n/a)";
        private bool _isOutputOn;
        private CancellationTokenSource? _autoCts;
        private DateTime _lastReadbackUtc = DateTime.MinValue;
        private DateTime _lastVoltageReadbackUtc = DateTime.MinValue;
        private DateTime _lastCurrentReadbackUtc = DateTime.MinValue;
        private bool _readbackWarningShown;
        private readonly SemaphoreSlim _queryLock = new(1, 1);

        private const int ReadbackResponseTimeoutMs = 100;
        private const int AutoQueryIntervalMs = 300;

        public double VoltageReadback
        {
            get => _voltageReadback;
            private set
            {
                if (_voltageReadback != value)
                {
                    _voltageReadback = value;
                    OnPropertyChanged();
                }
            }
        }

        public double CurrentReadback
        {
            get => _currentReadback;
            private set
            {
                if (_currentReadback != value)
                {
                    _currentReadback = value;
                    OnPropertyChanged();
                }
            }
        }

        public string Mode
        {
            get => _mode;
            private set
            {
                if (_mode != value)
                {
                    _mode = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsOutputOn
        {
            get => _isOutputOn;
            private set
            {
                if (_isOutputOn != value)
                {
                    _isOutputOn = value;
                    OnPropertyChanged();
                }
            }
        }

        public event Action<string>? RawFrameReceived;
        public event Action<string>? Message;

        public PowerSupplyController(SerialService? serial = null)
        {
            Serial = serial ?? new SerialService();
            Serial.FrameReceived += OnFrame;
        }

        public Task RefreshPortsAsync() => Serial.RefreshPortsAsync();

        public async Task<bool> ConnectAsync()
        {
            if (Serial.IsOpen)
                return true;

            if (!await Serial.OpenAsync())
                return false;

            _lastReadbackUtc = DateTime.MinValue;
            _lastVoltageReadbackUtc = DateTime.MinValue;
            _lastCurrentReadbackUtc = DateTime.MinValue;
            _readbackWarningShown = false;

            _autoCts?.Cancel();
            _autoCts?.Dispose();
            _autoCts = new CancellationTokenSource();
            _ = AutoQueryLoopAsync(_autoCts.Token);

            Message?.Invoke("Connected. Response-paced auto-query started.");
            return true;
        }

        public async Task DisconnectAsync()
        {
            _autoCts?.Cancel();
            await Serial.SafeDisconnectAsync();
            IsOutputOn = false;
        }

        public Task<bool> SetVoltageAsync(double volts)
        {
            if (volts < 0) throw new ArgumentOutOfRangeException(nameof(volts));
            return Serial.SendSetVoltageAsync(Protocol.Encode6(volts));
        }

        public Task<bool> SetCurrentAsync(double amps)
        {
            if (amps < 0) throw new ArgumentOutOfRangeException(nameof(amps));
            return Serial.SendSetCurrentAsync(Protocol.Encode6(amps));
        }

        public Task<bool> SetRemoteModeAsync() => Serial.SendRemoteAsync();
        public Task<bool> SetLocalModeAsync() => Serial.SendLocalAsync();

        public async Task<bool> OutputOnAsync()
        {
            bool sent = await Serial.OutputOnAsync();
            if (sent) IsOutputOn = true;
            return sent;
        }

        public async Task<bool> OutputOffAsync()
        {
            bool sent = await Serial.OutputOffAsync();
            if (sent) IsOutputOn = false;
            return sent;
        }

        public async Task QueryReadbackAsync(CancellationToken ct = default)
        {
            await _queryLock.WaitAsync(ct);
            try
            {
                bool gotCurrent = await QueryAndWaitAsync(
                    Serial.QueryCurrent033Async,
                    () => _lastCurrentReadbackUtc,
                    ct);

                if (!gotCurrent && !ct.IsCancellationRequested)
                {
                    await QueryAndWaitAsync(
                        Serial.QueryCurrent000Async,
                        () => _lastCurrentReadbackUtc,
                        ct);
                }

                bool gotVoltage = await QueryAndWaitAsync(
                    Serial.QueryVoltage122Async,
                    () => _lastVoltageReadbackUtc,
                    ct);

                if (!gotVoltage && !ct.IsCancellationRequested)
                {
                    await QueryAndWaitAsync(
                        Serial.QueryVoltage000Async,
                        () => _lastVoltageReadbackUtc,
                        ct);
                }
            }
            finally
            {
                _queryLock.Release();
            }
        }

        private async Task AutoQueryLoopAsync(CancellationToken ct)
        {
            DateTime started = DateTime.UtcNow;

            try
            {
                while (!ct.IsCancellationRequested && Serial.IsOpen)
                {
                    await QueryReadbackAsync(ct);

                    if (!_readbackWarningShown &&
                        _lastReadbackUtc == DateTime.MinValue &&
                        (DateTime.UtcNow - started).TotalSeconds > 3)
                    {
                        _readbackWarningShown = true;
                        Message?.Invoke("No readback frames received yet. Check PSU remote/lock state and selected COM port.");
                    }

                    await Task.Delay(AutoQueryIntervalMs, ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Message?.Invoke("AutoQuery error: " + ex.Message);
            }
        }

        private static async Task<bool> QueryAndWaitAsync(
            Func<Task<bool>> sendQuery,
            Func<DateTime> lastResponseUtc,
            CancellationToken ct)
        {
            DateTime before = lastResponseUtc();
            if (!await sendQuery()) return false;

            DateTime deadline = DateTime.UtcNow.AddMilliseconds(ReadbackResponseTimeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (lastResponseUtc() > before) return true;
                await Task.Delay(5, ct);
            }

            return lastResponseUtc() > before;
        }

        private void OnFrame(string frame)
        {
            try
            {
                if (frame.StartsWith("<12"))
                {
                    VoltageReadback = Protocol.ParseVoltage(frame);
                    DateTime now = DateTime.UtcNow;
                    _lastReadbackUtc = now;
                    _lastVoltageReadbackUtc = now;
                }
                else if (frame.StartsWith("<14") || frame.StartsWith("<C4"))
                {
                    var parsed = Protocol.ParseCurrent(frame);
                    Mode = parsed.Mode;
                    CurrentReadback = parsed.A;
                    DateTime now = DateTime.UtcNow;
                    _lastReadbackUtc = now;
                    _lastCurrentReadbackUtc = now;
                }

                RawFrameReceived?.Invoke(frame);
            }
            catch (Exception ex)
            {
                Message?.Invoke("Parse error: " + ex.Message);
            }
        }

        public void Dispose()
        {
            _autoCts?.Cancel();
            _autoCts?.Dispose();
            Serial.FrameReceived -= OnFrame;
            Serial.Dispose();
            _queryLock.Dispose();
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
