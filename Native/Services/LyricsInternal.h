#pragma once

#include "../vnotch_common.h"

namespace vnotch::services::lyrics_internal {

int32_t TryParseYrcLine(
    const char16_t* raw_line,
    int32_t raw_line_len,
    int64_t* out_line_ticks,
    char16_t* out_text_buf,
    int32_t out_text_cap,
    int32_t* out_text_len,
    VnLyricWord* out_words,
    int32_t out_words_cap,
    int32_t* out_words_count,
    uint8_t* out_ok) noexcept;

int32_t TryParseKrcLine(
    const char16_t* raw_line,
    int32_t raw_line_len,
    int64_t* out_line_ticks,
    char16_t* out_text_buf,
    int32_t out_text_cap,
    int32_t* out_text_len,
    VnLyricWord* out_words,
    int32_t out_words_cap,
    int32_t* out_words_count,
    uint8_t* out_ok) noexcept;

int32_t ScoreNetEaseCandidate(
    const char16_t* target_title_ptr,
    int32_t target_title_len,
    const char16_t* target_artist_ptr,
    int32_t target_artist_len,
    int32_t target_duration_sec,
    const char16_t* cand_title_ptr,
    int32_t cand_title_len,
    const char16_t* cand_artists_ptr,
    int32_t cand_artists_len,
    int32_t cand_duration_ms,
    int32_t* out_score,
    uint8_t* out_accepted) noexcept;

} // namespace vnotch::services::lyrics_internal
