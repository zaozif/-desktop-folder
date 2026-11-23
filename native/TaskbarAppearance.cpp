// SPDX-License-Identifier: GPL-3.0-only
// Appearance logic adapted from lzxujun's Taskbar brightness and opacity tuner
// at ramensoftware/windhawk-mods commit b2a584ca279e3cf93bcbc2ea78a2cef240441cdb.
// Taskbar element discovery and settings are provided by Desktop Folders.
#include <windows.h>
#include "TaskbarAppearance.h"

#include <algorithm>
#include <cmath>
#include <cwchar>
#include <optional>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

#undef GetCurrentTime

#include <d2d1_1.h>

#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.Effects.h>
#include <winrt/Windows.UI.Composition.h>
#include <winrt/Windows.UI.Xaml.Controls.Primitives.h>
#include <winrt/Windows.UI.Xaml.Hosting.h>
#include <winrt/Windows.UI.Xaml.Media.h>
#include <winrt/Windows.UI.Xaml.Shapes.h>
#include <winrt/Windows.UI.Xaml.h>

namespace wuc = winrt::Windows::UI::Composition;
namespace wge = winrt::Windows::Graphics::Effects;

using namespace winrt::Windows::UI::Xaml;

////////////////////////////////////////////////////////////////////////////////
// windows.graphics.effects.interop.h (subset, from Windows 11 Taskbar Styler)
#ifndef BUILD_WINDOWS
namespace ABI {
#endif
namespace Windows {
namespace Graphics {
namespace Effects {

typedef interface IGraphicsEffectSource                         IGraphicsEffectSource;
typedef interface IGraphicsEffectD2D1Interop                    IGraphicsEffectD2D1Interop;

typedef enum GRAPHICS_EFFECT_PROPERTY_MAPPING
{
    GRAPHICS_EFFECT_PROPERTY_MAPPING_UNKNOWN,
    GRAPHICS_EFFECT_PROPERTY_MAPPING_DIRECT,
    GRAPHICS_EFFECT_PROPERTY_MAPPING_VECTORX,
    GRAPHICS_EFFECT_PROPERTY_MAPPING_VECTORY,
    GRAPHICS_EFFECT_PROPERTY_MAPPING_VECTORZ,
    GRAPHICS_EFFECT_PROPERTY_MAPPING_VECTORW,
    GRAPHICS_EFFECT_PROPERTY_MAPPING_RECT_TO_VECTOR4,
    GRAPHICS_EFFECT_PROPERTY_MAPPING_RADIANS_TO_DEGREES,
    GRAPHICS_EFFECT_PROPERTY_MAPPING_COLORMATRIX_ALPHA_MODE,
    GRAPHICS_EFFECT_PROPERTY_MAPPING_COLOR_TO_VECTOR3,
    GRAPHICS_EFFECT_PROPERTY_MAPPING_COLOR_TO_VECTOR4
} GRAPHICS_EFFECT_PROPERTY_MAPPING;

#undef INTERFACE
#define INTERFACE IGraphicsEffectD2D1Interop
DECLARE_INTERFACE_IID_(IGraphicsEffectD2D1Interop, IUnknown, "2FC57384-A068-44D7-A331-30982FCF7177")
{
    STDMETHOD(GetEffectId)(
        _Out_ GUID * id
        ) PURE;

    STDMETHOD(GetNamedPropertyMapping)(
        LPCWSTR name,
        _Out_ UINT * index,
        _Out_ GRAPHICS_EFFECT_PROPERTY_MAPPING * mapping
        ) PURE;

    STDMETHOD(GetPropertyCount)(
        _Out_ UINT * count
        ) PURE;

    STDMETHOD(GetProperty)(
        UINT index,
        _Outptr_ winrt::impl::abi_t<winrt::Windows::Foundation::IPropertyValue> ** value
        ) PURE;

    STDMETHOD(GetSource)(
        UINT index,
        _Outptr_ IGraphicsEffectSource ** source
        ) PURE;

    STDMETHOD(GetSourceCount)(
        _Out_ UINT * count
        ) PURE;
};

} // namespace Effects
} // namespace Graphics
} // namespace Windows
#ifndef BUILD_WINDOWS
} // namespace ABI
#endif

template <> inline constexpr winrt::guid winrt::impl::guid_v<ABI::Windows::Graphics::Effects::IGraphicsEffectD2D1Interop>{
    0x2FC57384, 0xA068, 0x44D7, { 0xA3, 0x31, 0x30, 0x98, 0x2F, 0xCF, 0x71, 0x77 }
};

// Required for the ABI IPropertyValue pointer used by GetProperty(): the
// MinGW based compiler doesn't provide a uuid for the ABI projection of
// IPropertyValue, so it is aliased to the projected interface's uuid here.
template <> inline constexpr winrt::guid winrt::impl::guid_v<winrt::impl::abi_t<winrt::Windows::Foundation::IPropertyValue>>{
    winrt::impl::guid_v<winrt::Windows::Foundation::IPropertyValue>
};

namespace awge = ABI::Windows::Graphics::Effects;

// {921F03D6-641C-47DF-852D-B4BB6153AE11} - the Direct2D color matrix effect
// CLSID. It is duplicated here as a local constant instead of using the
// CLSID_D2D1ColorMatrix macro from d2d1effects.h, because that macro expands
// to an extern declaration which the Windhawk linker can't resolve.
constexpr GUID kColorMatrixEffectId{
    0x921f03d6, 0x641c, 0x47df, {0x85, 0x2d, 0xb4, 0xbb, 0x61, 0x53, 0xae, 0x11}};

////////////////////////////////////////////////////////////////////////////////
// ColorMatrixEffect (from Windows 11 Taskbar Styler)
struct ColorMatrixEffect
    : winrt::implements<ColorMatrixEffect,
                        wge::IGraphicsEffect,
                        wge::IGraphicsEffectSource,
                        awge::IGraphicsEffectD2D1Interop> {
    wge::IGraphicsEffectSource Source{nullptr};

    // D2D1_MATRIX_5X4_F: 5 rows x 4 columns (20 floats), identity by default.
    // Rows are the output RGBA channels, columns are the input RGBA channels,
    // and the last row holds the per-channel offsets.
    float Matrix[20] = {
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
        0, 0, 0, 0,
    };

    uint32_t AlphaMode = D2D1_COLORMATRIX_ALPHA_MODE_PREMULTIPLIED;
    bool ClampOutput = false;

    // IGraphicsEffectD2D1Interop
    HRESULT STDMETHODCALLTYPE GetEffectId(GUID* id) noexcept override {
        if (!id) {
            return E_INVALIDARG;
        }

        *id = kColorMatrixEffectId;
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE GetNamedPropertyMapping(
        LPCWSTR name,
        UINT* index,
        awge::GRAPHICS_EFFECT_PROPERTY_MAPPING* mapping) noexcept override {
        if (!index || !mapping) {
            return E_INVALIDARG;
        }

        const std::wstring_view nameView(name);
        if (nameView == L"ColorMatrix") {
            *index = D2D1_COLORMATRIX_PROP_COLOR_MATRIX;
            *mapping = awge::GRAPHICS_EFFECT_PROPERTY_MAPPING_DIRECT;
            return S_OK;
        }

        if (nameView == L"AlphaMode") {
            *index = D2D1_COLORMATRIX_PROP_ALPHA_MODE;
            *mapping = awge::GRAPHICS_EFFECT_PROPERTY_MAPPING_DIRECT;
            return S_OK;
        }

        if (nameView == L"ClampOutput") {
            *index = D2D1_COLORMATRIX_PROP_CLAMP_OUTPUT;
            *mapping = awge::GRAPHICS_EFFECT_PROPERTY_MAPPING_DIRECT;
            return S_OK;
        }

        return E_INVALIDARG;
    }

    HRESULT STDMETHODCALLTYPE GetPropertyCount(UINT* count) noexcept override {
        if (!count) {
            return E_INVALIDARG;
        }

        *count = 3;
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE GetProperty(
        UINT index,
        winrt::impl::abi_t<winrt::Windows::Foundation::IPropertyValue>**
            value) noexcept override try {
        if (!value) {
            return E_INVALIDARG;
        }

        switch (index) {
            case D2D1_COLORMATRIX_PROP_COLOR_MATRIX:
                *value = winrt::Windows::Foundation::PropertyValue::
                    CreateSingleArray(winrt::array_view<const float>(
                        Matrix, Matrix + 20))
                        .as<winrt::impl::abi_t<
                            winrt::Windows::Foundation::IPropertyValue>>()
                        .detach();
                break;

            case D2D1_COLORMATRIX_PROP_ALPHA_MODE:
                *value = winrt::Windows::Foundation::PropertyValue::
                    CreateUInt32(AlphaMode)
                        .as<winrt::impl::abi_t<
                            winrt::Windows::Foundation::IPropertyValue>>()
                        .detach();
                break;

            case D2D1_COLORMATRIX_PROP_CLAMP_OUTPUT:
                *value = winrt::Windows::Foundation::PropertyValue::
                    CreateBoolean(ClampOutput)
                        .as<winrt::impl::abi_t<
                            winrt::Windows::Foundation::IPropertyValue>>()
                        .detach();
                break;

            default:
                return E_BOUNDS;
        }

        return S_OK;
    }
    catch (...) {
        return winrt::to_hresult();
    }

    HRESULT STDMETHODCALLTYPE GetSource(
        UINT index,
        awge::IGraphicsEffectSource** source) noexcept override {
        if (!source) {
            return E_INVALIDARG;
        }

        if (index == 0 && Source) {
            winrt::copy_to_abi(Source, *reinterpret_cast<void**>(source));
            return S_OK;
        }

        return E_BOUNDS;
    }

    HRESULT STDMETHODCALLTYPE GetSourceCount(UINT* count) noexcept override {
        if (!count) {
            return E_INVALIDARG;
        }

        *count = 1;
        return S_OK;
    }

    // IGraphicsEffect
    winrt::hstring Name() { return m_name; }

    void Name(winrt::hstring name) { m_name = std::move(name); }

private:
    winrt::hstring m_name = L"ColorMatrixEffect";
};

////////////////////////////////////////////////////////////////////////////////
// BackdropAdjustBrush: renders the live backdrop (what is behind the taskbar)
// with a brightness adjustment via a color matrix effect.
class BackdropAdjustBrush
    : public Media::XamlCompositionBrushBaseT<BackdropAdjustBrush> {
public:
    BackdropAdjustBrush(wuc::Compositor const& compositor, float brightness)
        : m_compositor(compositor),
          m_brightness(brightness) {}

    void OnConnected() {
        if (CompositionBrush()) {
            return;
        }

        CompositionBrush(CreateEffectBrush());
    }

    void OnDisconnected() {
        CompositionBrush(nullptr);
    }

private:
    wuc::CompositionBrush CreateEffectBrush() {
        auto backdropBrush = m_compositor.CreateBackdropBrush();

        // Dimming: the color matrix scales the color channels toward black.
        // Brightening is not supported (the mod exists to make the taskbar
        // dimmer), so the bias row of the matrix stays at zero.
        auto brightMatrix = winrt::make_self<ColorMatrixEffect>();
        brightMatrix->Source = wuc::CompositionEffectSourceParameter(L"source");

        float brightness = std::clamp(m_brightness, -1.0f, 0.0f);
        float scale = 1.0f + brightness;
        auto& bm = brightMatrix->Matrix;
        bm[0] = scale; bm[1] = 0.0f;  bm[2] = 0.0f;  bm[3] = 0.0f;
        bm[4] = 0.0f;  bm[5] = scale; bm[6] = 0.0f;  bm[7] = 0.0f;
        bm[8] = 0.0f;  bm[9] = 0.0f;  bm[10] = scale; bm[11] = 0.0f;
        bm[12] = 0.0f; bm[13] = 0.0f; bm[14] = 0.0f; bm[15] = 1.0f;
        bm[16] = 0.0f; bm[17] = 0.0f; bm[18] = 0.0f; bm[19] = 0.0f;
        brightMatrix->ClampOutput = true;
        brightMatrix->Name(L"BrightnessEffect");

        auto factory = m_compositor.CreateEffectFactory(*brightMatrix);
        auto brush = factory.CreateBrush();
        brush.SetSourceParameter(L"source", backdropBrush);

        return brush;
    }

    wuc::Compositor m_compositor;
    float m_brightness;
};

////////////////////////////////////////////////////////////////////////////////

namespace {
thread_local struct {
    int backgroundDimming;     // 0..100 dimming amount
    int backgroundOpacity;     // 0..100, 100 = no change
    int iconsOpacity;          // 0..100, 100 = no change
    bool topLine;              // false = hide the taskbar top line
} g_settings;

// Finds a direct child by its XAML name.
FrameworkElement FindChildByName(FrameworkElement element, PCWSTR name) {
    int childrenCount = Media::VisualTreeHelper::GetChildrenCount(element);
    for (int i = 0; i < childrenCount; i++) {
        auto child = Media::VisualTreeHelper::GetChild(element, i)
                         .try_as<FrameworkElement>();
        if (child) {
            try {
                if (child.Name() == name) {
                    return child;
                }
            } catch (...) {
            }
        }
    }
    return nullptr;
}

// Finds a direct child by its XAML class name.
FrameworkElement FindChildByClassName(FrameworkElement element,
                                      PCWSTR className) {
    int childrenCount = Media::VisualTreeHelper::GetChildrenCount(element);
    for (int i = 0; i < childrenCount; i++) {
        auto child = Media::VisualTreeHelper::GetChild(element, i)
                         .try_as<FrameworkElement>();
        if (child) {
            try {
                if (winrt::get_class_name(child) == className) {
                    return child;
                }
            } catch (...) {
            }
        }
    }
    return nullptr;
}

struct AppearanceState {
    // The XamlRoot content element of the taskbar this state belongs to. It is
    // used to look the state up again when the settings change.
    winrt::weak_ref<FrameworkElement> root;
    winrt::weak_ref<FrameworkElement> backgroundFill;
    double originalFillOpacity = 1.0;
    std::optional<double> appliedFillOpacity;
    // backgroundFillReplaced records whether the fill of the background
    // rectangle was replaced with a backdrop brush (background dimming). The
    // original brush is held and put back by assignment: elements of the
    // taskbar XAML template receive their values as local values, so
    // ClearValue would wipe the fill instead of reverting it. installedFillBrush
    // holds a weak reference to the brush the mod installed: the taskbar can
    // replace the fill with its own brush in the meantime (theme changes and
    // transparency toggles rewrite the background), and the saved brush must
    // then not overwrite that newer value. RestoreState only ever runs on the
    // taskbar UI thread, so the strong reference is always released from the
    // thread the object belongs to.
    bool backgroundFillReplaced = false;
    Media::Brush originalFillBrush = nullptr;
    winrt::weak_ref<Media::Brush> installedFillBrush;
    // The brightness of an acrylic background is adjusted by dimming its
    // TintColor in place: the brush itself is never replaced, so the DWM
    // material behind it stays exactly as Windows drew it. tintAdjusted
    // records that this path was taken. appliedTintColor serves as an
    // ownership marker on restore: if the taskbar has changed the tint in
    // the meantime (accent color change, theme switch), the saved original
    // tint must not overwrite the newer value
    // (AdoptExternalValueAsOriginal).
    bool tintAdjusted = false;
    winrt::Windows::UI::Color originalTintColor{};
    winrt::Windows::UI::Color appliedTintColor{};
    // The taskbar top line rectangle. topLineHidden records whether the line
    // was actually hidden, so that restore only touches properties which were
    // modified. The line is hidden with Opacity only: that is a plain value
    // which can be handed back verbatim, and it is independent of how the line
    // happens to be drawn (fill or stroke). Visibility, Fill and
    // StrokeThickness are deliberately never written - the line belongs to the
    // taskbar XAML template and holds its values as local values, so
    // overwriting them destroys the original look for good (ClearValue does not
    // revert a template value, it wipes it).
    winrt::weak_ref<FrameworkElement> topLine;
    bool topLineHidden = false;
    double originalTopLineOpacity = 1.0;
    // Foreground elements with their original opacity. The drag grip handle
    // (Rectangle#Gripper) is NOT part of this layer since v1.12.0: Windows
    // renders it permanently, so writing any layer opacity onto it made the
    // handle surface at that opacity. It is system chrome, excluded via
    // IsSystemChromeElement, and Windows manages its visibility itself.
    struct ForegroundOpacity {
        winrt::weak_ref<FrameworkElement> element;
        double original;
        double applied;
    };
    std::vector<ForegroundOpacity> foregroundOpacity;
};


// The original brush is owned by the taskbar UI thread. Do not release it
// from the CRT at process shutdown after the XAML core has been destroyed.
[[clang::no_destroy]] thread_local std::vector<AppearanceState> g_states;

template <typename T>
bool EnumDescendants(FrameworkElement element, T&& enumCallback, int depth = 0) {
    constexpr int kMaxDepth = 12;
    if (depth > kMaxDepth) {
        return false;
    }

    int childrenCount = Media::VisualTreeHelper::GetChildrenCount(element);

    for (int i = 0; i < childrenCount; i++) {
        auto child = Media::VisualTreeHelper::GetChild(element, i)
                         .try_as<FrameworkElement>();
        if (!child) {
            continue;
        }

        if (enumCallback(child) || EnumDescendants(child, enumCallback, depth + 1))
            return true;
    }
    return false;
}

FrameworkElement FindDescendantByName(FrameworkElement element, PCWSTR name) {
    FrameworkElement result = nullptr;
    EnumDescendants(element, [&](FrameworkElement child) {
        try {
            if (child.Name() == name) {
                result = child;
                return true;
            }
        } catch (...) {
            // The element may be torn down mid-walk.
        }
        return false;
    });
    return result;
}

// Flyouts (the hover window previews, the tray overflow menu, Alt+Tab, ...) are
// hosted by elements inside the taskbar tree and render outside of the visible
// taskbar. Adjusting them leaks the adjustment outside of the taskbar (a gray
// strip above it), so they are skipped. Popups are recognized by their type,
// the rest by the class name of the host element.
bool IsFlyoutHost(FrameworkElement element) {
    try {
        if (element.try_as<Controls::Primitives::Popup>()) {
            return true;
        }
    } catch (...) {
    }

    try {
        auto classText = winrt::get_class_name(element);
        std::wstring_view className(classText);
        return className.find(L"Switcher") != std::wstring_view::npos ||
               className.find(L"TaskbarExperienceHost") !=
                   std::wstring_view::npos ||
               className.find(L"Flyout") != std::wstring_view::npos;
    } catch (...) {
        return false;
    }
}

// The background rectangles (the taskbar background and the background of the
// hover flyout) are not part of the icon and text layer: the taskbar background
// is adjusted separately, and the flyout background must not be touched at all.
bool IsBackgroundRectangle(FrameworkElement element) {
    try {
        auto name = element.Name();
        return name == L"BackgroundFill" || name == L"BackgroundStroke";
    } catch (...) {
        return false;
    }
}

// System chrome inside the taskbar which is not icon and text content: the
// stroke which closes the taskbar at the bottom screen edge, and the drag
// grip container. See the notes inside the function for why the grip is
// excluded since v1.12.0.
bool IsSystemChromeElement(FrameworkElement element) {
    try {
        auto name = element.Name();
        // ScreenEdgeStroke closes the taskbar at the bottom screen edge.
        // GripperControl hosts the small drag handle at the top center of the
        // taskbar: Windows renders it permanently (its native XAML opacity is
        // 1.00), so fading it with the icon layer made it surface at that
        // opacity. It is not icon and text content - it is excluded entirely
        // and its visibility stays under Windows' own control (on the
        // default, locked taskbar it is always rendered, not only while
        // dragging icons).
        if (name == L"ScreenEdgeStroke" || name == L"GripperControl") {
            return true;
        }
    } catch (...) {
    }
    return false;
}

// Returns true when the subtree is pure icon and text content, i.e. it contains
// neither the background nor a flyout host, so that it can be adjusted as a
// single unit. The walk is bounded, an unexpectedly large subtree counts as
// impure, in which case the caller descends into it instead.
bool IsPureIconSubtree(FrameworkElement element) {
    constexpr int kMaxDepth = 12;
    constexpr int kMaxElements = 400;

    std::vector<std::pair<FrameworkElement, int>> stack;
    stack.push_back({element, 0});

    int visited = 0;
    while (!stack.empty()) {
        auto [current, depth] = stack.back();
        stack.pop_back();

        if (!current || IsBackgroundRectangle(current) ||
            IsFlyoutHost(current) || depth > kMaxDepth) {
            return false;
        }

        if (++visited > kMaxElements) {
            return false;
        }

        int childrenCount = 0;
        try {
            childrenCount = Media::VisualTreeHelper::GetChildrenCount(current);
        } catch (...) {
            return false;
        }

        for (int i = 0; i < childrenCount; i++) {
            FrameworkElement child = nullptr;
            try {
                child = Media::VisualTreeHelper::GetChild(current, i)
                            .try_as<FrameworkElement>();
            } catch (...) {
            }
            if (child) {
                stack.push_back({child, depth + 1});
            }
        }
    }

    return true;
}

// The taskbar XAML tree looks roughly like this (Windows 11):
//
//   <content element of the taskbar XamlRoot>
//     Taskbar.TaskbarFrame
//       Grid#RootGrid
//         Taskbar.TaskbarBackground#BackgroundControl
//           Grid
//             Rectangle#BackgroundFill
//             Rectangle#BackgroundStroke
//         ... Start/Search/Task View buttons, task buttons, badges, gripper ...
//         Popup                    <- hover previews and other flyouts
//     SystemTray.SystemTrayFrame
//
// The icon and text layer is therefore everything below the content element
// except the background and except flyout hosts. The class names of the
// individual containers differ between Windows builds, so instead of matching a
// hard-coded list of class names the tree is walked and every subtree which
// contains neither the background nor a flyout host is adjusted as a unit.
void CollectIconLayers(FrameworkElement element,
                       FrameworkElement content,
                       double bandTop,
                       double bandBottom,
                       int depth,
                       std::vector<FrameworkElement>& elements) {
    constexpr int kMaxDepth = 12;

    if (!element || depth > kMaxDepth) {
        return;
    }

    // The walk is pre-order and stops descending once an element has been
    // collected, so no element is visited twice and no descendant of a
    // collected element is ever reached.
    if (IsFlyoutHost(element) || IsBackgroundRectangle(element) ||
        IsSystemChromeElement(element)) {
        return;
    }

    bool visible = true;
    try {
        visible = element.Visibility() == Visibility::Visible &&
                  element.ActualWidth() > 0 && element.ActualHeight() > 0;
    } catch (...) {
    }

    if (visible && IsPureIconSubtree(element)) {
        // Elements outside of the visible taskbar area leak the adjustment
        // outside of the taskbar (a gray strip above it), so only elements
        // fully inside the visible band qualify.
        bool withinVisibleTaskbar = true;
        try {
            auto origin = element.TransformToVisual(content)
                              .TransformPoint(
                                  winrt::Windows::Foundation::Point{0, 0});
            withinVisibleTaskbar =
                origin.X >= -1 && origin.Y >= -1 &&
                origin.X + element.ActualWidth() <= content.ActualWidth() + 1 &&
                origin.Y + element.ActualHeight() <= content.ActualHeight() + 1;

            // The island extends above the visible taskbar: the element must
            // also lie within the background area and not just fit into the
            // island.
            if (withinVisibleTaskbar && bandBottom > bandTop) {
                withinVisibleTaskbar =
                    origin.Y >= bandTop - 1 &&
                    origin.Y + element.ActualHeight() <= bandBottom + 1;
            }
        } catch (...) {
        }

        if (withinVisibleTaskbar) {
            elements.push_back(element);
            return;
        }
    }

    // Either a mixed container (it holds the background or a flyout), or an
    // element which is not fully inside the visible taskbar: descend into the
    // children.
    int childrenCount = 0;
    try {
        childrenCount = Media::VisualTreeHelper::GetChildrenCount(element);
    } catch (...) {
        return;
    }

    for (int i = 0; i < childrenCount; i++) {
        FrameworkElement child = nullptr;
        try {
            child = Media::VisualTreeHelper::GetChild(element, i)
                        .try_as<FrameworkElement>();
        } catch (...) {
        }
        CollectIconLayers(child, content, bandTop, bandBottom, depth + 1,
                          elements);
    }
}

// Collects the containers which form the icons and text layer. Elements which
// contain the background, or which are already covered by another collected
// element, are skipped, so that an adjustment is never applied twice and the
// background is never affected by a foreground adjustment.
//
// bandTop/bandBottom are the vertical bounds of the visible taskbar area in
// content coordinates, as given by the background rectangle. Elements outside
// of them are split up instead of being adjusted as a whole: the XAML island
// which hosts the taskbar is larger than the visible taskbar (the extra space
// above it hosts flyouts), and an adjustment which lands there shows up as a
// gray strip above the taskbar.
std::vector<FrameworkElement> CollectForegroundElements(
    FrameworkElement content, double bandTop, double bandBottom) {
    std::vector<FrameworkElement> elements;
    CollectIconLayers(content, content, bandTop, bandBottom, 0, elements);


    return elements;
}

void RestoreState(AppearanceState& state) {
    if (state.tintAdjusted) {
        state.tintAdjusted = false;
        if (auto backgroundFill = state.backgroundFill.get()) {
            if (auto rect = backgroundFill.try_as<Shapes::Rectangle>()) {
                // The tint was adjusted in place. Put the saved original
                // tint back only if the fill still shows the dimmed value
                // the mod wrote: if Windows has rewritten the tint in the
                // meantime (accent color change, theme switch), it has
                // already restored itself and the saved value must not
                // overwrite the newer one (the same idea as Taskbar
                // Styler's AdoptExternalValueAsOriginal).
                try {
                    if (auto acrylic =
                            rect.Fill().try_as<Media::AcrylicBrush>()) {
                        if (acrylic.TintColor() == state.appliedTintColor) {
                            acrylic.TintColor(state.originalTintColor);
                        }
                    }
                } catch (...) {
                }
            }
        }
    }

    if (state.backgroundFillReplaced) {
        state.backgroundFillReplaced = false;
        if (auto backgroundFill = state.backgroundFill.get()) {
            if (auto rect = backgroundFill.try_as<Shapes::Rectangle>()) {
                // The fill was replaced with a backdrop brush for the
                // brightness adjustment. Put the saved brush back by
                // assignment: template elements hold their values as local
                // values, so ClearValue would wipe the fill instead of
                // reverting it. The comparison makes sure the taskbar has
                // not replaced our brush with its own in the meantime
                // (theme changes and transparency toggles rewrite the
                // background): if it has, the taskbar has already restored
                // itself and the saved brush must not overwrite the newer
                // value (the same idea as Taskbar Styler's
                // AdoptExternalValueAsOriginal).
                try {
                    if (state.installedFillBrush.get() == rect.Fill()) {
                        rect.Fill(state.originalFillBrush);
                    }
                } catch (...) {
                }
            }
        }
        state.originalFillBrush = nullptr;
        state.installedFillBrush = nullptr;
    }

    if (auto backgroundFill = state.backgroundFill.get(); backgroundFill && state.appliedFillOpacity) {
        try {
            if (std::abs(backgroundFill.Opacity() - *state.appliedFillOpacity) < 0.000001)
                backgroundFill.Opacity(state.originalFillOpacity);
        } catch (...) {
        }
    }

    if (state.topLineHidden) {
        if (auto topLine = state.topLine.get()) {
            // The line was hidden with Opacity only; assigning the saved value
            // back brings it back verbatim.
            try {
                if (topLine.Opacity() == 0.0)
                    topLine.Opacity(state.originalTopLineOpacity);
            } catch (...) {
            }
        }
        state.topLineHidden = false;
    }

    for (auto& [element, originalOpacity, appliedOpacity] : state.foregroundOpacity) {
        if (auto elem = element.get()) {
            try {
                if (std::abs(elem.Opacity() - appliedOpacity) < 0.000001)
                    elem.Opacity(originalOpacity);
            } catch (...) {
            }
        }
    }
}

// Restores all tracked taskbars and forgets about them. Used when all settings
// are back to their defaults, or when the mod is unloading, so that nothing is
// left adjusted. A failure of one state must not stop the others: an aborted
// loop would leave mod-image code (the backdrop brush) installed on the
// remaining taskbars, which crashes Explorer once the mod is unloaded.
void RestoreAllStates() {
    for (auto& state : g_states) {
        try {
            RestoreState(state);
        } catch (...) {
        }
    }

    g_states.clear();
}

std::pair<AppearanceState*, size_t> FindStateForRoot(
    FrameworkElement root) {
    for (size_t i = 0; i < g_states.size(); i++) {
        if (auto stateRoot = g_states[i].root.get();
            stateRoot && stateRoot == root) {
            return {&g_states[i], i};
        }
    }

    return {nullptr, 0};
}

// The vertical bounds of the visible taskbar area in content coordinates.
// The island which hosts the taskbar is larger than the visible taskbar (the
// extra space above it hosts flyouts), and an adjustment which lands there
// shows up as a gray strip above the taskbar. The background rectangle is the
// visible taskbar, so it defines the band.
std::pair<double, double> GetVisibleBand(FrameworkElement content,
                                         FrameworkElement backgroundFill) {
    double bandTop = 0.0;
    double bandBottom = content.ActualHeight();
    try {
        auto origin = backgroundFill.TransformToVisual(content)
                          .TransformPoint(
                              winrt::Windows::Foundation::Point{0, 0});
        bandTop = origin.Y;
        bandBottom = origin.Y + backgroundFill.ActualHeight();
    } catch (...) {
    }
    return {bandTop, bandBottom};
}

// Looks the thin line at the top edge of the taskbar up and hides it on
// request. The line is drawn by the taskbar border rectangle
// (Rectangle#BackgroundStroke, a sibling of Rectangle#BackgroundFill). It is
// looked up among the siblings of BackgroundFill first, as the taskbar flyouts
// contain rectangles with the same names. Hiding only changes the opacity, so
// the original look can always be restored verbatim.
void ApplyTopLineStyle(FrameworkElement content,
                       FrameworkElement backgroundFillElem,
                       AppearanceState& state) {
    FrameworkElement topLineElem = nullptr;
    if (auto parent =
            Media::VisualTreeHelper::GetParent(backgroundFillElem)
                .try_as<FrameworkElement>()) {
        int childrenCount =
            Media::VisualTreeHelper::GetChildrenCount(parent);
        for (int i = 0; i < childrenCount; i++) {
            auto child = Media::VisualTreeHelper::GetChild(parent, i)
                             .try_as<FrameworkElement>();
            if (child && child.Name() == L"BackgroundStroke") {
                topLineElem = child;
                break;
            }
        }
    }
    if (!topLineElem) {
        topLineElem = FindDescendantByName(content, L"BackgroundStroke");
    }

    if (!topLineElem) {
        return;
    }

    auto topLineRect = topLineElem.try_as<Shapes::Rectangle>();
    if (!topLineRect) {
        return;
    }

    state.topLine = winrt::make_weak(topLineRect.as<FrameworkElement>());
    state.originalTopLineOpacity = topLineRect.Opacity();

    if (!g_settings.topLine) {
        state.topLineHidden = true;
        // Hide the line with Opacity only. Visibility, Fill and
        // StrokeThickness are never written: they belong to the taskbar
        // template, and overwriting them destroys the original look for good.
        topLineRect.Opacity(0.0);
    }
}

// Scales a single color channel by the dimming factor and clamps the result
// to the byte range.
BYTE ScaleColorChannel(BYTE value, float scale) {
    int scaled = (int)(value * scale + 0.5f);
    return (BYTE)std::clamp(scaled, 0, 255);
}

// Adjusts the background rectangle: opacity directly, brightness by one of
// three strategies, tried in order: dimming the acrylic tint in place
// (preferred), dimming the solid fill color itself, or replacing the fill
// with a live backdrop brush filtered by a color matrix effect.
void ApplyBackgroundStyle(Shapes::Rectangle const& backgroundFill,
                          AppearanceState& state) {
    bool hasOpacity = g_settings.backgroundOpacity != 100;
    bool hasBrightness = g_settings.backgroundDimming != 0;

    if (hasOpacity) {
        state.appliedFillOpacity = g_settings.backgroundOpacity / 100.0;
        backgroundFill.Opacity(*state.appliedFillOpacity);
        state.appliedFillOpacity = backgroundFill.Opacity();
    }

    if (!hasBrightness) {
        return;
    }

    float scale = 1.0f - g_settings.backgroundDimming / 100.0f;

    // Prefer dimming an acrylic fill in place: with transparency effects on,
    // the taskbar fill is an AcrylicBrush and the accent color (when "Show
    // accent color on Start and taskbar" is enabled) only changes its
    // TintColor. Scaling the RGB channels of the tint and keeping the alpha
    // darkens exactly the color the taskbar is showing, while the brush - and
    // with it the DWM material behind it - is left untouched.
    if (auto acrylic = backgroundFill.Fill().try_as<Media::AcrylicBrush>()) {
        try {
            auto tint = acrylic.TintColor();
            auto dimmed = winrt::Windows::UI::Color{};
            dimmed.A = tint.A;
            dimmed.R = ScaleColorChannel(tint.R, scale);
            dimmed.G = ScaleColorChannel(tint.G, scale);
            dimmed.B = ScaleColorChannel(tint.B, scale);

            acrylic.TintColor(dimmed);
            state.tintAdjusted = true;
            state.originalTintColor = tint;
            state.appliedTintColor = dimmed;
            return;
        } catch (...) {
            // Fall through to the other strategies.
        }
    }

    // Next, dim a plain solid fill directly: scaling the RGB channels and
    // keeping the alpha reproduces exactly the color the taskbar is showing,
    // only darker. This covers the taskbar when transparency effects are off
    // and it draws a plain color - the backdrop approach below would dim the
    // content behind the taskbar instead of the color on it.
    if (auto solid = backgroundFill.Fill().try_as<Media::SolidColorBrush>()) {
        try {
            auto color = solid.Color();
            auto dimmed = winrt::Windows::UI::Color{};
            dimmed.A = color.A;
            dimmed.R = ScaleColorChannel(color.R, scale);
            dimmed.G = ScaleColorChannel(color.G, scale);
            dimmed.B = ScaleColorChannel(color.B, scale);

            auto dimBrush = Media::SolidColorBrush();
            dimBrush.Color(dimmed);
            dimBrush.Opacity(solid.Opacity());

            state.originalFillBrush = solid;
            state.installedFillBrush =
                winrt::make_weak(dimBrush.as<Media::Brush>());
            backgroundFill.Fill(dimBrush);
            state.backgroundFillReplaced = true;
            return;
        } catch (...) {
            // Fall through to the backdrop brush below.
        }
    }

    try {
        auto compositor =
            winrt::Windows::UI::Xaml::Hosting::ElementCompositionPreview::
                GetElementVisual(backgroundFill)
                    .Compositor();
        float brightness = -g_settings.backgroundDimming / 100.0f;
        auto brush = winrt::make<BackdropAdjustBrush>(compositor, brightness);
        state.originalFillBrush = backgroundFill.Fill();
        state.installedFillBrush = winrt::make_weak(brush.as<Media::Brush>());
        backgroundFill.Fill(brush);
        state.backgroundFillReplaced = true;
    } catch (winrt::hresult_error const& e) {
        // Keep going with the other adjustments.
    } catch (...) {
        // Keep going with the other adjustments.
    }
}

// Adjusts the icon and text elements: opacity directly.
void ApplyForegroundStyle(std::vector<FrameworkElement> const& elements,
                          AppearanceState& state) {

    if (g_settings.iconsOpacity == 100) {
        return;
    }

    for (auto& element : elements) {
        // Fade multiplicatively instead of overwriting: an element the
        // system itself has hidden (Opacity = 0) stays hidden, because
        // original * factor is still 0. Elements the system shows
        // (original = 1) get exactly the layer opacity.
        double original = element.Opacity();
        double applied = original * (g_settings.iconsOpacity / 100.0);
        state.foregroundOpacity.push_back({winrt::make_weak(element), original, applied});
        element.Opacity(applied);
        state.foregroundOpacity.back().applied = element.Opacity();
    }
}

// Locates the taskbar background rectangle. The known tree path is walked
// explicitly first: flyouts contain rectangles with the same names, and a
// depth-first search just takes whatever it reaches first. The search falls
// back to a bounded tree walk for Windows builds with a different structure.
FrameworkElement FindBackgroundFill(FrameworkElement content) {
    FrameworkElement child = content;
    if ((child = FindChildByName(child, L"RootGrid")) &&
        (child = FindChildByName(child, L"BackgroundControl")) &&
        (child = FindChildByClassName(
             child, L"Windows.UI.Xaml.Controls.Grid")) &&
        (child = FindChildByName(child, L"BackgroundFill"))) {
        return child;
    }

    if (auto control = FindDescendantByName(content, L"BackgroundControl"))
        return FindDescendantByName(control, L"BackgroundFill");
    return FindDescendantByName(content, L"BackgroundFill");
}

// The XAML tree can be mutated from several threads and elements can be torn
// down while being touched. A top level catch keeps a WinRT exception from
// propagating into the taskbar thread and crashing Explorer; the retry logic
// treats a failure the same way as a tree which is not ready yet.
bool ApplyStyleImpl(XamlRoot const& xamlRoot);

bool ApplyStyle(XamlRoot const& xamlRoot) {
    try {
        return ApplyStyleImpl(xamlRoot);
    } catch (...) {
        return false;
    }
}

bool ApplyStyleImpl(XamlRoot const& xamlRoot) {
    auto content = xamlRoot.Content().as<FrameworkElement>();
    if (!content) {
        return false;
    }

    std::erase_if(g_states, [](auto& state) {
        if (state.root.get()) return false;
        RestoreState(state);
        return true;
    });


    auto [state, stateIndex] = FindStateForRoot(content);

    // Locate the taskbar background rectangle.
    auto backgroundFillElem = FindBackgroundFill(content);
    if (!backgroundFillElem) {
        return false;
    }

    auto backgroundFill = backgroundFillElem.try_as<Shapes::Rectangle>();
    if (!backgroundFill) {
        return false;
    }

    bool hasForegroundOpacity = g_settings.iconsOpacity != 100;

    // The icon and text layer is collected up front. If it is requested but
    // comes back empty, the taskbar content exists while not being laid out
    // yet, i.e. all of its elements are still zero-sized. In that case nothing
    // is modified at all, so that the retry which covers this state doesn't
    // make the background flicker by re-applying itself over and over.
    std::vector<FrameworkElement> foregroundElements;
    if (hasForegroundOpacity) {
        auto [bandTop, bandBottom] =
            GetVisibleBand(content, backgroundFillElem);
        foregroundElements =
            CollectForegroundElements(content, bandTop, bandBottom);
        if (foregroundElements.empty()) {
            return false;
        }
    }

    // Restore any previous state for this root before re-applying.
    if (state) {
        RestoreState(*state);
        g_states.erase(g_states.begin() + stateIndex);
    }

    // The state is added to the container before it is filled in: if any of
    // the steps below throws, ApplyStyle's top level catch reports the
    // failure, and the partially applied state is still tracked and restored
    // like any other - most importantly, a replaced background fill is never
    // left behind unaccounted for.
    AppearanceState& newState = g_states.emplace_back();
    newState.root = winrt::make_weak(content);
    newState.backgroundFill =
        winrt::make_weak(backgroundFill.as<FrameworkElement>());
    newState.originalFillOpacity = backgroundFill.Opacity();

    ApplyTopLineStyle(content, backgroundFillElem, newState);

    ApplyBackgroundStyle(backgroundFill, newState);

    if (hasForegroundOpacity) {
        ApplyForegroundStyle(foregroundElements, newState);
    }

    return (g_settings.topLine || newState.topLineHidden) &&
           (g_settings.backgroundDimming == 0 || newState.tintAdjusted || newState.backgroundFillReplaced);
}


} // namespace

bool ApplyTaskbarAppearance(XamlRoot const& root, unsigned settings) {
    g_settings.backgroundDimming = 100 - int(settings & 127);
    g_settings.backgroundOpacity = int((settings >> 7) & 127);
    g_settings.iconsOpacity = int((settings >> 14) & 127);
    g_settings.topLine = (settings & (1u << 21)) != 0;
    return ApplyStyle(root);
}

void RestoreTaskbarAppearance() {
    RestoreAllStates();
}
