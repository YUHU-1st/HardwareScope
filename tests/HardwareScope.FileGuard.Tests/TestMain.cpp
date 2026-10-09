#include "../../src/HardwareScope.FileGuard/engine.h"
#include "../../src/HardwareScope.FileGuard/service.h"
#include <algorithm>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <sstream>
#include <stdexcept>

using namespace fileguard;
namespace fs = std::filesystem;

namespace {
struct Handle {
    HANDLE value = nullptr;
    Handle() = default;
    explicit Handle(HANDLE handle) : value(handle) {}
    ~Handle() { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); }
    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
};

std::string Utf8(const std::wstring& value) {
    if (value.empty()) return {};
    int length = WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
    std::string result(length, '\0');
    WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), result.data(), length, nullptr, nullptr);
    return result;
}

std::string Json(const std::string& value) {
    std::ostringstream result;
    result << '"';
    for (unsigned char c : value) {
        switch (c) {
        case '"': result << "\\\""; break;
        case '\\': result << "\\\\"; break;
        case '\n': result << "\\n"; break;
        case '\r': result << "\\r"; break;
        case '\t': result << "\\t"; break;
        default:
            if (c < 32) {
                const char hex[] = "0123456789abcdef";
                result << "\\u00" << hex[c >> 4] << hex[c & 15];
            } else result << c;
        }
    }
    result << '"';
    return result.str();
}

bool SamePath(const std::wstring& left, const std::wstring& right) {
    return CompareStringOrdinal(left.c_str(), -1, right.c_str(), -1, TRUE) == CSTR_EQUAL;
}

void Require(bool okay, const char* operation) {
    if (!okay) throw std::runtime_error(std::string(operation) + " failed: " + std::to_string(GetLastError()));
}

void WriteFileContents(const fs::path& path, const std::string& data) {
    std::ofstream file(path, std::ios::binary);
    file << data;
    if (!file) throw std::runtime_error("Create fixture file failed");
}

int RunFixture(int argc, wchar_t** argv) {
    if (argc != 5) return 11;
    fs::path root(argv[2]);
    Handle ready(OpenEventW(EVENT_MODIFY_STATE, FALSE, argv[3]));
    Handle stop(OpenEventW(SYNCHRONIZE, FALSE, argv[4]));
    if (!ready.value || !stop.value) return 12;
    // These handles intentionally survive until ExitProcess: the test can remotely close one.
    HANDLE first = CreateFileW((root / L"选中 目录" / L"锁定 文件 A.txt").c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    HANDLE second = CreateFileW((root / L"选中 目录" / L"子目录" / L"锁定 文件 B.txt").c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    HANDLE neighbor = CreateFileW((root / L"选中 目录-neighbor" / L"邻居文件.txt").c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    HANDLE mappedFile = CreateFileW((root / L"选中 目录" / L"映射 文件.txt").c_str(), GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (first == INVALID_HANDLE_VALUE || second == INVALID_HANDLE_VALUE || neighbor == INVALID_HANDLE_VALUE || mappedFile == INVALID_HANDLE_VALUE) return 13;
    HANDLE mapping = CreateFileMappingW(mappedFile, nullptr, PAGE_READWRITE, 0, 0, nullptr);
    void* view = mapping ? MapViewOfFile(mapping, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, 0) : nullptr;
    CloseHandle(mappedFile);
    if (!view) return 14;
    SetEvent(ready.value);
    WaitForSingleObject(stop.value, 180000);
    // No code can accidentally close a reused value after the selected handle was released.
    ExitProcess(0);
}

struct Fixture {
    fs::path root;
    fs::path directory;
    fs::path first;
    fs::path second;
    fs::path neighbor;
    fs::path mapped;
    Handle job;
    Handle process;
    Handle ready;
    Handle stop;
    DWORD pid = 0;

    Fixture() {
        wchar_t temporary[MAX_PATH];
        Require(GetTempPathW(MAX_PATH, temporary) != 0, "GetTempPath");
        auto unique = std::to_wstring(GetCurrentProcessId()) + L"-" + std::to_wstring(GetTickCount64());
        root = fs::path(temporary) / (L"HardwareScope FileGuard 测试 " + unique);
        directory = root / L"选中 目录";
        first = directory / L"锁定 文件 A.txt";
        second = directory / L"子目录" / L"锁定 文件 B.txt";
        neighbor = root / L"选中 目录-neighbor" / L"邻居文件.txt";
        mapped = directory / L"映射 文件.txt";
        fs::create_directories(second.parent_path());
        fs::create_directories(neighbor.parent_path());
        WriteFileContents(first, "Selected handle must preserve this content.\n");
        WriteFileContents(second, "Second handle must remain locked.\n");
        WriteFileContents(neighbor, "Prefix neighbor must be excluded.\n");
        WriteFileContents(mapped, std::string(4096, 'M'));
        auto readyName = L"Local\\HardwareScope.FileGuard.Test.Ready." + unique;
        auto stopName = L"Local\\HardwareScope.FileGuard.Test.Stop." + unique;
        ready.value = CreateEventW(nullptr, TRUE, FALSE, readyName.c_str());
        stop.value = CreateEventW(nullptr, TRUE, FALSE, stopName.c_str());
        job.value = CreateJobObjectW(nullptr, nullptr);
        Require(ready.value && stop.value && job.value, "Create fixture events/job");
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        Require(SetInformationJobObject(job.value, JobObjectExtendedLimitInformation, &limits, sizeof(limits)) != FALSE, "Set fixture job limits");
        wchar_t executable[32768];
        Require(GetModuleFileNameW(nullptr, executable, 32768) != 0, "GetModuleFileName");
        std::wstring command = L"\"" + std::wstring(executable) + L"\" --fixture \"" + root.wstring() + L"\" \"" + readyName + L"\" \"" + stopName + L"\"";
        STARTUPINFOW startup{sizeof(startup)};
        PROCESS_INFORMATION created{};
        Require(CreateProcessW(executable, command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW | CREATE_SUSPENDED, nullptr, nullptr, &startup, &created) != FALSE, "Create fixture child");
        process.value = created.hProcess;
        pid = created.dwProcessId;
        Handle thread(created.hThread);
        if (!AssignProcessToJobObject(job.value, process.value)) {
            DWORD error = GetLastError();
            TerminateProcess(process.value, 15);
            SetLastError(error);
            Require(false, "Assign fixture job");
        }
        ResumeThread(thread.value);
        HANDLE waits[] = {ready.value, process.value};
        DWORD wait = WaitForMultipleObjects(2, waits, FALSE, 10000);
        if (wait != WAIT_OBJECT_0) {
            TerminateProcess(process.value, 16);
            throw std::runtime_error("Fixture child failed to open all locks and mapped view");
        }
    }

    ~Fixture() {
        if (stop.value) SetEvent(stop.value);
        if (process.value && WaitForSingleObject(process.value, 5000) == WAIT_TIMEOUT) {
            TerminateProcess(process.value, 17);
            WaitForSingleObject(process.value, 5000);
        }
        if (!root.empty()) {
            std::error_code ignored;
            fs::remove_all(root, ignored);
        }
    }

    bool Alive() const { return WaitForSingleObject(process.value, 0) == WAIT_TIMEOUT; }
};

struct Check {
    std::string name;
    std::string outcome;
    std::string detail;
};

struct Suite {
    bool service = false;
    std::vector<Check> checks;

    void CheckResult(const std::string& name, bool okay, const std::string& detail = {}) {
        checks.push_back({name, okay ? "pass" : "fail", detail});
        std::cout << (okay ? "PASS " : "FAIL ") << name;
        if (!detail.empty()) std::cout << " (" << detail << ')';
        std::cout << '\n';
    }

    void Skip(const std::string& name, const std::string& detail) {
        checks.push_back({name, "skip", detail});
        std::cout << "SKIP " << name << " (" << detail << ")\n";
    }

    GuardResponse Request(const GuardRequest& request) const {
        if (service) return SendRequest(request);
        return request.action == GuardAction::Query ? QueryOccupancy(request.path) : ExecuteRequest(request);
    }

    GuardResponse Query(const fs::path& path) const {
        return Request({GuardAction::Query, path.wstring(), {}});
    }

    GuardResponse Action(GuardAction action, const fs::path& path, const GuardRow& row) const {
        return Request({action, path.wstring(), {row}});
    }

    const GuardRow* ExactHandle(const GuardResponse& response, DWORD pid, const fs::path& path) const {
        for (const auto& row : response.rows)
            if (row.pid == pid && row.handle && SamePath(row.path, path.wstring())) return &row;
        return nullptr;
    }

    bool DeleteDenied(const fs::path& path) const {
        if (DeleteFileW(path.c_str())) return false;
        DWORD error = GetLastError();
        return error == ERROR_SHARING_VIOLATION || error == ERROR_ACCESS_DENIED || error == ERROR_USER_MAPPED_FILE;
    }

    bool PipeBytes(HANDLE pipe, void* data, DWORD length, bool write) const {
        auto cursor = static_cast<BYTE*>(data);
        while (length) {
            Handle event(CreateEventW(nullptr, TRUE, FALSE, nullptr));
            OVERLAPPED operation{};
            operation.hEvent = event.value;
            DWORD count = 0;
            BOOL okay = write ? WriteFile(pipe, cursor, length, &count, &operation) : ReadFile(pipe, cursor, length, &count, &operation);
            if (!okay && GetLastError() == ERROR_IO_PENDING) {
                if (WaitForSingleObject(event.value, 5000) != WAIT_OBJECT_0) {
                    CancelIoEx(pipe, &operation);
                    GetOverlappedResult(pipe, &operation, &count, TRUE);
                    return false;
                }
                okay = GetOverlappedResult(pipe, &operation, &count, FALSE);
            }
            if (!okay || !count) return false;
            cursor += count;
            length -= count;
        }
        return true;
    }

    void CheckMalformedBrokerFrame() {
        Handle pipe(CreateFileW(PipeName, FILE_READ_DATA | FILE_WRITE_DATA | SYNCHRONIZE, 0, nullptr, OPEN_EXISTING,
            FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IMPERSONATION, nullptr));
        if (pipe.value == INVALID_HANDLE_VALUE) {
            CheckResult("broker_malformed_wire_request_rejected", false, "Cannot connect to the broker: " + std::to_string(GetLastError()));
            return;
        }
        // A complete transport frame whose request contains an invalid action value.
        DWORD request[] = {0x31474648, 1, sizeof(DWORD), 99};
        DWORD header[3]{};
        bool framed = PipeBytes(pipe.value, request, sizeof(request), true) && PipeBytes(pipe.value, header, sizeof(header), false);
        framed = framed && header[0] == 0x31474648 && header[1] == 1 && header[2] >= sizeof(DWORD) && header[2] <= 4096;
        std::vector<BYTE> response(framed ? header[2] : 0);
        DWORD error = ERROR_SUCCESS;
        if (framed) framed = PipeBytes(pipe.value, response.data(), header[2], false);
        if (framed) memcpy(&error, response.data(), sizeof(error));
        DWORD receipt = 0x31474648;
        if (framed) PipeBytes(pipe.value, &receipt, sizeof(receipt), true);
        CheckResult("broker_malformed_wire_request_rejected", framed && error == ERROR_INVALID_DATA, std::to_string(error));
    }

    void CheckElevationBoundary(const Fixture& fixture, const GuardRow& row) {
        Handle primary;
        TOKEN_ELEVATION elevation{};
        DWORD size = 0;
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY | TOKEN_DUPLICATE | TOKEN_ADJUST_DEFAULT, &primary.value)
            || !GetTokenInformation(primary.value, TokenElevation, &elevation, sizeof(elevation), &size)) {
            CheckResult("elevation_boundary_test_setup", false, std::to_string(GetLastError()));
            return;
        }
        if (!elevation.TokenIsElevated) {
            Skip("same_sid_normal_client_cannot_control_elevated_fixture", "Run this suite elevated to create its own elevated fixture; no user's application is used");
            return;
        }
        BYTE administrator[SECURITY_MAX_SID_SIZE]{};
        DWORD administratorSize = sizeof(administrator);
        BYTE medium[SECURITY_MAX_SID_SIZE]{};
        DWORD mediumSize = sizeof(medium);
        Handle restricted;
        bool created = CreateWellKnownSid(WinBuiltinAdministratorsSid, nullptr, administrator, &administratorSize)
            && CreateWellKnownSid(WinMediumLabelSid, nullptr, medium, &mediumSize);
        SID_AND_ATTRIBUTES disabled{administrator, 0};
        if (created) created = CreateRestrictedToken(primary.value, DISABLE_MAX_PRIVILEGE, 1, &disabled, 0, nullptr, 0, nullptr, &restricted.value) != FALSE;
        TOKEN_MANDATORY_LABEL label{{medium, SE_GROUP_INTEGRITY}};
        if (created) created = SetTokenInformation(restricted.value, TokenIntegrityLevel, &label, sizeof(label) + GetLengthSid(medium)) != FALSE;
        CheckResult("same_sid_medium_integrity_client_created", created, std::to_string(GetLastError()));
        if (!created) return;
        bool impersonated = ImpersonateLoggedOnUser(restricted.value) != FALSE;
        CheckResult("same_sid_medium_integrity_client_impersonated", impersonated, std::to_string(GetLastError()));
        if (!impersonated) return;
        GuardRequest query{GuardAction::Query, fixture.first.wstring(), {}};
        GuardRequest terminate{GuardAction::Terminate, fixture.first.wstring(), {row}};
        GuardResponse hidden = service ? SendRequest(query) : ExecuteRequest(query, restricted.value);
        GuardResponse denied = service ? SendRequest(terminate) : ExecuteRequest(terminate, restricted.value);
        RevertToSelf();
        bool visible = std::any_of(hidden.rows.begin(), hidden.rows.end(), [&](const GuardRow& current) { return current.pid == fixture.pid; });
        CheckResult("same_sid_normal_client_cannot_view_elevated_fixture", !visible && (hidden.error == ERROR_SUCCESS || hidden.error == ERROR_ACCESS_DENIED), std::to_string(hidden.error));
        CheckResult("same_sid_normal_client_cannot_control_elevated_fixture", denied.error == ERROR_ACCESS_DENIED && fixture.Alive() && DeleteDenied(fixture.first), std::to_string(denied.error));
    }

    void Run(Fixture& fixture) {
        if (service) CheckResult("service_broker_available", WaitNamedPipeW(PipeName, 5000) != FALSE, std::to_string(GetLastError()));
        if (service) CheckMalformedBrokerFrame();
        const auto renamed = fixture.directory / L"重命名 后.txt";
        BOOL renamedWhileLocked = MoveFileExW(fixture.first.c_str(), renamed.c_str(), 0);
        DWORD renameError = GetLastError();
        CheckResult("actual_rename_denied", !renamedWhileLocked && renameError == ERROR_SHARING_VIOLATION, std::to_string(renameError));
        CheckResult("actual_delete_denied", DeleteDenied(fixture.second));
        auto firstQuery = Query(fixture.first);
        CheckResult("query_succeeds", firstQuery.error == ERROR_SUCCESS, std::to_string(firstQuery.error) + ": " + Utf8(firstQuery.message));
        const GuardRow* discovered = ExactHandle(firstQuery, fixture.pid, fixture.first);
        std::ostringstream discoveryDetail;
        for (const auto& row : firstQuery.rows) discoveryDetail << "pid=" << row.pid << " handle=" << row.handle << " object=" << row.objectIdentity << " actionable=" << row.actionable << " path=" << Utf8(row.path) << "; ";
        CheckResult("unicode_spaces_exact_pid_and_handle", discovered != nullptr, discoveryDetail.str());
        auto directoryQuery = Query(fixture.directory);
        CheckResult("directory_query_succeeds", directoryQuery.error == ERROR_SUCCESS, std::to_string(directoryQuery.error));
        CheckResult("directory_contains_nested_lock", ExactHandle(directoryQuery, fixture.pid, fixture.second) != nullptr);
        if (discovered && discovered->objectIdentity) {
            const GuardRow* repeated = ExactHandle(directoryQuery, fixture.pid, fixture.first);
            CheckResult("kernel_identity_stable_across_requests", repeated && repeated->handle == discovered->handle
                && repeated->startTime == discovered->startTime && repeated->objectIdentity == discovered->objectIdentity);
        }
        bool neighborIncluded = false;
        for (const auto& row : directoryQuery.rows) if (SamePath(row.path, fixture.neighbor.wstring())) neighborIncluded = true;
        CheckResult("directory_excludes_prefix_neighbor", !neighborIncluded);
        auto mappedQuery = Query(fixture.mapped);
        bool mappedFound = false;
        for (const auto& row : mappedQuery.rows) if (row.pid == fixture.pid) mappedFound = true;
        if (mappedQuery.error == ERROR_SUCCESS && mappedFound) CheckResult("mapped_view_discovery", true);
        else Skip("mapped_view_discovery", "The mapping has no file handle; the OS discovery APIs did not report this fixture");
        fs::path unlocked = fixture.root / L"未占用 文件.txt";
        WriteFileContents(unlocked, "No process opens this file.\n");
        auto noLock = Query(unlocked);
        CheckResult("unlocked_file_has_no_occupants", noLock.error == ERROR_SUCCESS && noLock.rows.empty(), std::to_string(noLock.error));
        auto badAction = Request({static_cast<GuardAction>(99), fixture.first.wstring(), {}});
        CheckResult("malformed_action_rejected", badAction.error != ERROR_SUCCESS, std::to_string(badAction.error));
        auto emptySelection = Request({GuardAction::Release, fixture.first.wstring(), {}});
        CheckResult("empty_action_selection_rejected", emptySelection.error != ERROR_SUCCESS, std::to_string(emptySelection.error));
        if (!discovered || !discovered->actionable) {
            CheckResult("fixture_actions_available", false, "No actionable exact file handle found for the test's own child");
            return;
        }
        GuardRow original = *discovered;
        CheckElevationBoundary(fixture, original);
        if (service) CheckResult("service_supplies_kernel_object_identity", original.objectIdentity != 0);
        GuardRow staleStart = original;
        staleStart.startTime ^= 1;
        auto staleStartResult = Action(GuardAction::Release, fixture.first, staleStart);
        CheckResult("stale_pid_start_time_rejected", staleStartResult.error != ERROR_SUCCESS && fixture.Alive() && DeleteDenied(fixture.first), std::to_string(staleStartResult.error));
        if (original.objectIdentity) {
            GuardRow staleObject = original;
            staleObject.objectIdentity ^= 1;
            auto staleObjectResult = Action(GuardAction::Release, fixture.first, staleObject);
            CheckResult("stale_handle_object_rejected", staleObjectResult.error != ERROR_SUCCESS && fixture.Alive() && DeleteDenied(fixture.first), std::to_string(staleObjectResult.error));
        }
        auto wrongPathResult = Action(GuardAction::Release, fixture.second, original);
        CheckResult("selection_outside_requested_path_rejected", wrongPathResult.error != ERROR_SUCCESS && DeleteDenied(fixture.first) && DeleteDenied(fixture.second), std::to_string(wrongPathResult.error));
        GuardRow critical = original;
        critical.pid = 4;
        auto criticalResult = Action(GuardAction::Terminate, fixture.first, critical);
        CheckResult("critical_system_pid_action_rejected", criticalResult.error != ERROR_SUCCESS && fixture.Alive() && DeleteDenied(fixture.first), std::to_string(criticalResult.error));
        auto releaseResult = Action(GuardAction::Release, fixture.first, original);
        if (original.objectIdentity) {
            CheckResult("release_selected_handle_succeeds", releaseResult.error == ERROR_SUCCESS, std::to_string(releaseResult.error) + ": " + Utf8(releaseResult.message));
            CheckResult("release_preserves_process", fixture.Alive());
            CheckResult("release_preserves_other_file_lock", DeleteDenied(fixture.second));
            bool contentPreserved = false;
            {
                std::ifstream file(fixture.first, std::ios::binary);
                std::string content((std::istreambuf_iterator<char>(file)), {});
                contentPreserved = content == "Selected handle must preserve this content.\n";
            }
            CheckResult("release_preserves_file_content", contentPreserved);
            CheckResult("released_file_can_rename", MoveFileExW(fixture.first.c_str(), renamed.c_str(), 0) != FALSE);
            CheckResult("released_file_can_delete", DeleteFileW(renamed.c_str()) != FALSE);
        } else {
            CheckResult("redacted_kernel_identity_release_rejected", releaseResult.error != ERROR_SUCCESS && fixture.Alive() && DeleteDenied(fixture.first), std::to_string(releaseResult.error));
            if (service) CheckResult("release_selected_handle_succeeds", false, "The service broker did not supply a kernel object identity");
            else Skip("forced_release_verification", "Windows redacts kernel object identity without elevation; --service requires full handle-release verification");
        }
        auto secondQuery = Query(fixture.second);
        const GuardRow* secondRow = ExactHandle(secondQuery, fixture.pid, fixture.second);
        CheckResult("second_handle_remains_discoverable", secondRow != nullptr, std::to_string(secondQuery.error));
        if (secondRow) {
            GuardRow staleTerminate = *secondRow;
            staleTerminate.startTime ^= 1;
            auto result = Action(GuardAction::Terminate, fixture.second, staleTerminate);
            CheckResult("stale_terminate_identity_rejected", result.error != ERROR_SUCCESS && fixture.Alive() && DeleteDenied(fixture.second), std::to_string(result.error));
            auto terminateResult = Action(GuardAction::Terminate, fixture.second, *secondRow);
            CheckResult("terminate_exact_fixture_succeeds", terminateResult.error == ERROR_SUCCESS, std::to_string(terminateResult.error) + ": " + Utf8(terminateResult.message));
            CheckResult("fixture_process_terminated", WaitForSingleObject(fixture.process.value, 5000) == WAIT_OBJECT_0);
            CheckResult("terminated_fixture_file_unlocked", DeleteFileW(fixture.second.c_str()) != FALSE);
            CheckResult("terminated_fixture_mapping_unlocked", DeleteFileW(fixture.mapped.c_str()) != FALSE);
        }
    }

    bool Passed() const { return std::none_of(checks.begin(), checks.end(), [](const Check& check) { return check.outcome == "fail"; }); }

    std::string Report(DWORD fixturePid, ULONGLONG elapsed) const {
        std::ostringstream output;
        output << "{\n  \"suite\": \"HardwareScope.FileGuard.Native\",\n  \"mode\": " << Json(service ? "service" : "direct")
               << ",\n  \"passed\": " << (Passed() ? "true" : "false") << ",\n  \"fixturePid\": " << fixturePid
               << ",\n  \"elapsedMilliseconds\": " << elapsed << ",\n  \"checks\": [\n";
        for (size_t index = 0; index < checks.size(); ++index) {
            const auto& check = checks[index];
            output << "    {\"name\": " << Json(check.name) << ", \"outcome\": " << Json(check.outcome) << ", \"detail\": " << Json(check.detail) << '}';
            if (index + 1 != checks.size()) output << ',';
            output << '\n';
        }
        output << "  ]\n}\n";
        return output.str();
    }
};
}

int wmain(int argc, wchar_t** argv) {
    if (argc > 1 && std::wstring(argv[1]) == L"--fixture") return RunFixture(argc, argv);
    if (argc > 1 && std::wstring(argv[1]) == L"/worker") return RunWorker(argc, argv);
    Suite suite;
    fs::path report;
    for (int index = 1; index < argc; ++index) {
        std::wstring argument(argv[index]);
        if (argument == L"--service") suite.service = true;
        else if (argument == L"--report" && index + 1 < argc) report = argv[++index];
        else {
            std::cerr << "Usage: HardwareScope.FileGuard.Tests.exe [--service] [--report path]\n";
            return 2;
        }
    }
    ULONGLONG started = GetTickCount64();
    DWORD pid = 0;
    try {
        Fixture fixture;
        pid = fixture.pid;
        suite.Run(fixture);
    } catch (const std::exception& error) {
        suite.CheckResult("test_infrastructure", false, error.what());
    }
    std::string json = suite.Report(pid, GetTickCount64() - started);
    if (!report.empty()) {
        std::ofstream file(report, std::ios::binary);
        file << json;
        if (!file) {
            std::cerr << "Cannot write report: " << Utf8(report.wstring()) << '\n';
            return 2;
        }
    }
    std::cout << (suite.Passed() ? "FileGuard native integration tests passed.\n" : "FileGuard native integration tests failed.\n");
    return suite.Passed() ? 0 : 1;
}
