#include "../vnotch_common.h"
#include "../vnotch_handle_registry.h"
#include <winhttp.h>
#include <algorithm>
#include <atomic>
#include <cerrno>
#include <cstdint>
#include <memory>
#include <string>
#include <utility>
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
    // Cancellation is monotonic for the lifetime of the request: once set it is never cleared.
    // state_lock makes "check cancelled, then publish the request handle" and "set cancelled, then
    // take the published handle" atomic with respect to each other, so a cancel can never slip
    // between the check and the publish and leave a handle that nobody closes.
    SRWLOCK state_lock = SRWLOCK_INIT;
    std::atomic<bool> cancelled{false};
    std::atomic<HINTERNET> live_request{nullptr};

    Request() = default;
    Request(const Request&) = delete;
    Request& operator=(const Request&) = delete;

    bool IsCancelled() const noexcept { return cancelled.load(std::memory_order_acquire); }

    void Cancel() noexcept {
        HINTERNET active = nullptr;
        HINTERNET conn = nullptr;
        AcquireSRWLockExclusive(&state_lock);
        cancelled.store(true, std::memory_order_release);
        active = live_request.exchange(nullptr, std::memory_order_acq_rel);
        conn = std::exchange(connect, nullptr);
        ReleaseSRWLockExclusive(&state_lock);
        if (active) WinHttpCloseHandle(active);
        if (conn) WinHttpCloseHandle(conn);
    }

    // Publishes connect + request handles atomically so Cancel or a concurrent Send can close them.
    // Displaced handles from any prior Send and rejected handles after Cancel are closed outside state_lock.
    bool PublishHandles(HINTERNET new_conn, HINTERNET new_req) noexcept {
        HINTERNET old_req = nullptr;
        HINTERNET old_conn = nullptr;
        AcquireSRWLockExclusive(&state_lock);
        const bool accepted = !cancelled.load(std::memory_order_acquire);
        if (accepted) {
            old_req = live_request.exchange(new_req, std::memory_order_acq_rel);
            old_conn = std::exchange(connect, new_conn);
        }
        ReleaseSRWLockExclusive(&state_lock);
        if (old_req) WinHttpCloseHandle(old_req);
        if (old_conn) WinHttpCloseHandle(old_conn);
        if (!accepted) {
            WinHttpCloseHandle(new_req);
            WinHttpCloseHandle(new_conn);
        }
        return accepted;
    }

    void CloseHandles() noexcept {
        HINTERNET active = nullptr;
        HINTERNET conn = nullptr;
        AcquireSRWLockExclusive(&state_lock);
        active = live_request.exchange(nullptr, std::memory_order_acq_rel);
        conn = std::exchange(connect, nullptr);
        ReleaseSRWLockExclusive(&state_lock);
        if (active) WinHttpCloseHandle(active);
        if (conn) WinHttpCloseHandle(conn);
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

// A send or read holds a lease for its whole duration, so vn_http_destroy can never free a request that is
// still in use: the object is destroyed when the last lease is released.
vnotch::HandleRegistry<Request> g_requests;

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
        *out_handle = hp::g_requests.Add(std::make_unique<hp::Request>());
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
    auto lease = hp::g_requests.Acquire(handle);
    hp::Request* req = lease.get();
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
        const std::wstring verb = hp::ToWide(method, method_length);
        const std::wstring header_block = headers_length > 0 ? hp::ToWide(headers, headers_length) : std::wstring();

        req->CloseHandles();
        if (req->IsCancelled()) {
            *out_error = ERROR_WINHTTP_OPERATION_CANCELLED;
            return VN_ERR_FAIL;
        }
        HINTERNET conn = WinHttpConnect(session, host.c_str(), parts.nPort, 0);
        if (!conn) {
            *out_error = static_cast<int32_t>(GetLastError());
            return VN_ERR_FAIL;
        }
        HINTERNET request = WinHttpOpenRequest(conn, verb.c_str(), target.c_str(), nullptr, WINHTTP_NO_REFERER,
                                               WINHTTP_DEFAULT_ACCEPT_TYPES, secure ? WINHTTP_FLAG_SECURE : 0);
        if (!request) {
            *out_error = static_cast<int32_t>(GetLastError());
            WinHttpCloseHandle(conn);
            return VN_ERR_FAIL;
        }
        if (!req->PublishHandles(conn, request)) {
            *out_error = ERROR_WINHTTP_OPERATION_CANCELLED;
            return VN_ERR_FAIL;
        }

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
            // Open-Meteo sends zlib-wrapped deflate when deflate is advertised, which WinHTTP's deflate decoder rejects with E_ABORT.
            DWORD decompression = WINHTTP_DECOMPRESSION_FLAG_GZIP;
            WinHttpSetOption(request, WINHTTP_OPTION_DECOMPRESSION, &decompression, sizeof(decompression));
        }

        if (req->IsCancelled()) {
            *out_error = ERROR_WINHTTP_OPERATION_CANCELLED;
            return VN_ERR_FAIL;
        }
        const BOOL sent = WinHttpSendRequest(
            request,
            header_block.empty() ? WINHTTP_NO_ADDITIONAL_HEADERS : header_block.c_str(),
            header_block.empty() ? 0 : static_cast<DWORD>(header_block.size()),
            body_length > 0 ? const_cast<uint8_t*>(body) : WINHTTP_NO_REQUEST_DATA,
            static_cast<DWORD>(body_length), static_cast<DWORD>(body_length), 0);
        if (!sent || !WinHttpReceiveResponse(request, nullptr)) {
            *out_error = req->IsCancelled() ? static_cast<int32_t>(ERROR_WINHTTP_OPERATION_CANCELLED) : static_cast<int32_t>(GetLastError());
            return VN_ERR_FAIL;
        }

        DWORD status = 0;
        DWORD size = sizeof(status);
        if (!WinHttpQueryHeaders(request, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER, WINHTTP_HEADER_NAME_BY_INDEX,
                                 &status, &size, WINHTTP_NO_HEADER_INDEX)) {
            *out_error = req->IsCancelled() ? static_cast<int32_t>(ERROR_WINHTTP_OPERATION_CANCELLED) : static_cast<int32_t>(GetLastError());
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
    if (!out_length) return VN_ERR_INVALID_ARG;
    *out_length = 0;
    auto lease = hp::g_requests.Acquire(handle);
    hp::Request* req = lease.get();
    HINTERNET request = req ? req->live_request.load(std::memory_order_acquire) : nullptr;
    if (!req || !request || capacity < 0 || capacity > hp::kMaxHeaderChars || (capacity > 0 && !buffer))
        return VN_ERR_INVALID_ARG;
    DWORD bytes = static_cast<DWORD>(capacity) * sizeof(char16_t);
    if (WinHttpQueryHeaders(request, WINHTTP_QUERY_RAW_HEADERS_CRLF, WINHTTP_HEADER_NAME_BY_INDEX,
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
    if (!out_length) return VN_ERR_INVALID_ARG;
    *out_length = -1;
    auto lease = hp::g_requests.Acquire(handle);
    hp::Request* req = lease.get();
    HINTERNET request = req ? req->live_request.load(std::memory_order_acquire) : nullptr;
    if (!req || !request) return VN_ERR_INVALID_ARG;
    wchar_t text[32]{};
    DWORD bytes = sizeof(text);
    if (WinHttpQueryHeaders(request, WINHTTP_QUERY_CONTENT_LENGTH, WINHTTP_HEADER_NAME_BY_INDEX, text, &bytes,
                            WINHTTP_NO_HEADER_INDEX)) {
        wchar_t* end = nullptr;
        errno = 0;
        const long long value = _wcstoi64(text, &end, 10);
        if (errno == 0 && end != text && value >= 0) *out_length = value;
    }
    return VN_OK;
}

// Blocking read. out_read = 0 with VN_OK means end of body.
VN_EXPORT int32_t __stdcall vn_http_read(int64_t handle, uint8_t* buffer, int32_t capacity, int32_t* out_read, int32_t* out_error) noexcept {
    if (!out_read || !out_error) return VN_ERR_INVALID_ARG;
    *out_read = 0;
    *out_error = 0;
    auto lease = hp::g_requests.Acquire(handle);
    hp::Request* req = lease.get();
    if (!req || !buffer || capacity <= 0) return VN_ERR_INVALID_ARG;
    HINTERNET request = req->live_request.load(std::memory_order_acquire);
    if (!request) {
        *out_error = ERROR_WINHTTP_OPERATION_CANCELLED;
        return VN_ERR_FAIL;
    }
    DWORD read = 0;
    if (!WinHttpReadData(request, buffer, static_cast<DWORD>(capacity), &read)) {
        *out_error = req->IsCancelled() ? static_cast<int32_t>(ERROR_WINHTTP_OPERATION_CANCELLED) : static_cast<int32_t>(GetLastError());
        return VN_ERR_FAIL;
    }
    *out_read = static_cast<int32_t>(read);
    return VN_OK;
}

// Aborts blocked sends and reads. Safe from any thread; the handle must still be destroyed afterwards.
// Cancelling is permanent: a send that has not published its request handle yet fails instead of starting.
VN_EXPORT int32_t __stdcall vn_http_cancel(int64_t handle) noexcept {
    auto lease = hp::g_requests.Acquire(handle);
    hp::Request* req = lease.get();
    if (!req) return VN_ERR_INVALID_ARG;
    req->Cancel();
    return VN_OK;
}

// Releases the request. A send or read still in flight is aborted first and keeps the object alive until it
// returns, so destroy never frees memory that another thread is using.
VN_EXPORT int32_t __stdcall vn_http_destroy(int64_t handle) noexcept {
    if (handle <= 0) return VN_ERR_INVALID_ARG;
    {
        auto lease = hp::g_requests.Acquire(handle);
        if (lease) lease->Cancel();
    }
    hp::g_requests.Remove(handle);
    return VN_OK;
}

} // extern "C"
