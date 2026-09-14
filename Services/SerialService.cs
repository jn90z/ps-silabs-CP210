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
        public string DisplayName { get => _displayName; set { if (_displayName != value) { _displayName = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName))); } } }
        public override string ToString() => DisplayName;
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    public class SerialService : INotifyPropertyChanged, IDisposable
    {
        public ObservableCollection<SerialPortDevice> Ports { get; } = new();
        public ObservableCollection<int> BaudRates { get; } = new() { 1200, 2400, 4800, 9600, 19200 };
        private SerialPortDevice? _selectedPort;
        public SerialPortDevice? SelectedPort { get => _selectedPort; set { if (_selectedPort != value) { _selectedPort = value; OnPropertyChanged(); } } }
        public int SelectedBaud { get; set; } = 9600;
        private string _status = "Disconnected";
        public string Status { get => _status; private set { if (_status != value) { _status = value; OnPropertyChanged(); } } }
        public bool IsOpen => _port?.IsOpen == true;
        public event Action<string>? FrameReceived;
        private SerialPort? _port;
        private readonly StringBuilder _rx = new();
        private readonly SemaphoreSlim _portLock = new(1, 1);
        public void RefreshPorts() { string? current = SelectedPort?.PortName; Ports.Clear(); foreach (var n in SerialPort.GetPortNames().OrderBy(x => x)) Ports.Add(new SerialPortDevice { PortName=n, DisplayName=n }); if (!string.IsNullOrWhiteSpace(current)) SelectedPort=Ports.FirstOrDefault(p=>p.PortName==current); SelectedPort ??= Ports.FirstOrDefault(); OnPropertyChanged(nameof(Ports)); OnPropertyChanged(nameof(SelectedPort)); }
        public async Task RefreshPortsAsync() { RefreshPorts(); Dictionary<string,string> map; try { map=await Task.Run(GetPortFriendlyNames); } catch { return; } string? prev=SelectedPort?.PortName; foreach(var p in Ports.ToList()) if(map.TryGetValue(p.PortName,out var f)){ if(IsSystemDeviceName(f)){Ports.Remove(p);continue;} p.DisplayName=f;} SelectedPort=Ports.FirstOrDefault(p=>IsPreferredUsbSerialName(p.DisplayName))??Ports.FirstOrDefault(p=>p.PortName==prev)??Ports.FirstOrDefault(); OnPropertyChanged(nameof(Ports)); OnPropertyChanged(nameof(SelectedPort)); }
        private static Dictionary<string,string> GetPortFriendlyNames(){var map=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);try{using var s=new ManagementObjectSearcher("SELECT Name FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");foreach(ManagementObject d in s.Get()){string? n=d["Name"]?.ToString();if(string.IsNullOrWhiteSpace(n))continue;var m=Regex.Match(n,@"\((COM\d+)\)",RegexOptions.IgnoreCase);if(m.Success)map[m.Groups[1].Value]=n;}}catch{}return map;}
        private static bool IsSystemDeviceName(string name){if(string.IsNullOrWhiteSpace(name))return false;string n=name.ToLowerInvariant();return (n.Contains("intel")&&(n.Contains("amt")||n.Contains("active management")||n.Contains("sol")))||n.Contains("serial-over-lan")||n.Contains("serial over lan")||n.Contains("bios")||n.Contains("management engine")||n.Contains("active management technology")||n.Contains("system management")||n.Contains("motherboard")||n.Contains("baseboard");}
        private static bool IsPreferredUsbSerialName(string name){if(string.IsNullOrWhiteSpace(name)||IsSystemDeviceName(name))return false;string n=name.ToLowerInvariant();return n.Contains("usb")||n.Contains("uart")||n.Contains("cp210")||n.Contains("ch340")||n.Contains("ch341")||n.Contains("ftdi")||n.Contains("prolific")||n.Contains("silicon labs")||n.Contains("usb-serial")||n.Contains("usb serial");}
        public bool Open()=>OpenAsync().GetAwaiter().GetResult();
        public async Task<bool> OpenAsync(){await _portLock.WaitAsync();try{if(IsOpen){Status=$"Already connected {_port?.PortName} @ {SelectedBaud}";return true;}if(SelectedPort==null||string.IsNullOrWhiteSpace(SelectedPort.PortName)){Status="No COM port selected";return false;}HardCloseInternal();string portName=SelectedPort.PortName;for(int attempt=1;attempt<=2;attempt++){try{_rx.Clear();_port=new SerialPort(portName,SelectedBaud,Parity.None,8,StopBits.One){Handshake=Handshake.None,DtrEnable=false,RtsEnable=false,ReadTimeout=500,WriteTimeout=500,Encoding=Encoding.ASCII};_port.DataReceived+=Port_DataReceived;_port.Open();try{_port.DiscardInBuffer();_port.DiscardOutBuffer();}catch{}Status=$"Connected {portName} @ {SelectedBaud}";OnPropertyChanged(nameof(IsOpen));return true;}catch(UnauthorizedAccessException){HardCloseInternal();if(attempt==1){await Task.Delay(350);continue;}Status=$"Open failed: Access to {portName} is denied. Close other serial apps, unplug/replug USB, or power-cycle the PSU if LOCK stays on.";OnPropertyChanged(nameof(IsOpen));return false;}catch(Exception ex){HardCloseInternal();if(attempt==1){await Task.Delay(350);continue;}Status="Open failed: "+ex.Message;OnPropertyChanged(nameof(IsOpen));return false;}}return false;}finally{_portLock.Release();}}
        public void Close(){_portLock.Wait();try{HardCloseInternal();Status="Disconnected";OnPropertyChanged(nameof(IsOpen));}finally{_portLock.Release();}}
        public async Task SafeDisconnectAsync(){await _portLock.WaitAsync();try{if(_port!=null&&_port.IsOpen){try{await SendInternalNoLockAsync("08000000000");await Task.Delay(30);await SendInternalNoLockAsync("09200000000");await Task.Delay(80);}catch{}}HardCloseInternal();Status="Disconnected";OnPropertyChanged(nameof(IsOpen));}finally{_portLock.Release();}}
        private void HardCloseInternal(){try{if(_port!=null){try{_port.DataReceived-=Port_DataReceived;}catch{}if(_port.IsOpen){try{_port.DiscardInBuffer();}catch{}try{_port.DiscardOutBuffer();}catch{}try{_port.Close();}catch{}}try{_port.Dispose();}catch{}}}finally{_port=null;_rx.Clear();}}
        private void Port_DataReceived(object sender,SerialDataReceivedEventArgs e){try{var sp=(SerialPort)sender;_rx.Append(sp.ReadExisting());while(true){var buf=_rx.ToString();int a=buf.IndexOf('<'),b=buf.IndexOf('>',a+1);if(a>=0&&b>a){var frame=buf.Substring(a,b-a+1);_rx.Clear();_rx.Append(buf.Substring(b+1));FrameReceived?.Invoke(frame);}else break;}}catch{}}
        private async Task<bool> SendAsync(string p){await _portLock.WaitAsync();try{return await SendInternalNoLockAsync(p);}finally{_portLock.Release();}}
        private async Task<bool> SendInternalNoLockAsync(string p){if(_port==null||!_port.IsOpen||p.Length!=11)return false;await Task.Delay(6);_port.Write("<"+p+">");return true;}
        public Task<bool> SendRemoteAsync()=>SendAsync("09100000000"); public Task<bool> SendLocalAsync()=>SendAsync("09200000000"); public Task<bool> OutputOnAsync()=>SendAsync("07000000000"); public Task<bool> OutputOffAsync()=>SendAsync("08000000000"); public Task<bool> QueryCurrent033Async()=>SendAsync("04003300000"); public Task<bool> QueryVoltage122Async()=>SendAsync("02012200000"); public Task<bool> QueryCurrent000Async()=>SendAsync("04000000000"); public Task<bool> QueryVoltage000Async()=>SendAsync("02000000000"); public Task<bool> SendSetVoltageAsync(string d)=>SendAsync("01"+d+"000"); public Task<bool> SendSetCurrentAsync(string d)=>SendAsync("03"+d+"000");
        public void Dispose(){HardCloseInternal();_portLock.Dispose();}
        public event PropertyChangedEventHandler? PropertyChanged; private void OnPropertyChanged([CallerMemberName]string? name=null)=>PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(name));
    }
}
