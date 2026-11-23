// SPDX-License-Identifier: GPL-3.0-only
#include <windows.h>
#include <ocidl.h>
#include <xamlom.h>
#include <algorithm>
#include <memory>
#include <string>
#include <string_view>
#include <thread>
#include <vector>
#include "TaskbarAppearance.h"

namespace {
using winrt::Windows::UI::Xaml::FrameworkElement;

constexpr CLSID bridgeClass{0x4f807566, 0x6d8a, 0x478c, {0xb7, 0x12, 0x2f, 0xde, 0xae, 0x02, 0xb6, 0x67}};
constexpr auto settingsName = L"Local\\DesktopFolders.TaskbarAppearance.v2";
constexpr auto markerPrefix = L"Local\\DesktopFolders.TaskbarWatcher.v2.";

struct SharedSettings {
    volatile LONG value;
    volatile LONG applied;
};

struct TaskbarThread {
    std::vector<winrt::weak_ref<FrameworkElement>> frames;
    LONG last = 0;
    UINT_PTR timer = 0;
    ULONGLONG nextPoll = 0;
    bool dirty = true;
    bool updating = false;
};

// Keep these thread-bound XAML references away from the CRT shutdown path.
[[clang::no_destroy]] thread_local TaskbarThread taskbar;

struct CloseHandleOnExit {
    void operator()(void* handle) const noexcept { CloseHandle(handle); }
};
struct UnmapOnExit {
    void operator()(void* view) const noexcept { UnmapViewOfFile(view); }
};
struct FreeBstr {
    void operator()(wchar_t* text) const noexcept { SysFreeString(text); }
};

void CALLBACK UpdateTaskbar(HWND, UINT, UINT_PTR, DWORD) noexcept {
    auto now = GetTickCount64();
    if (taskbar.updating || now < taskbar.nextPoll) return;
    taskbar.updating = true;
    try {
        std::unique_ptr<void, CloseHandleOnExit> mapping(OpenFileMappingW(FILE_MAP_WRITE, FALSE, settingsName));
        std::unique_ptr<void, UnmapOnExit> view(mapping ? MapViewOfFile(mapping.get(), FILE_MAP_WRITE, 0, 0, sizeof(SharedSettings)) : nullptr);
        auto settings = static_cast<SharedSettings*>(view.get());
        taskbar.nextPoll = settings ? 0 : now + 750;
        LONG desired = settings ? settings->value : 0;
        if (desired == 0) {
            if (taskbar.last != 0 || taskbar.dirty)
                RestoreTaskbarAppearance();
            taskbar.last = 0;
            taskbar.dirty = false;
            if (settings) InterlockedExchange(&settings->applied, 0);
        } else if (desired != taskbar.last || taskbar.dirty) {
            std::erase_if(taskbar.frames, [](auto& frame) { return !frame.get(); });
            bool applied = !taskbar.frames.empty();
            for (auto& frame : taskbar.frames) {
                auto element = frame.get();
                auto root = element ? element.XamlRoot() : nullptr;
                applied = (root && ApplyTaskbarAppearance(root, static_cast<unsigned>(desired))) && applied;
            }
            taskbar.dirty = !applied;
            taskbar.last = desired;
            if (settings) InterlockedExchange(&settings->applied, applied ? desired : -2);
        } else if (settings) {
            InterlockedExchange(&settings->applied, desired);
        }
    } catch (...) {
        taskbar.dirty = true;
    }
    taskbar.updating = false;
}

struct VisualWatcher : winrt::implements<VisualWatcher, IVisualTreeServiceCallback2> {
    explicit VisualWatcher(winrt::com_ptr<IXamlDiagnostics> site) : diagnostics(std::move(site)) {}

    HRESULT STDMETHODCALLTYPE OnVisualTreeChange(ParentChildRelation, VisualElement element, VisualMutationType mutation) noexcept override {
        std::unique_ptr<wchar_t, FreeBstr> type(element.Type), name(element.Name);
        std::unique_ptr<wchar_t, FreeBstr> file(element.SrcInfo.FileName), hash(element.SrcInfo.Hash);
        try {
            std::wstring_view typeName(type ? type.get() : L"", SysStringLen(type.get()));
            std::wstring_view elementName(name ? name.get() : L"", SysStringLen(name.get()));
            if (mutation == Add && typeName == L"Taskbar.TaskbarFrame") {
                winrt::Windows::Foundation::IInspectable instance;
                if (SUCCEEDED(diagnostics->GetIInspectableFromHandle(element.Handle, reinterpret_cast<::IInspectable**>(winrt::put_abi(instance))))) {
                    auto frame = instance.try_as<FrameworkElement>();
                    if (frame && std::none_of(taskbar.frames.begin(), taskbar.frames.end(), [&](auto& item) { return item.get() == frame; })) {
                        taskbar.frames.push_back(winrt::make_weak(frame));
                        taskbar.dirty = true;
                        if (!taskbar.timer) taskbar.timer = SetTimer(nullptr, 0, 160, UpdateTaskbar);
                    }
                }
            } else if (taskbar.timer && (elementName == L"BackgroundFill" || elementName == L"BackgroundStroke" ||
                       typeName == L"SystemTray.SystemTrayFrame")) {
                taskbar.dirty = true;
            }
        } catch (...) {
        }
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE OnElementStateChanged(InstanceHandle, VisualElementState, LPCWSTR) noexcept override { return S_OK; }

private:
    winrt::com_ptr<IXamlDiagnostics> diagnostics;
};

struct BridgeSite : winrt::implements<BridgeSite, IObjectWithSite> {
    HRESULT STDMETHODCALLTYPE SetSite(IUnknown* source) noexcept override try {
        site.copy_from(source);
        if (!source) return S_OK;
        auto diagnostics = site.as<IXamlDiagnostics>();
        auto watcher = winrt::make_self<VisualWatcher>(diagnostics);
        auto service = diagnostics.as<IVisualTreeService>();
        // XAML dispatches callbacks to its UI thread. Advise can wait for that
        // thread during startup and is therefore called from a worker thread.
        std::thread([watcher, service] {
            try {
                if (SUCCEEDED(service->AdviseVisualTreeChange(watcher.get()))) {
                    const auto name = markerPrefix + std::to_wstring(GetCurrentProcessId());
                    // One attachment stays registered with Explorer until it restarts.
                    CreateEventW(nullptr, TRUE, TRUE, name.c_str());
                }
            } catch (...) {
            }
        }).detach();
        return S_OK;
    } catch (...) { return winrt::to_hresult(); }

    HRESULT STDMETHODCALLTYPE GetSite(REFIID iid, void** result) noexcept override {
        return site ? site->QueryInterface(iid, result) : E_FAIL;
    }

private:
    winrt::com_ptr<IUnknown> site;
};

struct BridgeFactory : winrt::implements<BridgeFactory, IClassFactory> {
    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID iid, void** result) noexcept override try {
        if (outer) return CLASS_E_NOAGGREGATION;
        return winrt::make<BridgeSite>().as(iid, result);
    } catch (...) { return winrt::to_hresult(); }

    HRESULT STDMETHODCALLTYPE LockServer(BOOL) noexcept override { return S_OK; }
};
}

extern "C" __declspec(dllexport) HRESULT WINAPI DllGetClassObject(REFCLSID clsid, REFIID iid, void** result) try {
    if (clsid != bridgeClass) return CLASS_E_CLASSNOTAVAILABLE;
    return winrt::make<BridgeFactory>().as(iid, result);
} catch (...) { return winrt::to_hresult(); }

extern "C" __declspec(dllexport) HRESULT WINAPI DllCanUnloadNow() {
    // Explorer owns the taskbar callbacks and may keep them for its lifetime.
    return S_FALSE;
}
