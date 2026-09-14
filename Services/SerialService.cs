using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO.Ports;
using System.Linq;
using System.Management;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SPPS2001D_Pro.Services
{
    public class SerialPortDevice : INotifyPropertyChanged
    {
        public string PortName { get; set; } = "";

        private string _displayName = "";
        public string DisplayName
        {
            get => _displayName;
            set
            {
                if (_displayName != value)
                {
                    _displayName = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
                }
            }
        }

        public override string ToString() => DisplayName;
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public class SerialService : INotifyPropertyChanged, IDisposable
    {
        public ObservableCollection<SerialPortDevice> Ports { get; } = new();
        public ObservableCollection<int> BaudRates { get; } = new() { 1200, 2400, 4800, 9600, 19200 };

        private SerialPortDevice? _selectedPort;
        public SerialPortDevice? SelectedPort
        {
            get => _selectedPort;
            set
            {
                if (_selectedPort != value)
                {
                    _selectedPort = value;
                    OnPropertyChanged();
                }
            }
        }

        public int SelectedBaud { get; set; } = 9600;

        private string _status = "Disconnected";
        public string Status
        {
            get => _status;
            private set
            {
                if (_status != value)
                {
                    _status = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsOpen => _port?.IsOpen == true;
        public event Action<string>? FrameReceived;

        private SerialPort? _port;
        private readonly StringBuilder _rx = new();
        private readonly object _rxSync = new();
        private readonly SemaphoreSlim _portLock = new(1, 1);

        public void RefreshPorts()
        {
            string? currentPortName = SelectedPort?.PortName;

            Ports.Clear();

            foreach (var portName in SerialPort.GetPortNames().OrderBy(x => x))
            {
                Ports.Add(new SerialPortDevice
                {
                    PortName = portName,
                    DisplayName = portName
                });
            }

            if (!string.IsNullOrWhiteSpace(currentPortName))
                SelectedPort = Ports.FirstOrDefault(p => p.PortName == currentPortName);

            SelectedPort ??= Ports.FirstOrDefault();

            OnPropertyChanged(nameof(Ports));
            OnPropertyChanged(nameof(SelectedPort));
        }

        public async Task RefreshPortsAsync()
        {
            RefreshPorts();

            Dictionary<string, string> friendlyMap;
            try
            {
                friendlyMap = await Task.Run(GetPortFriendlyNames);
            }
            catch
            {
                return;
            }

            string? previousSelected = SelectedPort?.PortName;

            foreach (var port in Ports.ToList())
            {
                if (friendlyMap.TryGetValue(port.PortName, out var friendlyName))
                {
                    if (IsSystemDeviceName(friendlyName))
                    {
                        Ports.Remove(port);
                        continue;
                    }

                    port.DisplayName = friendlyName;
                }
            }

            var preferred = Ports.FirstOrDefault(p => IsPreferredUsbSerialName(p.DisplayName))
                         ?? Ports.FirstOrDefault(p => p.PortName == previousSelected)
                         ?? Ports.FirstOrDefault();

            SelectedPort = preferred;

            OnPropertyChanged(nameof(Ports));
            OnPropertyChanged(nameof(SelectedPort));
        }

        private static Dictionary<string, string> GetPortFriendlyNames()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");

                foreach (ManagementObject device in searcher.Get())
                {
                    string? name = device["Name"]?.ToString();
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    var match = Regex.Match(name, @"\((COM\d+)\)", RegexOptions.IgnoreCase);
                    if (match.Success)
                        map[match.Groups[1].Value] = name;
                }
            }
            catch { }

            return map;
        }

        private static bool IsSystemDeviceName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;

            string n = name.ToLowerInvariant();

            return
                (n.Contains("intel") && (n.Contains("amt") || n.Contains("active management") || n.Contains("sol"))) ||
                n.Contains("serial-over-lan") ||
                n.Contains("serial over lan") ||
                n.Contains("bios") ||
                n.Contains("management engine") ||
                n.Contains("active management technology") ||
                n.Contains("system management") ||
                n.Contains("motherboard") ||
                n.Contains("baseboard");
        }

        private static bool IsPreferredUsbSerialName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;

            string n = name.ToLowerInvariant();

            if (IsSystemDeviceName(name)) return false;

            return
                n.Contains("usb") ||
                n.Contains("uart") ||
                n.Contains("cp210") ||
                n.Contains("ch340") ||
                n.Contains("ch341") ||
                n.Contains("ftdi") ||
                n.Contains("prolific") ||
                n.Contains("silicon labs") ||
                n.Contains("usb-serial") ||
                n.Contains("usb serial");
        }

        public bool Open() => OpenAsync().GetAwaiter().GetResult();

        public async Task<bool> OpenAsync()
        {
            await _portLock.WaitAsync();
            try
            {
                if (IsOpen)
                {
                    Status = $"Already connected {_port?.PortName} @ {SelectedBaud}";
                    return true;
                }

                if (SelectedPort == null || string.IsNullOrWhiteSpace(SelectedPort.PortName))
                {
                    Status = "No COM port selected";
                    return false;
                }

                HardCloseInternal();

                string portName = SelectedPort.PortName;

                for (int attempt = 1; attempt <= 2; attempt++)
                {
                    try
                    {
                        _rx.Clear();

                        _port = new SerialPort(portName, SelectedBaud, Parity.None, 8, StopBits.One)
                        {
                            Handshake = Handshake.None,
                            DtrEnable = false,
                            RtsEnable = false,
                            ReadTimeout = 500,
                            WriteTimeout = 500,
                            Encoding = Encoding.ASCII
                        };

                        _port.DataReceived += Port_DataReceived;
                        _port.Open();

                        try { _port.DiscardInBuffer(); _port.DiscardOutBuffer(); } catch { }

                        Status = $"Connected {portName} @ {SelectedBaud}";
                        OnPropertyChanged(nameof(IsOpen));
                        return true;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        HardCloseInternal();

                        if (attempt == 1)
                        {
                            await Task.Delay(350);
                            continue;
                        }

                        Status = $"Open failed: Access to {portName} is denied. Close other serial apps, unplug/replug USB, or power-cycle the PSU if LOCK stays on.";
                        OnPropertyChanged(nameof(IsOpen));
                        return false;
                    }
                    catch (Exception ex)
                    {
                        HardCloseInternal();

                        if (attempt == 1)
                        {
                            await Task.Delay(350);
                            continue;
                        }

                        Status = "Open failed: " + ex.Message;
                        OnPropertyChanged(nameof(IsOpen));
                        return false;
                    }
                }

                return false;
            }
            finally
            {
                _portLock.Release();
            }
        }

        public void Close()
        {
            _portLock.Wait();
            try
            {
                HardCloseInternal();
                Status = "Disconnected";
                OnPropertyChanged(nameof(IsOpen));
            }
            finally
            {
                _portLock.Release();
            }
        }

        public async Task SafeDisconnectAsync()
        {
            await _portLock.WaitAsync();
            try
            {
                if (_port != null && _port.IsOpen)
                {
                    try
                    {
                        await SendInternalNoLockAsync("08000000000");
                        await Task.Delay(30);
                        await SendInternalNoLockAsync("09200000000");
                        await Task.Delay(80);
                    }
                    catch { }
                }

                HardCloseInternal();
                Status = "Disconnected";
                OnPropertyChanged(nameof(IsOpen));
            }
            finally
            {
                _portLock.Release();
            }
        }

        private void HardCloseInternal()
        {
            try
            {
                if (_port != null)
                {
                    try { _port.DataReceived -= Port_DataReceived; } catch { }

                    if (_port.IsOpen)
                    {
                        try { _port.DiscardInBuffer(); } catch { }
                        try { _port.DiscardOutBuffer(); } catch { }
                        try { _port.Close(); } catch { }
                    }

                    try { _port.Dispose(); } catch { }
                }
            }
            finally
            {
                _port = null;
                _rx.Clear();
            }
        }

        private void Port_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                var sp = (SerialPort)sender;
                var frames = new List<string>();

                lock (_rxSync)
                {
                    _rx.Append(sp.ReadExisting());

                    while (true)
                    {
                        string buf = _rx.ToString();
                        int start = buf.IndexOf('<');

                        // Drop noise before the start delimiter and cap the buffer if the
                        // device ever sends an unterminated/corrupt stream.
                        if (start < 0)
                        {
                            if (_rx.Length > 256) _rx.Clear();
                            break;
                        }
                        if (start > 0)
                        {
                            _rx.Remove(0, start);
                            buf = _rx.ToString();
                            start = 0;
                        }

                        int end = buf.IndexOf('>', start + 1);
                        if (end <= start)
                        {
                            if (_rx.Length > 256)
                                _rx.Remove(0, _rx.Length - 256);
                            break;
                        }

                        frames.Add(buf.Substring(start, end - start + 1));
                        _rx.Remove(0, end + 1);
                    }
                }

                // Invoke outside the RX lock so UI/event subscribers cannot block parsing.
                foreach (string frame in frames)
                    FrameReceived?.Invoke(frame);
            }
            catch (Exception ex)
            {
                Status = "Receive error: " + ex.Message;
            }
        }

        private async Task<bool> SendAsync(string payload11)
        {
            await _portLock.WaitAsync();
            try
            {
                return await SendInternalNoLockAsync(payload11);
            }
            finally
            {
                _portLock.Release();
            }
        }

        private async Task<bool> SendInternalNoLockAsync(string payload11)
        {
            if (_port == null || !_port.IsOpen || payload11.Length != 11) return false;

            await Task.Delay(6);
            _port.Write("<" + payload11 + ">");
            return true;
        }

        public Task<bool> SendRemoteAsync() => SendAsync("09100000000");
        public Task<bool> SendLocalAsync() => SendAsync("09200000000");
        public Task<bool> OutputOnAsync() => SendAsync("07000000000");
        public Task<bool> OutputOffAsync() => SendAsync("08000000000");
        public Task<bool> QueryCurrent033Async() => SendAsync("04003300000");
        public Task<bool> QueryVoltage122Async() => SendAsync("02012200000");
        public Task<bool> QueryCurrent000Async() => SendAsync("04000000000");
        public Task<bool> QueryVoltage000Async() => SendAsync("02000000000");
        public Task<bool> SendSetVoltageAsync(string sixDigits) => SendAsync("01" + sixDigits + "000");
        public Task<bool> SendSetCurrentAsync(string sixDigits) => SendAsync("03" + sixDigits + "000");

        public void Dispose()
        {
            HardCloseInternal();
            _portLock.Dispose();
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
