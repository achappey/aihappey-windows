#include <Windows.h>
#include <KnownFolders.h>
#include <ShlObj.h>
#include <windows.h>
#include <appmodel.h>
#include <shobjidl.h>
#include <shlwapi.h>
#include <sddl.h>
#include <aclapi.h>
#include <wrl.h>
#include <wrl/module.h>
#include <string>
#include <vector>
#include <stdexcept>
#include <cwctype>
#include "IntegrationConfig.h"

extern "C" HRESULT __stdcall DllCanUnloadNow();
using namespace Microsoft::WRL;
namespace {
constexpr size_t MaxPaths = 1024, MaxBytes = 1024 * 1024;
void Check(HRESULT hr) { if (FAILED(hr)) throw hr; }
void WinCheck(bool ok) { if (!ok) Check(HRESULT_FROM_WIN32(GetLastError())); }
struct Handle { HANDLE value = INVALID_HANDLE_VALUE; ~Handle() { if (value != INVALID_HANDLE_VALUE) CloseHandle(value); } };
struct Memory { void* value = nullptr; ~Memory() { if (value) LocalFree(value); } };
struct CoMemory { wchar_t* value = nullptr; ~CoMemory() { CoTaskMemFree(value); } };
bool Dutch() { wchar_t language[LOCALE_NAME_MAX_LENGTH]{}; GetUserDefaultLocaleName(language, LOCALE_NAME_MAX_LENGTH); return wcsncmp(language, L"nl", 2) == 0; }
std::wstring Family() {
    UINT32 length = 0;
    const auto result = GetCurrentPackageFamilyName(&length, nullptr);
    if (result != ERROR_INSUFFICIENT_BUFFER) throw HRESULT_FROM_WIN32(result);
    std::wstring family(length, L'\0');
    Check(HRESULT_FROM_WIN32(GetCurrentPackageFamilyName(&length, family.data())));
    family.resize(length - 1); return family;
}
std::wstring Json(const std::wstring& value) {
    std::wstring result = L"\"";
    for (auto c : value) {
        if (c == L'"' || c == L'\\') { result += L'\\'; result += c; }
        else if (c < 32) { wchar_t escape[7]{}; swprintf_s(escape, L"\\u%04x", static_cast<unsigned>(c)); result += escape; }
        else result += c;
    }
    return result + L'"';
}
std::string Utf8(const std::wstring& value) {
    const auto length = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
    WinCheck(length > 0); std::string result(static_cast<size_t>(length), '\0');
    WinCheck(WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), result.data(), length, nullptr, nullptr) != 0);
    return result;
}
std::wstring UserSid() {
    Handle token; WinCheck(OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token.value) != 0);
    DWORD size = 0; GetTokenInformation(token.value, TokenUser, nullptr, 0, &size);
    std::vector<BYTE> buffer(size); WinCheck(GetTokenInformation(token.value, TokenUser, buffer.data(), size, &size) != 0);
    Memory text; WinCheck(ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(buffer.data())->User.Sid, reinterpret_cast<LPWSTR*>(&text.value)) != 0);
    return static_cast<wchar_t*>(text.value);
}
void CheckDirectory(const std::wstring& path, bool privateAcl = false) {
    const auto attributes = GetFileAttributesW(path.c_str());
    if (attributes == INVALID_FILE_ATTRIBUTES || !(attributes & FILE_ATTRIBUTE_DIRECTORY) || (attributes & FILE_ATTRIBUTE_REPARSE_POINT)) throw E_ACCESSDENIED;
    if (!privateAcl) return;
    PSID owner = nullptr; PACL dacl = nullptr; Memory descriptor;
    Check(HRESULT_FROM_WIN32(GetNamedSecurityInfoW(path.c_str(), SE_FILE_OBJECT, OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION,
        &owner, nullptr, &dacl, nullptr, reinterpret_cast<PSECURITY_DESCRIPTOR*>(&descriptor.value))));
    Memory current; WinCheck(ConvertStringSidToSidW(UserSid().c_str(), &current.value) != 0);
    if (!owner || !EqualSid(owner, current.value) || !dacl) throw E_ACCESSDENIED;
    BYTE system[SECURITY_MAX_SID_SIZE]{}; DWORD size = sizeof(system); WinCheck(CreateWellKnownSid(WinLocalSystemSid, nullptr, system, &size) != 0);
    for (DWORD i = 0; i < dacl->AceCount; ++i) {
        void* ace = nullptr; WinCheck(GetAce(dacl, i, &ace) != 0);
        const auto header = static_cast<ACE_HEADER*>(ace);
        if (header->AceType == ACCESS_ALLOWED_ACE_TYPE) {
            const auto sid = &static_cast<ACCESS_ALLOWED_ACE*>(ace)->SidStart;
            if (!EqualSid(sid, current.value) && !EqualSid(sid, system)) throw E_ACCESSDENIED;
        } else if (header->AceType != ACCESS_DENIED_ACE_TYPE) throw E_ACCESSDENIED;
    }
}
std::wstring Inbox(const std::wstring& family) {
    CoMemory root; Check(SHGetKnownFolderPath(FOLDERID_LocalAppData, KF_FLAG_DEFAULT, nullptr, &root.value));
    std::wstring path = root.value;
    // Validate ancestors before entering package-local storage. Never create a missing package root.
    for (auto ancestor = path; ancestor.size() > 3;) {
        CheckDirectory(ancestor); const auto end = ancestor.find_last_of(L"\\/"); if (end == std::wstring::npos) break; ancestor.resize(end);
    }
    path += L"\\Packages"; CheckDirectory(path);
    path += L"\\" + family; CheckDirectory(path);
    path += L"\\LocalState";
    if (!CreateDirectoryW(path.c_str(), nullptr) && GetLastError() != ERROR_ALREADY_EXISTS) throw HRESULT_FROM_WIN32(GetLastError());
    CheckDirectory(path); path += L"\\ShellRequests";
    const auto sddl = L"O:" + UserSid() + L"D:P(A;OICI;FA;;;" + UserSid() + L")(A;OICI;FA;;;SY)";
    Memory security; WinCheck(ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.c_str(), SDDL_REVISION_1,
        reinterpret_cast<PSECURITY_DESCRIPTOR*>(&security.value), nullptr) != 0);
    SECURITY_ATTRIBUTES attributes{ sizeof(attributes), security.value, FALSE };
    if (!CreateDirectoryW(path.c_str(), &attributes) && GetLastError() != ERROR_ALREADY_EXISTS) throw HRESULT_FROM_WIN32(GetLastError());
    CheckDirectory(path, true); return path;
}
bool Eligible(IShellItem* item) {
    SFGAOF attributes = 0;
    return SUCCEEDED(item->GetAttributes(SFGAO_FILESYSTEM | SFGAO_FOLDER, &attributes))
        && (attributes & SFGAO_FILESYSTEM) && !(attributes & SFGAO_FOLDER);
}
void Deliver(IShellItemArray* items) {
    if (!items) throw E_INVALIDARG;
    DWORD count = 0; Check(items->GetCount(&count)); if (count == 0 || count > MaxPaths) throw E_INVALIDARG;
    const auto family = Family();
    GUID guid{}; Check(CoCreateGuid(&guid)); wchar_t text[40]{}; StringFromGUID2(guid, text, 40);
    std::wstring id; for (auto c : text) if (iswxdigit(c)) id += static_cast<wchar_t>(towlower(c));
    FILETIME now{}; GetSystemTimeAsFileTime(&now); ULARGE_INTEGER time{}; time.LowPart = now.dwLowDateTime; time.HighPart = now.dwHighDateTime;
    std::wstring json = L"{\"version\":1,\"id\":" + Json(id) + L",\"createdUnixSeconds\":"
        + std::to_wstring((time.QuadPart - 116444736000000000ULL) / 10000000ULL) + L",\"host\":" + Json(HOST_NAME)
        + L",\"packageFamily\":" + Json(family) + L",\"paths\":[";
    for (DWORD i = 0; i < count; ++i) {
        ComPtr<IShellItem> item; Check(items->GetItemAt(i, &item)); if (!Eligible(item.Get())) throw E_INVALIDARG;
        CoMemory name; Check(item->GetDisplayName(SIGDN_FILESYSPATH, &name.value));
        if (i) json += L','; json += Json(name.value);
        if (json.size() > MaxBytes) throw E_INVALIDARG;
    }
    const auto bytes = Utf8(json + L"]}"); if (bytes.size() > MaxBytes) throw E_INVALIDARG;
    const auto inbox = Inbox(family), temporary = inbox + L"\\" + id + L".tmp", request = inbox + L"\\" + id + L".json";
    {
        Handle file; file.value = CreateFileW(temporary.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
        WinCheck(file.value != INVALID_HANDLE_VALUE); DWORD written = 0;
        WinCheck(WriteFile(file.value, bytes.data(), static_cast<DWORD>(bytes.size()), &written, nullptr) != 0 && written == bytes.size());
        WinCheck(FlushFileBuffers(file.value) != 0);
    }
    if (!MoveFileExW(temporary.c_str(), request.c_str(), MOVEFILE_WRITE_THROUGH)) { const auto error = GetLastError(); DeleteFileW(temporary.c_str()); throw HRESULT_FROM_WIN32(error); }
    try {
        ComPtr<IApplicationActivationManager> activation; Check(CoCreateInstance(CLSID_ApplicationActivationManager, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&activation)));
        DWORD process = 0; Check(activation->ActivateApplication((family + L"!App").c_str(), (L"--shell-request " + id).c_str(), AO_NONE, &process));
    } catch (...) { DeleteFileW(request.c_str()); throw; }
}
}

class __declspec(uuid(COMMAND_CLSID)) ExplorerCommand final : public RuntimeClass<RuntimeClassFlags<ClassicCom>, IExplorerCommand> {
public:
    IFACEMETHODIMP GetTitle(IShellItemArray*, LPWSTR* title) override {
        return SHStrDupW(L"aihappey", title);
    }
    IFACEMETHODIMP GetIcon(IShellItemArray*, LPWSTR* icon) override {
        HMODULE module = nullptr; wchar_t path[32768]{};
        if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            reinterpret_cast<LPCWSTR>(&DllCanUnloadNow), &module)) return HRESULT_FROM_WIN32(GetLastError());
        if (!GetModuleFileNameW(module, path, 32768)) return HRESULT_FROM_WIN32(GetLastError());
        const std::wstring location(path); const auto end = location.find_last_of(L'\\');
        return SHStrDupW((location.substr(0, end) + L"\\Assets\\AppIcon.ico").c_str(), icon);
    }
    IFACEMETHODIMP GetToolTip(IShellItemArray*, LPWSTR* tooltip) override {
        return SHStrDupW(Dutch() ? L"Bestanden toevoegen aan de chat. Niet automatisch verzenden." : L"Attach files to the chat draft. Do not send automatically.", tooltip);
    }
    IFACEMETHODIMP GetCanonicalName(GUID* name) override { if (!name) return E_POINTER; *name = __uuidof(ExplorerCommand); return S_OK; }
    IFACEMETHODIMP GetState(IShellItemArray* items, BOOL, EXPCMDSTATE* state) override {
        if (!state) return E_POINTER; *state = ECS_HIDDEN;
        if (!items) return S_OK; DWORD count = 0; if (FAILED(items->GetCount(&count)) || !count || count > MaxPaths) return S_OK;
        for (DWORD i = 0; i < count; ++i) { ComPtr<IShellItem> item; if (FAILED(items->GetItemAt(i, &item)) || !Eligible(item.Get())) return S_OK; }
        *state = ECS_ENABLED; return S_OK;
    }
    IFACEMETHODIMP Invoke(IShellItemArray* items, IBindCtx*) override {
        try { Deliver(items); return S_OK; }
        catch (...) {
            MessageBoxW(nullptr, Dutch() ? L"De bestanden konden niet aan de chat worden toegevoegd. Er is niets verzonden. Probeer het opnieuw."
                : L"The files could not be attached to the chat. Nothing was sent. Please try again.", APP_NAME, MB_OK | MB_ICONWARNING);
            return E_FAIL;
        }
    }
    IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) override { if (!flags) return E_POINTER; *flags = ECF_DEFAULT; return S_OK; }
    IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** commands) override { if (commands) *commands = nullptr; return E_NOTIMPL; }
};
CoCreatableClass(ExplorerCommand);
extern "C" HRESULT __stdcall DllGetClassObject(REFCLSID clsid, REFIID iid, void** result) { return Module<InProc>::GetModule().GetClassObject(clsid, iid, result); }
extern "C" HRESULT __stdcall DllCanUnloadNow() { return Module<InProc>::GetModule().GetObjectCount() == 0 ? S_OK : S_FALSE; }
BOOL WINAPI DllMain(HINSTANCE module, DWORD reason, LPVOID) { if (reason == DLL_PROCESS_ATTACH) DisableThreadLibraryCalls(module); return TRUE; }
