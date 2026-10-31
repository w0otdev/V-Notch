#include "vnotch_common.h"
#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <span>
#include <vector>

namespace vnotch::services {

#pragma pack(push, 1)
struct VnDetection {
    float x1;
    float y1;
    float x2;
    float y2;
    float confidence;
    int32_t class_id;
};

struct VnSubjectBounds {
    float center_x;
    float center_y;
    float width;
    float height;
    float confidence;
    int32_t class_id;
};

struct VnCropRect {
    int32_t x;
    int32_t y;
    int32_t width;
    int32_t height;
};
#pragma pack(pop)

class SmartThumbnailCropService final {
public:
    static constexpr int32_t kModelInputSize = 416;
    static constexpr float kConfidenceThreshold = 0.35f;
    static constexpr float kPersonConfidenceThreshold = 0.10f;
    static constexpr float kNmsThreshold = 0.50f;
    static constexpr float kMinAreaRatio = 0.02f;
    static constexpr float kMinPersonAreaRatio = 0.005f;
    static constexpr float kSubjectMarginRatio = 0.15f;
    static constexpr float kMinCropRatio = 0.45f;

    static constexpr float GetClassPriority(int32_t class_id) noexcept {
        if (class_id == 0) return 5.0f;
        if (class_id >= 14 && class_id <= 23) return 2.5f;
        if (class_id == 39 || (class_id >= 41 && class_id <= 79)) return 2.0f;
        return 1.0f;
    }

    static constexpr bool IsValidPixelArgs(
        const uint8_t* pixels, int32_t w, int32_t h, int32_t stride, int32_t min_dim) noexcept
    {
        if (!pixels || w < min_dim || h < min_dim) return false;
        if (w > INT32_MAX / 4) return false;
        if (stride < w * 4) return false;
        if (static_cast<int64_t>(stride) * static_cast<int64_t>(h) > INT32_MAX) return false;
        return true;
    }

    static void FillTensorFromBgra32(
        const uint8_t* pixels, int32_t scaled_w, int32_t scaled_h, int32_t stride,
        float* tensor_buffer) noexcept
    {
        if (!tensor_buffer || !IsValidPixelArgs(pixels, scaled_w, scaled_h, stride, 1)) return;
        constexpr int32_t plane_size = kModelInputSize * kModelInputSize;
        std::fill_n(tensor_buffer, 3 * plane_size, 114.0f);

        int32_t max_y = std::min(scaled_h, kModelInputSize);
        int32_t max_x = std::min(scaled_w, kModelInputSize);
        for (int32_t y = 0; y < max_y; y++) {
            int32_t row_offset = y * stride;
            int32_t tensor_row_base = y * kModelInputSize;
            for (int32_t x = 0; x < max_x; x++) {
                int32_t pixel_idx = row_offset + x * 4;
                int32_t tensor_idx = tensor_row_base + x;

                tensor_buffer[tensor_idx] = static_cast<float>(pixels[pixel_idx]);
                tensor_buffer[plane_size + tensor_idx] = static_cast<float>(pixels[pixel_idx + 1]);
                tensor_buffer[2 * plane_size + tensor_idx] = static_cast<float>(pixels[pixel_idx + 2]);
            }
        }
    }

    static std::vector<VnDetection> ApplyNms(std::span<const VnDetection> detections) {
        std::vector<VnDetection> sorted(detections.begin(), detections.end());
        std::stable_sort(sorted.begin(), sorted.end(), [](const VnDetection& a, const VnDetection& b) {
            if (!std::isfinite(a.confidence)) return false;
            if (!std::isfinite(b.confidence)) return true;
            return a.confidence > b.confidence;
        });

        std::vector<VnDetection> result;
        result.reserve(sorted.size());
        std::vector<uint8_t> suppressed(sorted.size(), 0);

        for (size_t i = 0; i < sorted.size(); i++) {
            if (suppressed[i]) continue;
            result.push_back(sorted[i]);
            for (size_t j = i + 1; j < sorted.size(); j++) {
                if (suppressed[j]) continue;
                if (sorted[i].class_id == sorted[j].class_id && IoU(sorted[i], sorted[j]) > kNmsThreshold) {
                    suppressed[j] = 1;
                }
            }
        }
        return result;
    }

    static std::vector<VnDetection> ParseYoloxAndNms(
        const float* buffer, int32_t img_width, int32_t img_height, float scale,
        int32_t* out_raw_count)
    {
        if (out_raw_count) *out_raw_count = 0;
        if (!buffer || img_width <= 0 || img_height <= 0 || !std::isfinite(scale) || scale <= 0.0f) {
            return {};
        }

        std::vector<VnDetection> detections;
        detections.reserve(32);

        int32_t prediction = 0;
        constexpr int32_t strides[3] = {8, 16, 32};
        for (int32_t stride : strides) {
            int32_t grid_size = kModelInputSize / stride;
            for (int32_t y = 0; y < grid_size; y++) {
                for (int32_t x = 0; x < grid_size; x++, prediction++) {
                    VnDetection det{};
                    if (TryExtractPrediction(buffer, img_width, img_height, scale, x, y, stride, prediction, &det)) {
                        detections.push_back(det);
                    }
                }
            }
        }

        if (out_raw_count) *out_raw_count = static_cast<int32_t>(detections.size());
        return detections.size() > 1 ? ApplyNms(detections) : detections;
    }

    static bool FindBestSubjectBounds(
        std::span<const VnDetection> detections, int32_t img_width, int32_t img_height,
        VnSubjectBounds* out_bounds) noexcept
    {
        if (!out_bounds || detections.empty() || img_width <= 0 || img_height <= 0) return false;

        float img_area = static_cast<float>(img_width) * static_cast<float>(img_height);
        if (!std::isfinite(img_area) || img_area <= 0.0f) return false;
        const VnDetection* best = nullptr;
        float best_score = -3.402823466e+38F;

        for (const auto& d : detections) {
            if (!std::isfinite(d.x1) || !std::isfinite(d.y1) ||
                !std::isfinite(d.x2) || !std::isfinite(d.y2) ||
                !std::isfinite(d.confidence)) {
                continue;
            }
            float w_box = d.x2 - d.x1;
            float h_box = d.y2 - d.y1;
            if (w_box <= 0.0f || h_box <= 0.0f) continue;
            float area = w_box * h_box;
            if (!std::isfinite(area) || area <= 0.0f) continue;
            float area_share = area / img_area;
            float score = d.confidence * GetClassPriority(d.class_id) * std::sqrt(area_share) * GetCenterWeight(d, img_width, img_height);
            if (std::isfinite(score) && score > best_score) {
                best_score = score;
                best = &d;
            }
        }

        if (!best) return false;

        float cx = (best->x1 + best->x2) / 2.0f / img_width;
        float cy = (best->y1 + best->y2) / 2.0f / img_height;
        float w = (best->x2 - best->x1) / img_width;
        float h = (best->y2 - best->y1) / img_height;
        if (best->class_id == 0) {
            cy = (best->y1 + (best->y2 - best->y1) * 0.12f) / img_height;
        }

        out_bounds->center_x = cx;
        out_bounds->center_y = cy;
        out_bounds->width = w;
        out_bounds->height = h;
        out_bounds->confidence = best->confidence;
        out_bounds->class_id = best->class_id;
        return true;
    }

    static VnCropRect GetPortraitCropRect(const VnDetection& face, int32_t width, int32_t height) noexcept {
        float face_height = (std::isfinite(face.y1) && std::isfinite(face.y2)) ? std::max(0.0f, face.y2 - face.y1) : 0.0f;
        float face_width = (std::isfinite(face.x1) && std::isfinite(face.x2)) ? std::max(0.0f, face.x2 - face.x1) : 0.0f;
        float min_dim = static_cast<float>(std::max(1, std::min(width, height)));
        float raw_desired = std::max(face_height * 2.15f, face_width * 3.0f);
        if (!std::isfinite(raw_desired)) raw_desired = min_dim;
        int32_t side = static_cast<int32_t>(std::ceil(std::clamp(
            raw_desired,
            min_dim * kMinCropRatio,
            min_dim)));
        float top = (std::isfinite(face.y1) ? face.y1 : 0.0f) - face_height * 0.45f;
        float cx = (std::isfinite(face.x1) && std::isfinite(face.x2)) ? (face.x1 + face.x2) / 2.0f : (width / 2.0f);
        return BuildCropRect(cx, top + side / 2.0f, width, height, side);
    }

    static VnCropRect GetSinglePersonCropRect(const VnDetection& p, int32_t img_width, int32_t img_height, int32_t target_size) noexcept {
        float person_width = (std::isfinite(p.x1) && std::isfinite(p.x2)) ? std::max(0.0f, p.x2 - p.x1) : 0.0f;
        float person_height = (std::isfinite(p.y1) && std::isfinite(p.y2)) ? std::max(0.0f, p.y2 - p.y1) : 0.0f;
        float person_center_x = (p.x1 + p.x2) / 2.0f;
        float person_center_y = (p.y1 + p.y2) / 2.0f;

        int32_t crop_size = ComputeAdaptiveCropSize(person_width, person_height, img_width, img_height, target_size);
        if (person_height > crop_size && std::isfinite(p.y1)) {
            person_center_y = p.y1 - crop_size * 0.08f + crop_size / 2.0f;
        }
        return BuildCropRect(person_center_x, person_center_y, img_width, img_height, crop_size);
    }

    static VnCropRect GetGroupPersonCropRect(std::span<const VnDetection> persons, int32_t img_width, int32_t img_height, int32_t target_size) noexcept {
        float min_x = 3.402823466e+38F, min_y = 3.402823466e+38F;
        float max_x = -3.402823466e+38F, max_y = -3.402823466e+38F;
        bool any_valid = false;

        for (const auto& p : persons) {
            if (!std::isfinite(p.x1) || !std::isfinite(p.y1) || !std::isfinite(p.x2) || !std::isfinite(p.y2)) {
                continue;
            }
            any_valid = true;
            if (p.x1 < min_x) min_x = p.x1;
            if (p.y1 < min_y) min_y = p.y1;
            if (p.x2 > max_x) max_x = p.x2;
            if (p.y2 > max_y) max_y = p.y2;
        }

        if (!any_valid) {
            int32_t fallback_size = ComputeAdaptiveCropSize(0.0f, 0.0f, img_width, img_height, target_size);
            return BuildCropRect(img_width / 2.0f, img_height / 2.0f, img_width, img_height, fallback_size);
        }

        float group_w = std::max(0.0f, max_x - min_x);
        float group_h = std::max(0.0f, max_y - min_y);
        float group_center_x = (min_x + max_x) / 2.0f;
        float group_center_y = (min_y + max_y) / 2.0f;

        int32_t crop_size = ComputeAdaptiveCropSize(group_w, group_h, img_width, img_height, target_size);
        return BuildCropRect(group_center_x, group_center_y, img_width, img_height, crop_size);
    }

    static VnCropRect GetBestObjectCropRect(std::span<const VnDetection> objects, int32_t img_width, int32_t img_height, int32_t target_size) noexcept {
        if (objects.empty()) {
            int32_t fallback_size = ComputeAdaptiveCropSize(0.0f, 0.0f, img_width, img_height, target_size);
            return BuildCropRect(img_width / 2.0f, img_height / 2.0f, img_width, img_height, fallback_size);
        }
        const VnDetection* best_obj = &objects[0];
        float best_score = -3.402823466e+38F;

        for (const auto& d : objects) {
            if (!std::isfinite(d.x1) || !std::isfinite(d.y1) ||
                !std::isfinite(d.x2) || !std::isfinite(d.y2) ||
                !std::isfinite(d.confidence)) {
                continue;
            }
            float score = d.confidence * GetCenterWeight(d, img_width, img_height) * GetClassPriority(d.class_id);
            if (std::isfinite(score) && score > best_score) {
                best_score = score;
                best_obj = &d;
            }
        }

        float obj_center_x = (best_obj->x1 + best_obj->x2) / 2.0f;
        float obj_center_y = (best_obj->y1 + best_obj->y2) / 2.0f;
        float obj_width = (std::isfinite(best_obj->x1) && std::isfinite(best_obj->x2)) ? std::max(0.0f, best_obj->x2 - best_obj->x1) : 0.0f;
        float obj_height = (std::isfinite(best_obj->y1) && std::isfinite(best_obj->y2)) ? std::max(0.0f, best_obj->y2 - best_obj->y1) : 0.0f;

        int32_t obj_crop = ComputeAdaptiveCropSize(obj_width, obj_height, img_width, img_height, target_size);
        return BuildCropRect(obj_center_x, obj_center_y, img_width, img_height, obj_crop);
    }

    static VnCropRect ComputeSaliencyCropRect(
        const uint8_t* pixels, int32_t w, int32_t h, int32_t stride,
        int32_t img_width, int32_t img_height, int32_t target_size) noexcept
    {
        int32_t max_crop_size = std::min(img_width, img_height);
        int32_t crop_size = std::min(target_size, max_crop_size);
        if (!IsValidPixelArgs(pixels, w, h, stride, 8)) {
            return BuildCropRect(img_width / 2.0f, img_height / 2.0f, img_width, img_height, crop_size);
        }

        constexpr int32_t grid_size = 8;
        int32_t cell_w = w / grid_size;
        int32_t cell_h = h / grid_size;
        std::array<std::array<float, grid_size>, grid_size> saliency_map{};

        for (int32_t gy = 0; gy < grid_size; gy++) {
            for (int32_t gx = 0; gx < grid_size; gx++) {
                int32_t sx = gx * cell_w;
                int32_t sy = gy * cell_h;
                int32_t ex = std::min(sx + cell_w, w - 1);
                int32_t ey = std::min(sy + cell_h, h - 1);
                saliency_map[gy][gx] = ComputeCellSaliency(pixels, stride, w, h, sx, sy, ex, ey, gx, gy, grid_size);
            }
        }

        float best_score = -1.0f;
        int32_t best_gx = grid_size / 2, best_gy = grid_size / 2;
        for (int32_t gy = 0; gy < grid_size - 1; gy++) {
            for (int32_t gx = 0; gx < grid_size - 1; gx++) {
                float block_score = saliency_map[gy][gx] + saliency_map[gy][gx + 1]
                                  + saliency_map[gy + 1][gx] + saliency_map[gy + 1][gx + 1];
                if (block_score > best_score) {
                    best_score = block_score;
                    best_gx = gx;
                    best_gy = gy;
                }
            }
        }

        float saliency_center_x = (best_gx + 1.0f) / grid_size * img_width;
        float saliency_center_y = (best_gy + 1.0f) / grid_size * img_height;
        return BuildCropRect(saliency_center_x, saliency_center_y, img_width, img_height, crop_size);
    }

    static bool TryDetectTextCropRect(
        const uint8_t* pixels, int32_t w, int32_t h, int32_t stride,
        int32_t img_width, int32_t img_height, int32_t crop_size,
        VnCropRect* out_rect) noexcept
    {
        if (!out_rect || img_width <= 0 || img_height <= 0 || !IsValidPixelArgs(pixels, w, h, stride, 8)) {
            return false;
        }
        constexpr int32_t grid_size = 8;
        int32_t cell_w = img_width / grid_size;
        int32_t cell_h = img_height / grid_size;
        int32_t a_cell_w = w / grid_size;
        int32_t a_cell_h = h / grid_size;

        int32_t region_count = 0;
        float min_x = 3.402823466e+38F, max_x = -3.402823466e+38F;
        float min_y = 3.402823466e+38F, max_y = -3.402823466e+38F;

        for (int32_t gy = 0; gy < grid_size; gy++) {
            for (int32_t gx = 0; gx < grid_size; gx++) {
                int32_t sx = gx * a_cell_w;
                int32_t sy = gy * a_cell_h;
                int32_t ex = std::min(sx + a_cell_w, w - 1);
                int32_t ey = std::min(sy + a_cell_h, h - 1);

                double edge_ratio = CalculateCellEdgeRatio(pixels, stride, w, h, sx, sy, ex, ey);
                if (edge_ratio > 0.30) {
                    region_count++;
                    float x1 = static_cast<float>(gx * cell_w);
                    float y1 = static_cast<float>(gy * cell_h);
                    float x2 = static_cast<float>((gx + 1) * cell_w);
                    float y2 = static_cast<float>((gy + 1) * cell_h);
                    if (x1 < min_x) min_x = x1;
                    if (x2 > max_x) max_x = x2;
                    if (y1 < min_y) min_y = y1;
                    if (y2 > max_y) max_y = y2;
                }
            }
        }

        if (region_count < 2) return false;
        *out_rect = BuildCropRect((min_x + max_x) / 2.0f, (min_y + max_y) / 2.0f, img_width, img_height, crop_size);
        return true;
    }

    static int32_t DetectTextRegions(
        const uint8_t* pixels, int32_t w, int32_t h, int32_t stride,
        int32_t img_width, int32_t img_height,
        VnCropRect* out_regions, int32_t out_cap) noexcept
    {
        if (img_width <= 0 || img_height <= 0 || !IsValidPixelArgs(pixels, w, h, stride, 8)) {
            return 0;
        }
        constexpr int32_t grid_size = 8;
        int32_t cell_w = img_width / grid_size;
        int32_t cell_h = img_height / grid_size;
        int32_t a_cell_w = w / grid_size;
        int32_t a_cell_h = h / grid_size;

        int32_t count = 0;
        for (int32_t gy = 0; gy < grid_size; gy++) {
            for (int32_t gx = 0; gx < grid_size; gx++) {
                int32_t sx = gx * a_cell_w;
                int32_t sy = gy * a_cell_h;
                int32_t ex = std::min(sx + a_cell_w, w - 1);
                int32_t ey = std::min(sy + a_cell_h, h - 1);

                double edge_ratio = CalculateCellEdgeRatio(pixels, stride, w, h, sx, sy, ex, ey);
                if (edge_ratio > 0.30) {
                    if (out_regions && count < out_cap) {
                        out_regions[count] = VnCropRect{gx * cell_w, gy * cell_h, cell_w, cell_h};
                    }
                    count++;
                }
            }
        }
        return count;
    }

    static VnCropRect BuildCropRect(float center_x, float center_y, int32_t img_width, int32_t img_height, int32_t crop_size) noexcept {
        img_width = std::max(1, img_width);
        img_height = std::max(1, img_height);
        const int32_t max_side = std::min(img_width, img_height);
        crop_size = std::clamp(crop_size, 1, max_side);
        if (!std::isfinite(center_x)) center_x = img_width / 2.0f;
        if (!std::isfinite(center_y)) center_y = img_height / 2.0f;
        float raw_x = std::clamp(center_x - crop_size / 2.0f, 0.0f, static_cast<float>(img_width - crop_size));
        float raw_y = std::clamp(center_y - crop_size / 2.0f, 0.0f, static_cast<float>(img_height - crop_size));
        int32_t crop_x = static_cast<int32_t>(raw_x);
        int32_t crop_y = static_cast<int32_t>(raw_y);
        crop_x = std::max(0, std::min(crop_x, img_width - crop_size));
        crop_y = std::max(0, std::min(crop_y, img_height - crop_size));
        return VnCropRect{crop_x, crop_y, crop_size, crop_size};
    }

private:
    static bool TryExtractPrediction(
        const float* buffer, int32_t img_width, int32_t img_height, float scale,
        int32_t grid_x, int32_t grid_y, int32_t stride, int32_t i,
        VnDetection* out_det) noexcept
    {
        if (!buffer || !out_det || img_width <= 0 || img_height <= 0 ||
            !std::isfinite(scale) || scale <= 0.0f) {
            return false;
        }
        constexpr int32_t channels = 85;
        int32_t offset = i * channels;
        float raw_cx = buffer[offset];
        float raw_cy = buffer[offset + 1];
        float raw_w = buffer[offset + 2];
        float raw_h = buffer[offset + 3];
        float objectness = buffer[offset + 4];
        if (!std::isfinite(raw_cx) || !std::isfinite(raw_cy) ||
            !std::isfinite(raw_w) || !std::isfinite(raw_h) ||
            !std::isfinite(objectness) ||
            objectness < kPersonConfidenceThreshold || objectness > 1.0f) {
            return false;
        }

        float max_score = 0.0f;
        int32_t max_class_id = -1;
        for (int32_t c = 5; c < channels; c++) {
            float score = buffer[offset + c];
            if (std::isfinite(score) && score > max_score && score <= 1.0f) {
                max_score = score;
                max_class_id = c - 5;
            }
        }

        if (max_class_id < 0) return false;

        bool is_person = (max_class_id == 0);
        max_score *= objectness;
        float threshold = is_person ? kPersonConfidenceThreshold : kConfidenceThreshold;
        if (!std::isfinite(max_score) || max_score < threshold) return false;

        float cx = (raw_cx + grid_x) * stride;
        float cy = (raw_cy + grid_y) * stride;
        float w = std::exp(raw_w) * stride;
        float h = std::exp(raw_h) * stride;
        if (!std::isfinite(cx) || !std::isfinite(cy) || !std::isfinite(w) || !std::isfinite(h)) {
            return false;
        }

        float max_w = static_cast<float>(img_width);
        float max_h = static_cast<float>(img_height);
        float raw_x1 = (cx - w / 2.0f) / scale;
        float raw_y1 = (cy - h / 2.0f) / scale;
        float raw_x2 = (cx + w / 2.0f) / scale;
        float raw_y2 = (cy + h / 2.0f) / scale;
        if (!std::isfinite(raw_x1) || !std::isfinite(raw_y1) ||
            !std::isfinite(raw_x2) || !std::isfinite(raw_y2)) {
            return false;
        }

        float x1 = std::clamp(raw_x1, 0.0f, max_w);
        float y1 = std::clamp(raw_y1, 0.0f, max_h);
        float x2 = std::clamp(raw_x2, 0.0f, max_w);
        float y2 = std::clamp(raw_y2, 0.0f, max_h);

        float bbox_area = (x2 - x1) * (y2 - y1);
        float img_area = max_w * max_h;
        if (!std::isfinite(bbox_area) || !std::isfinite(img_area) || img_area <= 0.0f) {
            return false;
        }
        float area_ratio = bbox_area / img_area;
        float min_ratio = is_person ? kMinPersonAreaRatio : kMinAreaRatio;

        if (!std::isfinite(area_ratio) || area_ratio < min_ratio || x2 - x1 < 5.0f || y2 - y1 < 5.0f) return false;

        out_det->x1 = x1;
        out_det->y1 = y1;
        out_det->x2 = x2;
        out_det->y2 = y2;
        out_det->confidence = max_score;
        out_det->class_id = max_class_id;
        return true;
    }

    static float IoU(const VnDetection& a, const VnDetection& b) noexcept {
        if (!std::isfinite(a.x1) || !std::isfinite(a.y1) || !std::isfinite(a.x2) || !std::isfinite(a.y2) ||
            !std::isfinite(b.x1) || !std::isfinite(b.y1) || !std::isfinite(b.x2) || !std::isfinite(b.y2)) {
            return 0.0f;
        }
        float inter_x1 = std::max(a.x1, b.x1);
        float inter_y1 = std::max(a.y1, b.y1);
        float inter_x2 = std::min(a.x2, b.x2);
        float inter_y2 = std::min(a.y2, b.y2);

        float inter_area = std::max(0.0f, inter_x2 - inter_x1) * std::max(0.0f, inter_y2 - inter_y1);
        float area_a = std::max(0.0f, a.x2 - a.x1) * std::max(0.0f, a.y2 - a.y1);
        float area_b = std::max(0.0f, b.x2 - b.x1) * std::max(0.0f, b.y2 - b.y1);
        float denom = area_a + area_b - inter_area + 1e-6f;

        return (denom > 0.0f && std::isfinite(denom)) ? (inter_area / denom) : 0.0f;
    }

    static float GetCenterWeight(const VnDetection& d, int32_t img_width, int32_t img_height) noexcept {
        if (img_width <= 0 || img_height <= 0) return 0.0f;
        float obj_center_x = (d.x1 + d.x2) / 2.0f;
        float obj_center_y = (d.y1 + d.y2) / 2.0f;
        float img_center_x = img_width / 2.0f;
        float img_center_y = img_height / 2.0f;

        float dx = (obj_center_x - img_center_x) / img_center_x;
        float dy = (obj_center_y - img_center_y) / img_center_y;
        float dist = std::sqrt(dx * dx + dy * dy) / std::sqrt(2.0f);

        return std::isfinite(dist) ? (1.0f - dist * 0.5f) : 0.0f;
    }

    static int32_t ComputeAdaptiveCropSize(float subject_w, float subject_h, int32_t img_width, int32_t img_height, int32_t target_size) noexcept {
        int32_t max_crop = std::max(1, std::min(img_width, img_height));
        float safe_w = std::isfinite(subject_w) ? std::max(0.0f, subject_w) : 0.0f;
        float safe_h = std::isfinite(subject_h) ? std::max(0.0f, subject_h) : 0.0f;
        float subject_extent = std::max(safe_w, safe_h);
        float desired = subject_extent * (1.0f + 2.0f * kSubjectMarginRatio);
        if (!std::isfinite(desired)) desired = static_cast<float>(max_crop);
        float min_crop = max_crop * kMinCropRatio;
        float size = std::clamp(desired, min_crop, static_cast<float>(max_crop));
        if (target_size > 0) {
            size = std::max(size, static_cast<float>(std::min(target_size, max_crop)));
        }
        return static_cast<int32_t>(std::nearbyint(size));
    }

    static double CalculateCellEdgeRatio(
        const uint8_t* pixels, int32_t stride, int32_t width, int32_t height,
        int32_t start_x, int32_t start_y, int32_t end_x, int32_t end_y) noexcept
    {
        double edge_count = 0.0;
        int32_t pixel_count = 0;

        for (int32_t y = start_y; y < end_y; y++) {
            for (int32_t x = start_x; x < end_x; x++) {
                if (x + 1 >= width || y + 1 >= height) continue;
                int32_t i = y * stride + x * 4;
                int32_t i_right = i + 4;
                int32_t i_below = (y + 1) * stride + x * 4;

                double lum = 0.299 * pixels[i + 2] + 0.587 * pixels[i + 1] + 0.114 * pixels[i];
                double lum_r = 0.299 * pixels[i_right + 2] + 0.587 * pixels[i_right + 1] + 0.114 * pixels[i_right];
                double lum_b = 0.299 * pixels[i_below + 2] + 0.587 * pixels[i_below + 1] + 0.114 * pixels[i_below];

                double grad = std::abs(lum - lum_r) + std::abs(lum - lum_b);
                if (grad > 40.0) edge_count += 1.0;
                pixel_count++;
            }
        }
        return pixel_count > 0 ? edge_count / pixel_count : 0.0;
    }

    static float ComputeCellSaliency(
        const uint8_t* pixels, int32_t stride, int32_t width, int32_t height,
        int32_t start_x, int32_t start_y, int32_t end_x, int32_t end_y,
        int32_t gx, int32_t gy, int32_t grid_size) noexcept
    {
        double total_contrast = 0.0;
        double total_brightness = 0.0;
        double total_sat = 0.0;
        int32_t pixel_count = 0;

        for (int32_t y = start_y; y < end_y; y++) {
            for (int32_t x = start_x; x < end_x; x++) {
                if (x + 1 >= width || y + 1 >= height) continue;
                int32_t i = y * stride + x * 4;
                int32_t i_right = i + 4;
                int32_t i_below = (y + 1) * stride + x * 4;

                double lum = 0.299 * pixels[i + 2] + 0.587 * pixels[i + 1] + 0.114 * pixels[i];
                double lum_r = 0.299 * pixels[i_right + 2] + 0.587 * pixels[i_right + 1] + 0.114 * pixels[i_right];
                double lum_b = 0.299 * pixels[i_below + 2] + 0.587 * pixels[i_below + 1] + 0.114 * pixels[i_below];

                total_contrast += std::abs(lum - lum_r) + std::abs(lum - lum_b);
                total_brightness += lum;

                double r = pixels[i + 2] / 255.0;
                double g = pixels[i + 1] / 255.0;
                double b = pixels[i] / 255.0;
                double max_c = std::max(r, std::max(g, b));
                double min_c = std::min(r, std::min(g, b));
                total_sat += max_c > 0.0 ? (max_c - min_c) / max_c : 0.0;

                pixel_count++;
            }
        }

        if (pixel_count == 0) return 0.0f;

        double avg_contrast = total_contrast / pixel_count;
        double avg_brightness = total_brightness / pixel_count;
        double avg_sat = total_sat / pixel_count;

        double brightness_penalty = (avg_brightness < 20.0 || avg_brightness > 240.0) ? 0.3 : 1.0;
        double contrast_factor = avg_contrast < 40.0 ? avg_contrast / 40.0 : 1.0 - (avg_contrast - 40.0) / 120.0;
        contrast_factor = std::clamp(contrast_factor, 0.1, 1.0);

        float cx = (gx + 0.5f) / grid_size;
        float cy = (gy + 0.5f) / grid_size;
        float center_dist = std::sqrt((cx - 0.5f) * (cx - 0.5f) + (cy - 0.5f) * (cy - 0.5f));
        float center_bias = std::max(0.1f, 1.0f - center_dist * 1.6f);

        return static_cast<float>((avg_sat * 60.0 + contrast_factor * 20.0) * brightness_penalty * center_bias);
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_smartcrop_fill_tensor_from_bgra32(
    const uint8_t* pixels, int32_t scaled_w, int32_t scaled_h, int32_t stride,
    float* tensor_buffer) noexcept
{
    if (!tensor_buffer || !vnotch::services::SmartThumbnailCropService::IsValidPixelArgs(pixels, scaled_w, scaled_h, stride, 1)) {
        return VN_ERR_INVALID_ARG;
    }
    vnotch::services::SmartThumbnailCropService::FillTensorFromBgra32(pixels, scaled_w, scaled_h, stride, tensor_buffer);
    return VN_OK;
}

VN_EXPORT int32_t vn_smartcrop_apply_nms(
    const vnotch::services::VnDetection* in_detections, int32_t in_count,
    vnotch::services::VnDetection* out_detections, int32_t out_cap, int32_t* out_count) noexcept
{
    if (!out_count) return VN_ERR_INVALID_ARG;
    *out_count = 0;
    if (!in_detections || in_count <= 0) return VN_OK;
    try {
        auto res = vnotch::services::SmartThumbnailCropService::ApplyNms(
            std::span<const vnotch::services::VnDetection>(in_detections, static_cast<size_t>(in_count)));
        int32_t req = static_cast<int32_t>(res.size());
        *out_count = req;
        if (req == 0) return VN_OK;
        if (!out_detections || out_cap < req) return VN_ERR_BUFFER_TOO_SMALL;
        std::copy(res.begin(), res.end(), out_detections);
        return VN_OK;
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t vn_smartcrop_parse_yolox_and_nms(
    const float* buffer, int32_t img_width, int32_t img_height, float scale,
    vnotch::services::VnDetection* out_detections, int32_t out_cap,
    int32_t* out_raw_count, int32_t* out_nms_count) noexcept
{
    if (out_nms_count) *out_nms_count = 0;
    if (out_raw_count) *out_raw_count = 0;
    if (!buffer || !out_nms_count || img_width <= 0 || img_height <= 0 ||
        !std::isfinite(scale) || scale <= 0.0f) {
        return VN_ERR_INVALID_ARG;
    }
    try {
        auto res = vnotch::services::SmartThumbnailCropService::ParseYoloxAndNms(
            buffer, img_width, img_height, scale, out_raw_count);
        int32_t req = static_cast<int32_t>(res.size());
        *out_nms_count = req;
        if (req == 0) return VN_OK;
        if (!out_detections || out_cap < req) return VN_ERR_BUFFER_TOO_SMALL;
        std::copy(res.begin(), res.end(), out_detections);
        return VN_OK;
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t vn_smartcrop_find_best_subject_bounds(
    const vnotch::services::VnDetection* detections, int32_t count,
    int32_t img_width, int32_t img_height,
    vnotch::services::VnSubjectBounds* out_bounds, uint8_t* out_found) noexcept
{
    if (!out_bounds || !out_found) return VN_ERR_INVALID_ARG;
    *out_found = 0;
    if (!detections || count <= 0) return VN_OK;
    *out_found = vnotch::services::SmartThumbnailCropService::FindBestSubjectBounds(
        std::span<const vnotch::services::VnDetection>(detections, static_cast<size_t>(count)),
        img_width, img_height, out_bounds) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t vn_smartcrop_get_portrait_crop_rect(
    const vnotch::services::VnDetection* face, int32_t width, int32_t height,
    vnotch::services::VnCropRect* out_rect) noexcept
{
    if (!face || !out_rect || width <= 0 || height <= 0) return VN_ERR_INVALID_ARG;
    *out_rect = vnotch::services::SmartThumbnailCropService::GetPortraitCropRect(*face, width, height);
    return VN_OK;
}

VN_EXPORT int32_t vn_smartcrop_get_single_person_crop_rect(
    const vnotch::services::VnDetection* person, int32_t img_width, int32_t img_height, int32_t target_size,
    vnotch::services::VnCropRect* out_rect) noexcept
{
    if (!person || !out_rect || img_width <= 0 || img_height <= 0) return VN_ERR_INVALID_ARG;
    *out_rect = vnotch::services::SmartThumbnailCropService::GetSinglePersonCropRect(*person, img_width, img_height, target_size);
    return VN_OK;
}

VN_EXPORT int32_t vn_smartcrop_get_group_person_crop_rect(
    const vnotch::services::VnDetection* persons, int32_t count,
    int32_t img_width, int32_t img_height, int32_t target_size,
    vnotch::services::VnCropRect* out_rect) noexcept
{
    if (!persons || count <= 0 || !out_rect || img_width <= 0 || img_height <= 0) return VN_ERR_INVALID_ARG;
    *out_rect = vnotch::services::SmartThumbnailCropService::GetGroupPersonCropRect(
        std::span<const vnotch::services::VnDetection>(persons, static_cast<size_t>(count)),
        img_width, img_height, target_size);
    return VN_OK;
}

VN_EXPORT int32_t vn_smartcrop_get_best_object_crop_rect(
    const vnotch::services::VnDetection* objects, int32_t count,
    int32_t img_width, int32_t img_height, int32_t target_size,
    vnotch::services::VnCropRect* out_rect) noexcept
{
    if (!objects || count <= 0 || !out_rect || img_width <= 0 || img_height <= 0) return VN_ERR_INVALID_ARG;
    *out_rect = vnotch::services::SmartThumbnailCropService::GetBestObjectCropRect(
        std::span<const vnotch::services::VnDetection>(objects, static_cast<size_t>(count)),
        img_width, img_height, target_size);
    return VN_OK;
}

VN_EXPORT int32_t vn_smartcrop_compute_saliency_crop_rect(
    const uint8_t* pixels, int32_t w, int32_t h, int32_t stride,
    int32_t img_width, int32_t img_height, int32_t target_size,
    vnotch::services::VnCropRect* out_rect) noexcept
{
    if (!out_rect || img_width <= 0 || img_height <= 0 ||
        !vnotch::services::SmartThumbnailCropService::IsValidPixelArgs(pixels, w, h, stride, 8)) {
        return VN_ERR_INVALID_ARG;
    }
    *out_rect = vnotch::services::SmartThumbnailCropService::ComputeSaliencyCropRect(
        pixels, w, h, stride, img_width, img_height, target_size);
    return VN_OK;
}

VN_EXPORT int32_t vn_smartcrop_try_detect_text_crop_rect(
    const uint8_t* pixels, int32_t w, int32_t h, int32_t stride,
    int32_t img_width, int32_t img_height, int32_t crop_size,
    vnotch::services::VnCropRect* out_rect, uint8_t* out_found) noexcept
{
    if (!out_rect || !out_found || img_width <= 0 || img_height <= 0 ||
        !vnotch::services::SmartThumbnailCropService::IsValidPixelArgs(pixels, w, h, stride, 8)) {
        return VN_ERR_INVALID_ARG;
    }
    *out_found = vnotch::services::SmartThumbnailCropService::TryDetectTextCropRect(
        pixels, w, h, stride, img_width, img_height, crop_size, out_rect) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t vn_smartcrop_detect_text_regions(
    const uint8_t* pixels, int32_t w, int32_t h, int32_t stride,
    int32_t img_width, int32_t img_height,
    vnotch::services::VnCropRect* out_regions, int32_t out_cap, int32_t* out_count) noexcept
{
    if (!out_count || img_width <= 0 || img_height <= 0 ||
        !vnotch::services::SmartThumbnailCropService::IsValidPixelArgs(pixels, w, h, stride, 8)) {
        return VN_ERR_INVALID_ARG;
    }
    *out_count = vnotch::services::SmartThumbnailCropService::DetectTextRegions(
        pixels, w, h, stride, img_width, img_height, out_regions, out_cap);
    return VN_OK;
}

} // extern "C"
