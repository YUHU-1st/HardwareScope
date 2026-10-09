#pragma once
#include "engine.h"
namespace fileguard {
inline constexpr wchar_t ServiceName[] = L"HardwareScopeFileGuard";
inline constexpr wchar_t PipeName[] = L"\\\\.\\pipe\\HardwareScope.FileGuard.v1";
// Blocks for at most the broker/worker deadline; call from the UI's background thread.
GuardResponse SendRequest(const GuardRequest& request);
int RunService();
// Full argv: executable /worker readHandle writeHandle clientTokenHandle servicePid.
int RunWorker(int argc, wchar_t** argv);
}
