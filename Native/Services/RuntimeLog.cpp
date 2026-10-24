#include "../vnotch_common.h"
#include <string_view>

namespace vnotch::services {

class RuntimeLog final {
public:
    static constexpr bool IsLevelEnabled(bool initialized, int32_t level, int32_t minimum_level) noexcept {
        return initialized && level >= minimum_level;
    }

    static constexpr int32_t ResolveQueueCapacity(int32_t level, int32_t normal_cap, int32_t critical_cap) noexcept {
        // LogLevel.Warn == 3
        return level >= 3 ? critical_cap : normal_cap;
    }

    static constexpr bool ShouldRotateLog(
        bool force_session_rotation,
        int64_t current_length,
        int32_t incoming_bytes,
        int64_t max_log_size_bytes) noexcept {
        if (current_length <= 0) return false;
        if (!force_session_rotation && (current_length + incoming_bytes) <= max_log_size_bytes) return false;
        return true;
    }

    static constexpr bool ShouldForwardToCrashReporter(bool initialized, std::u16string_view category) noexcept {
        return !initialized && !vnotch::vn_starts_with_icase_ascii(category, u"CRASH");
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_runtime_log_is_level_enabled(
    uint8_t initialized,
    int32_t level,
    int32_t minimum_level,
    uint8_t* out_enabled) noexcept {
    if (!out_enabled) return VN_E_INVALID_ARG;
    *out_enabled = vnotch::services::RuntimeLog::IsLevelEnabled(initialized != 0, level, minimum_level) ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t vn_runtime_log_resolve_queue_capacity(
    int32_t level,
    int32_t normal_cap,
    int32_t critical_cap,
    int32_t* out_capacity) noexcept {
    if (!out_capacity) return VN_E_INVALID_ARG;
    *out_capacity = vnotch::services::RuntimeLog::ResolveQueueCapacity(level, normal_cap, critical_cap);
    return VN_OK;
}

VN_EXPORT int32_t vn_runtime_log_should_rotate(
    uint8_t force_session_rotation,
    int64_t current_length,
    int32_t incoming_bytes,
    int64_t max_log_size_bytes,
    uint8_t* out_rotate) noexcept {
    if (!out_rotate) return VN_E_INVALID_ARG;
    *out_rotate = vnotch::services::RuntimeLog::ShouldRotateLog(
        force_session_rotation != 0, current_length, incoming_bytes, max_log_size_bytes) ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t vn_runtime_log_should_forward_crash(
    uint8_t initialized,
    const char16_t* category,
    int32_t category_len,
    uint8_t* out_forward) noexcept {
    if (!out_forward || category_len < 0) return VN_E_INVALID_ARG;
    std::u16string_view sv(category ? category : u"", static_cast<size_t>(category_len));
    *out_forward = vnotch::services::RuntimeLog::ShouldForwardToCrashReporter(initialized != 0, sv) ? 1u : 0u;
    return VN_OK;
}

} // extern "C"
