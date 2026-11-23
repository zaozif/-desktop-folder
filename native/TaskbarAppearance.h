// SPDX-License-Identifier: GPL-3.0-only
#pragma once

#ifdef GetCurrentTime
#undef GetCurrentTime
#endif
#include <winrt/Windows.UI.Xaml.h>

bool ApplyTaskbarAppearance(winrt::Windows::UI::Xaml::XamlRoot const& root, unsigned settings);
void RestoreTaskbarAppearance();
