#include <windows.h>
#include <shobjidl.h>
#include <shlobj.h>
#include <wrl.h>
#include <vector>
#include <iostream>

// Invokes the registered packaged command, not a stand-in launcher. Run in a disposable VM.
int wmain(int argc, wchar_t** argv) {
    if (argc < 3) { std::wcerr << L"ExplorerProbe CLSID file [file ...]\n"; return 2; }
    HRESULT result = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(result)) return 3;
    GUID clsid{}; result = CLSIDFromString(argv[1], &clsid);
    Microsoft::WRL::ComPtr<IExplorerCommand> command;
    if (SUCCEEDED(result)) result = CoCreateInstance(clsid, nullptr, CLSCTX_LOCAL_SERVER, IID_PPV_ARGS(&command));
    std::vector<PIDLIST_ABSOLUTE> ids;
    for (int i = 2; SUCCEEDED(result) && i < argc; ++i) {
        PIDLIST_ABSOLUTE id = nullptr; result = SHParseDisplayName(argv[i], nullptr, &id, 0, nullptr); if (SUCCEEDED(result)) ids.push_back(id);
    }
    Microsoft::WRL::ComPtr<IShellItemArray> items;
    if (SUCCEEDED(result)) result = SHCreateShellItemArrayFromIDLists(static_cast<UINT>(ids.size()), const_cast<PCIDLIST_ABSOLUTE*>(ids.data()), &items);
    EXPCMDSTATE state = ECS_HIDDEN;
    if (SUCCEEDED(result)) result = command->GetState(items.Get(), FALSE, &state);
    if (SUCCEEDED(result) && state != ECS_ENABLED) result = E_FAIL;
    if (SUCCEEDED(result)) result = command->Invoke(items.Get(), nullptr);
    for (auto id : ids) CoTaskMemFree(id);
    items.Reset(); command.Reset(); CoUninitialize();
    std::wcout << L"Result: 0x" << std::hex << result << L"\n";
    return SUCCEEDED(result) ? 0 : 1;
}
