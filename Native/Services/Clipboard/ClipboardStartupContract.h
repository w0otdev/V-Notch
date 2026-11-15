#pragma once
#include <cstdint>

// Startup is streamed in at most 32 items. Each item owns a 4 KiB plaintext
// slice supplied by the caller; larger records use the regular bounded reader.
inline constexpr int32_t kClipboardStartupBatch = 32;
inline constexpr int32_t kClipboardStartupStride = 4096;
#pragma pack(push, 1)
struct VnClipboardStartupItem {
    int32_t directory_offset, directory_length;
    int32_t status, payload_length, migration_status;
};
#pragma pack(pop)
static_assert(sizeof(VnClipboardStartupItem) == 20);
