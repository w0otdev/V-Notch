#include "../vnotch_common.h"
#include <winhttp.h>
#include <atomic>
#include <cstdint>
#include <memory>
#include <string>
#include <vector>

#pragma comment(lib, "winhttp.lib")

// WinHTTP transport behind the managed NativeWinHttpHandler. A request object owns its connect and request
// handles. Reads are blocking; the caller aborts them with vn_http_cancel from another thread.

namespace vnotch::services::http {

constexpr uint32_t kFlagAllowRedirect = 1u << 0;
constexpr uint32_t kFlagUseCookies = 1u << 1;
constexpr uint32_t kFlagDecompress = 1u << 2;
constexpr int32_t kMaxUrlChars = 8192;
constexpr int32_t kMaxHeaderChars = 65536;
constexpr int32_t kMaxBodyBytes = 64 * 1024 * 1024;
constexpr uint32_t kResolveTimeoutMs = 15000;

struct Request {
    HINTERNET connect = nullptr;
    HINTERNET request = nullptr;
    std::atomic<bool> cancelled{false};
    std::atomic<HINTERNET> live_request{nullptr};

    Request() = default;
    Request(const Request&) = delete;
    Request& operator=(const Request&) = delete;

    void CloseHandles() noexcept {
        HINTERNET active = live_request.exchange(nullptr);
        if (active) WinHttpCloseHandle(active);
        if (connect) {
            WinHttpCloseHandle(connect);
            connect = nullptr;
        }
        request = nullptr;
    }
    ~Request() { CloseHandles(); }
};

INIT_ONCE g_session_once = INIT_ONCE_STATIC_INIT;
HINTERNET g_session = nullptr;

BOOL CALLBACK OpenSession(PINIT_ONCE, PVOID, PVOID*) noexcept {
    g_session = WinHttpOpen(L"VNotch", WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY, WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0);
    if (g_session == nullptr) {
        // Windows versions without automatic proxy support fall back to the default proxy.
        g_session = WinHttpOpen(L"VNotch", WINHTTP_ACCESS_TYPE_DEFAULT_PROXY, WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0);
    }
    if (g_session) {
        DWORD protocols = WINHTTP_FLAG_SECURE_PROTOCOL_TLS1_2 | 0x00002000; // TLS 1.2 | TLS 1.3 where available
        if (!WinHttpSetOption(g_session, WINHTTP_OPTION_SECURE_PROTOCOLS, &protocols, sizeof(protocols))) {
            protocols = WINHTTP_FLAG_SECURE_PROTOCOL_TLS1_2;
            WinHttpSetOption(g_session, WINHTTP_OPTION_SECURE_PROTOCOLS, &protocols, sizeof(protocols));
        }
        DWORD http2 = WINHTTP_PROTOCOL_FLAG_HTTP2;
        WinHttpSetOption(g_session, WINHTTP_OPTION_ENABLE_HTTP_PROTOCOL, &http2, sizeof(http2));
    }
    return TRUE;
}

HINTERNET Session() noexcept {
    InitOnceExecuteOnce(&g_session_once, OpenSession, nullptr, nullptr);
    return g_session;
}

SRWLOCK g_lock = SRWLOCK_INIT;
std::vector<std::unique_ptr<Request>> g_requests;

struct Guard {
    SRWLOCK* lock;
    explicit Guard(SRWLOCK* l) noexcept : lock(l) { AcquireSRWLockExclusive(lock); }
    Guard(const Guard&) = delete;
    Guard& operator=(const Guard&) = delete;
    ~Guard() { ReleaseSRWLockExclusive(lock); }
};

Request* Find(int64_t handle) noexcept {
    Guard guard(&g_lock);
    if (handle <= 0 || static_cast<size_t>(handle) > g_requests.size()) return nullptr;
    return g_requests[static_cast<size_t>(handle) - 1].get();
}

std::wstring ToWide(const char16_t* text, int32_t length) {
    return std::wstring(reinterpret_cast<const wchar_t*>(text), static_cast<size_t>(length));
}

} // namespace vnotch::services::http

namespace hp = vnotch::services::http;

extern "C" {

VN_EXPORT int32_t __stdcall vn_http_create(int64_t* out_handle) noexcept {
    if (!out_handle) return VN_ERR_INVALID_ARG;
    *out_handle = 0;
    try {
        auto request = std::make_unique<hp::Request>();
        hp::Guard guard(&hp::g_lock);
        for (size_t i = 0; i < hp::g_requests.size(); ++i) {
            if (!hp::g_requests[i]) {
                hp::g_requests[i] = std::move(request);
                *out_handle = static_cast<int64_t>(i) + 1;
                return VN_OK;
            }
        }
        hp::g_requests.push_back(std::move(request));
        *out_handle = static_cast<int64_t>(hp::g_requests.size());
        return VN_OK;
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

// Sends the request and waits for the response headers. out_status is the HTTP status; out_error is the
// Win32/WinHTTP error code when the call fails (0 otherwise).
VN_EXPORT int32_t __stdcall vn_http_send(
    int64_t handle,
    const char16_t* method, int32_t method_length,
    const char16_t* url, int32_t url_length,
    const char16_t* headers, int32_t headers_length,
    const uint8_t* body, int32_t body_length,
    uint32_t timeout_ms, uint32_t flags,
    int32_t* out_status, int32_t* out_error) noexcept {
    if (!out_status || !out_error) return VN_ERR_INVALID_ARG;
    *out_status = 0;
    *out_error = 0;
    hp::Request* req = hp::Find(handle);
    if (!req || !method || method_length <= 0 || method_length > 16 || !url || url_length <= 0 || url_length > hp::kMaxUrlChars ||
        headers_length < 0 || headers_length > hp::kMaxHeaderChars || (headers_length > 0 && !headers) ||
        body_length < 0 || body_length > hp::kMaxBodyBytes || (body_length > 0 && !body))
        return VN_ERR_INVALID_ARG;

    try {
        HINTERNET session = hp::Session();
        if (!session) {
            *out_error = static_cast<int32_t>(GetLastError());
            return VN_ERR_FAIL;
        }
        const std::wstring wide_url = hp::ToWide(url, url_length);
        URL_COMPONENTS parts{};
        parts.dwStructSize = sizeof(parts);
        parts.dwSchemeLength = parts.dwHostNameLength = parts.dwUrlPathLength = parts.dwExtraInfoLength = static_cast<DWORD>(-1);
        if (!WinHttpCrackUrl(wide_url.c_str(), static_cast<DWORD>(wide_url.size()), 0, &parts) || parts.dwHostNameLength == 0) {
            *out_error = static_cast<int32_t>(GetLastError());
            return VN_ERR_INVALID_ARG;
        }
        const bool secure = parts.nScheme == INTERNET_SCHEME_HTTPS;
        if (!secure && parts.nScheme != INTERNET_SCHEME_HTTP) return VN_ERR_INVALID_ARG;

        const std::wstring host(parts.lpszHostName, parts.dwHostNameLength);
        std::wstring target(parts.lpszUrlPath, parts.dwUrlPathLength);
        target.append(parts.lpszExtraInfo, parts.dwExtraInfoLength);
        if (target.empty()) target = L"/";

        req->CloseHandles();
        req->cancelled = false;
        req->connect = WinHttpConnect(session, host.c_str(), parts.nPort, 0);
        if (!req->connect) {
            *out_error = static_cast<int32_t>(GetLastError());
            return VN_ERR_FAIL;
        }
        const std::wstring verb = hp::ToWide(method, method_length);
        HINTERNET request = WinHttpOpenRequest(req->connect, verb.c_str(), target.c_str(), nullptr, WINHTTP_NO_REFERER,
                                               WINHTTP_DEFAULT_ACCEPT_TYPES, secure ? WINHTTP_FLAG_SECURE : 0);
        if (!request) {
            *out_error = static_cast<int32_t>(GetLastError());
            return VN_ERR_FAIL;
        }
        req->request = request;
        req->live_request.store(request);

        const int timeout = static_cast<int>(std::min<uint32_t>(timeout_ms, static_cast<uint32_t>(INT32_MAX)));
        WinHttpSetTimeouts(request, static_cast<int>(hp::kResolveTimeoutMs), timeout, timeout, timeout);
        if ((flags & hp::kFlagAllowRedirect) == 0) {
            DWORD policy = WINHTTP_OPTION_REDIRECT_POLICY_NEVER;
            WinHttpSetOption(request, WINHTTP_OPTION_REDIRECT_POLICY, &policy, sizeof(policy));
        }
        DWORD disabled = 0;
        if ((flags & hp::kFlagUseCookies) == 0) disabled |= WINHTTP_DISABLE_COOKIES;
        if ((flags & hp::kFlagAllowRedirect) == 0) disabled |= WINHTTP_DISABLE_REDIRECTS;
        if (disabled != 0) WinHttpSetOption(request, WINHTTP_OPTION_DISABLE_FEATURE, &disabled, sizeof(disabled));
        if ((flags & hp::kFlagDecompress) != 0) {
            DWORD decompression = WINHTTP_DECOMPRESSION_FLAG_ALL;
            WinHttpSetOption(request, WINHTTP_OPTION_DECOMPRESSION, &decompression, sizeof(decompression));
        }

        const std::wstring header_block = headers_length > 0 ? hp::ToWide(headers, headers_length) : std::wstring();
        const BOOL sent = WinHttpSendRequest(
            request,
            header_block.empty() ? WINHTTP_NO_ADDITIONAL_HEADERS : header_block.c_str(),
            header_block.empty() ? 0 : static_cast<DWORD>(header_block.size()),
            body_length > 0 ? const_cast<uint8_t*>(body) : WINHTTP_NO_REQUEST_DATA,
            static_cast<DWORD>(body_length), static_cast<DWORD>(body_length), 0);
        if (!sent || !WinHttpReceiveResponse(request, nullptr)) {
            *out_error = static_cast<int32_t>(GetLastError());
            return VN_ERR_FAIL;
        }

        DWORD status = 0;
        DWORD size = sizeof(status);
        if (!WinHttpQueryHeaders(request, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER, WINHTTP_HEADER_NAME_BY_INDEX,
                                 &status, &size, WINHTTP_NO_HEADER_INDEX)) {
            *out_error = static_cast<int32_t>(GetLastError());
            return VN_ERR_FAIL;
        }
        *out_status = static_cast<int32_t>(status);
        return VN_OK;
    } catch (...) {
        *out_error = static_cast<int32_t>(ERROR_OUTOFMEMORY);
        return VN_ERR_FAIL;
    }
}

// Raw response header block ("HTTP/1.1 200 OK\r\nName: value\r\n..."). Two-step: BUFFER_TOO_SMALL returns the
// required length (in characters) in out_length.
VN_EXPORT int32_t __stdcall vn_http_get_headers(int64_t handle, char16_t* buffer, int32_t capacity, int32_t* out_length) noexcept {
    hp::Request* req = hp::Find(handle);
    if (!req || !req->request || !out_length || capacity < 0 || (capacity > 0 && !buffer)) return VN_ERR_INVALID_ARG;
    DWORD bytes = static_cast<DWORD>(capacity) * sizeof(char16_t);
    if (WinHttpQueryHeaders(req->request, WINHTTP_QUERY_RAW_HEADERS_CRLF, WINHTTP_HEADER_NAME_BY_INDEX,
                            capacity > 0 ? buffer : WINHTTP_NO_OUTPUT_BUFFER, &bytes, WINHTTP_NO_HEADER_INDEX)) {
        *out_length = static_cast<int32_t>(bytes / sizeof(char16_t));
        return VN_OK;
    }
    if (GetLastError() == ERROR_INSUFFICIENT_BUFFER) {
        *out_length = static_cast<int32_t>(bytes / sizeof(char16_t)) + 1;
        return VN_ERR_BUFFER_TOO_SMALL;
    }
    return VN_ERR_FAIL;
}

// Content-Length of the (possibly compressed) response, or -1 when absent.
VN_EXPORT int32_t __stdcall vn_http_get_content_length(int64_t handle, int64_t* out_length) noexcept {
    hp::Request* req = hp::Find(handle);
    if (!req || !req->request || !out_length) return VN_ERR_INVALID_ARG;
    wchar_t text[32]{};
    DWORD bytes = sizeof(text);
    *out_length = -1;
    if (WinHttpQueryHeaders(req->request, WINHTTP_QUERY_CONTENT_LENGTH, WINHTTP_HEADER_NAME_BY_INDEX, text, &bytes,
                            WINHTTP_NO_HEADER_INDEX)) {
        wchar_t* end = nullptr;
        const long long value = _wcstoi64(text, &end, 10);
        if (end != text && value >= 0) *out_length = value;
    }
    return VN_OK;
}

// Blocking read. out_read = 0 with VN_OK means end of body.
VN_EXPORT int32_t __stdcall vn_http_read(int64_t handle, uint8_t* buffer, int32_t capacity, int32_t* out_read, int32_t* out_error) noexcept {
    hp::Request* req = hp::Find(handle);
    if (!req || !buffer || capacity <= 0 || !out_read || !out_error) return VN_ERR_INVALID_ARG;
    *out_read = 0;
    *out_error = 0;
    HINTERNET request = req->live_request.load();
    if (!request) {
        *out_error = ERROR_WINHTTP_OPERATION_CANCELLED;
        return VN_ERR_FAIL;
    }
    DWORD read = 0;
    if (!WinHttpReadData(request, buffer, static_cast<DWORD>(capacity), &read)) {
        *out_error = static_cast<int32_t>(GetLastError());
        return VN_ERR_FAIL;
    }
    *out_read = static_cast<int32_t>(read);
    return VN_OK;
}

// Aborts blocked sends and reads. Safe from any thread; the handle must still be destroyed afterwards.
VN_EXPORT int32_t __stdcall vn_http_cancel(int64_t handle) noexcept {
    hp::Request* req = hp::Find(handle);
    if (!req) return VN_ERR_INVALID_ARG;
    req->cancelled = true;
    HINTERNET active = req->live_request.exchange(nullptr);
    if (active) WinHttpCloseHandle(active);
    return VN_OK;
}

// Releases the request. The caller must not have a send or read in flight.
VN_EXPORT int32_t __stdcall vn_http_destroy(int64_t handle) noexcept {
    hp::Guard guard(&hp::g_lock);
    if (handle <= 0 || static_cast<size_t>(handle) > hp::g_requests.size()) return VN_ERR_INVALID_ARG;
    hp::g_requests[static_cast<size_t>(handle) - 1].reset();
    return VN_OK;
}

} // extern "C"
