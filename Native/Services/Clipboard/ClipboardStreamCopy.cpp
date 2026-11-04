#include "vnotch_native.h"
#include <array>
using ReadCallback = int32_t (*)(intptr_t, uint8_t*, int32_t);
using WriteCallback = int32_t (*)(intptr_t, const uint8_t*, int32_t);

// Compression, hashing and Personal encryption remain stream adapters. The transfer
// loop and its fixed buffer are native; regular files use ReadFile/WriteFile directly.
VN_EXPORT int32_t vn_clipboard_stream_copy(intptr_t source, intptr_t destination, ReadCallback read, WriteCallback write,
    intptr_t context, int64_t maximum, int64_t* written, int32_t* error) noexcept {
    if (!written || !error || maximum < 0 || (!source && !read) || (!destination && !write)) return VN_ERR_INVALID_ARG;
    *written = 0; *error = 0;
    struct Buffer { std::array<uint8_t, 65536> bytes; ~Buffer() { SecureZeroMemory(bytes.data(), bytes.size()); } } buffer;
    while (true) {
        int32_t count = 0;
        if (source) {
            DWORD received = 0;
            if (!ReadFile(reinterpret_cast<HANDLE>(source), buffer.bytes.data(), static_cast<DWORD>(buffer.bytes.size()), &received, nullptr)) { *error = GetLastError(); return VN_ERR_FAIL; }
            count = static_cast<int32_t>(received);
        } else count = read(context, buffer.bytes.data(), static_cast<int32_t>(buffer.bytes.size()));
        if (count < 0 || static_cast<size_t>(count) > buffer.bytes.size()) return VN_ERR_FAIL;
        if (count == 0) return VN_OK;
        if (count > maximum - *written) return 1;
        if (destination) {
            DWORD sent = 0;
            while (sent < static_cast<DWORD>(count)) {
                DWORD chunk = 0;
                if (!WriteFile(reinterpret_cast<HANDLE>(destination), buffer.bytes.data() + sent, count - sent, &chunk, nullptr) || !chunk) { *error = GetLastError(); return VN_ERR_FAIL; }
                sent += chunk;
            }
        } else if (write(context, buffer.bytes.data(), count) != 0) return VN_ERR_FAIL;
        *written += count;
    }
}
