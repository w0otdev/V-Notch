#pragma once
#include <cstdint>

// The optional CPU runtime exposes only a no-throw C ABI. No STL object crosses it.
using VnTranslationCancelled = uint8_t(__stdcall*)(intptr_t);
constexpr int32_t VN_TRANSLATION_CANCELLED = -20;
constexpr int32_t VN_TRANSLATION_LANGUAGE = -21;
constexpr int32_t VN_TRANSLATION_RUNTIME = -22;
constexpr int32_t VN_TRANSLATION_MODEL = -23;
constexpr int32_t VN_TRANSLATION_LENGTH = -24;

using VnLiteOpen = int32_t(__stdcall*)(const wchar_t*, int32_t, int32_t, int64_t*) noexcept;
using VnLiteTranslate = int32_t(__stdcall*)(int64_t, const wchar_t*, int32_t,
    const wchar_t*, int32_t, VnTranslationCancelled, intptr_t, wchar_t*, int32_t, int32_t*) noexcept;
using VnLiteClose = int32_t(__stdcall*)(int64_t) noexcept;
using VnLiteReleaseIdle = int32_t(__stdcall*)(int64_t, int64_t, uint8_t*) noexcept;
using VnLiteSetNatural = int32_t(__stdcall*)(int64_t, uint8_t) noexcept;
