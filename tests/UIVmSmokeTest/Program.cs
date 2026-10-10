using SerialBridge.App.ViewModels;

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
