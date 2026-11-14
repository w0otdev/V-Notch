#include "../vnotch_native.h"
#include <memory>
#include <string>
#include <vector>

namespace
{
    constexpr int64_t kMinuteTicks = 600'000'000LL;
    constexpr size_t kMaxEntries = 256;

    struct QuotaEntry
    {
        int32_t provider;
        std::wstring account;
        std::wstring bucket;
        std::wstring label;
        int32_t duration;
        int64_t reset;
        int64_t observed;
        uint8_t notified;
    };

    struct QuotaTracker
    {
        std::vector<QuotaEntry> entries;
    };

    bool ValidText(const wchar_t* text, int32_t length) noexcept
    {
        return length >= 0 && length <= 1024 && (length == 0 || text != nullptr);
    }
}

VN_EXPORT int32_t __stdcall vn_ai_quota_notifications_create(void** out_tracker) noexcept
{
    if (!out_tracker) return VN_ERR_INVALID_ARG;
    *out_tracker = nullptr;
    try
    {
        *out_tracker = std::make_unique<QuotaTracker>().release();
        return VN_OK;
    }
    catch (...) { return VN_ERR_FAIL; }
}

VN_EXPORT int32_t __stdcall vn_ai_quota_notifications_destroy(void* tracker) noexcept
{
    const std::unique_ptr<QuotaTracker> owned(static_cast<QuotaTracker*>(tracker));
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_ai_quota_notifications_evaluate(
    void* tracker, int32_t provider, int32_t state,
    const wchar_t* account_ptr, int32_t account_len,
    const wchar_t* bucket_ptr, int32_t bucket_len,
    const wchar_t* label_ptr, int32_t label_len,
    int32_t duration_minutes, double used_percent,
    int64_t reset_utc_ticks, int64_t updated_utc_ticks, int64_t now_utc_ticks,
    int32_t* out_threshold) noexcept
{
    if (!tracker || !out_threshold || !ValidText(account_ptr, account_len) ||
        !ValidText(bucket_ptr, bucket_len) || !ValidText(label_ptr, label_len))
        return VN_ERR_INVALID_ARG;
    *out_threshold = 0;
    // Ready, fresh, finite samples only. A passed reset never invents replenishment.
    if (state != 3 || !std::isfinite(used_percent) || used_percent < 0 || used_percent > 100 ||
        updated_utc_ticks <= 0 || now_utc_ticks <= 0 ||
        updated_utc_ticks < now_utc_ticks - 10 * kMinuteTicks ||
        updated_utc_ticks > now_utc_ticks + kMinuteTicks ||
        (reset_utc_ticks != 0 && reset_utc_ticks <= now_utc_ticks))
        return VN_OK;
    try
    {
        const std::wstring_view account(account_ptr ? account_ptr : L"", static_cast<size_t>(account_len));
        const std::wstring_view bucket(bucket_ptr ? bucket_ptr : L"", static_cast<size_t>(bucket_len));
        const std::wstring_view label(label_ptr ? label_ptr : L"", static_cast<size_t>(label_len));
        auto& entries = static_cast<QuotaTracker*>(tracker)->entries;
        auto it = std::find_if(entries.begin(), entries.end(), [&](const QuotaEntry& entry)
        {
            return entry.provider == provider && entry.account == account && entry.bucket == bucket &&
                entry.label == label && entry.duration == duration_minutes;
        });
        if (it == entries.end())
        {
            if (entries.size() >= kMaxEntries)
            {
                // Keep the tracker bounded without forgetting active low-quota windows.
                std::erase_if(entries, [&](const QuotaEntry& entry)
                { return entry.reset != 0 && entry.reset <= now_utc_ticks; });
                if (entries.size() >= kMaxEntries) return VN_OK;
            }
            entries.push_back({ provider, std::wstring(account), std::wstring(bucket), std::wstring(label),
                duration_minutes, reset_utc_ticks, updated_utc_ticks, 0 });
            it = entries.end() - 1;
        }
        if (updated_utc_ticks < it->observed) return VN_OK;
        if (it->reset != 0 && it->reset <= now_utc_ticks && reset_utc_ticks > now_utc_ticks)
            it->notified = 0;
        // Providers without a reset timestamp can rearm after a confirmed recovery.
        if (it->reset == 0 && reset_utc_ticks == 0 && used_percent <= 50)
            it->notified = 0;
        if (reset_utc_ticks != 0) it->reset = reset_utc_ticks;
        it->observed = updated_utc_ticks;

        // Strictly below the remaining percentage; send only the most urgent crossed level.
        if (used_percent > 80 && (it->notified & 2) == 0)
        {
            it->notified |= 3;
            *out_threshold = 20;
        }
        else if (used_percent > 50 && (it->notified & 1) == 0)
        {
            it->notified |= 1;
            *out_threshold = 50;
        }
        return VN_OK;
    }
    catch (...) { return VN_ERR_FAIL; }
}
