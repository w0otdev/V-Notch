#include "vnotch_common.h"

using namespace vnotch;

namespace vnotch::services
{
    class TranslationJsonBoundary final
    {
    public:
        static int32_t vn_json_boundary_append_impl(VnJsonBoundaryState* state,
    const char16_t* chunk_ptr, int32_t chunk_len,
    uint8_t* out_completed) noexcept
        {
    if (!state || !out_completed) return VN_ERR_INVALID_ARG;
    *out_completed = 0;

    std::u16string_view chunk = vnotch::make_view(chunk_ptr, chunk_len);
    for (char16_t c : chunk)
    {
        if (state->quoted != 0)
        {
            if (state->escaped != 0) state->escaped = 0;
            else if (c == u'\\') state->escaped = 1;
            else if (c == u'"') state->quoted = 0;
            continue;
        }
        if (c == u'"') state->quoted = 1;
        else if (c == u'{')
        {
            state->started = 1;
            state->depth++;
        }
        else if (c == u'}' && state->started != 0 && --state->depth == 0)
        {
            *out_completed = 1;
            return VN_OK;
        }
    }
    return VN_OK;
}

    };
} // namespace vnotch::services

VN_EXPORT int32_t vn_json_boundary_append(VnJsonBoundaryState* state,
    const char16_t* chunk_ptr, int32_t chunk_len,
    uint8_t* out_completed) noexcept
{
    return vnotch::services::TranslationJsonBoundary::vn_json_boundary_append_impl(state, chunk_ptr, chunk_len, out_completed);
}

