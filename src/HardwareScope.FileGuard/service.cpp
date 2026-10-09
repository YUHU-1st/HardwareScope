#include "service.h"
#include <sddl.h>
#include <objbase.h>
#include <bcrypt.h>
#include <algorithm>
#include <memory>
#include <limits>

#pragma comment(lib, "Advapi32.lib")
#pragma comment(lib, "Ole32.lib")
#pragma comment(lib, "Bcrypt.lib")

namespace fileguard {
namespace {
struct CloseHandleDeleter { void operator()(void* h) const { if (h && h != INVALID_HANDLE_VALUE) CloseHandle(h); } };
using Handle = std::unique_ptr<void, CloseHandleDeleter>;
constexpr DWORD Magic = 0x31474648; // HFG1
constexpr DWORD Version = 1;
constexpr DWORD MaxBytes = 4 * 1024 * 1024;
constexpr DWORD MaxRows = 1024;
constexpr DWORD Timeout = 20000;
constexpr DWORD PipeClientAccess = FILE_READ_DATA | FILE_WRITE_DATA | FILE_READ_EA | FILE_WRITE_EA
    | FILE_READ_ATTRIBUTES | FILE_WRITE_ATTRIBUTES | READ_CONTROL | SYNCHRONIZE;
SERVICE_STATUS_HANDLE statusHandle = nullptr;
SERVICE_STATUS serviceStatus{};
HANDLE stopEvent = nullptr;

GuardResponse Failure(DWORD error, const wchar_t* message) {
    GuardResponse result; result.error = error; result.message = message; return result;
}
struct Writer {
    std::vector<BYTE> data;
    bool valid = true;
    template<typename T> void number(T value) {
        if (data.size() > MaxBytes - sizeof(T)) { valid = false; return; }
        const BYTE* p = reinterpret_cast<const BYTE*>(&value); data.insert(data.end(), p, p + sizeof(T));
    }
    void string(const std::wstring& value, size_t max = 32767) {
        if (value.size() > max || value.find(L'\0') != std::wstring::npos || data.size() + 4 + value.size() * sizeof(wchar_t) > MaxBytes) { valid = false; return; }
        number<DWORD>(static_cast<DWORD>(value.size()));
        const BYTE* p = reinterpret_cast<const BYTE*>(value.data()); data.insert(data.end(), p, p + value.size() * sizeof(wchar_t));
    }
};
struct Reader {
    const std::vector<BYTE>& data;
    size_t offset = 0;
    bool valid = true;
    template<typename T> T number() {
        T value{};
        if (offset > data.size() || data.size() - offset < sizeof(T)) { valid = false; return value; }
        memcpy(&value, data.data() + offset, sizeof(T)); offset += sizeof(T); return value;
    }
    std::wstring string(size_t max = 32767) {
        DWORD length = number<DWORD>();
        if (!valid || length > max || offset > data.size() || data.size() - offset < size_t(length) * sizeof(wchar_t)) { valid = false; return {}; }
        std::wstring value(length, L'\0');
        memcpy(value.data(), data.data() + offset, size_t(length) * sizeof(wchar_t)); offset += size_t(length) * sizeof(wchar_t);
        if (value.find(L'\0') != std::wstring::npos) valid = false;
        return value;
    }
    bool complete() const { return valid && offset == data.size(); }
};
bool EncodeRequest(const GuardRequest& request, std::vector<BYTE>& bytes) {
    if (request.selection.size() > MaxRows || static_cast<uint32_t>(request.action) > 2) return false;
    Writer out; out.number<uint32_t>(static_cast<uint32_t>(request.action)); out.string(request.path);
    out.number<DWORD>(static_cast<DWORD>(request.selection.size()));
    for (const auto& row : request.selection) {
        out.number<DWORD>(row.pid); out.number<uint64_t>(row.startTime); out.number<uint64_t>(row.handle);
        out.number<uint64_t>(row.objectIdentity); out.string(row.path);
    }
    bytes = std::move(out.data); return out.valid;
}
bool DecodeRequest(const std::vector<BYTE>& bytes, GuardRequest& request) {
    Reader in{bytes}; DWORD action = in.number<DWORD>();
    if (action > 2) return false;
    request.action = static_cast<GuardAction>(action); request.path = in.string();
    DWORD count = in.number<DWORD>(); if (!in.valid || count > MaxRows) return false;
    request.selection.reserve(count);
    for (DWORD i = 0; i < count && in.valid; ++i) {
        GuardRow row; row.pid = in.number<DWORD>(); row.startTime = in.number<uint64_t>(); row.handle = in.number<uint64_t>();
        row.objectIdentity = in.number<uint64_t>(); row.path = in.string(); request.selection.push_back(std::move(row));
    }
    return in.complete();
}
bool EncodeResponse(const GuardResponse& response, std::vector<BYTE>& bytes) {
    if (response.rows.size() > MaxRows) return false;
    Writer out; out.number<DWORD>(response.error); out.number<DWORD>(response.partial ? 1 : 0);
    out.string(response.message, 8192); out.number<DWORD>(static_cast<DWORD>(response.rows.size()));
    for (const auto& row : response.rows) {
        out.number<DWORD>(row.pid); out.number<uint64_t>(row.startTime); out.number<uint64_t>(row.handle); out.number<uint64_t>(row.objectIdentity);
        out.string(row.path); out.string(row.processName, 1024); out.string(row.imagePath); out.string(row.kind, 1024); out.string(row.status, 1024);
        out.number<DWORD>(row.actionable ? 1 : 0);
    }
    bytes = std::move(out.data); return out.valid;
}
bool DecodeResponse(const std::vector<BYTE>& bytes, GuardResponse& response) {
    Reader in{bytes}; response.error = in.number<DWORD>(); DWORD partial = in.number<DWORD>();
    if (partial > 1) return false;
    response.partial = partial != 0; response.message = in.string(8192);
    DWORD count = in.number<DWORD>(); if (!in.valid || count > MaxRows) return false;
    response.rows.reserve(count);
    for (DWORD i = 0; i < count && in.valid; ++i) {
        GuardRow row; row.pid = in.number<DWORD>(); row.startTime = in.number<uint64_t>(); row.handle = in.number<uint64_t>(); row.objectIdentity = in.number<uint64_t>();
        row.path = in.string(); row.processName = in.string(1024); row.imagePath = in.string(); row.kind = in.string(1024); row.status = in.string(1024);
        DWORD actionable = in.number<DWORD>(); if (actionable > 1) return false; row.actionable = actionable != 0;
        response.rows.push_back(std::move(row));
    }
    return in.complete();
}
DWORD Remaining(ULONGLONG deadline) {
    ULONGLONG now = GetTickCount64(); return now >= deadline ? 0 : static_cast<DWORD>(std::min<ULONGLONG>(deadline - now, MAXDWORD));
}
bool Transfer(HANDLE pipe, void* bytes, DWORD length, bool write, bool overlapped, ULONGLONG deadline, HANDLE cancel, DWORD& error) {
    auto p = reinterpret_cast<BYTE*>(bytes);
    while (length) {
        DWORD done = 0;
        if (!overlapped) {
            BOOL okay = write ? WriteFile(pipe, p, length, &done, nullptr) : ReadFile(pipe, p, length, &done, nullptr);
            if (!okay || !done) { error = okay ? ERROR_BROKEN_PIPE : GetLastError(); return false; }
        } else {
            Handle event(CreateEventW(nullptr, TRUE, FALSE, nullptr));
            if (!event) { error = GetLastError(); return false; }
            OVERLAPPED operation{}; operation.hEvent = event.get();
            BOOL okay = write ? WriteFile(pipe, p, length, &done, &operation) : ReadFile(pipe, p, length, &done, &operation);
            if (!okay && GetLastError() == ERROR_IO_PENDING) {
                HANDLE events[] = {event.get(), cancel};
                DWORD wait = WaitForMultipleObjects(cancel ? 2 : 1, events, FALSE, Remaining(deadline));
                if (wait != WAIT_OBJECT_0) {
                    CancelIoEx(pipe, &operation); WaitForSingleObject(event.get(), INFINITE);
                    error = wait == WAIT_OBJECT_0 + 1 ? ERROR_OPERATION_ABORTED : ERROR_TIMEOUT; return false;
                }
                okay = GetOverlappedResult(pipe, &operation, &done, FALSE);
            }
            if (!okay || !done) { error = okay ? ERROR_BROKEN_PIPE : GetLastError(); return false; }
        }
        p += done; length -= done;
    }
    return true;
}
bool WriteFrame(HANDLE pipe, const std::vector<BYTE>& bytes, bool overlapped, ULONGLONG deadline, HANDLE cancel, DWORD& error) {
    if (bytes.size() > MaxBytes) { error = ERROR_INVALID_DATA; return false; }
    DWORD header[] = {Magic, Version, static_cast<DWORD>(bytes.size())};
    return Transfer(pipe, header, sizeof(header), true, overlapped, deadline, cancel, error)
        && Transfer(pipe, const_cast<BYTE*>(bytes.data()), static_cast<DWORD>(bytes.size()), true, overlapped, deadline, cancel, error);
}
bool ReadFrame(HANDLE pipe, std::vector<BYTE>& bytes, bool overlapped, ULONGLONG deadline, HANDLE cancel, DWORD& error) {
    DWORD header[3]{};
    if (!Transfer(pipe, header, sizeof(header), false, overlapped, deadline, cancel, error)) return false;
    if (header[0] != Magic || header[1] != Version || header[2] > MaxBytes || header[2] == 0) { error = ERROR_INVALID_DATA; return false; }
    bytes.resize(header[2]); return Transfer(pipe, bytes.data(), header[2], false, overlapped, deadline, cancel, error);
}
Handle OwnImpersonationToken() {
    HANDLE primary = nullptr, duplicate = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY | TOKEN_DUPLICATE, &primary)) return {};
    Handle owned(primary);
    if (!DuplicateTokenEx(primary, TOKEN_QUERY | TOKEN_IMPERSONATE | TOKEN_DUPLICATE, nullptr, SecurityImpersonation, TokenImpersonation, &duplicate)) return {};
    return Handle(duplicate);
}
GuardResponse RunIsolated(const GuardRequest& request, HANDLE token, DWORD daemonPid, HANDLE cancel) {
    const ULONGLONG deadline = GetTickCount64() + Timeout;
    std::vector<BYTE> bytes;
    if (!EncodeRequest(request, bytes)) return Failure(ERROR_INVALID_PARAMETER, L"请求过长或包含无效操作。");
    struct Secret {
        GuardIdentityKey key{};
        bool valid = BCryptGenRandom(nullptr, key.data(), static_cast<ULONG>(key.size()), BCRYPT_USE_SYSTEM_PREFERRED_RNG) >= 0;
    };
    static const Secret secret;
    if (!secret.valid || bytes.size() > MaxBytes - secret.key.size()) return Failure(ERROR_GEN_FAILURE, L"无法建立安全工作进程身份。");
    // This envelope is written only to the private inherited worker channel, never to an external client.
    bytes.insert(bytes.begin(), secret.key.begin(), secret.key.end());
    GUID id{}; if (FAILED(CoCreateGuid(&id))) return Failure(ERROR_GEN_FAILURE, L"无法创建工作进程通道。");
    wchar_t guid[40]{}; StringFromGUID2(id, guid, 40);
    std::wstring name = L"\\\\.\\pipe\\HardwareScope.FileGuard.worker." + std::to_wstring(GetCurrentProcessId()) + L"." + guid;
    Handle server(CreateNamedPipeW(name.c_str(), PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED | FILE_FLAG_FIRST_PIPE_INSTANCE,
        PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS, 1, 65536, 65536, 0, nullptr));
    if (server.get() == INVALID_HANDLE_VALUE) return Failure(GetLastError(), L"无法创建工作进程通道。");
    SECURITY_ATTRIBUTES inheritable{sizeof(SECURITY_ATTRIBUTES), nullptr, TRUE};
    Handle childPipe(CreateFileW(name.c_str(), GENERIC_READ | GENERIC_WRITE, 0, &inheritable, OPEN_EXISTING, 0, nullptr));
    if (childPipe.get() == INVALID_HANDLE_VALUE) return Failure(GetLastError(), L"无法连接工作进程通道。");
    OVERLAPPED connection{}; Handle connected(CreateEventW(nullptr, TRUE, FALSE, nullptr)); connection.hEvent = connected.get();
    if (!ConnectNamedPipe(server.get(), &connection) && GetLastError() != ERROR_PIPE_CONNECTED) return Failure(GetLastError(), L"工作进程通道连接失败。");
    HANDLE inheritedToken = nullptr;
    if (!DuplicateHandle(GetCurrentProcess(), token, GetCurrentProcess(), &inheritedToken, TOKEN_QUERY | TOKEN_IMPERSONATE | TOKEN_DUPLICATE, TRUE, 0))
        return Failure(GetLastError(), L"无法复制请求用户身份。");
    Handle ownedToken(inheritedToken);
    Handle job(CreateJobObjectW(nullptr, nullptr));
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{}; limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    if (!job || !SetInformationJobObject(job.get(), JobObjectExtendedLimitInformation, &limits, sizeof(limits)))
        return Failure(GetLastError(), L"无法建立工作进程超时保护。");
    std::wstring exe(32768, L'\0'); DWORD n = GetModuleFileNameW(nullptr, exe.data(), static_cast<DWORD>(exe.size()));
    if (!n || n >= exe.size()) return Failure(ERROR_BAD_PATHNAME, L"无法确定程序路径。"); exe.resize(n);
    std::wstring command = L"\"" + exe + L"\" /worker " + std::to_wstring(reinterpret_cast<uintptr_t>(childPipe.get())) + L" "
        + std::to_wstring(reinterpret_cast<uintptr_t>(childPipe.get())) + L" " + std::to_wstring(reinterpret_cast<uintptr_t>(inheritedToken)) + L" " + std::to_wstring(daemonPid);
    SIZE_T attributeSize = 0; InitializeProcThreadAttributeList(nullptr, 1, 0, &attributeSize);
    std::vector<BYTE> attributeBuffer(attributeSize);
    STARTUPINFOEXW startup{}; startup.StartupInfo.cb = sizeof(startup); startup.lpAttributeList = reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(attributeBuffer.data());
    if (!InitializeProcThreadAttributeList(startup.lpAttributeList, 1, 0, &attributeSize)) return Failure(GetLastError(), L"无法限制工作进程继承句柄。");
    HANDLE inherit[] = {childPipe.get(), inheritedToken};
    if (!UpdateProcThreadAttribute(startup.lpAttributeList, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, inherit, sizeof(inherit), nullptr, nullptr)) {
        DWORD error = GetLastError(); DeleteProcThreadAttributeList(startup.lpAttributeList); return Failure(error, L"无法限制工作进程继承句柄。");
    }
    PROCESS_INFORMATION child{};
    BOOL created = CreateProcessW(exe.c_str(), command.data(), nullptr, nullptr, TRUE,
        CREATE_NO_WINDOW | CREATE_SUSPENDED | EXTENDED_STARTUPINFO_PRESENT, nullptr, nullptr, &startup.StartupInfo, &child);
    DWORD creationError = GetLastError(); DeleteProcThreadAttributeList(startup.lpAttributeList);
    if (!created) return Failure(creationError, L"无法启动后台工作进程。");
    Handle process(child.hProcess), thread(child.hThread);
    if (!AssignProcessToJobObject(job.get(), child.hProcess)) {
        DWORD error = GetLastError(); TerminateProcess(child.hProcess, error); return Failure(error, L"无法保护后台工作进程。");
    }
    if (ResumeThread(child.hThread) == static_cast<DWORD>(-1)) return Failure(GetLastError(), L"后台工作进程启动失败。");
    childPipe.reset(); ownedToken.reset();
    DWORD error = ERROR_SUCCESS;
    if (!WriteFrame(server.get(), bytes, true, deadline, cancel, error) || !ReadFrame(server.get(), bytes, true, deadline, cancel, error)) {
        return Failure(error, error == ERROR_TIMEOUT ? L"查询超过 20 秒，工作进程已停止；后台服务仍可使用。" : L"后台工作进程通信失败。");
    }
    GuardResponse result;
    if (!DecodeResponse(bytes, result)) return Failure(ERROR_INVALID_DATA, L"后台工作进程返回了无效结果。");
    return result;
}
DWORD RunningServicePid(DWORD& error) {
    SC_HANDLE manager = OpenSCManagerW(nullptr, nullptr, SC_MANAGER_CONNECT);
    if (!manager) { error = GetLastError(); return 0; }
    SC_HANDLE service = OpenServiceW(manager, ServiceName, SERVICE_QUERY_STATUS);
    if (!service) { error = GetLastError(); CloseServiceHandle(manager); return 0; }
    SERVICE_STATUS_PROCESS status{}; DWORD needed = 0;
    BOOL okay = QueryServiceStatusEx(service, SC_STATUS_PROCESS_INFO, reinterpret_cast<BYTE*>(&status), sizeof(status), &needed);
    error = okay ? ERROR_SUCCESS : GetLastError(); CloseServiceHandle(service); CloseServiceHandle(manager);
    if (!okay || status.dwCurrentState != SERVICE_RUNNING) return 0;
    return status.dwProcessId;
}
void Report(DWORD state, DWORD error = ERROR_SUCCESS) {
    serviceStatus.dwServiceType = SERVICE_WIN32_OWN_PROCESS; serviceStatus.dwCurrentState = state;
    serviceStatus.dwControlsAccepted = state == SERVICE_RUNNING ? SERVICE_ACCEPT_STOP | SERVICE_ACCEPT_SHUTDOWN : 0;
    serviceStatus.dwWin32ExitCode = error; serviceStatus.dwWaitHint = state == SERVICE_STOP_PENDING ? 5000 : 0;
    serviceStatus.dwCheckPoint = state == SERVICE_STOP_PENDING ? 1 : 0;
    if (statusHandle) SetServiceStatus(statusHandle, &serviceStatus);
}
DWORD WINAPI Control(DWORD control, DWORD, void*, void*) {
    if (control == SERVICE_CONTROL_STOP || control == SERVICE_CONTROL_SHUTDOWN) { Report(SERVICE_STOP_PENDING); if (stopEvent) SetEvent(stopEvent); }
    return NO_ERROR;
}
void WINAPI ServiceMain(DWORD, wchar_t**) {
    statusHandle = RegisterServiceCtrlHandlerExW(ServiceName, Control, nullptr);
    if (!statusHandle) return;
    Report(SERVICE_START_PENDING);
    Handle stop(CreateEventW(nullptr, TRUE, FALSE, nullptr)); stopEvent = stop.get();
    if (!stop) { Report(SERVICE_STOPPED, GetLastError()); return; }
    // FILE_CREATE_PIPE_INSTANCE shares bit 0x4 with FILE_APPEND_DATA and is deliberately excluded for clients.
    PSECURITY_DESCRIPTOR descriptor = nullptr;
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(L"D:P(A;;GA;;;SY)(A;;0x0012019B;;;BA)(A;;0x0012019B;;;AU)", SDDL_REVISION_1, &descriptor, nullptr)) {
        Report(SERVICE_STOPPED, GetLastError()); stopEvent = nullptr; return;
    }
    SECURITY_ATTRIBUTES security{sizeof(SECURITY_ATTRIBUTES), descriptor, FALSE};
    DWORD shutdownError = ERROR_SUCCESS;
    Handle pipe(CreateNamedPipeW(PipeName, PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED | FILE_FLAG_FIRST_PIPE_INSTANCE,
        PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS, 1, 65536, 65536, 0, &security));
    if (pipe.get() == INVALID_HANDLE_VALUE) { shutdownError = GetLastError(); LocalFree(descriptor); stopEvent = nullptr; Report(SERVICE_STOPPED, shutdownError); return; }
    Report(SERVICE_RUNNING);
    while (WaitForSingleObject(stop.get(), 0) != WAIT_OBJECT_0) {
        Handle event(CreateEventW(nullptr, TRUE, FALSE, nullptr)); OVERLAPPED connection{}; connection.hEvent = event.get();
        BOOL connected = ConnectNamedPipe(pipe.get(), &connection);
        if (!connected) {
            DWORD error = GetLastError();
            if (error == ERROR_IO_PENDING) {
                HANDLE waits[] = {event.get(), stop.get()}; DWORD wait = WaitForMultipleObjects(2, waits, FALSE, INFINITE);
                if (wait != WAIT_OBJECT_0) { CancelIoEx(pipe.get(), &connection); WaitForSingleObject(event.get(), INFINITE); break; }
                DWORD unused = 0; connected = GetOverlappedResult(pipe.get(), &connection, &unused, FALSE);
            } else connected = error == ERROR_PIPE_CONNECTED;
        }
        if (!connected) continue;
        const ULONGLONG deadline = GetTickCount64() + Timeout;
        std::vector<BYTE> bytes; GuardRequest request; GuardResponse response; DWORD error = 0;
        if (!ReadFrame(pipe.get(), bytes, true, deadline, stop.get(), error)) { DisconnectNamedPipe(pipe.get()); continue; }
        if (!DecodeRequest(bytes, request)) response = Failure(ERROR_INVALID_DATA, L"请求格式无效。");
        else {
            HANDLE rawToken = nullptr; Handle token;
            if (ImpersonateNamedPipeClient(pipe.get())) {
                if (OpenThreadToken(GetCurrentThread(), TOKEN_QUERY | TOKEN_DUPLICATE | TOKEN_IMPERSONATE, TRUE, &rawToken)) {
                    HANDLE duplicate = nullptr;
                    if (DuplicateTokenEx(rawToken, TOKEN_QUERY | TOKEN_DUPLICATE | TOKEN_IMPERSONATE, nullptr, SecurityImpersonation, TokenImpersonation, &duplicate)) token.reset(duplicate);
                    CloseHandle(rawToken);
                }
                RevertToSelf();
            }
            response = token ? RunIsolated(request, token.get(), GetCurrentProcessId(), stop.get()) : Failure(ERROR_ACCESS_DENIED, L"无法验证请求用户身份。");
        }
        if (!EncodeResponse(response, bytes)) { response = Failure(ERROR_MORE_DATA, L"占用列表过大，请选择更小的目录。"); EncodeResponse(response, bytes); }
        const ULONGLONG responseDeadline = GetTickCount64() + 2000;
        if (WriteFrame(pipe.get(), bytes, true, responseDeadline, stop.get(), error)) {
            // A receipt prevents DisconnectNamedPipe from discarding an unread buffered response.
            DWORD receipt = 0;
            Transfer(pipe.get(), &receipt, sizeof(receipt), false, true, responseDeadline, stop.get(), error);
        }
        DisconnectNamedPipe(pipe.get());
    }
    LocalFree(descriptor); stopEvent = nullptr; Report(SERVICE_STOPPED, shutdownError);
}
bool ParseHandle(const wchar_t* text, uint64_t& value) {
    if (!text || !*text || *text == L'-' || *text == L'+') return false;
    wchar_t* end = nullptr; errno = 0; value = _wcstoui64(text, &end, 10);
    return errno == 0 && end && !*end && value != 0;
}
}

GuardResponse SendRequest(const GuardRequest& request) {
    std::vector<BYTE> bytes;
    if (!EncodeRequest(request, bytes)) return Failure(ERROR_INVALID_PARAMETER, L"请求过长或包含无效操作。");
    DWORD serviceError = 0; DWORD daemonPid = RunningServicePid(serviceError);
    if (!daemonPid) {
        if (serviceError != ERROR_SUCCESS && serviceError != ERROR_SERVICE_DOES_NOT_EXIST && serviceError != ERROR_SERVICE_NOT_ACTIVE)
            return Failure(serviceError, L"无法验证后台服务状态。");
        auto token = OwnImpersonationToken();
        if (!token) return Failure(GetLastError(), L"无法读取当前用户身份。");
        auto response = RunIsolated(request, token.get(), 0, nullptr);
        response.message = L"后台服务未运行，已使用当前用户权限的临时工作进程。 " + response.message;
        return response;
    }
    const ULONGLONG deadline = GetTickCount64() + Timeout + 3000;
    HANDLE raw = INVALID_HANDLE_VALUE;
    for (;;) {
        raw = CreateFileW(PipeName, PipeClientAccess, 0, nullptr, OPEN_EXISTING,
            FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IMPERSONATION, nullptr);
        if (raw != INVALID_HANDLE_VALUE) break;
        DWORD error = GetLastError();
        if (error != ERROR_PIPE_BUSY) return Failure(error, L"无法连接后台服务。");
        DWORD remaining = Remaining(deadline);
        if (!remaining || !WaitNamedPipeW(PipeName, remaining)) return Failure(GetLastError(), L"后台服务正忙，请稍后重试。");
    }
    Handle pipe(raw); ULONG actualPid = 0;
    if (!GetNamedPipeServerProcessId(raw, &actualPid) || actualPid != daemonPid)
        return Failure(ERROR_ACCESS_DENIED, L"后台通道的进程身份不匹配，已拒绝连接。");
    DWORD error = 0;
    if (!WriteFrame(raw, bytes, true, deadline, nullptr, error) || !ReadFrame(raw, bytes, true, deadline, nullptr, error))
        return Failure(error, error == ERROR_TIMEOUT ? L"后台服务响应超时，请稍后重试。" : L"后台服务通信失败。");
    GuardResponse response;
    DWORD receipt = Magic;
    Transfer(raw, &receipt, sizeof(receipt), true, true, deadline, nullptr, error);
    return DecodeResponse(bytes, response) ? response : Failure(ERROR_INVALID_DATA, L"后台服务返回了无效结果。");
}

int RunService() {
    SERVICE_TABLE_ENTRYW table[] = {{const_cast<wchar_t*>(ServiceName), ServiceMain}, {nullptr, nullptr}};
    return StartServiceCtrlDispatcherW(table) ? 0 : static_cast<int>(GetLastError());
}

int RunWorker(int argc, wchar_t** argv) {
    if (argc != 6 || wcscmp(argv[1], L"/worker") != 0) return ERROR_INVALID_PARAMETER;
    uint64_t readValue = 0, writeValue = 0, tokenValue = 0, daemonValue = 0;
    if (!ParseHandle(argv[2], readValue) || !ParseHandle(argv[3], writeValue) || !ParseHandle(argv[4], tokenValue)) return ERROR_INVALID_PARAMETER;
    wchar_t* end = nullptr; errno = 0; daemonValue = _wcstoui64(argv[5], &end, 10);
    if (errno || !end || !*argv[5] || *end || daemonValue > MAXDWORD) return ERROR_INVALID_PARAMETER;
    Handle read(reinterpret_cast<HANDLE>(readValue));
    Handle write(readValue == writeValue ? nullptr : reinterpret_cast<HANDLE>(writeValue));
    Handle token(reinterpret_cast<HANDLE>(tokenValue));
    DWORD error = 0; std::vector<BYTE> bytes; GuardRequest request;
    if (!ReadFrame(read.get(), bytes, false, 0, nullptr, error)) return static_cast<int>(error);
    if (bytes.size() < GuardIdentityKey{}.size()) return ERROR_INVALID_DATA;
    GuardIdentityKey key{}; memcpy(key.data(), bytes.data(), key.size()); bytes.erase(bytes.begin(), bytes.begin() + key.size());
    GuardResponse response = DecodeRequest(bytes, request) ? ExecuteRequest(request, token.get(), static_cast<DWORD>(daemonValue), &key) : Failure(ERROR_INVALID_DATA, L"请求格式无效。");
    SecureZeroMemory(key.data(), key.size());
    if (!EncodeResponse(response, bytes)) { response = Failure(ERROR_MORE_DATA, L"占用列表过大，请选择更小的目录。"); EncodeResponse(response, bytes); }
    if (!WriteFrame(write ? write.get() : read.get(), bytes, false, 0, nullptr, error)) return static_cast<int>(error);
    return 0;
}
}
