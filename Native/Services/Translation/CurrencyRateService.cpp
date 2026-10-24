#include "vnotch_common.h"

namespace vnotch::services
{

class CurrencyRateService final
{
public:
    static constexpr bool ValidateRateWindow(
        std::u16string_view rate_base,
        std::u16string_view rate_quote,
        std::u16string_view source,
        std::u16string_view target,
        uint8_t rate_in_bounds,
        int32_t rate_date_day_number,
        int32_t rate_checked_day_number,
        int32_t today_day_number) noexcept
    {
        if (rate_in_bounds == 0)
        {
            return false;
        }
        if (rate_base != source || rate_quote != target)
        {
            return false;
        }
        return rate_checked_day_number <= today_day_number &&
               rate_date_day_number <= today_day_number + 1 &&
               rate_date_day_number >= today_day_number - 7 &&
               rate_date_day_number <= rate_checked_day_number + 1;
    }
};

} // namespace vnotch::services

extern "C"
{

VN_EXPORT int32_t vn_currency_rate_validate(
    const char16_t* base_ptr, int32_t base_len,
    const char16_t* quote_ptr, int32_t quote_len,
    const char16_t* src_ptr, int32_t src_len,
    const char16_t* tgt_ptr, int32_t tgt_len,
    uint8_t rate_in_bounds,
    int32_t rate_date_day_number,
    int32_t rate_checked_day_number,
    int32_t today_day_number,
    uint8_t* out_valid) noexcept
{
    if (out_valid == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_valid = vnotch::services::CurrencyRateService::ValidateRateWindow(
        vnotch::make_u16_view(base_ptr, base_len),
        vnotch::make_u16_view(quote_ptr, quote_len),
        vnotch::make_u16_view(src_ptr, src_len),
        vnotch::make_u16_view(tgt_ptr, tgt_len),
        rate_in_bounds,
        rate_date_day_number,
        rate_checked_day_number,
        today_day_number) ? 1u : 0u;
    return VN_OK;
}

} // extern "C"
