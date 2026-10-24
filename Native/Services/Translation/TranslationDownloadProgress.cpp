#include "vnotch_common.h"
#include <algorithm>
#include <cstdint>

namespace vnotch::services {

class TranslationDownloadProgress final {
public:
    static constexpr double ComputePercent(int64_t downloaded_bytes, int64_t total_bytes) noexcept {
        return total_bytes > 0
            ? std::clamp(static_cast<double>(downloaded_bytes) * 100.0 / static_cast<double>(total_bytes), 0.0, 100.0)
            : 0.0;
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_transdownload_compute_percent(
    int64_t downloaded_bytes, int64_t total_bytes, double* out_percent) noexcept
{
    if (!out_percent) return VN_ERR_INVALID_ARG;
    *out_percent = vnotch::services::TranslationDownloadProgress::ComputePercent(downloaded_bytes, total_bytes);
    return VN_OK;
}

} // extern "C"
