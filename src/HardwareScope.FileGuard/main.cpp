#include <windows.h>
#include <commctrl.h>
#include <shellapi.h>
#include <shlobj.h>
#include <uxtheme.h>
#include <dwmapi.h>
#include <wincodec.h>
#include <wrl/client.h>
#include <algorithm>
#include <set>
#include <string>
#include <thread>
#include <vector>
#include "engine.h"
#include "service.h"
using namespace fileguard;
#pragma comment(lib, "Uxtheme.lib")
#pragma comment(lib, "Dwmapi.lib")
#pragma comment(lib, "Windowscodecs.lib")

static constexpr UINT Completed = WM_APP + 1;
static constexpr UINT Capture = WM_APP + 2;
static HINSTANCE instance;
static std::wstring Executable() {
    std::vector<wchar_t> path(32768);
    GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size()));
    return path.data();
}
static std::wstring ErrorText(DWORD error) {
    wchar_t* text = nullptr;
    FormatMessageW(FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS,
        nullptr, error, 0, reinterpret_cast<wchar_t*>(&text), 0, nullptr);
    std::wstring result = text ? text : L"未知错误";
    if (text) LocalFree(text);
    return result + L"（" + std::to_wstring(error) + L"）";
}
static DWORD StopServiceAndWait(SC_HANDLE service) {
    SERVICE_STATUS_PROCESS status{}; DWORD bytes = 0;
    if (!QueryServiceStatusEx(service, SC_STATUS_PROCESS_INFO, reinterpret_cast<BYTE*>(&status), sizeof(status), &bytes)) return GetLastError();
    if (status.dwCurrentState == SERVICE_STOPPED) return 0;
    if (status.dwCurrentState != SERVICE_STOP_PENDING) {
        SERVICE_STATUS ignored{};
        if (!ControlService(service, SERVICE_CONTROL_STOP, &ignored)) {
            DWORD error = GetLastError();
            if (error != ERROR_SERVICE_NOT_ACTIVE) return error;
        }
    }
    ULONGLONG deadline = GetTickCount64() + 25000;
    do {
        if (!QueryServiceStatusEx(service, SC_STATUS_PROCESS_INFO, reinterpret_cast<BYTE*>(&status), sizeof(status), &bytes)) return GetLastError();
        if (status.dwCurrentState == SERVICE_STOPPED) return 0;
        Sleep(100);
    } while (GetTickCount64() < deadline);
    return ERROR_TIMEOUT;
}
// Only the protected installed copy can become a LocalSystem service.
static bool IsInstalledLocation() {
    PWSTR directory = nullptr;
    if (FAILED(SHGetKnownFolderPath(FOLDERID_ProgramFiles, 0, nullptr, &directory))) return false;
    std::wstring prefix = directory; CoTaskMemFree(directory); prefix += L"\\";
    auto path = Executable();
    return path.size() > prefix.size() && _wcsnicmp(path.c_str(), prefix.c_str(), prefix.size()) == 0;
}
static DWORD ConfigureService(const std::wstring& action) {
    bool install = action == L"install-auto" || action == L"install-manual";
    if (!install && action != L"auto" && action != L"manual" && action != L"start" && action != L"stop" && action != L"uninstall") return ERROR_INVALID_PARAMETER;
    if (install && !IsInstalledLocation()) return ERROR_ACCESS_DENIED;
    SC_HANDLE manager = OpenSCManagerW(nullptr, nullptr, SC_MANAGER_CONNECT | (install ? SC_MANAGER_CREATE_SERVICE : 0));
    if (!manager) return GetLastError();
    DWORD access = SERVICE_QUERY_STATUS | SERVICE_QUERY_CONFIG;
    if (install || action == L"auto" || action == L"manual") access |= SERVICE_CHANGE_CONFIG | SERVICE_START;
    if (action == L"start") access |= SERVICE_START;
    if (action == L"stop" || action == L"uninstall") access |= SERVICE_STOP;
    if (action == L"uninstall") access |= DELETE;
    SC_HANDLE service = OpenServiceW(manager, ServiceName, access);
    DWORD error = service ? 0 : GetLastError();
    auto command = L"\"" + Executable() + L"\" /service";
    if (!service && error == ERROR_SERVICE_DOES_NOT_EXIST && install) {
        service = CreateServiceW(manager, ServiceName, L"HardwareScope 文件占用服务", access,
            SERVICE_WIN32_OWN_PROCESS, action == L"install-auto" ? SERVICE_AUTO_START : SERVICE_DEMAND_START,
            SERVICE_ERROR_NORMAL, command.c_str(), nullptr, nullptr, nullptr, nullptr, nullptr);
        error = service ? 0 : GetLastError();
    }
    if (!service) {
        CloseServiceHandle(manager);
        return ((action == L"stop" || action == L"uninstall") && error == ERROR_SERVICE_DOES_NOT_EXIST) ? 0 : error;
    }
    if (install || action == L"auto" || action == L"manual") {
        bool automatic = action == L"auto" || action == L"install-auto";
        if (!ChangeServiceConfigW(service, SERVICE_NO_CHANGE, automatic ? SERVICE_AUTO_START : SERVICE_DEMAND_START,
                SERVICE_NO_CHANGE, install ? command.c_str() : nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr)) error = GetLastError();
        SERVICE_DELAYED_AUTO_START_INFO delay{ automatic ? TRUE : FALSE };
        if (!error && !ChangeServiceConfig2W(service, SERVICE_CONFIG_DELAYED_AUTO_START_INFO, &delay)) error = GetLastError();
        if (!error && install) {
            SERVICE_DESCRIPTIONW description{ const_cast<wchar_t*>(L"按需查看文件占用并执行用户选择的释放句柄或结束进程操作。空闲时不扫描、不联网、不显示窗口。") };
            if (!ChangeServiceConfig2W(service, SERVICE_CONFIG_DESCRIPTION, &description)) error = GetLastError();
        }
        if (!error && !StartServiceW(service, 0, nullptr)) {
            auto startError = GetLastError();
            if (startError != ERROR_SERVICE_ALREADY_RUNNING) error = startError;
        }
    } else if (action == L"start") {
        if (!StartServiceW(service, 0, nullptr) && GetLastError() != ERROR_SERVICE_ALREADY_RUNNING) error = GetLastError();
    } else {
        error = StopServiceAndWait(service);
        if (!error && action == L"uninstall" && !DeleteService(service)) error = GetLastError();
    }
    if (!error && (install || action == L"start")) {
        SERVICE_STATUS_PROCESS status{}; DWORD bytes = 0;
        ULONGLONG deadline = GetTickCount64() + 10000;
        do {
            if (!QueryServiceStatusEx(service, SC_STATUS_PROCESS_INFO, reinterpret_cast<BYTE*>(&status), sizeof(status), &bytes)) { error = GetLastError(); break; }
            if (status.dwCurrentState == SERVICE_RUNNING) break;
            if (status.dwCurrentState == SERVICE_STOPPED) { error = status.dwWin32ExitCode ? status.dwWin32ExitCode : ERROR_SERVICE_NOT_ACTIVE; break; }
            Sleep(100);
        } while (GetTickCount64() < deadline);
        if (!error && status.dwCurrentState != SERVICE_RUNNING) error = ERROR_TIMEOUT;
    }
    CloseServiceHandle(service); CloseServiceHandle(manager);
    return error;
}
static std::wstring ServiceText() {
    SC_HANDLE manager = OpenSCManagerW(nullptr, nullptr, SC_MANAGER_CONNECT);
    SC_HANDLE service = manager ? OpenServiceW(manager, ServiceName, SERVICE_QUERY_STATUS | SERVICE_QUERY_CONFIG) : nullptr;
    if (!service) { if (manager) CloseServiceHandle(manager); return L"服务未安装 · 可使用当前权限查询；安装版支持安静后台服务"; }
    SERVICE_STATUS_PROCESS state{}; DWORD bytes = 0;
    QueryServiceStatusEx(service, SC_STATUS_PROCESS_INFO, reinterpret_cast<BYTE*>(&state), sizeof(state), &bytes);
    std::vector<BYTE> config(8192);
    bool automatic = QueryServiceConfigW(service, reinterpret_cast<QUERY_SERVICE_CONFIGW*>(config.data()), static_cast<DWORD>(config.size()), &bytes) && reinterpret_cast<QUERY_SERVICE_CONFIGW*>(config.data())->dwStartType == SERVICE_AUTO_START;
    CloseServiceHandle(service); CloseServiceHandle(manager);
    return std::wstring(L"后台服务：") + (state.dwCurrentState == SERVICE_RUNNING ? L"运行中" : L"已停止") + (automatic ? L" · 开机自动启动" : L" · 手动启动") + L" · 空闲时不扫描";
}

enum Controls { Path = 101, BrowseFile, BrowseFolder, Refresh, Release, Terminate, ElevateButton, Automatic, Manual, Start, Stop, List, Status, ServiceLabel, Note, Heading, Subtitle, ThemeSystem, ThemeLight, ThemeDark };
struct Palette {
    COLORREF background, sidebar, card, input, elevated, text, muted, stroke, strong, accent, signal, warning, danger;
};
static constexpr COLORREF Hex(DWORD value) { return RGB((value >> 16) & 255, (value >> 8) & 255, value & 255); }
// Native GDI interpretation of KUNLUN, https://github.com/yoruuuchan/yoru-design-systems.
// The original visual tokens are retained; installed Windows fonts replace web fonts.
static constexpr Palette DarkPalette{ Hex(0x06080D), Hex(0x04060A), Hex(0x0D1320), Hex(0x02040A), Hex(0x131A2A), Hex(0xE3EAF3), Hex(0x95A3B4), Hex(0x1A2540), Hex(0x2A3A5C), Hex(0x5FD9FF), Hex(0x00B8FF), Hex(0xFFB000), Hex(0xFF7878) };
static constexpr Palette LightPalette{ Hex(0xE2E9F2), Hex(0xD2DCE8), Hex(0xF7FAFD), Hex(0xD9E2EE), Hex(0xFFFFFF), Hex(0x0C1526), Hex(0x4A5C7A), Hex(0xC7D3E3), Hex(0x9DAEC7), Hex(0x0075A8), Hex(0x0098D4), Hex(0x956100), Hex(0xB22020) };
struct WindowState {
    HWND window{}, path{}, list{}, status{}, service{};
    HWND hover{};
    HFONT font{}, headingFont{}, mono{}, micro{};
    HBRUSH background{}, card{}, input{};
    HIMAGELIST rowImages{};
    Palette colors = LightPalette;
    UINT dpi = 96;
    UINT appearanceMessage = 0;
    DWORD theme = 0;
    int themeOverride = -1;
    bool dark = false;
    std::wstring capturePath;
    int captureResult = 0;
    bool captureSmall = false;
    bool busy = false, closePending = false;
    std::vector<GuardRow> rows;
    std::thread worker;
};
static WindowState ui;
static int Scale(int value) { return MulDiv(value, static_cast<int>(ui.dpi), 96); }
static HWND Child(int id) { return GetDlgItem(ui.window, id); }
static COLORREF Blend(COLORREF a, COLORREF b, int amount) {
    return RGB((GetRValue(a) * (100 - amount) + GetRValue(b) * amount) / 100,
        (GetGValue(a) * (100 - amount) + GetGValue(b) * amount) / 100,
        (GetBValue(a) * (100 - amount) + GetBValue(b) * amount) / 100);
}
static DWORD ReadTheme() {
    DWORD value = 0, size = sizeof(value);
    if (RegGetValueW(HKEY_CURRENT_USER, L"Software\\HardwareScope\\Appearance", L"Theme", RRF_RT_REG_DWORD, nullptr, &value, &size) != ERROR_SUCCESS || value > 2) return 0;
    return value;
}
static bool SystemDark() {
    DWORD light = 1, size = sizeof(light);
    RegGetValueW(HKEY_CURRENT_USER, L"Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize", L"AppsUseLightTheme", RRF_RT_REG_DWORD, nullptr, &light, &size);
    return light == 0;
}
static void ApplyTheme() {
    ui.theme = ui.themeOverride >= 0 ? static_cast<DWORD>(ui.themeOverride) : ReadTheme();
    ui.dark = ui.theme == 2 || (ui.theme == 0 && SystemDark());
    ui.colors = ui.dark ? DarkPalette : LightPalette;
    if (ui.background) DeleteObject(ui.background);
    if (ui.card) DeleteObject(ui.card);
    if (ui.input) DeleteObject(ui.input);
    ui.background = CreateSolidBrush(ui.colors.background); ui.card = CreateSolidBrush(ui.colors.card); ui.input = CreateSolidBrush(ui.colors.input);
    BOOL dark = ui.dark ? TRUE : FALSE;
    DwmSetWindowAttribute(ui.window, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, &dark, sizeof(dark));
    DWORD square = 1 /* DWMWCP_DONOTROUND */;
    DwmSetWindowAttribute(ui.window, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, &square, sizeof(square));
    if (ui.list) {
        ListView_SetBkColor(ui.list, ui.colors.card); ListView_SetTextBkColor(ui.list, ui.colors.card); ListView_SetTextColor(ui.list, ui.colors.text);
        InvalidateRect(ListView_GetHeader(ui.list), nullptr, TRUE);
    }
    RedrawWindow(ui.window, nullptr, nullptr, RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN);
}
static void SaveTheme(DWORD theme) {
    HKEY key = nullptr;
    LSTATUS error = RegCreateKeyExW(HKEY_CURRENT_USER, L"Software\\HardwareScope\\Appearance", 0, nullptr, 0, KEY_QUERY_VALUE | KEY_SET_VALUE, nullptr, &key, nullptr);
    if (error == ERROR_SUCCESS) {
        DWORD type = 0, size = 0;
        LSTATUS old = RegQueryValueExW(key, L"Theme", nullptr, &type, nullptr, &size);
        if (old == ERROR_SUCCESS && size <= 65536) {
            std::vector<BYTE> value(size);
            error = RegQueryValueExW(key, L"Theme", nullptr, &type, value.data(), &size);
            if (error == ERROR_SUCCESS) error = RegSetValueExW(key, L"ThemeBackup", 0, type, value.data(), size);
        } else if (old != ERROR_FILE_NOT_FOUND) error = old == ERROR_SUCCESS ? ERROR_INVALID_DATA : old;
        if (error == ERROR_SUCCESS) error = RegSetValueExW(key, L"Theme", 0, REG_DWORD, reinterpret_cast<const BYTE*>(&theme), sizeof(theme));
        RegCloseKey(key);
    }
    if (error != ERROR_SUCCESS) { SetWindowTextW(ui.status, (L"主题设置未保存：" + ErrorText(static_cast<DWORD>(error))).c_str()); return; }
    ui.themeOverride = -1; ApplyTheme();
    SendNotifyMessageW(HWND_BROADCAST, ui.appearanceMessage, 0, 0);
}
static void Chamfer(HDC dc, RECT rect, COLORREF fill, COLORREF stroke, int cut = 12) {
    int c = Scale(cut);
    POINT points[] = {{rect.left, rect.top}, {rect.right - c, rect.top}, {rect.right, rect.top + c},
        {rect.right, rect.bottom}, {rect.left + c, rect.bottom}, {rect.left, rect.bottom - c}};
    HBRUSH brush = CreateSolidBrush(fill); HPEN pen = CreatePen(PS_SOLID, Scale(1), stroke);
    HGDIOBJ oldBrush = SelectObject(dc, brush), oldPen = SelectObject(dc, pen);
    Polygon(dc, points, 6); SelectObject(dc, oldPen); SelectObject(dc, oldBrush); DeleteObject(pen); DeleteObject(brush);
}
static void Text(HDC dc, const std::wstring& text, RECT rect, HFONT font, COLORREF color, UINT format = DT_LEFT | DT_SINGLELINE | DT_VCENTER | DT_END_ELLIPSIS | DT_NOPREFIX) {
    HGDIOBJ previous = SelectObject(dc, font); SetBkMode(dc, TRANSPARENT); SetTextColor(dc, color);
    DrawTextW(dc, text.c_str(), static_cast<int>(text.size()), &rect, format); SelectObject(dc, previous);
}
static LRESULT CALLBACK ButtonProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam, UINT_PTR, DWORD_PTR) {
    if (message == WM_MOUSEMOVE && ui.hover != window) {
        if (ui.hover) InvalidateRect(ui.hover, nullptr, FALSE);
        ui.hover = window; TRACKMOUSEEVENT track{ sizeof(track), TME_LEAVE, window, 0 }; TrackMouseEvent(&track); InvalidateRect(window, nullptr, FALSE);
    } else if (message == WM_MOUSELEAVE && ui.hover == window) { ui.hover = nullptr; InvalidateRect(window, nullptr, FALSE); }
    return DefSubclassProc(window, message, wParam, lParam);
}
static HWND Add(const wchar_t* type, const wchar_t* title, DWORD style, int id) {
    bool button = wcscmp(type, L"BUTTON") == 0;
    if (button) style = (style & ~BS_TYPEMASK) | BS_OWNERDRAW;
    auto control = CreateWindowExW(0, type, title,
        WS_CHILD | WS_VISIBLE | style, 0, 0, 0, 0, ui.window, reinterpret_cast<HMENU>(static_cast<INT_PTR>(id)), instance, nullptr);
    SendMessageW(control, WM_SETFONT, reinterpret_cast<WPARAM>(ui.font), TRUE);
    if (button) SetWindowSubclass(control, ButtonProc, 1, 0);
    return control;
}
static void SetFonts() {
    if (ui.font) DeleteObject(ui.font);
    if (ui.headingFont) DeleteObject(ui.headingFont);
    if (ui.mono) DeleteObject(ui.mono);
    if (ui.micro) DeleteObject(ui.micro);
    ui.font = CreateFontW(-Scale(14), 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET, 0, 0, CLEARTYPE_QUALITY, 0, L"Microsoft YaHei UI");
    ui.headingFont = CreateFontW(-Scale(28), 0, 0, 0, FW_SEMIBOLD, FALSE, FALSE, FALSE, DEFAULT_CHARSET, 0, 0, CLEARTYPE_QUALITY, 0, L"Microsoft YaHei UI");
    ui.mono = CreateFontW(-Scale(14), 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET, 0, 0, CLEARTYPE_QUALITY, 0, L"Consolas");
    ui.micro = CreateFontW(-Scale(12), 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET, 0, 0, CLEARTYPE_QUALITY, 0, L"Consolas");
    for (int i = Path; i <= ThemeDark; ++i) SendMessageW(Child(i), WM_SETFONT, reinterpret_cast<WPARAM>(i == Heading ? ui.headingFont : (i == Path ? ui.mono : ui.font)), TRUE);
    if (ui.list) {
        HIMAGELIST old = ui.rowImages;
        ui.rowImages = ImageList_Create(1, Scale(36), ILC_COLOR32, 1, 1); ImageList_SetImageCount(ui.rowImages, 1);
        ListView_SetImageList(ui.list, ui.rowImages, LVSIL_SMALL); if (old) ImageList_Destroy(old);
    }
}
static void Layout() {
    RECT area{}; GetClientRect(ui.window, &area);
    int width = MulDiv(area.right, 96, static_cast<int>(ui.dpi)), height = MulDiv(area.bottom, 96, static_cast<int>(ui.dpi));
    bool compact = height < 720;
    auto place = [](int id, int x, int y, int w, int h) { MoveWindow(Child(id), Scale(x), Scale(y), Scale(w), Scale(h), TRUE); };
    place(Heading, 24, compact ? 36 : 44, width - 424, 40); place(Subtitle, 24, compact ? 80 : 92, width - 48, 24);
    place(ThemeSystem, width - 372, 44, 108, 36); place(ThemeLight, width - 256, 44, 108, 36); place(ThemeDark, width - 140, 44, 116, 36);
    place(ServiceLabel, 44, compact ? 152 : 172, width - 584, 40);
    int serviceY = compact ? 148 : 176;
    place(Automatic, width - 516, serviceY, 112, 36); place(Manual, width - 396, serviceY, 112, 36);
    place(Start, width - 276, serviceY, 112, 36); place(Stop, width - 156, serviceY, 112, 36);
    int pathY = compact ? 244 : 280;
    place(Path, 56, pathY + 8, width - 460, 24); place(BrowseFile, width - 388, pathY, 104, 40); place(BrowseFolder, width - 276, pathY, 112, 40);
    place(Refresh, width - 156, pathY, 112, 40);
    int listY = compact ? 368 : 412;
    place(List, 44, listY, width - 88, std::max(80, height - listY - 168));
    place(Status, 24, height - 140, width - 48, 36); place(Note, 24, height - 96, width - 48, 24);
    place(ElevateButton, 24, height - 64, 216, 40);
    place(Release, width - 356, height - 64, 160, 40); place(Terminate, width - 184, height - 64, 160, 40);
    const int widths[] = {208, 72, 304, std::max(264, width - 680)};
    for (int i = 0; i < 4; ++i) ListView_SetColumnWidth(ui.list, i, Scale(widths[i]));
    InvalidateRect(ui.window, nullptr, TRUE);
}
static RECT DipRect(int x, int y, int width, int height) { return {Scale(x), Scale(y), Scale(x + width), Scale(y + height)}; }
static void PaintWindow(HDC dc) {
    RECT rect{}; GetClientRect(ui.window, &rect); FillRect(dc, &rect, ui.background);
    int width = MulDiv(rect.right, 96, static_cast<int>(ui.dpi)), height = MulDiv(rect.bottom, 96, static_cast<int>(ui.dpi));
    bool compact = height < 720;
    HPEN grid = CreatePen(PS_SOLID, 1, Blend(ui.colors.background, ui.colors.stroke, 28)); HGDIOBJ old = SelectObject(dc, grid);
    for (int x = 24; x < width; x += 40) { MoveToEx(dc, Scale(x), 0, nullptr); LineTo(dc, Scale(x), Scale(128)); }
    for (int y = 8; y < 128; y += 40) { MoveToEx(dc, 0, Scale(y), nullptr); LineTo(dc, rect.right, Scale(y)); }
    SelectObject(dc, old); DeleteObject(grid);
    Text(dc, L"HARDWARESCOPE // FILE OPERATIONS", DipRect(24, 16, 520, 20), ui.micro, ui.colors.accent);
    Text(dc, L"APPEARANCE", DipRect(width - 372, 16, 344, 20), ui.micro, ui.colors.muted, DT_RIGHT | DT_SINGLELINE | DT_VCENTER);
    int serviceTop = compact ? 112 : 132, targetTop = compact ? 204 : 240, locksTop = compact ? 320 : 364;
    Chamfer(dc, DipRect(24, serviceTop, width - 48, compact ? 80 : 96), ui.colors.card, ui.colors.stroke);
    Text(dc, L"01 // BACKGROUND SERVICE", DipRect(44, serviceTop + 16, 400, 20), ui.micro, ui.colors.accent);
    Chamfer(dc, DipRect(24, targetTop, width - 48, compact ? 104 : 112), ui.colors.card, ui.colors.stroke);
    Text(dc, L"02 // TARGET PATH", DipRect(44, targetTop + 16, 400, 20), ui.micro, ui.colors.accent);
    Chamfer(dc, DipRect(44, targetTop + 40, width - 436, 40), ui.colors.input, GetFocus() == ui.path ? ui.colors.accent : ui.colors.strong, 6);
    Text(dc, L"FILE / DIRECTORY  ·  按需检查文件及子目录占用", DipRect(44, compact ? 284 : 328, width - 88, 20), ui.micro, ui.colors.muted);
    Chamfer(dc, DipRect(24, locksTop, width - 48, height - locksTop - 156), ui.colors.card, ui.colors.stroke);
    Text(dc, L"03 // LOCK OWNERS", DipRect(44, locksTop + 16, 360, 20), ui.micro, ui.colors.accent);
    int count = 0; for (int i = 0; i < static_cast<int>(ui.rows.size()); ++i) if (ListView_GetCheckState(ui.list, i)) ++count;
    wchar_t counters[80]{}; swprintf_s(counters, L"%02zu RECORDS / %02d SELECTED", ui.rows.size(), count);
    Text(dc, counters, DipRect(width - 420, locksTop + 16, 376, 20), ui.micro, ui.colors.muted, DT_RIGHT | DT_SINGLELINE | DT_VCENTER);
}
static void DrawButton(const DRAWITEMSTRUCT& item) {
    RECT rect = item.rcItem;
    bool inPanel = item.CtlID == BrowseFile || item.CtlID == BrowseFolder || item.CtlID == Refresh || (item.CtlID >= Automatic && item.CtlID <= Stop);
    FillRect(item.hDC, &rect, inPanel ? ui.card : ui.background);
    bool enabled = (item.itemState & ODS_DISABLED) == 0;
    bool active = item.CtlID >= ThemeSystem && item.CtlID <= ThemeDark && item.CtlID - ThemeSystem == ui.theme;
    bool hover = ui.hover == item.hwndItem && enabled;
    COLORREF signal = item.CtlID == Terminate ? ui.colors.danger : (item.CtlID == Release ? ui.colors.warning : ui.colors.accent);
    COLORREF border = enabled && (active || hover || item.CtlID == Refresh || item.CtlID == Release || item.CtlID == Terminate) ? signal : ui.colors.strong;
    COLORREF foreground = enabled ? ((active || item.CtlID == Refresh || item.CtlID == Release || item.CtlID == Terminate) ? signal : ui.colors.text) : Blend(ui.colors.muted, ui.colors.background, 34);
    Chamfer(item.hDC, rect, active || hover ? ui.colors.elevated : ui.colors.input, border, 6);
    wchar_t title[128]{}; GetWindowTextW(item.hwndItem, title, 128);
    if (item.itemState & ODS_SELECTED) OffsetRect(&rect, Scale(1), Scale(1));
    Text(item.hDC, title, rect, ui.font, foreground, DT_CENTER | DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX);
    if ((item.itemState & ODS_FOCUS) && !(item.itemState & ODS_NOFOCUSRECT)) {
        InflateRect(&rect, -Scale(5), -Scale(5)); SetTextColor(item.hDC, signal); DrawFocusRect(item.hDC, &rect);
    }
}
static void PaintHeader(HWND window, HDC dc) {
    RECT area{}; GetClientRect(window, &area);
    HBRUSH brush = CreateSolidBrush(ui.colors.elevated); FillRect(dc, &area, brush); DeleteObject(brush);
    for (int i = 0; i < Header_GetItemCount(window); ++i) {
        RECT rect{}; Header_GetItemRect(window, i, &rect); wchar_t title[128]{};
        HDITEMW item{}; item.mask = HDI_TEXT; item.pszText = title; item.cchTextMax = 128; Header_GetItem(window, i, &item);
        rect.left += Scale(12); Text(dc, title, rect, ui.micro, ui.colors.muted);
    }
    HPEN pen = CreatePen(PS_SOLID, 1, ui.colors.strong); HGDIOBJ old = SelectObject(dc, pen);
    MoveToEx(dc, 0, area.bottom - 1, nullptr); LineTo(dc, area.right, area.bottom - 1); SelectObject(dc, old); DeleteObject(pen);
}
static LRESULT CALLBACK HeaderProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam, UINT_PTR, DWORD_PTR) {
    if (message == HDM_LAYOUT) {
        LRESULT result = DefSubclassProc(window, message, wParam, lParam);
        auto layout = reinterpret_cast<HDLAYOUT*>(lParam); int delta = Scale(36) - layout->pwpos->cy;
        layout->pwpos->cy = Scale(36); layout->prc->top += delta; return result;
    }
    if (message == WM_ERASEBKGND) return 1;
    if (message == WM_PRINTCLIENT) { PaintHeader(window, reinterpret_cast<HDC>(wParam)); return 0; }
    if (message == WM_PAINT) {
        PAINTSTRUCT paint{}; HDC dc = BeginPaint(window, &paint); PaintHeader(window, dc);
        EndPaint(window, &paint); return 0;
    }
    return DefSubclassProc(window, message, wParam, lParam);
}
static void DrawRow(NMLVCUSTOMDRAW& draw) {
    int index = static_cast<int>(draw.nmcd.dwItemSpec); if (index < 0 || index >= static_cast<int>(ui.rows.size())) return;
    RECT rect{}; ListView_GetItemRect(ui.list, index, &rect, LVIR_BOUNDS);
    COLORREF background = index % 2 ? Blend(ui.colors.card, ui.colors.elevated, 36) : ui.colors.card;
    bool selected = (draw.nmcd.uItemState & CDIS_SELECTED) != 0;
    if (selected) background = ui.colors.elevated;
    HBRUSH brush = CreateSolidBrush(background); FillRect(draw.nmcd.hdc, &rect, brush); DeleteObject(brush);
    if (selected) { RECT bar = rect; bar.right = bar.left + Scale(3); brush = CreateSolidBrush(ui.colors.accent); FillRect(draw.nmcd.hdc, &bar, brush); DeleteObject(brush); }
    RECT check{rect.left + Scale(12), rect.top + (rect.bottom - rect.top - Scale(14)) / 2, 0, 0}; check.right = check.left + Scale(14); check.bottom = check.top + Scale(14);
    bool checked = ListView_GetCheckState(ui.list, index) != FALSE;
    Chamfer(draw.nmcd.hdc, check, checked ? ui.colors.signal : ui.colors.input, checked ? ui.colors.signal : ui.colors.strong, 1);
    if (checked) {
        HPEN pen = CreatePen(PS_SOLID, Scale(2), ui.colors.background); HGDIOBJ old = SelectObject(draw.nmcd.hdc, pen);
        MoveToEx(draw.nmcd.hdc, check.left + Scale(3), check.top + Scale(7), nullptr); LineTo(draw.nmcd.hdc, check.left + Scale(6), check.top + Scale(10)); LineTo(draw.nmcd.hdc, check.left + Scale(11), check.top + Scale(4));
        SelectObject(draw.nmcd.hdc, old); DeleteObject(pen);
    }
    const auto& row = ui.rows[index];
    std::wstring values[] = {row.processName, std::to_wstring(row.pid), row.kind + (row.status.empty() ? L"" : L" · " + row.status), row.path};
    HWND header = ListView_GetHeader(ui.list);
    for (int column = 0; column < 4; ++column) {
        RECT cell{}; Header_GetItemRect(header, column, &cell); MapWindowPoints(header, ui.list, reinterpret_cast<POINT*>(&cell), 2);
        cell.top = rect.top; cell.bottom = rect.bottom; cell.left += Scale(column == 0 ? 36 : 12); cell.right -= Scale(12);
        Text(draw.nmcd.hdc, values[column], cell, column == 2 ? ui.font : ui.mono, column == 1 ? ui.colors.accent : (row.actionable ? ui.colors.text : ui.colors.muted));
    }
}
static void PaintEmptyList(HWND window, HDC dc) {
    if (ui.rows.empty()) {
        RECT rect{}; GetClientRect(window, &rect); rect.top += Scale(36);
        RECT line = rect; int middle = (rect.top + rect.bottom) / 2; line.top = middle - Scale(32); line.bottom = middle;
        Text(dc, ui.busy ? L"// INSPECTING HANDLES" : L"// AWAITING TARGET", line, ui.mono, ui.busy ? ui.colors.accent : ui.colors.muted, DT_CENTER | DT_SINGLELINE | DT_VCENTER);
        line.top = middle + Scale(4); line.bottom = middle + Scale(32);
        Text(dc, ui.busy ? L"按需查询正在运行 · 等待占用结果" : L"选择文件或文件夹，然后执行 SCAN", line, ui.font, ui.colors.muted, DT_CENTER | DT_SINGLELINE | DT_VCENTER);
    }
}
static LRESULT CALLBACK ListProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam, UINT_PTR, DWORD_PTR) {
    if (message == WM_LBUTTONDOWN && !ui.busy) {
        LVHITTESTINFO hit{}; hit.pt = {static_cast<short>(LOWORD(lParam)), static_cast<short>(HIWORD(lParam))};
        int index = ListView_HitTest(window, &hit);
        if (index >= 0) {
            RECT row{}; ListView_GetItemRect(window, index, &row, LVIR_BOUNDS);
            RECT check{row.left + Scale(12), row.top + (row.bottom - row.top - Scale(14)) / 2, 0, 0};
            check.right = check.left + Scale(14); check.bottom = check.top + Scale(14);
            if (PtInRect(&check, hit.pt)) { SetFocus(window); ListView_SetCheckState(window, index, !ListView_GetCheckState(window, index)); return 0; }
        }
    }
    LRESULT result = DefSubclassProc(window, message, wParam, lParam);
    if (message == WM_PAINT) { HDC dc = GetDC(window); PaintEmptyList(window, dc); ReleaseDC(window, dc); }
    else if (message == WM_PRINTCLIENT) PaintEmptyList(window, reinterpret_cast<HDC>(wParam));
    return result;
}
static HRESULT CaptureWindow(const std::wstring& path) {
    // Render this application's client and child controls; no desktop/screen automation is used.
    RECT rect{}; GetClientRect(ui.window, &rect);
    HDC source = GetDC(ui.window), memory = CreateCompatibleDC(source);
    HBITMAP bitmap = CreateCompatibleBitmap(source, rect.right, rect.bottom);
    HGDIOBJ old = SelectObject(memory, bitmap);
    PaintWindow(memory);
    for (HWND child = GetWindow(ui.window, GW_CHILD); child; child = GetWindow(child, GW_HWNDNEXT)) {
        RECT area{}; GetWindowRect(child, &area); MapWindowPoints(nullptr, ui.window, reinterpret_cast<POINT*>(&area), 2);
        int saved = SaveDC(memory); SetViewportOrgEx(memory, area.left, area.top, nullptr);
        IntersectClipRect(memory, 0, 0, area.right - area.left, area.bottom - area.top);
        SendMessageW(child, WM_PRINT, reinterpret_cast<WPARAM>(memory), PRF_CLIENT | PRF_ERASEBKGND | PRF_CHILDREN);
        RestoreDC(memory, saved);
    }
    SelectObject(memory, old); DeleteDC(memory); ReleaseDC(ui.window, source);
    using Microsoft::WRL::ComPtr;
    ComPtr<IWICImagingFactory> factory; ComPtr<IWICBitmap> image; ComPtr<IWICStream> stream;
    ComPtr<IWICBitmapEncoder> encoder; ComPtr<IWICBitmapFrameEncode> frame; ComPtr<IWICFormatConverter> converter;
    HRESULT result = CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&factory));
    if (SUCCEEDED(result)) result = factory->CreateBitmapFromHBITMAP(bitmap, nullptr, WICBitmapIgnoreAlpha, &image);
    DeleteObject(bitmap);
    if (SUCCEEDED(result)) result = factory->CreateStream(&stream);
    if (SUCCEEDED(result)) result = stream->InitializeFromFilename(path.c_str(), GENERIC_WRITE);
    if (SUCCEEDED(result)) result = factory->CreateEncoder(GUID_ContainerFormatPng, nullptr, &encoder);
    if (SUCCEEDED(result)) result = encoder->Initialize(stream.Get(), WICBitmapEncoderNoCache);
    if (SUCCEEDED(result)) result = encoder->CreateNewFrame(&frame, nullptr);
    if (SUCCEEDED(result)) result = frame->Initialize(nullptr);
    if (SUCCEEDED(result)) result = frame->SetSize(static_cast<UINT>(rect.right), static_cast<UINT>(rect.bottom));
    WICPixelFormatGUID format = GUID_WICPixelFormat24bppBGR;
    if (SUCCEEDED(result)) result = frame->SetPixelFormat(&format);
    if (SUCCEEDED(result)) result = factory->CreateFormatConverter(&converter);
    if (SUCCEEDED(result)) result = converter->Initialize(image.Get(), format, WICBitmapDitherTypeNone, nullptr, 0, WICBitmapPaletteTypeCustom);
    if (SUCCEEDED(result)) result = frame->WriteSource(converter.Get(), nullptr);
    if (SUCCEEDED(result)) result = frame->Commit();
    if (SUCCEEDED(result)) result = encoder->Commit();
    return result;
}
static std::wstring PathText() {
    int count = GetWindowTextLengthW(ui.path);
    std::vector<wchar_t> path(static_cast<size_t>(count) + 1);
    GetWindowTextW(ui.path, path.data(), count + 1); return path.data();
}
static std::vector<GuardRow> Selection() {
    std::vector<GuardRow> rows;
    for (int i = 0; i < static_cast<int>(ui.rows.size()); ++i) if (ListView_GetCheckState(ui.list, i)) rows.push_back(ui.rows[i]);
    return rows;
}
static void Buttons() {
    auto selected = Selection();
    EnableWindow(Child(Release), !ui.busy && !selected.empty() && std::all_of(selected.begin(), selected.end(), [](const GuardRow& row) { return row.actionable && row.handle != 0 && row.objectIdentity != 0; }));
    EnableWindow(Child(Terminate), !ui.busy && !selected.empty() && std::all_of(selected.begin(), selected.end(), [](const GuardRow& row) { return row.actionable; }));
    for (auto id : { BrowseFile, BrowseFolder, Refresh, ElevateButton, Automatic, Manual, Start, Stop, Path, List }) EnableWindow(Child(id), !ui.busy);
    InvalidateRect(ui.list, nullptr, FALSE); InvalidateRect(ui.window, nullptr, FALSE);
}
static void Populate(const GuardResponse& response) {
    ListView_DeleteAllItems(ui.list); ui.rows = response.rows;
    for (int i = 0; i < static_cast<int>(ui.rows.size()); ++i) {
        const auto& row = ui.rows[i];
        LVITEMW item{}; item.mask = LVIF_TEXT; item.iItem = i;
        item.pszText = const_cast<wchar_t*>(row.processName.c_str()); ListView_InsertItem(ui.list, &item);
        auto pid = std::to_wstring(row.pid); ListView_SetItemText(ui.list, i, 1, pid.data());
        auto kind = row.kind + (row.status.empty() ? L"" : L" · " + row.status);
        ListView_SetItemText(ui.list, i, 2, kind.data());
        ListView_SetItemText(ui.list, i, 3, const_cast<wchar_t*>(row.path.c_str()));
    }
    std::wstring message = response.message;
    if (response.error) message += L" · " + ErrorText(response.error);
    if (!response.error && response.rows.empty()) message += L" · 未发现当前权限可见的占用；权限不足或内核驱动占用可能无法列出。";
    SetWindowTextW(ui.status, message.c_str()); SetWindowTextW(ui.service, ServiceText().c_str());
    InvalidateRect(ui.window, nullptr, FALSE);
}
static void BeginRequest(GuardAction action) {
    if (ui.busy) return;
    GuardRequest request; request.action = action; request.path = PathText(); request.selection = Selection();
    if (request.path.empty()) { SetWindowTextW(ui.status, L"请选择或输入需要检查的文件、文件夹路径。"); return; }
    if (action != GuardAction::Query) {
        if (request.selection.empty()) return;
        std::set<DWORD> pids; for (const auto& row : request.selection) pids.insert(row.pid);
        std::wstring question;
        if (action == GuardAction::Release) question = L"将强制关闭勾选的 " + std::to_wstring(request.selection.size()) + L" 个文件句柄，保留应用进程。\n\n应用可能丢失未保存数据、发生错误或崩溃；请先保存工作。文件映射需要关闭应用才能释放。\n\n继续释放这些句柄？";
        else question = L"将立即结束勾选的 " + std::to_wstring(pids.size()) + L" 个进程。\n\n这些进程中未保存的数据会丢失。\n\n继续结束这些进程？";
        if (MessageBoxW(ui.window, question.c_str(), action == GuardAction::Release ? L"确认释放文件句柄" : L"确认结束进程", MB_YESNO | MB_ICONWARNING | MB_DEFBUTTON2) != IDYES) return;
    }
    ui.busy = true; Buttons(); SetWindowTextW(ui.status, L"正在处理… 仅检查所选路径，最长等待约 20 秒。");
    HWND window = ui.window;
    ui.worker = std::thread([window, request = std::move(request)] {
        auto result = new GuardResponse(SendRequest(request));
        if (!PostMessageW(window, Completed, 0, reinterpret_cast<LPARAM>(result))) delete result;
    });
}
static void Choose(bool folder) {
    IFileOpenDialog* dialog = nullptr;
    if (FAILED(CoCreateInstance(CLSID_FileOpenDialog, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&dialog)))) return;
    DWORD flags = 0; dialog->GetOptions(&flags);
    dialog->SetOptions(flags | FOS_FORCEFILESYSTEM | (folder ? FOS_PICKFOLDERS : FOS_FILEMUSTEXIST));
    dialog->SetTitle(folder ? L"选择要查看占用的文件夹" : L"选择要查看占用的文件");
    if (SUCCEEDED(dialog->Show(ui.window))) {
        IShellItem* item = nullptr; PWSTR path = nullptr;
        if (SUCCEEDED(dialog->GetResult(&item))) {
            if (SUCCEEDED(item->GetDisplayName(SIGDN_FILESYSPATH, &path))) { SetWindowTextW(ui.path, path); CoTaskMemFree(path); }
            item->Release();
            BeginRequest(GuardAction::Query);
        }
    }
    dialog->Release();
}
static void ElevateView() {
    auto path = PathText();
    if (path.find(L'"') != std::wstring::npos) return;
    auto arguments = path.empty() ? L"" : L"/inspect \"" + path + L"\"";
    auto executable = Executable();
    SHELLEXECUTEINFOW launch{ sizeof(launch) }; launch.fMask = SEE_MASK_NOCLOSEPROCESS;
    launch.hwnd = ui.window; launch.lpVerb = L"runas"; launch.lpFile = executable.c_str(); launch.lpParameters = arguments.c_str(); launch.nShow = SW_SHOWNORMAL;
    if (ShellExecuteExW(&launch)) { if (launch.hProcess) CloseHandle(launch.hProcess); DestroyWindow(ui.window); }
    else SetWindowTextW(ui.status, GetLastError() == ERROR_CANCELLED ? L"已取消管理员授权。" : ErrorText(GetLastError()).c_str());
}
static void Configure(const wchar_t* action) {
    auto executable = Executable(); auto arguments = std::wstring(L"/config ") + action;
    SHELLEXECUTEINFOW launch{ sizeof(launch) }; launch.fMask = SEE_MASK_NOCLOSEPROCESS;
    launch.hwnd = ui.window; launch.lpVerb = L"runas"; launch.lpFile = executable.c_str(); launch.lpParameters = arguments.c_str(); launch.nShow = SW_HIDE;
    if (!ShellExecuteExW(&launch)) { SetWindowTextW(ui.status, L"服务设置未更改：未获得管理员授权。"); return; }
    ui.busy = true; Buttons(); SetWindowTextW(ui.status, L"正在更改后台服务设置…");
    HWND window = ui.window; HANDLE process = launch.hProcess;
    ui.worker = std::thread([window, process] {
        WaitForSingleObject(process, INFINITE); DWORD error = 0; GetExitCodeProcess(process, &error); CloseHandle(process);
        auto result = new GuardResponse;
        result->error = error; result->message = error ? L"服务设置失败，请先运行安装程序。" : L"后台服务设置已更新。";
        if (!PostMessageW(window, Completed, 1, reinterpret_cast<LPARAM>(result))) delete result;
    });
}
static LRESULT CALLBACK WindowProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam) {
    if (ui.appearanceMessage && message == ui.appearanceMessage) { ApplyTheme(); return 0; }
    switch (message) {
    case WM_CREATE: {
        ui.window = window; ui.dpi = GetDpiForWindow(window);
        ui.appearanceMessage = RegisterWindowMessageW(L"HardwareScope.AppearanceChanged.v1");
        ChangeWindowMessageFilterEx(window, ui.appearanceMessage, MSGFLT_ALLOW, nullptr);
        ApplyTheme(); SetFonts();
        Add(L"STATIC", L"文件占用 / FILE GUARD", 0, Heading);
        Add(L"STATIC", L"查看占用来源 · 选择性解除句柄 · 精确停止进程", 0, Subtitle);
        Add(L"BUTTON", L"SYSTEM 跟随", WS_TABSTOP, ThemeSystem);
        Add(L"BUTTON", L"LIGHT 浅色", WS_TABSTOP, ThemeLight);
        Add(L"BUTTON", L"DARK 深色", WS_TABSTOP, ThemeDark);
        ui.path = Add(L"EDIT", L"", ES_AUTOHSCROLL | WS_TABSTOP, Path);
        SendMessageW(ui.path, EM_SETCUEBANNER, FALSE, reinterpret_cast<LPARAM>(L"输入文件或文件夹的完整路径"));
        Add(L"BUTTON", L"文件 / FILE", WS_TABSTOP, BrowseFile);
        Add(L"BUTTON", L"目录 / DIR", WS_TABSTOP, BrowseFolder);
        ui.service = Add(L"STATIC", ServiceText().c_str(), 0, ServiceLabel);
        Add(L"BUTTON", L"自启 / AUTO", WS_TABSTOP, Automatic);
        Add(L"BUTTON", L"手动 / MANUAL", WS_TABSTOP, Manual);
        Add(L"BUTTON", L"启动 / START", WS_TABSTOP, Start);
        Add(L"BUTTON", L"停止 / STOP", WS_TABSTOP, Stop);
        Add(L"STATIC", L"操作提示 / 保存工作后再解除句柄或停止进程。需要查看其他用户及管理员应用时，使用管理员视图。", 0, Note);
        ui.list = Add(WC_LISTVIEWW, L"", LVS_REPORT | LVS_SHOWSELALWAYS | WS_TABSTOP, List);
        ListView_SetExtendedListViewStyle(ui.list, LVS_EX_FULLROWSELECT | LVS_EX_CHECKBOXES | LVS_EX_DOUBLEBUFFER | LVS_EX_LABELTIP);
        SetWindowTheme(ui.list, L"", L"");
        SetWindowSubclass(ui.list, ListProc, 1, 0); SetWindowSubclass(ListView_GetHeader(ui.list), HeaderProc, 1, 0);
        const wchar_t* titles[] = { L"APP / 进程", L"PID", L"STATUS / 占用状态", L"LOCKED FILE / 文件路径" };
        for (int i = 0; i < 4; ++i) { LVCOLUMNW column{}; column.mask = LVCF_TEXT | LVCF_WIDTH; column.pszText = const_cast<wchar_t*>(titles[i]); column.cx = 100; ListView_InsertColumn(ui.list, i, &column); }
        ui.status = Add(L"STATIC", L"READY / 选择目标后执行检查。后台服务保持安静，空闲时不扫描文件。", 0, Status);
        Add(L"BUTTON", L"检查 / SCAN", WS_TABSTOP, Refresh);
        Add(L"BUTTON", L"管理员视图 / ADMIN", WS_TABSTOP, ElevateButton);
        Add(L"BUTTON", L"解除句柄 / RELEASE", WS_TABSTOP, Release);
        Add(L"BUTTON", L"停止进程 / STOP", WS_TABSTOP, Terminate);
        SetFonts(); ApplyTheme(); Layout(); Buttons(); return 0;
    }
    case WM_ERASEBKGND: return 1;
    case WM_PAINT: {
        PAINTSTRUCT paint{}; HDC dc = BeginPaint(window, &paint); PaintWindow(dc); EndPaint(window, &paint); return 0;
    }
    case WM_PRINTCLIENT: PaintWindow(reinterpret_cast<HDC>(wParam)); return 0;
    case WM_DRAWITEM: DrawButton(*reinterpret_cast<DRAWITEMSTRUCT*>(lParam)); return TRUE;
    case WM_CTLCOLOREDIT: {
        HDC dc = reinterpret_cast<HDC>(wParam); SetTextColor(dc, ui.colors.text); SetBkColor(dc, ui.colors.input); return reinterpret_cast<LRESULT>(ui.input);
    }
    case WM_CTLCOLORSTATIC: {
        HDC dc = reinterpret_cast<HDC>(wParam); int id = GetDlgCtrlID(reinterpret_cast<HWND>(lParam));
        SetTextColor(dc, id == Heading ? ui.colors.text : (id == Note ? ui.colors.warning : ui.colors.muted));
        SetBkColor(dc, id == ServiceLabel ? ui.colors.card : ui.colors.background);
        return reinterpret_cast<LRESULT>(id == ServiceLabel ? ui.card : ui.background);
    }
    case WM_SETTINGCHANGE: if (ui.theme == 0) ApplyTheme(); return 0;
    case WM_SIZE: Layout(); return 0;
    case WM_GETMINMAXINFO: {
        auto info = reinterpret_cast<MINMAXINFO*>(lParam); info->ptMinTrackSize = { Scale(960), Scale(680) }; return 0;
    }
    case WM_DPICHANGED: {
        ui.dpi = HIWORD(wParam); SetFonts(); auto rect = reinterpret_cast<RECT*>(lParam);
        SetWindowPos(window, nullptr, rect->left, rect->top, rect->right - rect->left, rect->bottom - rect->top, SWP_NOZORDER | SWP_NOACTIVATE); Layout(); return 0;
    }
    case WM_COMMAND:
        if (LOWORD(wParam) == Path && (HIWORD(wParam) == EN_SETFOCUS || HIWORD(wParam) == EN_KILLFOCUS)) { InvalidateRect(window, nullptr, FALSE); return 0; }
        if (HIWORD(wParam) != BN_CLICKED) break;
        if (LOWORD(wParam) >= ThemeSystem && LOWORD(wParam) <= ThemeDark) { SaveTheme(LOWORD(wParam) - ThemeSystem); return 0; }
        if (ui.busy) break;
        switch (LOWORD(wParam)) {
        case BrowseFile: Choose(false); break; case BrowseFolder: Choose(true); break;
        case Refresh: BeginRequest(GuardAction::Query); break;
        case Release: BeginRequest(GuardAction::Release); break;
        case Terminate: BeginRequest(GuardAction::Terminate); break;
        case ElevateButton: ElevateView(); break;
        case Automatic: Configure(L"auto"); break; case Manual: Configure(L"manual"); break;
        case Start: Configure(L"start"); break; case Stop: Configure(L"stop"); break;
        } return 0;
    case WM_NOTIFY: {
        auto notification = reinterpret_cast<NMHDR*>(lParam);
        if (notification->idFrom == List && notification->code == LVN_ITEMCHANGED) Buttons();
        if (notification->idFrom == List && notification->code == NM_CUSTOMDRAW) {
            auto draw = reinterpret_cast<NMLVCUSTOMDRAW*>(lParam);
            if (draw->nmcd.dwDrawStage == CDDS_PREPAINT) return CDRF_NOTIFYITEMDRAW;
            if (draw->nmcd.dwDrawStage == CDDS_ITEMPREPAINT) { DrawRow(*draw); return CDRF_SKIPDEFAULT; }
        }
        return 0;
    }
    case Completed: {
        if (ui.worker.joinable()) ui.worker.join(); ui.busy = false;
        auto result = reinterpret_cast<GuardResponse*>(lParam);
        if (wParam == 1) {
            SetWindowTextW(ui.service, ServiceText().c_str());
            auto text = result->message + (result->error ? L" · " + ErrorText(result->error) : L""); SetWindowTextW(ui.status, text.c_str());
        } else Populate(*result);
        delete result; Buttons(); if (ui.closePending) DestroyWindow(window); else if (!ui.capturePath.empty()) PostMessageW(window, Capture, 0, 0); return 0;
    }
    case Capture: {
        UpdateWindow(window); HRESULT result = CaptureWindow(ui.capturePath); ui.captureResult = SUCCEEDED(result) ? 0 : static_cast<int>(result); DestroyWindow(window); return 0;
    }
    case WM_CLOSE:
        if (ui.busy) { ui.closePending = true; ShowWindow(window, SW_HIDE); } else DestroyWindow(window);
        return 0;
    case WM_DESTROY: PostQuitMessage(0); return 0;
    }
    return DefWindowProcW(window, message, wParam, lParam);
}
int WINAPI wWinMain(HINSTANCE app, HINSTANCE, PWSTR, int show) {
    instance = app; int argc = 0; wchar_t** argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (!argv) return ERROR_INVALID_PARAMETER;
    if (argc > 1 && std::wstring(argv[1]) == L"/service") { LocalFree(argv); return RunService(); }
    if (argc > 1 && std::wstring(argv[1]) == L"/worker") { int code = RunWorker(argc, argv); LocalFree(argv); return code; }
    if (argc == 3 && std::wstring(argv[1]) == L"/config") { DWORD code = ConfigureService(argv[2]); LocalFree(argv); return static_cast<int>(code); }
    std::wstring path;
    for (int i = 1; i < argc; ++i) {
        std::wstring argument = argv[i];
        if (i + 1 >= argc) { LocalFree(argv); return ERROR_INVALID_PARAMETER; }
        if (argument == L"/inspect") path = argv[++i];
        else if (argument == L"/smoke-ui") ui.capturePath = argv[++i];
        else if (argument == L"/smoke-size") {
            if (std::wstring(argv[++i]) != L"small") { LocalFree(argv); return ERROR_INVALID_PARAMETER; }
            ui.captureSmall = true;
        }
        else if (argument == L"/theme") {
            std::wstring theme = argv[++i];
            if (theme == L"system") ui.themeOverride = 0; else if (theme == L"light") ui.themeOverride = 1; else if (theme == L"dark") ui.themeOverride = 2;
            else { LocalFree(argv); return ERROR_INVALID_PARAMETER; }
        } else { LocalFree(argv); return ERROR_INVALID_PARAMETER; }
    }
    LocalFree(argv);
    ui.dpi = GetDpiForSystem();
    CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    INITCOMMONCONTROLSEX common{ sizeof(common), ICC_LISTVIEW_CLASSES }; InitCommonControlsEx(&common);
    WNDCLASSEXW klass{ sizeof(klass) }; klass.lpfnWndProc = WindowProc; klass.hInstance = app; klass.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    klass.hIcon = LoadIconW(app, MAKEINTRESOURCEW(1)); klass.hIconSm = klass.hIcon;
    klass.lpszClassName = L"HardwareScope.FileGuard.Window";
    RegisterClassExW(&klass);
    RECT work{}; SystemParametersInfoW(SPI_GETWORKAREA, 0, &work, 0);
    int width = std::min<int>(Scale(1280), work.right - work.left - Scale(16));
    int height = std::min<int>(Scale(820), work.bottom - work.top - Scale(16));
    if (ui.captureSmall) { width = Scale(960); height = Scale(680); }
    HWND window = CreateWindowExW(0, klass.lpszClassName, L"HARDWARESCOPE / 文件占用", WS_OVERLAPPEDWINDOW | WS_CLIPCHILDREN,
        work.left + (work.right - work.left - width) / 2, work.top + (work.bottom - work.top - height) / 2,
        width, height, nullptr, nullptr, app, nullptr);
    if (!window) { CoUninitialize(); return static_cast<int>(GetLastError()); }
    ShowWindow(window, show); UpdateWindow(window);
    if (!path.empty()) { SetWindowTextW(ui.path, path.c_str()); BeginRequest(GuardAction::Query); }
    if (!ui.capturePath.empty() && !ui.busy) PostMessageW(window, Capture, 0, 0);
    MSG message{};
    while (GetMessageW(&message, nullptr, 0, 0) > 0) if (!IsDialogMessageW(window, &message)) { TranslateMessage(&message); DispatchMessageW(&message); }
    if (ui.worker.joinable()) ui.worker.join();
    DeleteObject(ui.font); DeleteObject(ui.headingFont); DeleteObject(ui.mono); DeleteObject(ui.micro);
    DeleteObject(ui.background); DeleteObject(ui.card); DeleteObject(ui.input); if (ui.rowImages) ImageList_Destroy(ui.rowImages);
    CoUninitialize(); return ui.captureResult;
}
