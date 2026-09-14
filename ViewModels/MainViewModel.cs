using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SPPS2001D_Pro.Services;

namespace SPPS2001D_Pro.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        public SerialService Serial { get; } = new();
        public bool LoggingEnabled { get; set; }
        public string SerialConnectText => Serial.IsOpen ? "Disconnect" : "Connect";

        private double _vset, _iset, _vread, _iread;
        private string _mode = "(n/a)", _log = "";
        private bool _isOutputOn;
        private CancellationTokenSource? _autoCts;
        private DateTime _lastReadbackUtc = DateTime.MinValue;
        private DateTime _lastVoltageReadbackUtc = DateTime.MinValue;
        private DateTime _lastCurrentReadbackUtc = DateTime.MinValue;
        private bool _readbackWarningShown;
        private readonly SemaphoreSlim _queryLock = new(1, 1);
        private const int ReadbackResponseTimeoutMs = 100;
        private const int AutoQueryIntervalMs = 300;

        public bool IsOutputOn
        {
            get => _isOutputOn;
            set
            {
                if (_isOutputOn != value)
                {
                    _isOutputOn = value;
                    OnPropertyChanged();
                }
            }
        }

        public double Vset { get => _vset; set { _vset = value; OnPropertyChanged(); } }
        public double Iset { get => _iset; set { _iset = value; OnPropertyChanged(); } }

        public double Vread
        {
            get => _vread;
            set
            {
                _vread = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(VoltageDisplay));
                OnPropertyChanged(nameof(PowerDisplay));
            }
        }

        public double Iread
        {
            get => _iread;
            set
            {
                _iread = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CurrentDisplay));
                OnPropertyChanged(nameof(PowerDisplay));
            }
        }

        public string Mode
        {
            get => _mode;
            set
            {
                _mode = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayModeLabel));
                OnPropertyChanged(nameof(ModeBrush));
            }
        }

        public string Log { get => _log; set { _log = value; OnPropertyChanged(); } }

        public string VoltageDisplay => Vread.ToString("000.0");
        public string CurrentDisplay => Iread.ToString("0.000");
        public string PowerDisplay => (Vread * Iread).ToString("000.0");
        public string DisplayModeLabel => Mode == "CC" ? "C.C" : Mode == "CV" ? "C.V" : "---";

        public Brush StatusBrush => Serial.IsOpen ? Brushes.LimeGreen :
            (Serial.Status.StartsWith("Open failed") || Serial.Status.StartsWith("No COM") ? Brushes.OrangeRed : Brushes.Gray);

        public Brush ModeBrush => Mode == "CC" ? Brushes.MediumPurple : Mode == "CV" ? Brushes.Cyan : Brushes.Gray;

        public ICommand ToggleConnectCommand { get; }
        public ICommand SetVCommand { get; }
        public ICommand SetICommand { get; }
        public ICommand OutputOnCommand { get; }
        public ICommand OutputOffCommand { get; }
        public ICommand QueryVICommand { get; }
        public ICommand RemoteModeCommand { get; }
        public ICommand LocalModeCommand { get; }

        public MainViewModel()
        {
            _ = Serial.RefreshPortsAsync();
            Serial.FrameReceived += OnFrame;
            Serial.PropertyChanged += (_, __) =>
            {
                OnPropertyChanged(nameof(SerialConnectText));
                OnPropertyChanged(nameof(StatusBrush));
            };

            ToggleConnectCommand = new RelayCommand(async _ =>
            {
                if (Serial.IsOpen)
                {
                    _autoCts?.Cancel();

                    await Serial.SafeDisconnectAsync();
                    IsOutputOn = false;
                }
                else if (await Serial.OpenAsync())
                {
                    _lastReadbackUtc = DateTime.MinValue;
                    _lastVoltageReadbackUtc = DateTime.MinValue;
                    _lastCurrentReadbackUtc = DateTime.MinValue;
                    _readbackWarningShown = false;
                    _autoCts = new CancellationTokenSource();
                    _ = AutoQueryLoop(_autoCts.Token);
                    Append("Connected. Response-paced auto-query started.");
                }

                OnPropertyChanged(nameof(SerialConnectText));
                OnPropertyChanged(nameof(StatusBrush));
            });

            RemoteModeCommand = new RelayCommand(async _ => await Serial.SendRemoteAsync());
            LocalModeCommand = new RelayCommand(async _ => await Serial.SendLocalAsync());
            SetVCommand = new RelayCommand(async _ => await Serial.SendSetVoltageAsync(Protocol.Encode6(Vset)));
            SetICommand = new RelayCommand(async _ => await Serial.SendSetCurrentAsync(Protocol.Encode6(Iset)));

            OutputOnCommand = new RelayCommand(async _ =>
            {
                if (await Serial.OutputOnAsync())
                    IsOutputOn = true;
            });

            OutputOffCommand = new RelayCommand(async _ =>
            {
                if (await Serial.OutputOffAsync())
                    IsOutputOn = false;
            });

            QueryVICommand = new RelayCommand(async _ => await QueryVI());
        }

        private async Task AutoQueryLoop(CancellationToken ct)
        {
            DateTime started = DateTime.UtcNow;

            try
            {
                while (!ct.IsCancellationRequested && Serial.IsOpen)
                {
                    await QueryVI();

                    if (!_readbackWarningShown &&
                        _lastReadbackUtc == DateTime.MinValue &&
                        (DateTime.UtcNow - started).TotalSeconds > 3)
                    {
                        _readbackWarningShown = true;
                        Append("No readback frames received yet. Check PSU remote/lock state and selected COM port.");
                    }

                    // Refresh quickly, but leave headroom for commands initiated by the user.
                    await Task.Delay(AutoQueryIntervalMs, ct);
                }
            }
            catch (TaskCanceledException) { }
            catch (Exception ex)
            {
                Append("AutoQuery error: " + ex.Message);
            }
        }

        private async Task QueryVI()
        {
            await _queryLock.WaitAsync();
            try
            {
                // One request at a time. We wait for the matching readback before sending
                // the next request, which avoids a backlog of replies on slower hardware.
                bool gotCurrent = await QueryAndWaitAsync(
                    Serial.QueryCurrent033Async,
                    () => _lastCurrentReadbackUtc);

                if (!gotCurrent)
                {
                    await QueryAndWaitAsync(
                        Serial.QueryCurrent000Async,
                        () => _lastCurrentReadbackUtc);
                }

                bool gotVoltage = await QueryAndWaitAsync(
                    Serial.QueryVoltage122Async,
                    () => _lastVoltageReadbackUtc);

                if (!gotVoltage)
                {
                    await QueryAndWaitAsync(
                        Serial.QueryVoltage000Async,
                        () => _lastVoltageReadbackUtc);
                }
            }
            finally
            {
                _queryLock.Release();
            }
        }

        private static async Task<bool> QueryAndWaitAsync(
            Func<Task<bool>> sendQuery,
            Func<DateTime> lastResponseUtc)
        {
            DateTime before = lastResponseUtc();
            if (!await sendQuery()) return false;

            DateTime deadline = DateTime.UtcNow.AddMilliseconds(ReadbackResponseTimeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (lastResponseUtc() > before) return true;
                await Task.Delay(5);
            }

            return lastResponseUtc() > before;
        }

        private void OnFrame(string frame)
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (frame.StartsWith("<12"))
                    {
                        Vread = Protocol.ParseVoltage(frame);
                        _lastReadbackUtc = DateTime.UtcNow;
                        _lastVoltageReadbackUtc = DateTime.UtcNow;
                    }
                    else if (frame.StartsWith("<14") || frame.StartsWith("<C4"))
                    {
                        var r = Protocol.ParseCurrent(frame);
                        Mode = r.Mode;
                        Iread = r.A;
                        _lastReadbackUtc = DateTime.UtcNow;
                        _lastCurrentReadbackUtc = DateTime.UtcNow;
                    }

                    if (LoggingEnabled)
                    {
                        try
                        {
                            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "spps2001d_log.csv");
                            string line = string.Format("{0:O},{1:F3},{2:F3},{3},{4}\r\n", DateTime.Now, Vread, Iread, Mode, frame);
                            File.AppendAllText(path, line);
                        }
                        catch { }
                    }
                }
                catch (Exception ex)
                {
                    Append("Parse error: " + ex.Message);
                }
            }));
        }

        private void Append(string line)
        {
            Log += $"[{DateTime.Now:HH:mm:ss}] {line}\r\n";
            OnPropertyChanged(nameof(Log));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class RelayCommand : ICommand
    {
        private readonly Action<object?> _exec;
        private readonly Func<object?, bool>? _can;
        public RelayCommand(Action<object?> exec, Func<object?, bool>? can = null) { _exec = exec; _can = can; }
        public bool CanExecute(object? parameter) => _can?.Invoke(parameter) ?? true;
        public void Execute(object? parameter) => _exec(parameter);
        public event EventHandler? CanExecuteChanged;
    }
}
