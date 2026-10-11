using System.IO.Ports;
using SerialBridge.App.ViewModels;
using SerialBridge.Core.Models;

// 回归测试：编辑映射时必须完整加载已保存配置（此前构造顺序 bug 导致全为默认值）
var saved = new MappingConfig
{
    Name = "海思",
    PortName = "COM7",
    BaudRate = 9600,
    DataBits = 7,
    Parity = Parity.Even,
    StopBits = StopBits.Two,
    FlowControl = Handshake.RequestToSend,
    ListenIp = "192.168.1.5",
    ListenPort = 9100,
    ClientMode = ClientMode.Multi,
    MaxClients = 5,
    AutoReconnect = false,
    ReconnectIntervalMs = 7000,
    AutoStart = true,
};
var editVm = new MappingEditViewModel(saved);
bool editLoaded =
    editVm.Name == "海思" &&
    editVm.PortName == "COM7" &&
    editVm.BaudRate == 9600 &&
    editVm.DataBits == 7 &&
    editVm.Parity == Parity.Even &&
    editVm.StopBits == StopBits.Two &&
    editVm.FlowControl == Handshake.RequestToSend &&
    editVm.ListenIp == "192.168.1.5" &&
    editVm.ListenPortText == "9100" &&
    editVm.IsSingleClient == false &&
    editVm.MaxClients == 5 &&
    editVm.AutoReconnect == false &&
    editVm.ReconnectSecondsText == "7" &&
    editVm.AutoStart == true;
Console.WriteLine($"编辑加载回归: {(editLoaded ? "PASS" : "FAIL")}" +
    (editLoaded ? "" : $" 实际值: name={editVm.Name} port={editVm.PortName} baud={editVm.BaudRateText} mode={editVm.IsSingleClient} ip={editVm.ListenIp} port2={editVm.ListenPortText} max={editVm.MaxClients} ar={editVm.AutoReconnect} ri={editVm.ReconnectSecondsText} as={editVm.AutoStart}"));
if (!editLoaded) return 1;

// 验证：切换单/多客户端模式时 ClientModeIsSingle 必须发出 PropertyChanged 通知，
// 否则 XAML 的 Visibility/IsEnabled 绑定不会刷新（即“最大客户端数量不出现”的根因）。
var vm = new MappingEditViewModel(null);
string? lastNotification = null;
vm.PropertyChanged += (_, e) => lastNotification = e.PropertyName;

bool fail = false;
var before = vm.ClientModeIsSingle;                       // 应为 true（默认单客户端）
vm.SetClientModeMultiCommand.Execute(null);
var after = vm.ClientModeIsSingle;                        // 应为 false
var notifiedOnSwitch = lastNotification == nameof(MappingEditViewModel.ClientModeIsSingle);

Console.WriteLine($"before={before}, after={after}, notifiedOnSwitch={notifiedOnSwitch}");
if (!before || after || !notifiedOnSwitch) fail = true;

lastNotification = null;
vm.SetClientModeSingleCommand.Execute(null);
var back = vm.ClientModeIsSingle;
var notifiedOnBack = lastNotification == nameof(MappingEditViewModel.ClientModeIsSingle);
Console.WriteLine($"back={back}, notifiedOnBack={notifiedOnBack}");
if (!back || !notifiedOnBack) fail = true;

Console.WriteLine(fail ? "FAIL" : "PASS: ClientModeIsSingle 通知链路正常，绑定可刷新");
return fail ? 1 : 0;
