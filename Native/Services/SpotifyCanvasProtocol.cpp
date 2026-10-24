#include "vnotch_common.h"

using namespace vnotch;

namespace vnotch::services
{
    class SpotifyCanvasProtocol final
    {
    public:
        static constexpr int32_t varint_length(uint32_t value) noexcept
        {
            int32_t len = 1;
            while (value >= 0x80u)
            {
                ++len;
                value >>= 7;
            }
            return len;
        }
        
        static constexpr void write_varint(uint8_t* dst, int32_t& offset, uint32_t value) noexcept
        {
            while (value >= 0x80u)
            {
                dst[offset++] = static_cast<uint8_t>(value | 0x80u);
                value >>= 7;
            }
            dst[offset++] = static_cast<uint8_t>(value);
        }
        
        static constexpr bool try_read_varint(std::span<const uint8_t> data, size_t& offset, uint64_t& value) noexcept
        {
            value = 0;
            for (int32_t shift = 0; shift < 64 && offset < data.size(); shift += 7)
            {
                uint8_t current = data[offset++];
                value |= static_cast<uint64_t>(current & 0x7Fu) << shift;
                if ((current & 0x80u) == 0u)
                {
                    return true;
                }
            }
            return false;
        }
        
        static constexpr bool try_read_length_delimited(
            std::span<const uint8_t> data,
            size_t& offset,
            std::span<const uint8_t>& out_slice) noexcept
        {
            out_slice = {};
            uint64_t length = 0;
            if (!try_read_varint(data, offset, length) || length > 0x7FFFFFFFULL)
            {
                return false;
            }
            size_t len = static_cast<size_t>(length);
            if (offset > data.size() || len > data.size() - offset)
            {
                return false;
            }
            out_slice = data.subspan(offset, len);
            offset += len;
            return true;
        }
        
        static constexpr bool try_skip_field(std::span<const uint8_t> data, size_t& offset, int32_t wire_type) noexcept
        {
            uint64_t dummy = 0;
            std::span<const uint8_t> dummy_slice{};
            switch (wire_type)
            {
                case 0:
                    return try_read_varint(data, offset, dummy);
                case 1:
                    if (offset > data.size() || data.size() - offset < 8) return false;
                    offset += 8;
                    return true;
                case 2:
                    return try_read_length_delimited(data, offset, dummy_slice);
                case 5:
                    if (offset > data.size() || data.size() - offset < 4) return false;
                    offset += 4;
                    return true;
                default:
                    return false;
            }
        }

        static int32_t vn_spotify_canvas_build_request_impl(const uint8_t* track_id_utf8,
    int32_t track_id_len,
    uint8_t* out_buf,
    int32_t out_cap,
    int32_t* out_len) noexcept
        {
    if (out_len == nullptr || track_id_len < 0 || (track_id_len > 0 && track_id_utf8 == nullptr))
    {
        return VN_ERR_INVALID_ARG;
    }
    constexpr const char PREFIX[] = "spotify:track:";
    constexpr int32_t PREFIX_LEN = 14;

    int32_t uri_len = PREFIX_LEN + track_id_len;
    int32_t track_len = 1 + varint_length(static_cast<uint32_t>(uri_len)) + uri_len;
    int32_t total_len = 1 + varint_length(static_cast<uint32_t>(track_len)) + track_len;
    *out_len = total_len;
    if (out_buf == nullptr || out_cap < total_len)
    {
        return VN_ERR_BUFFER_TOO_SMALL;
    }

    int32_t offset = 0;
    out_buf[offset++] = 0x0Au;
    write_varint(out_buf, offset, static_cast<uint32_t>(track_len));
    out_buf[offset++] = 0x0Au;
    write_varint(out_buf, offset, static_cast<uint32_t>(uri_len));
    std::memcpy(out_buf + offset, PREFIX, PREFIX_LEN);
    offset += PREFIX_LEN;
    if (track_id_len > 0)
    {
        std::memcpy(out_buf + offset, track_id_utf8, static_cast<size_t>(track_id_len));
    }
    return VN_OK;
}

        static int32_t vn_spotify_canvas_extract_next_candidate_impl(const uint8_t* protobuf_ptr,
    int32_t protobuf_len,
    int32_t* io_outer_offset,
    int32_t* out_url_offset,
    int32_t* out_url_len,
    uint8_t* out_found) noexcept
        {
    if (io_outer_offset == nullptr || out_url_offset == nullptr || out_url_len == nullptr || out_found == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_found = 0u;
    *out_url_offset = 0;
    *out_url_len = 0;
    if (protobuf_ptr == nullptr || protobuf_len <= 0 || *io_outer_offset < 0)
    {
        return VN_OK;
    }

    std::span<const uint8_t> data(protobuf_ptr, static_cast<size_t>(protobuf_len));
    size_t offset = static_cast<size_t>(*io_outer_offset);
    while (offset < data.size())
    {
        uint64_t tag = 0;
        if (!try_read_varint(data, offset, tag))
        {
            *io_outer_offset = protobuf_len;
            return VN_ERR_FAIL;
        }
        int32_t field_number = static_cast<int32_t>(tag >> 3);
        int32_t wire_type = static_cast<int32_t>(tag & 0x07u);
        if (field_number == 1 && wire_type == 2)
        {
            std::span<const uint8_t> canvas{};
            if (!try_read_length_delimited(data, offset, canvas))
            {
                *io_outer_offset = protobuf_len;
                return VN_ERR_FAIL;
            }
            size_t inner = 0;
            while (inner < canvas.size())
            {
                uint64_t ctag = 0;
                if (!try_read_varint(canvas, inner, ctag))
                {
                    *io_outer_offset = protobuf_len;
                    return VN_ERR_FAIL;
                }
                int32_t cfield = static_cast<int32_t>(ctag >> 3);
                int32_t cwire = static_cast<int32_t>(ctag & 0x07u);
                if (cfield == 2 && cwire == 2)
                {
                    std::span<const uint8_t> val{};
                    if (!try_read_length_delimited(canvas, inner, val))
                    {
                        *io_outer_offset = protobuf_len;
                        return VN_ERR_FAIL;
                    }
                    *out_url_offset = static_cast<int32_t>(val.data() - protobuf_ptr);
                    *out_url_len = static_cast<int32_t>(val.size());
                    *out_found = 1u;
                    *io_outer_offset = static_cast<int32_t>(offset);
                    return VN_OK;
                }
                else if (!try_skip_field(canvas, inner, cwire))
                {
                    *io_outer_offset = protobuf_len;
                    return VN_ERR_FAIL;
                }
            }
        }
        else if (!try_skip_field(data, offset, wire_type))
        {
            *io_outer_offset = protobuf_len;
            return VN_ERR_FAIL;
        }
    }
    *io_outer_offset = static_cast<int32_t>(offset);
    return VN_OK;
}

    };
} // namespace vnotch::services

VN_EXPORT int32_t vn_spotify_canvas_build_request(const uint8_t* track_id_utf8,
    int32_t track_id_len,
    uint8_t* out_buf,
    int32_t out_cap,
    int32_t* out_len) noexcept
{
    return vnotch::services::SpotifyCanvasProtocol::vn_spotify_canvas_build_request_impl(track_id_utf8, track_id_len, out_buf, out_cap, out_len);
}

VN_EXPORT int32_t vn_spotify_canvas_extract_next_candidate(const uint8_t* protobuf_ptr,
    int32_t protobuf_len,
    int32_t* io_outer_offset,
    int32_t* out_url_offset,
    int32_t* out_url_len,
    uint8_t* out_found) noexcept
{
    return vnotch::services::SpotifyCanvasProtocol::vn_spotify_canvas_extract_next_candidate_impl(protobuf_ptr, protobuf_len, io_outer_offset, out_url_offset, out_url_len, out_found);
}

