#include "vnotch_common.h"
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <shellapi.h>
#include <cstdint>
#include <cstring>
#include <cwchar>
#include <cwctype>
#include <string>
#include <string_view>
#include <vector>

#pragma comment(lib, "shell32.lib")

namespace vnotch::services {

class FfmpegRecordingProbe final {
public:
    static bool IsScreenCaptureCommand(std::u16string_view command) {
        if (IsNullOrWhiteSpace(command)) return false;

        std::wstring cmd_w(reinterpret_cast<const wchar_t*>(command.data()), command.size());
        int count = 0;
        LPWSTR* argv = CommandLineToArgvW(cmd_w.c_str(), &count);
        if (argv == nullptr) return false;

        struct ArgvGuard {
            LPWSTR* ptr;
            ~ArgvGuard() noexcept {
                if (ptr != nullptr) {
                    LocalFree(ptr);
                }
            }
        } guard{argv};

        bool has_format = false;
        std::u16string_view format{};
        bool capture = false;

        for (int i = 1; i < count; i++) {
            std::u16string_view arg(reinterpret_cast<const char16_t*>(argv[i]), std::wcslen(argv[i]));
            if (arg == u"-h" || arg == u"-help" || arg == u"-version" ||
                arg == u"-list_devices" || arg == u"-list_options") {
                return false;
            }
            if (i + 1 >= count) continue;

            std::u16string_view value(reinterpret_cast<const char16_t*>(argv[i + 1]), std::wcslen(argv[i + 1]));
            if ((arg == u"-frames:v" || arg == u"-vframes") && value == u"1") {
                return false;
            }
            if (arg == u"-f") {
                has_format = true;
                format = value;
                i++;
            } else if (arg == u"-i") {
                if (has_format) {
                    if (format == u"gdigrab") {
                        capture |= (value == u"desktop" || value.starts_with(u"title=") || value.starts_with(u"hwnd="));
                    } else if (format == u"dshow") {
                        capture |= StartsWithAsciiIgnoreCase(value, u"video=screen-capture-recorder");
                    } else if (format == u"lavfi") {
                        capture |= HasDesktopFilter(value);
                    }
                }
                has_format = false;
                format = {};
                i++;
            } else if (arg == u"-filter_complex" || arg == u"-lavfi") {
                capture |= HasDesktopFilter(value);
                i++;
            }
        }

        return capture;
    }

private:
    static bool IsWhiteSpace(char16_t ch) noexcept {
        return std::iswspace(static_cast<wint_t>(ch)) != 0;
    }

    static bool IsNullOrWhiteSpace(std::u16string_view s) noexcept {
        for (char16_t ch : s) {
            if (!IsWhiteSpace(ch)) return false;
        }
        return true;
    }

    static constexpr char16_t ToLowerAscii(char16_t c) noexcept {
        return (c >= u'A' && c <= u'Z') ? static_cast<char16_t>(c + 32) : c;
    }

    static bool StartsWithAsciiIgnoreCase(std::u16string_view s, std::u16string_view prefix) noexcept {
        if (s.size() < prefix.size()) return false;
        for (size_t i = 0; i < prefix.size(); i++) {
            if (ToLowerAscii(s[i]) != ToLowerAscii(prefix[i])) return false;
        }
        return true;
    }

    static bool HasDesktopFilter(std::u16string_view value) noexcept {
        return MatchesFilterToken(value, u"ddagrab") || MatchesFilterToken(value, u"gfxcapture");
    }

    static bool MatchesFilterToken(std::u16string_view value, std::u16string_view token) noexcept {
        size_t pos = 0;
        while ((pos = value.find(token, pos)) != std::u16string_view::npos) {
            size_t left = pos;
            while (left > 0 && IsWhiteSpace(value[left - 1])) {
                left--;
            }
            bool valid_prefix = (left == 0) ||
                                (value[left - 1] == u';' || value[left - 1] == u',' || value[left - 1] == u']');

            size_t right = pos + token.size();
            while (right < value.size() && IsWhiteSpace(value[right])) {
                right++;
            }
            bool valid_suffix = (right == value.size()) ||
                                (value[right] == u'=' || value[right] == u',' || value[right] == u';' || value[right] == u'[');

            if (valid_prefix && valid_suffix) {
                return true;
            }
            pos += 1;
        }
        return false;
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_ffmpeg_is_screen_capture_command(
    const char16_t* cmd_ptr, int32_t cmd_len, uint8_t* out_is_capture) noexcept
{
    if (!out_is_capture) return VN_ERR_INVALID_ARG;
    if (!cmd_ptr || cmd_len <= 0) {
        *out_is_capture = 0;
        return VN_OK;
    }
    try {
        *out_is_capture = vnotch::services::FfmpegRecordingProbe::IsScreenCaptureCommand(
            std::u16string_view(cmd_ptr, static_cast<size_t>(cmd_len))) ? 1 : 0;
        return VN_OK;
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t __stdcall vn_ffmpeg_read_process_command_line(
    uint32_t process_id,
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_written) noexcept
{
    if (out_written == nullptr || process_id == 0 || out_cap < 0 || (out_cap > 0 && out_buf == nullptr)) {
        return VN_ERR_INVALID_ARG;
    }
    *out_written = 0;

    HANDLE proc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, process_id);
    if (proc == nullptr) {
        return VN_ERR_FAIL;
    }
    struct HandleGuard {
        HANDLE h;
        ~HandleGuard() noexcept {
            if (h != nullptr) {
                CloseHandle(h);
            }
        }
    } guard{proc};

    using NtQueryInformationProcessFn = LONG(NTAPI*)(
        HANDLE ProcessHandle,
        ULONG ProcessInformationClass,
        PVOID ProcessInformation,
        ULONG ProcessInformationLength,
        PULONG ReturnLength);

    static const NtQueryInformationProcessFn query_fn = []() noexcept -> NtQueryInformationProcessFn {
        HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
        if (ntdll == nullptr) return nullptr;
        return reinterpret_cast<NtQueryInformationProcessFn>(
            GetProcAddress(ntdll, "NtQueryInformationProcess"));
    }();
    if (query_fn == nullptr) {
        return VN_ERR_FAIL;
    }

    struct VnUnicodeString {
        USHORT Length;
        USHORT MaximumLength;
        PWSTR Buffer;
    };

    static constexpr ULONG kProcessCommandLineInformation = 60;
    static constexpr LONG kStatusInfoLengthMismatch = static_cast<LONG>(0xC0000004u);
    static constexpr LONG kStatusBufferOverflow = static_cast<LONG>(0x80000005u);
    static constexpr LONG kStatusBufferTooSmall = static_cast<LONG>(0xC0000023u);

    try {
        alignas(8) uint8_t stack_buf[4096];
        ULONG return_len = 0;
        uint8_t* active_buf = stack_buf;
        std::vector<uint8_t> heap_buf;

        LONG status = query_fn(proc, kProcessCommandLineInformation, stack_buf, sizeof(stack_buf), &return_len);
        if (status == kStatusInfoLengthMismatch || status == kStatusBufferOverflow || status == kStatusBufferTooSmall) {
            const ULONG alloc_size = (return_len > sizeof(stack_buf) && return_len <= 65536u) ? return_len : 65536u;
            heap_buf.resize(alloc_size);
            active_buf = heap_buf.data();
            status = query_fn(proc, kProcessCommandLineInformation, active_buf, alloc_size, &return_len);
        }
        if (status < 0) {
            return VN_ERR_FAIL;
        }

        const auto* ustr = reinterpret_cast<const VnUnicodeString*>(active_buf);
        const int32_t char_len = static_cast<int32_t>(ustr->Length / sizeof(wchar_t));
        if (ustr->Buffer == nullptr || char_len <= 0) {
            return VN_ERR_FAIL;
        }
        if (out_cap < char_len) {
            *out_written = char_len;
            return VN_ERR_BUFFER_TOO_SMALL;
        }
        std::memcpy(out_buf, ustr->Buffer, static_cast<size_t>(char_len) * sizeof(char16_t));
        *out_written = char_len;
        return VN_OK;
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

} // extern "C"
