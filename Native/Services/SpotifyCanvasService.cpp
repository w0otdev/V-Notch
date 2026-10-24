#include "vnotch_common.h"

namespace vnotch::services
{

class SpotifyCanvasService final
{
public:
    static bool NormalizeSessionCookie(
        std::u16string_view value,
        int32_t& out_start,
        int32_t& out_len) noexcept
    {
        std::u16string_view trimmed = vnotch::trim_view(value);
        if (trimmed.empty() || trimmed.size() > 4096)
        {
            out_start = 0;
            out_len = 0;
            return false;
        }
        for (char16_t c : trimmed)
        {
            if (c == u';' || c == u'\r' || c == u'\n')
            {
                out_start = 0;
                out_len = 0;
                return false;
            }
        }
        out_start = static_cast<int32_t>(trimmed.data() - value.data());
        out_len = static_cast<int32_t>(trimmed.size());
        return true;
    }
};

} // namespace vnotch::services

extern "C"
{

VN_EXPORT int32_t vn_spotify_canvas_service_normalize_cookie(
    const char16_t* val_ptr,
    int32_t val_len,
    uint8_t* out_valid,
    int32_t* out_start,
    int32_t* out_len) noexcept
{
    if (out_valid == nullptr || out_start == nullptr || out_len == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    int32_t s = 0, l = 0;
    bool ok = val_ptr != nullptr &&
              vnotch::services::SpotifyCanvasService::NormalizeSessionCookie(
                  vnotch::make_u16_view(val_ptr, val_len), s, l);
    *out_valid = ok ? 1u : 0u;
    *out_start = s;
    *out_len = l;
    return VN_OK;
}

} // extern "C"
