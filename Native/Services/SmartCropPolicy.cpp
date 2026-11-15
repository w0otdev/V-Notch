#include "vnotch_ext_core.h"

#include <tlhelp32.h>
#include <devpropdef.h>
#include <cfgmgr32.h>
#include <cwchar>

#pragma comment(lib, "cfgmgr32.lib")

#include "Parsing/WindowsText.h"
using namespace vnotch::windows_text;

VN_EXPORT int32_t __stdcall vn_smartcrop_select_single_prominent_person(
    const VnCoreDetection* detections,
    int32_t count,
    VnCoreDetection* out_person,
    uint8_t* out_found) noexcept
{
    if (!out_person || !out_found || count < 0 || (count > 0 && !detections))
        return VN_ERR_INVALID_ARG;

    *out_person = {};
    *out_found = 0;
    if (count == 0)
        return VN_OK;

    bool has_person = false;
    float best_confidence = 0.0f;
    for (int32_t i = 0; i < count; ++i)
    {
        if (detections[i].class_id != 0)
            continue;
        const float c = detections[i].confidence;
        if (!has_person)
        {
            best_confidence = c;
            has_person = true;
            if (std::isnan(c))
                break;
        }
        else if (std::isnan(c))
        {
            best_confidence = c;
            break;
        }
        else if (c > best_confidence)
        {
            best_confidence = c;
        }
    }

    if (!has_person)
        return VN_OK;

    const float min_confidence = best_confidence * 0.5f;
    int32_t match_count = 0;
    VnCoreDetection chosen{};
    for (int32_t i = 0; i < count; ++i)
    {
        if (detections[i].class_id != 0)
            continue;
        const float c = detections[i].confidence;
        if (std::isnan(c) || std::isnan(min_confidence) || !(c >= min_confidence))
            continue;
        if (++match_count > 1)
            return VN_OK;
        chosen = detections[i];
    }

    if (match_count == 1)
    {
        *out_person = chosen;
        *out_found = 1;
    }
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_smartcrop_translate_crop_detections(
    const VnCoreDetection* src,
    int32_t src_count,
    int32_t crop_x,
    int32_t crop_y,
    int32_t crop_width,
    int32_t crop_height,
    VnCoreDetection* out_dst,
    int32_t dst_cap,
    int32_t* out_written) noexcept
{
    if (!out_written || src_count < 0 || dst_cap < 0 ||
        (src_count > 0 && !src) || (dst_cap > 0 && !out_dst))
    {
        return VN_ERR_INVALID_ARG;
    }

    *out_written = 0;
    if (src_count == 0 || crop_width <= 0 || crop_height <= 0)
        return VN_OK;

    const float fx = static_cast<float>(crop_x);
    const float fy = static_cast<float>(crop_y);
    const float fw = static_cast<float>(crop_width);
    const float fh = static_cast<float>(crop_height);

    int32_t written = 0;
    for (int32_t i = 0; i < src_count; ++i)
    {
        VnCoreDetection t = src[i];
        t.x1 = clamp_f32_preserve_nan(t.x1 - fx, 0.0f, fw);
        t.x2 = clamp_f32_preserve_nan(t.x2 - fx, 0.0f, fw);
        t.y1 = clamp_f32_preserve_nan(t.y1 - fy, 0.0f, fh);
        t.y2 = clamp_f32_preserve_nan(t.y2 - fy, 0.0f, fh);
        if (!std::isnan(t.x1) && !std::isnan(t.x2) && !std::isnan(t.y1) && !std::isnan(t.y2) &&
            t.x2 > t.x1 && t.y2 > t.y1)
        {
            if (written >= dst_cap)
            {
                *out_written = written;
                return VN_ERR_BUFFER_TOO_SMALL;
            }
            out_dst[written++] = t;
        }
    }

    *out_written = written;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_smartcrop_partition_hybrid_candidates(
    const VnCoreDetection* src,
    int32_t src_count,
    VnCoreDetection* out_candidates,
    int32_t cap,
    int32_t* out_person_count,
    int32_t* out_object_count,
    int32_t* out_kept_count,
    uint8_t* out_is_person_mode) noexcept
{
    if (!out_person_count || !out_object_count || !out_kept_count || !out_is_person_mode ||
        src_count < 0 || cap < 0 || (src_count > 0 && !src) || (cap > 0 && !out_candidates))
    {
        return VN_ERR_INVALID_ARG;
    }

    *out_person_count = 0;
    *out_object_count = 0;
    *out_kept_count = 0;
    *out_is_person_mode = 0;

    if (src_count == 0)
        return VN_OK;
    if (cap < src_count)
        return VN_ERR_BUFFER_TOO_SMALL;

    int32_t person_count = 0;
    for (int32_t i = 0; i < src_count; ++i)
    {
        if (src[i].class_id == 0)
            out_candidates[person_count++] = src[i];
    }
    const int32_t object_count = src_count - person_count;
    *out_person_count = person_count;
    *out_object_count = object_count;

    if (person_count >= 1)
    {
        float best_confidence = out_candidates[0].confidence;
        if (!std::isnan(best_confidence))
        {
            for (int32_t i = 1; i < person_count; ++i)
            {
                const float c = out_candidates[i].confidence;
                if (std::isnan(c))
                {
                    best_confidence = c;
                    break;
                }
                if (c > best_confidence)
                    best_confidence = c;
            }
        }

        const float min_confidence = best_confidence * 0.5f;
        int32_t kept = 0;
        for (int32_t i = 0; i < person_count; ++i)
        {
            const float c = out_candidates[i].confidence;
            // Exact IEEE-754 parity with C# `!(c < minConfidence)` (true when NaN)
            const bool less_than = !std::isnan(c) && !std::isnan(min_confidence) && (c < min_confidence);
            if (!less_than)
                out_candidates[kept++] = out_candidates[i];
        }

        *out_kept_count = kept;
        *out_is_person_mode = 1;
        return VN_OK;
    }

    if (object_count > 0)
    {
        int32_t written = 0;
        for (int32_t i = 0; i < src_count; ++i)
        {
            if (src[i].class_id != 0)
                out_candidates[written++] = src[i];
        }
        *out_kept_count = written;
    }

    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_smartcrop_select_portrait_face(
    const VnCoreRect* face_boxes,
    int32_t face_count,
    double scale,
    const VnCoreDetection* person,
    VnCoreDetection* out_best_face,
    uint8_t* out_found) noexcept
{
    if (!person || !out_best_face || !out_found || face_count < 0 || (face_count > 0 && !face_boxes))
        return VN_ERR_INVALID_ARG;

    *out_best_face = {};
    *out_found = 0;
    if (face_count == 0 || !(scale > 0.0) || !std::isfinite(scale))
        return VN_OK;

    VnCoreDetection best{};
    float best_area = 0.0f;
    bool has_best = false;

    for (int32_t i = 0; i < face_count; ++i)
    {
        const VnCoreRect& box = face_boxes[i];
        VnCoreDetection cand{};
        cand.x1 = static_cast<float>(static_cast<double>(box.x) / scale);
        cand.y1 = static_cast<float>(static_cast<double>(box.y) / scale);
        cand.x2 = static_cast<float>(static_cast<double>(static_cast<int64_t>(box.x) + box.width) / scale);
        cand.y2 = static_cast<float>(static_cast<double>(static_cast<int64_t>(box.y) + box.height) / scale);

        const float cx = (cand.x1 + cand.x2) / 2.0f;
        const float cy = (cand.y1 + cand.y2) / 2.0f;
        const float area = (cand.x2 - cand.x1) * (cand.y2 - cand.y1);

        if (std::isnan(cx) || std::isnan(cy) || std::isnan(area))
            continue;
        if (cx < person->x1 || cx > person->x2 || cy < person->y1 || cy > person->y2 || area <= best_area)
            continue;

        best = cand;
        best_area = area;
        has_best = true;
    }

    if (has_best)
    {
        *out_best_face = best;
        *out_found = 1;
    }
    return VN_OK;
}

