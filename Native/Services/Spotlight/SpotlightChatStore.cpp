#include "vnotch_common.h"
#include <algorithm>
#include <cstdint>
#include <string_view>

namespace vnotch::services::spotlight {

class SpotlightChatStore final {
public:
    SpotlightChatStore() = delete;

    [[nodiscard]] static constexpr int32_t ComputeChatTitleLength(std::u16string_view content) noexcept {
        int32_t line_end = static_cast<int32_t>(content.size());
        for (size_t i = 0; i < content.size(); ++i) {
            if (content[i] == u'\r' || content[i] == u'\n') {
                line_end = static_cast<int32_t>(i);
                break;
            }
        }
        int32_t len = std::min(64, line_end);
        if (len > 0 && (content[static_cast<size_t>(len - 1)] & 0xFC00) == 0xD800) --len;
        return len;
    }
};

} // namespace vnotch::services::spotlight

extern "C" {

VN_EXPORT int32_t vn_spotlight_chat_compute_title_length(
    const char16_t* content,
    int32_t content_len,
    int32_t* out_title_len) noexcept {
    if (!out_title_len || (!content && content_len > 0) || content_len < 0) return VN_E_INVALID_ARG;
    const std::u16string_view sv(content ? content : u"", static_cast<size_t>(content_len));
    *out_title_len = vnotch::services::spotlight::SpotlightChatStore::ComputeChatTitleLength(sv);
    return VN_OK;
}

} // extern "C"
