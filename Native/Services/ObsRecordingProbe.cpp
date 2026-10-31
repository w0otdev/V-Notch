#include "vnotch_common.h"
#include <cstdint>
#include <cstring>
#include <string>
#include <string_view>

namespace vnotch::services {

class ObsRecordingProbe final {
public:
    static void FeedLogChunk(
        std::u16string_view partial,
        std::u16string_view text,
        uint8_t* io_recording,
        uint8_t* io_streaming,
        uint8_t* io_replay,
        char16_t* out_partial_buf,
        int32_t out_partial_cap,
        int32_t* out_partial_len)
    {
        if (text.empty()) {
            CopyPartial(partial, out_partial_buf, out_partial_cap, out_partial_len);
            return;
        }

        std::u16string combined;
        std::u16string_view remaining;
        if (partial.empty()) {
            remaining = text;
        } else {
            combined.reserve(partial.size() + text.size());
            combined.append(partial);
            combined.append(text);
            remaining = combined;
        }

        bool rec = (*io_recording != 0);
        bool str = (*io_streaming != 0);
        bool rep = (*io_replay != 0);

        size_t newline = 0;
        while ((newline = remaining.find(u'\n')) != std::u16string_view::npos) {
            std::u16string_view line = remaining.substr(0, newline);
            Apply(line, u"==== Recording Start ===", u"==== Recording Stop ===", &rec);
            Apply(line, u"==== Streaming Start ===", u"==== Streaming Stop ===", &str);
            Apply(line, u"==== Replay Buffer Start ===", u"==== Replay Buffer Stop ===", &rep);
            remaining.remove_prefix(newline + 1);
        }

        *io_recording = rec ? 1 : 0;
        *io_streaming = str ? 1 : 0;
        *io_replay = rep ? 1 : 0;

        if (remaining.size() <= 4096) {
            CopyPartial(remaining, out_partial_buf, out_partial_cap, out_partial_len);
        } else {
            *out_partial_len = 0;
        }
    }

private:
    static void Apply(
        std::u16string_view line,
        std::u16string_view start_marker,
        std::u16string_view stop_marker,
        bool* io_active) noexcept
    {
        if (line.find(start_marker) != std::u16string_view::npos) *io_active = true;
        if (line.find(stop_marker) != std::u16string_view::npos) *io_active = false;
    }

    static void CopyPartial(
        std::u16string_view s,
        char16_t* out_buf,
        int32_t out_cap,
        int32_t* out_len) noexcept
    {
        if (s.empty() || static_cast<int32_t>(s.size()) > out_cap || !out_buf) {
            *out_len = 0;
            return;
        }
        std::memcpy(out_buf, s.data(), s.size() * sizeof(char16_t));
        *out_len = static_cast<int32_t>(s.size());
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_obs_feed_log_chunk(
    const char16_t* partial_ptr, int32_t partial_len,
    const char16_t* text_ptr, int32_t text_len,
    uint8_t* io_recording, uint8_t* io_streaming, uint8_t* io_replay,
    char16_t* out_partial_buf, int32_t out_partial_cap, int32_t* out_partial_len) noexcept
{
    if (!io_recording || !io_streaming || !io_replay || !out_partial_len) return VN_ERR_INVALID_ARG;
    try {
        std::u16string_view partial_v = (partial_ptr && partial_len > 0)
            ? std::u16string_view(partial_ptr, static_cast<size_t>(partial_len))
            : std::u16string_view{};
        std::u16string_view text_v = (text_ptr && text_len > 0)
            ? std::u16string_view(text_ptr, static_cast<size_t>(text_len))
            : std::u16string_view{};
        vnotch::services::ObsRecordingProbe::FeedLogChunk(
            partial_v, text_v,
            io_recording, io_streaming, io_replay,
            out_partial_buf, out_partial_cap, out_partial_len);
        return VN_OK;
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

} // extern "C"
