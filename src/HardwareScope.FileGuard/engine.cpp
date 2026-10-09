#include "engine.h"
#include <RestartManager.h>
#include <winternl.h>
#include <bcrypt.h>
#include <algorithm>
#include <cwctype>
#include <memory>
#include <unordered_map>
#include <unordered_set>

#pragma comment(lib, "Rstrtmgr.lib")
#pragma comment(lib, "Advapi32.lib")
#pragma comment(lib, "Bcrypt.lib")

namespace fileguard {
namespace {
struct CloseHandleDeleter { void operator()(void* h) const { if (h && h != INVALID_HANDLE_VALUE) CloseHandle(h); } };
using Handle = std::unique_ptr<void, CloseHandleDeleter>;
struct ExtendedHandle {
    void* object; ULONG_PTR pid; ULONG_PTR handle; ULONG access;
    USHORT traceIndex; USHORT typeIndex; ULONG attributes; ULONG reserved;
};
struct ExtendedHandles { ULONG_PTR count; ULONG_PTR reserved; ExtendedHandle entries[1]; };
using NtQuerySystemInformationFn = LONG(NTAPI*)(ULONG, void*, ULONG, ULONG*);
constexpr LONG StatusLengthMismatch = static_cast<LONG>(0xC0000004L);
constexpr size_t MaxRows = 1024;
constexpr size_t MaxPath = 32767;

struct ProcessSecret {
    GuardIdentityKey key{};
    bool valid = BCryptGenRandom(nullptr, key.data(), static_cast<ULONG>(key.size()), BCRYPT_USE_SYSTEM_PREFERRED_RNG) >= 0;
};
const ProcessSecret& LocalSecret() { static const ProcessSecret secret; return secret; }
uint64_t ObjectIdentity(const ExtendedHandle& entry, uint64_t start, const GuardIdentityKey& key) {
    if (!entry.object) return 0; // Windows may redact raw pointers in an unprivileged snapshot.
    BCRYPT_ALG_HANDLE algorithm = nullptr; BCRYPT_HASH_HANDLE hash = nullptr;
    if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, BCRYPT_ALG_HANDLE_HMAC_FLAG) < 0) return 0;
    DWORD objectSize = 0, read = 0;
    bool okay = BCryptGetProperty(algorithm, BCRYPT_OBJECT_LENGTH, reinterpret_cast<BYTE*>(&objectSize), sizeof(objectSize), &read, 0) >= 0;
    std::vector<BYTE> object(objectSize);
    BYTE digest[32]{};
    uint64_t values[] = {reinterpret_cast<uint64_t>(entry.object), static_cast<uint64_t>(entry.pid), start, static_cast<uint64_t>(entry.handle)};
    if (okay) okay = BCryptCreateHash(algorithm, &hash, object.data(), objectSize, const_cast<BYTE*>(key.data()), static_cast<ULONG>(key.size()), 0) >= 0;
    if (okay) okay = BCryptHashData(hash, reinterpret_cast<BYTE*>(values), sizeof(values), 0) >= 0
        && BCryptFinishHash(hash, digest, sizeof(digest), 0) >= 0;
    uint64_t identity = 0;
    if (okay) memcpy(&identity, digest, sizeof(identity));
    if (hash) BCryptDestroyHash(hash);
    BCryptCloseAlgorithmProvider(algorithm, 0);
    return identity;
}

uint64_t FileTimeValue(FILETIME t) { return (uint64_t(t.dwHighDateTime) << 32) | t.dwLowDateTime; }
uint64_t StartTime(HANDLE process) {
    FILETIME created{}, ended{}, kernel{}, user{};
    return GetProcessTimes(process, &created, &ended, &kernel, &user) ? FileTimeValue(created) : 0;
}
std::wstring ErrorText(DWORD error) {
    wchar_t* raw = nullptr;
    FormatMessageW(FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS,
        nullptr, error, 0, reinterpret_cast<wchar_t*>(&raw), 0, nullptr);
    std::wstring text = raw ? raw : L"Windows 操作失败";
    if (raw) LocalFree(raw);
    while (!text.empty() && (text.back() == L'\r' || text.back() == L'\n')) text.pop_back();
    return text;
}
std::wstring CanonicalPath(HANDLE file) {
    DWORD n = GetFinalPathNameByHandleW(file, nullptr, 0, FILE_NAME_OPENED | VOLUME_NAME_DOS);
    if (!n || n > MaxPath) return {};
    std::wstring path(n, L'\0');
    DWORD read = GetFinalPathNameByHandleW(file, path.data(), n, FILE_NAME_OPENED | VOLUME_NAME_DOS);
    if (!read || read >= n) return {};
    path.resize(read);
    if (path.rfind(L"\\\\?\\UNC\\", 0) == 0) path = L"\\\\" + path.substr(8);
    else if (path.rfind(L"\\\\?\\", 0) == 0) path.erase(0, 4);
    while (path.size() > 3 && path.back() == L'\\') path.pop_back();
    return path;
}
bool SamePath(const std::wstring& a, const std::wstring& b) {
    return CompareStringOrdinal(a.c_str(), static_cast<int>(a.size()), b.c_str(), static_cast<int>(b.size()), TRUE) == CSTR_EQUAL;
}
bool Matches(const std::wstring& path, const std::wstring& target, bool directory) {
    if (SamePath(path, target)) return true;
    if (!directory || path.size() <= target.size()) return false;
    const size_t prefix = target.size();
    return CompareStringOrdinal(path.c_str(), static_cast<int>(prefix), target.c_str(), static_cast<int>(prefix), TRUE) == CSTR_EQUAL
        && (target.back() == L'\\' || path[prefix] == L'\\');
}
std::wstring ImagePath(HANDLE process) {
    std::wstring path(MaxPath, L'\0'); DWORD length = static_cast<DWORD>(path.size());
    if (!QueryFullProcessImageNameW(process, 0, path.data(), &length)) return {};
    path.resize(length); return path;
}
std::vector<BYTE> TokenSid(HANDLE token) {
    DWORD n = 0; GetTokenInformation(token, TokenUser, nullptr, 0, &n);
    if (!n) return {};
    std::vector<BYTE> data(n);
    if (!GetTokenInformation(token, TokenUser, data.data(), n, &n)) return {};
    auto sid = reinterpret_cast<TOKEN_USER*>(data.data())->User.Sid;
    std::vector<BYTE> result(GetLengthSid(sid)); CopySid(static_cast<DWORD>(result.size()), result.data(), sid); return result;
}
DWORD IntegrityLevel(HANDLE token) {
    DWORD n = 0; GetTokenInformation(token, TokenIntegrityLevel, nullptr, 0, &n);
    if (!n) return MAXDWORD;
    std::vector<BYTE> data(n);
    if (!GetTokenInformation(token, TokenIntegrityLevel, data.data(), n, &n)) return MAXDWORD;
    auto sid = reinterpret_cast<TOKEN_MANDATORY_LABEL*>(data.data())->Label.Sid;
    if (!IsValidSid(sid) || !*GetSidSubAuthorityCount(sid)) return MAXDWORD;
    return *GetSidSubAuthority(sid, *GetSidSubAuthorityCount(sid) - 1);
}
std::vector<BYTE> ProcessSid(HANDLE process, DWORD& integrity, bool& elevated) {
    HANDLE token = nullptr;
    if (!OpenProcessToken(process, TOKEN_QUERY, &token)) return {};
    Handle owned(token); integrity = IntegrityLevel(token);
    TOKEN_ELEVATION elevation{}; DWORD n = 0;
    if (!GetTokenInformation(token, TokenElevation, &elevation, sizeof(elevation), &n)) return {};
    elevated = elevation.TokenIsElevated != 0;
    return TokenSid(token);
}
struct Authorization {
    Handle ownedToken;
    HANDLE token = nullptr;
    std::vector<BYTE> sid;
    DWORD integrity = 0;
    bool admin = false;
};
Authorization Authorize(HANDLE provided) {
    Authorization auth;
    if (provided) auth.token = provided;
    else {
        HANDLE token = nullptr;
        if (OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY | TOKEN_DUPLICATE | TOKEN_IMPERSONATE, &token)) {
            auth.ownedToken.reset(token); auth.token = token;
        }
    }
    if (!auth.token) return auth;
    auth.sid = TokenSid(auth.token);
    auth.integrity = IntegrityLevel(auth.token);
    if (auth.integrity == MAXDWORD) { auth.sid.clear(); return auth; }
    TOKEN_ELEVATION elevation{}; DWORD n = 0;
    if (GetTokenInformation(auth.token, TokenElevation, &elevation, sizeof(elevation), &n) && elevation.TokenIsElevated) {
        BYTE adminSid[SECURITY_MAX_SID_SIZE]{}; DWORD size = sizeof(adminSid); BOOL member = FALSE;
        HANDLE duplicate = nullptr;
        if (CreateWellKnownSid(WinBuiltinAdministratorsSid, nullptr, adminSid, &size)
            && DuplicateToken(auth.token, SecurityImpersonation, &duplicate)) {
            Handle owned(duplicate);
            if (CheckTokenMembership(duplicate, adminSid, &member)) auth.admin = member != FALSE;
        }
    }
    return auth;
}
bool Allowed(const Authorization& auth, const std::vector<BYTE>& sid, DWORD integrity, bool elevated) {
    return !sid.empty() && (auth.admin || (!elevated && integrity <= auth.integrity && !auth.sid.empty()
        && EqualSid(const_cast<BYTE*>(auth.sid.data()), const_cast<BYTE*>(sid.data()))));
}
bool SafeProcess(HANDLE process, DWORD pid, DWORD servicePid, const std::vector<BYTE>& sid, const std::wstring& image) {
    if (pid <= 4 || pid == GetCurrentProcessId() || (servicePid && pid == servicePid)) return false;
    BOOL critical = FALSE;
    if (!IsProcessCritical(process, &critical) || critical) return false;
    if (sid.empty() || IsWellKnownSid(const_cast<BYTE*>(sid.data()), WinLocalSystemSid)
        || IsWellKnownSid(const_cast<BYTE*>(sid.data()), WinLocalServiceSid)
        || IsWellKnownSid(const_cast<BYTE*>(sid.data()), WinNetworkServiceSid)) return false;
    std::wstring name = image.substr(image.find_last_of(L"\\/") + 1);
    std::transform(name.begin(), name.end(), name.begin(), towlower);
    static const std::unordered_set<std::wstring> deny = {
        L"system", L"registry", L"smss.exe", L"csrss.exe", L"wininit.exe", L"winlogon.exe", L"lsass.exe",
        L"services.exe", L"svchost.exe", L"fontdrvhost.exe", L"dwm.exe", L"secure system", L"hardwarescope.fileguard.exe"
    };
    return !image.empty() && deny.find(name) == deny.end();
}
void EnableDebugPrivilege() {
    HANDLE raw = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY | TOKEN_ADJUST_PRIVILEGES, &raw)) return;
    Handle token(raw); TOKEN_PRIVILEGES p{}; p.PrivilegeCount = 1;
    if (!LookupPrivilegeValueW(nullptr, SE_DEBUG_NAME, &p.Privileges[0].Luid)) return;
    p.Privileges[0].Attributes = SE_PRIVILEGE_ENABLED; AdjustTokenPrivileges(token.get(), FALSE, &p, 0, nullptr, nullptr);
}
bool Snapshot(std::vector<BYTE>& data, DWORD& error) {
    auto fn = reinterpret_cast<NtQuerySystemInformationFn>(GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "NtQuerySystemInformation"));
    if (!fn) { error = ERROR_NOT_SUPPORTED; return false; }
    data.resize(1024 * 1024);
    for (;;) {
        ULONG needed = 0; LONG status = fn(64 /* SystemExtendedHandleInformation */, data.data(), static_cast<ULONG>(data.size()), &needed);
        if (status >= 0) {
            const auto all = reinterpret_cast<const ExtendedHandles*>(data.data());
            if (all->count > (data.size() - offsetof(ExtendedHandles, entries)) / sizeof(ExtendedHandle)) {
                error = ERROR_INVALID_DATA; return false;
            }
            return true;
        }
        if (status != StatusLengthMismatch || data.size() >= 256u * 1024u * 1024u) { error = ERROR_NOT_ENOUGH_MEMORY; return false; }
        data.resize(std::min<size_t>(256u * 1024u * 1024u, std::max<size_t>(data.size() * 2, size_t(needed) + 65536)));
    }
}
struct Target { Handle file; std::wstring path; bool directory = false; };
bool OpenTarget(const std::wstring& requested, const Authorization& auth, Target& target, DWORD& error) {
    if (requested.empty() || requested.size() > MaxPath || requested.find(L'\0') != std::wstring::npos || requested.rfind(L"\\\\.\\", 0) == 0) {
        error = ERROR_INVALID_PARAMETER; return false;
    }
    if (!auth.token || auth.sid.empty()) { error = ERROR_ACCESS_DENIED; return false; }
    if (!ImpersonateLoggedOnUser(auth.token)) { error = GetLastError(); return false; }
    HANDLE raw = CreateFileW(requested.c_str(), FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        nullptr, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, nullptr);
    error = raw == INVALID_HANDLE_VALUE ? GetLastError() : ERROR_SUCCESS;
    RevertToSelf();
    if (raw == INVALID_HANDLE_VALUE) return false;
    target.file.reset(raw);
    BY_HANDLE_FILE_INFORMATION info{};
    if (GetFileType(raw) != FILE_TYPE_DISK || !GetFileInformationByHandle(raw, &info)) { error = ERROR_INVALID_PARAMETER; return false; }
    target.directory = (info.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
    target.path = CanonicalPath(raw);
    if (target.path.empty()) { error = ERROR_PATH_NOT_FOUND; return false; }
    return true;
}
struct ProcessInfo {
    Handle process;
    std::vector<BYTE> sid;
    uint64_t start = 0;
    std::wstring image;
    bool safe = false;
};
ProcessInfo InspectProcess(DWORD pid, const Authorization& auth, DWORD servicePid, DWORD access) {
    ProcessInfo info;
    info.process.reset(OpenProcess(access | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid));
    if (!info.process) return info;
    DWORD integrity = MAXDWORD; bool elevated = true;
    info.sid = ProcessSid(info.process.get(), integrity, elevated);
    if (!Allowed(auth, info.sid, integrity, elevated)) { info.process.reset(); return info; }
    info.start = StartTime(info.process.get()); info.image = ImagePath(info.process.get());
    info.safe = SafeProcess(info.process.get(), pid, servicePid, info.sid, info.image);
    return info;
}
GuardRow MakeRow(DWORD pid, const ProcessInfo& process, const std::wstring& path) {
    GuardRow row; row.pid = pid; row.startTime = process.start; row.path = path; row.imagePath = process.image;
    row.processName = process.image.empty() ? L"未知应用" : process.image.substr(process.image.find_last_of(L"\\/") + 1);
    return row;
}
GuardResponse Scan(const Target& target, const Authorization& auth, DWORD servicePid, const GuardIdentityKey& key) {
    GuardResponse result; std::vector<BYTE> snapshot;
    if (!Snapshot(snapshot, result.error)) { result.message = ErrorText(result.error); return result; }
    auto all = reinterpret_cast<const ExtendedHandles*>(snapshot.data());
    USHORT fileType = 0;
    for (ULONG_PTR i = 0; i < all->count; ++i) {
        const auto& entry = all->entries[i];
        if (entry.pid == GetCurrentProcessId() && entry.handle == reinterpret_cast<ULONG_PTR>(target.file.get())) { fileType = entry.typeIndex; break; }
    }
    if (!fileType) { result.error = ERROR_NOT_SUPPORTED; result.message = L"无法确定文件句柄类型。"; return result; }
    std::unordered_map<DWORD, ProcessInfo> processes;
    std::unordered_set<DWORD> found;
    for (ULONG_PTR i = 0; i < all->count; ++i) {
        const auto& entry = all->entries[i];
        if (entry.typeIndex != fileType || entry.pid <= 4 || entry.pid > MAXDWORD || entry.pid == GetCurrentProcessId() || (servicePid && entry.pid == servicePid)) continue;
        DWORD pid = static_cast<DWORD>(entry.pid);
        auto it = processes.find(pid);
        if (it == processes.end()) it = processes.emplace(pid, InspectProcess(pid, auth, servicePid, PROCESS_DUP_HANDLE)).first;
        const auto& process = it->second;
        if (!process.process || !process.start) { result.partial = true; continue; }
        // System/device handles may block even a privileged name query. Protected owners are
        // never actionable; file queries can still show Restart Manager's read-only owner rows.
        if (!process.safe) { result.partial = true; continue; }
        HANDLE copy = nullptr;
        if (!DuplicateHandle(process.process.get(), reinterpret_cast<HANDLE>(entry.handle), GetCurrentProcess(), &copy, 0, FALSE, DUPLICATE_SAME_ACCESS)) continue;
        Handle duplicate(copy);
        if (GetFileType(copy) != FILE_TYPE_DISK) continue;
        std::wstring path = CanonicalPath(copy);
        if (path.empty() || !Matches(path, target.path, target.directory)) continue;
        GuardRow row = MakeRow(pid, process, path);
        row.handle = entry.handle; row.objectIdentity = ObjectIdentity(entry, process.start, key);
        row.kind = L"文件句柄";
        row.actionable = process.safe && !(entry.attributes & 1);
        row.status = !process.safe ? L"受保护进程，禁止操作" : ((entry.attributes & 1) ? L"句柄禁止关闭" :
            (row.objectIdentity ? L"可解除句柄或结束进程" : L"Windows 隐藏句柄身份；可结束进程，解除句柄需后台服务"));
        result.rows.push_back(std::move(row)); found.insert(pid);
        if (result.rows.size() >= MaxRows) { result.partial = true; break; }
    }
    // Restart Manager also reports applications with memory-mapped images that lack an open disk handle.
    if (!target.directory && target.path.size() < CCH_RM_MAX_APP_NAME * 32u) {
        DWORD session = 0; wchar_t sessionKey[CCH_RM_SESSION_KEY + 1]{};
        if (RmStartSession(&session, 0, sessionKey) == ERROR_SUCCESS) {
            const wchar_t* file = target.path.c_str();
            if (RmRegisterResources(session, 1, &file, 0, nullptr, 0, nullptr) == ERROR_SUCCESS) {
                UINT needed = 0, count = 0; DWORD reason = 0;
                DWORD status = RmGetList(session, &needed, &count, nullptr, &reason);
                if (status == ERROR_MORE_DATA && needed <= MaxRows) {
                    std::vector<RM_PROCESS_INFO> apps(needed); count = needed;
                    status = RmGetList(session, &needed, &count, apps.data(), &reason);
                    if (status == ERROR_SUCCESS) for (UINT i = 0; i < count && result.rows.size() < MaxRows; ++i) {
                        const auto& app = apps[i]; DWORD pid = app.Process.dwProcessId;
                        if (found.count(pid) || pid == GetCurrentProcessId() || pid == servicePid) continue;
                        auto process = InspectProcess(pid, auth, servicePid, 0);
                        if (!process.process || !process.start || process.start != FileTimeValue(app.Process.ProcessStartTime)) continue;
                        GuardRow row = MakeRow(pid, process, target.path);
                        row.kind = L"内存映射 / Restart Manager"; row.actionable = process.safe;
                        row.status = process.safe ? L"映射无法单独关闭，可结束进程" : L"受保护进程，禁止操作";
                        result.rows.push_back(std::move(row));
                    }
                }
            }
            RmEndSession(session);
        }
    }
    result.message = result.rows.empty() ? L"未找到当前权限可见的占用进程。" : L"已读取占用列表。";
    if (result.partial) result.message += L" 部分进程受权限限制或列表达到上限。";
    return result;
}
bool CurrentHandle(const GuardRow& selected, ExtendedHandle& match, const GuardIdentityKey& key) {
    std::vector<BYTE> data; DWORD error = 0;
    if (!Snapshot(data, error)) return false;
    auto all = reinterpret_cast<const ExtendedHandles*>(data.data());
    for (ULONG_PTR i = 0; i < all->count; ++i) {
        const auto& entry = all->entries[i];
        if (entry.pid == selected.pid && entry.handle == selected.handle) {
            match = entry; return ObjectIdentity(entry, selected.startTime, key) == selected.objectIdentity && !(entry.attributes & 1);
        }
    }
    return false;
}
}

GuardResponse QueryOccupancy(const std::wstring& path, HANDLE clientToken, DWORD servicePid, const GuardIdentityKey* identityKey) {
    GuardRequest request; request.path = path; return ExecuteRequest(request, clientToken, servicePid, identityKey);
}

GuardResponse ExecuteRequest(const GuardRequest& request, HANDLE clientToken, DWORD servicePid, const GuardIdentityKey* identityKey) {
    GuardResponse response;
    if (request.action != GuardAction::Query && request.action != GuardAction::Release && request.action != GuardAction::Terminate) {
        response.error = ERROR_INVALID_PARAMETER; response.message = L"无效操作。"; return response;
    }
    if (request.selection.size() > MaxRows || (request.action != GuardAction::Query && request.selection.empty())) {
        response.error = ERROR_INVALID_PARAMETER; response.message = L"请选择占用项。"; return response;
    }
    if (!identityKey && !LocalSecret().valid) { response.error = ERROR_GEN_FAILURE; response.message = L"无法建立安全句柄身份。"; return response; }
    const GuardIdentityKey& key = identityKey ? *identityKey : LocalSecret().key;
    auto auth = Authorize(clientToken);
    Target target;
    if (!OpenTarget(request.path, auth, target, response.error)) { response.message = L"无法以请求用户权限访问目标：" + ErrorText(response.error); return response; }
    EnableDebugPrivilege();
    if (request.action == GuardAction::Query) return Scan(target, auth, servicePid, key);
    unsigned succeeded = 0, failed = 0;
    std::unordered_set<DWORD> terminated;
    for (const auto& selected : request.selection) {
        if (request.action == GuardAction::Terminate && terminated.count(selected.pid)) continue;
        auto process = InspectProcess(selected.pid, auth, servicePid, PROCESS_DUP_HANDLE | (request.action == GuardAction::Terminate ? PROCESS_TERMINATE | SYNCHRONIZE : 0));
        DWORD error = ERROR_ACCESS_DENIED;
        bool valid = process.process && process.safe && process.start && process.start == selected.startTime
            && selected.pid > 4 && Matches(selected.path, target.path, target.directory);
        if (valid && selected.handle && (selected.objectIdentity || request.action == GuardAction::Release)) {
            ExtendedHandle current{}; HANDLE copy = nullptr;
            valid = selected.objectIdentity && CurrentHandle(selected, current, key)
                && DuplicateHandle(process.process.get(), reinterpret_cast<HANDLE>(selected.handle), GetCurrentProcess(), &copy, 0, FALSE, DUPLICATE_SAME_ACCESS);
            Handle duplicate(copy);
            valid = valid && copy && GetFileType(copy) == FILE_TYPE_DISK && SamePath(CanonicalPath(copy), selected.path);
            if (valid && request.action == GuardAction::Release) {
                // Refresh the source identity immediately before closing. Windows offers no atomic compare-and-close API.
                valid = StartTime(process.process.get()) == selected.startTime && CurrentHandle(selected, current, key);
                if (valid) {
                    HANDLE closedCopy = nullptr;
                    valid = DuplicateHandle(process.process.get(), reinterpret_cast<HANDLE>(selected.handle), GetCurrentProcess(), &closedCopy,
                        0, FALSE, DUPLICATE_CLOSE_SOURCE | DUPLICATE_SAME_ACCESS) != FALSE;
                    error = valid ? ERROR_SUCCESS : GetLastError(); Handle cleanup(closedCopy);
                }
            }
        } else if (valid) {
            if (request.action == GuardAction::Release) { valid = false; error = ERROR_NOT_SUPPORTED; }
            else {
                auto current = Scan(target, auth, servicePid, key);
                valid = current.error == ERROR_SUCCESS && std::any_of(current.rows.begin(), current.rows.end(), [&](const GuardRow& row) {
                    return row.pid == selected.pid && row.startTime == selected.startTime && SamePath(row.path, selected.path);
                });
            }
        }
        if (valid && request.action == GuardAction::Terminate) {
            ExtendedHandle current{};
            valid = StartTime(process.process.get()) == selected.startTime
                && (!selected.handle || !selected.objectIdentity || CurrentHandle(selected, current, key))
                && TerminateProcess(process.process.get(), ERROR_PROCESS_ABORTED);
            error = valid ? ERROR_SUCCESS : GetLastError();
            if (valid) { WaitForSingleObject(process.process.get(), 2000); terminated.insert(selected.pid); }
        }
        if (valid) ++succeeded;
        else { ++failed; if (!response.error) response.error = error; }
    }
    auto refreshed = Scan(target, auth, servicePid, key);
    response.rows = std::move(refreshed.rows); response.partial = refreshed.partial || failed != 0;
    response.message = L"成功 " + std::to_wstring(succeeded) + L" 项；拒绝或已失效 " + std::to_wstring(failed) + L" 项。";
    if (failed) response.message += L" 为防止误操作，已失效身份、系统进程和无权限项目不会执行。";
    return response;
}
}
