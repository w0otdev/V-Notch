#include "vnotch_common.h"
#include <cstdint>

namespace vnotch::services {

class NetworkPrivacy final {
public:
    // 0=Updates, 1=Artwork, 2=Lyrics, 3=Subtitles, 4=Canvas, 5=Weather, 6=Ai, 7=ExternalLinks, 8=TranslationModels, 9=CurrencyRates
    static constexpr bool Allows(
        bool enable_local_only_mode,
        int32_t feature,
        bool enable_online_artwork_lookup,
        bool enable_online_lyrics,
        bool allow_online_subtitles,
        bool allow_online_canvas,
        bool enable_spotify_canvas,
        bool canvas_consent_accepted,
        bool allow_online_weather,
        bool allow_online_ai,
        bool enable_currency_conversion) noexcept
    {
        if (enable_local_only_mode) return false;
        switch (feature) {
            case 1: return enable_online_artwork_lookup;
            case 2: return enable_online_lyrics;
            case 3: return allow_online_subtitles;
            case 4: return allow_online_canvas && enable_spotify_canvas && canvas_consent_accepted;
            case 5: return allow_online_weather;
            case 6: return allow_online_ai;
            case 9: return enable_currency_conversion;
            default: return true;
        }
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_netprivacy_allows(
    uint8_t enable_local_only_mode,
    int32_t feature,
    uint8_t enable_online_artwork_lookup,
    uint8_t enable_online_lyrics,
    uint8_t allow_online_subtitles,
    uint8_t allow_online_canvas,
    uint8_t enable_spotify_canvas,
    uint8_t canvas_consent_accepted,
    uint8_t allow_online_weather,
    uint8_t allow_online_ai,
    uint8_t enable_currency_conversion,
    uint8_t* out_allowed) noexcept
{
    if (!out_allowed) return VN_ERR_INVALID_ARG;
    *out_allowed = vnotch::services::NetworkPrivacy::Allows(
        enable_local_only_mode != 0,
        feature,
        enable_online_artwork_lookup != 0,
        enable_online_lyrics != 0,
        allow_online_subtitles != 0,
        allow_online_canvas != 0,
        enable_spotify_canvas != 0,
        canvas_consent_accepted != 0,
        allow_online_weather != 0,
        allow_online_ai != 0,
        enable_currency_conversion != 0) ? 1 : 0;
    return VN_OK;
}

} // extern "C"
