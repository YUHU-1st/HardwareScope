#pragma once
#include <windows.h>
#include <cstdint>
#include <array>
#include <string>
#include <vector>

namespace fileguard {
using GuardIdentityKey = std::array<BYTE, 32>;
enum class GuardAction : uint32_t { Query = 0, Release = 1, Terminate = 2 };
struct GuardRow {
    DWORD pid = 0;
    uint64_t startTime = 0;
    uint64_t handle = 0;
    uint64_t objectIdentity = 0; // Opaque keyed identity; never a kernel address.
    std::wstring path;
    std::wstring processName;
    std::wstring imagePath;
    std::wstring kind;
    std::wstring status;
    bool actionable = false;
};
struct GuardRequest {
    GuardAction action = GuardAction::Query;
    std::wstring path;
    std::vector<GuardRow> selection;
};
struct GuardResponse {
    DWORD error = ERROR_SUCCESS;
    std::wstring message;
    std::vector<GuardRow> rows;
    bool partial = false;
};
// The optional token is the pipe client's impersonation token, never supplied by UI wire data.
GuardResponse QueryOccupancy(const std::wstring& path, HANDLE clientToken = nullptr, DWORD servicePid = 0, const GuardIdentityKey* identityKey = nullptr);
GuardResponse ExecuteRequest(const GuardRequest& request, HANDLE clientToken = nullptr, DWORD servicePid = 0, const GuardIdentityKey* identityKey = nullptr);
}
