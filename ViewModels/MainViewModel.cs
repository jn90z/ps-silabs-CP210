using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SPPS2001D_Pro.Services;

namespace SPPS2001D_Pro.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        public PowerSupplyController Controller { get; } = new();
        public SerialService Serial => Controller.Serial;
        public bool LoggingEnabled { get; set; }
        public string SerialConnectText => Serial.IsOpen ? "Disconnect" : "Connect";

        private string _vsetText = "", _isetText = "";
        private string _log = "";

        public string VsetText
        {
            get => _vsetText;
            set
            {
                if (_vsetText != value)
                {
                    _vsetText = value;
                    OnPropertyChanged();
                }
            }
        }

        public string IsetText
        {
            get => _isetText;
            set
            {
                if (_isetText != value)
                {
                    _isetText = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsOutputOn => Controller.IsOutputOn;
        public double Vread => Controller.VoltageReadback;
        public double Iread => Controller.CurrentReadback;
        public string Mode => Controller.Mode;

        public string Log
        {
            get => _log;
            set
            {
                _log = value;
                OnPropertyChanged();
            }
        }

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
            _ = Controller.RefreshPortsAsync();

            Serial.PropertyChanged += (_, __) =>
            {
                RunOnUi(() =>
                {
                    OnPropertyChanged(nameof(SerialConnectText));
                    OnPropertyChanged(nameof(StatusBrush));
                });
            };

            Controller.PropertyChanged += (_, e) =>
            {
                RunOnUi(() => HandleControllerPropertyChanged(e.PropertyName));
            };

            Controller.Message += message => RunOnUi(() => Append(message));
            Controller.RawFrameReceived += frame => RunOnUi(() => LogFrameIfEnabled(frame));

            ToggleConnectCommand = new RelayCommand(async _ =>
            {
                if (Serial.IsOpen)
                    await Controller.DisconnectAsync();
                else
                    await Controller.ConnectAsync();

                OnPropertyChanged(nameof(SerialConnectText));
                OnPropertyChanged(nameof(StatusBrush));
            });

            RemoteModeCommand = new RelayCommand(async _ => await Controller.SetRemoteModeAsync());
            LocalModeCommand = new RelayCommand(async _ => await Controller.SetLocalModeAsync());
            SetVCommand = new RelayCommand(async _ => await SetVoltageAsync());
            SetICommand = new RelayCommand(async _ => await SetCurrentAsync());
            OutputOnCommand = new RelayCommand(async _ => await Controller.OutputOnAsync());
            OutputOffCommand = new RelayCommand(async _ => await Controller.OutputOffAsync());
            QueryVICommand = new RelayCommand(async _ => await Controller.QueryReadbackAsync());
        }

        private void HandleControllerPropertyChanged(string? propertyName)
        {
            switch (propertyName)
            {
                case nameof(PowerSupplyController.VoltageReadback):
                    OnPropertyChanged(nameof(Vread));
                    OnPropertyChanged(nameof(VoltageDisplay));
                    OnPropertyChanged(nameof(PowerDisplay));
                    break;

                case nameof(PowerSupplyController.CurrentReadback):
                    OnPropertyChanged(nameof(Iread));
                    OnPropertyChanged(nameof(CurrentDisplay));
                    OnPropertyChanged(nameof(PowerDisplay));
                    break;

                case nameof(PowerSupplyController.Mode):
                    OnPropertyChanged(nameof(Mode));
                    OnPropertyChanged(nameof(DisplayModeLabel));
                    OnPropertyChanged(nameof(ModeBrush));
                    break;

                case nameof(PowerSupplyController.IsOutputOn):
                    OnPropertyChanged(nameof(IsOutputOn));
                    break;
            }
        }

        private static bool TryParseSetpoint(string text, out double value)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
                   double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private async Task SetCurrentAsync()
        {
            if (!TryParseSetpoint(IsetText, out double value) || value < 0)
            {
                Append("Invalid current setpoint. Enter a non-negative number such as 0.100.");
                return;
            }

            if (!await Controller.SetCurrentAsync(value))
                Append("Current command was not sent. Check the connection.");
        }

        private async Task SetVoltageAsync()
        {
            if (!TryParseSetpoint(VsetText, out double value) || value < 0)
            {
                Append("Invalid voltage setpoint. Enter a non-negative number such as 12.000.");
                return;
            }

            if (!await Controller.SetVoltageAsync(value))
                Append("Voltage command was not sent. Check the connection.");
        }

        private void LogFrameIfEnabled(string frame)
        {
            if (!LoggingEnabled) return;

            try
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "spps2001d_log.csv");
                string line = string.Format(
                    "{0:O},{1:F3},{2:F3},{3},{4}\r\n",
                    DateTime.Now,
                    Vread,
                    Iread,
                    Mode,
                    frame);

                File.AppendAllText(path, line);
            }
            catch (Exception ex)
            {
                Append("Log write error: " + ex.Message);
            }
        }

        private static void RunOnUi(Action action)
        {
            if (Application.Current?.Dispatcher == null || Application.Current.Dispatcher.CheckAccess())
                action();
            else
                Application.Current.Dispatcher.BeginInvoke(action);
        }

        private void Append(string line)
        {
            Log += $"[{DateTime.Now:HH:mm:ss}] {line}\r\n";
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
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
