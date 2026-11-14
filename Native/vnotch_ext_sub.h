#pragma once

#include "vnotch_native.h"

#pragma pack(push, 1)

struct VnAiParsedWindow
{
    double used_percent;
    int64_t reset_unix_seconds;
    int32_t duration_minutes;
    uint8_t has_duration;
    uint8_t has_reset;
    uint8_t label_kind;
    uint8_t has_bucket;
    uint8_t bucket_len;
    uint8_t reserved[3];
    uint8_t bucket_ascii[64];
};
static_assert(sizeof(VnAiParsedWindow) == 92);

struct VnSubTextSlice
{
    int32_t offset;
    int32_t length;
};
static_assert(sizeof(VnSubTextSlice) == 8);

struct VnTextFactSpan
{
    int32_t offset;
    int32_t length;
    int32_t trimmed_length;
    uint8_t is_numeric;
    uint8_t is_idiomatic_one;
    uint8_t is_entity;
    uint8_t reserved;
};
static_assert(sizeof(VnTextFactSpan) == 16);

#pragma pack(pop)

// AiUsage
VN_EXPORT int32_t __stdcall vn_ai_usage_is_valid_bucket_id(
    const wchar_t* id_ptr, int32_t id_len, uint8_t* out_valid) noexcept;

VN_EXPORT int32_t __stdcall vn_ai_usage_parse_codex(
    const wchar_t* json_ptr, int32_t json_len,
    VnAiParsedWindow* out_windows, int32_t window_cap, int32_t* out_count) noexcept;

VN_EXPORT int32_t __stdcall vn_ai_usage_parse_claude(
    const wchar_t* json_ptr, int32_t json_len,
    VnAiParsedWindow* out_windows, int32_t window_cap, int32_t* out_count) noexcept;

VN_EXPORT int32_t __stdcall vn_claude_bridge_build_command(
    const wchar_t* script_ptr, int32_t script_len,
    wchar_t* out_buf, int32_t out_cap, int32_t* out_len) noexcept;

// Translation
VN_EXPORT int32_t __stdcall vn_translation_integrity_extract_facts(
    const wchar_t* text_ptr, int32_t text_len,
    VnTextFactSpan* out_spans, int32_t span_cap, int32_t* out_count) noexcept;

VN_EXPORT int32_t __stdcall vn_translation_integrity_inspect(
    const wchar_t* source_ptr, int32_t source_len,
    const wchar_t* translated_ptr, int32_t translated_len,
    uint8_t* out_literals_preserved, uint8_t* out_protected_preserved) noexcept;

VN_EXPORT int32_t __stdcall vn_qwen_translation_try_read_output(
    const wchar_t* json_ptr, int32_t json_len,
    wchar_t* out_buf, int32_t out_cap, int32_t* out_len, uint8_t* out_ok) noexcept;

VN_EXPORT int32_t __stdcall vn_translation_lang_split_passthrough_sentences(
    const wchar_t* text_ptr, int32_t text_len,
    VnSubTextSlice* out_slices, int32_t slice_cap, int32_t* out_count) noexcept;

VN_EXPORT int32_t __stdcall vn_translation_structured_find_shortcodes(
    const wchar_t* text_ptr, int32_t text_len,
    VnSubTextSlice* out_slices, int32_t slice_cap, int32_t* out_count) noexcept;

VN_EXPORT int32_t __stdcall vn_translation_structured_has_letter_outside_shortcodes(
    const wchar_t* text_ptr, int32_t text_len, uint8_t* out_has_letter) noexcept;

VN_EXPORT int32_t __stdcall vn_selected_price_has_conflicting_currency(
    const wchar_t* text_ptr, int32_t text_len,
    const wchar_t* source_ptr, int32_t source_len,
    const wchar_t* local_dollar_ptr, int32_t local_dollar_len,
    uint8_t* out_conflicting) noexcept;

VN_EXPORT int32_t __stdcall vn_selected_price_try_parse(
    const wchar_t* text_ptr, int32_t text_len,
    const wchar_t* source_ptr, int32_t source_len,
    const wchar_t* local_dollar_ptr, int32_t local_dollar_len,
    const wchar_t* override_ptr, int32_t override_len,
    wchar_t* out_amount_buf, int32_t amount_cap, int32_t* out_amount_len,
    wchar_t* out_currency_buf, int32_t currency_cap, int32_t* out_currency_len,
    uint8_t* out_assumed_currency, uint8_t* out_ok) noexcept;

// Spotlight
VN_EXPORT int32_t __stdcall vn_spotlight_ai_extract_response_text(
    const wchar_t* json_ptr, int32_t json_len, int32_t provider_kind,
    wchar_t* out_buf, int32_t out_cap, int32_t* out_len, uint8_t* out_ok) noexcept;

// Clipboard
VN_EXPORT int32_t __stdcall vn_clipboard_classify_text_traits(
    const wchar_t* text_ptr, int32_t text_len,
    uint8_t* out_has_email, uint8_t* out_looks_like_code) noexcept;

VN_EXPORT int32_t __stdcall vn_clipboard_is_public_ip_bytes(
    const uint8_t* addr_ptr, int32_t addr_len, uint8_t* out_is_public) noexcept;

VN_EXPORT int32_t __stdcall vn_clipboard_drop_is_blocked_device_path(
    const wchar_t* path_ptr, int32_t path_len, uint8_t* out_blocked) noexcept;

VN_EXPORT int32_t __stdcall vn_clipboard_drop_extract_html_img_src(
    const wchar_t* html_ptr, int32_t html_len,
    int32_t* out_offset, int32_t* out_length, uint8_t* out_found) noexcept;

VN_EXPORT int32_t __stdcall vn_clipboard_drop_extract_source_url(
    const wchar_t* html_ptr, int32_t html_len,
    int32_t* out_offset, int32_t* out_length, uint8_t* out_found) noexcept;

VN_EXPORT int32_t __stdcall vn_clipboard_is_secret_filename(
    const wchar_t* path_ptr, int32_t path_len, uint8_t* out_is_secret) noexcept;

VN_EXPORT int32_t __stdcall vn_clipboard_blob_build_nonce_aad(
    const uint8_t* stream_id_ptr, int32_t stream_id_len,
    uint64_t chunk_index, int32_t plain_len, uint8_t is_final,
    uint8_t* out_nonce, int32_t nonce_cap,
    uint8_t* out_aad, int32_t aad_cap) noexcept;

VN_EXPORT int32_t __stdcall vn_clipboard_blob_compute_geometry(
    int64_t file_len,
    int64_t* out_total_chunks,
    int32_t* out_last_plain_len,
    int64_t* out_plain_length,
    int32_t* out_error_kind) noexcept;
