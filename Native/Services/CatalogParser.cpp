#include "vnotch_common.h"
#include "vnotch_json_view.h"
#include <array>
#include <charconv>
#include <functional>

VN_EXPORT int32_t vn_spotify_extract_track_id(const char16_t*, int32_t, char16_t*, int32_t, int32_t*, uint8_t*) noexcept;
VN_EXPORT int32_t vn_spotify_match_normalized_score(const char16_t*, int32_t, const char16_t*, int32_t, int32_t, int32_t, int32_t*) noexcept;
VN_EXPORT int32_t vn_spotify_calculate_duration_score_delta(int64_t, int64_t, int32_t*) noexcept;
// These calls stay entirely inside the DLL, sharing the existing scoring rules.
namespace {
using Text = std::u16string;
using View = std::u16string_view;
struct Json : vnotch::json::Value {
    bool relaxed = true;
    Json(vnotch::json::Value value = {}, bool spotify = true) : Value(value), relaxed(spotify) {}
    template<class F> void each(F&& action) const {
        Value::each([&](auto key, auto value) { return action(Json(key, relaxed), Json(value, relaxed)); });
    }
    Json get(vnotch::json::Bytes name) const {
        if (!is('{')) return {};
        if (!relaxed) return Json(Value::get(name), false);
        Json result, fallback;
        each([&](vnotch::json::Value key, vnotch::json::Value value) {
            auto raw = key.bytes.substr(1, key.bytes.size() - 2);
            if (raw == name) { result = Json(value); return false; }
            Text decoded;
            if (raw.find('\\') != raw.npos) decoded = key.text();
            const size_t size = decoded.empty() ? raw.size() : decoded.size();
            if (!decoded.empty() && decoded.size() == name.size() && std::equal(decoded.begin(), decoded.end(), name.begin())) { result = Json(value); return false; }
            if (size == name.size() && fallback.bytes.empty()) {
                bool equal = true;
                for (size_t i = 0; i < name.size(); ++i) {
                    auto c = decoded.empty() ? static_cast<char16_t>(static_cast<unsigned char>(raw[i])) : decoded[i];
                    if (c >= u'A' && c <= u'Z') c += u'a' - u'A';
                    auto n = name[i]; if (n >= 'A' && n <= 'Z') n += 'a' - 'A';
                    if (c != static_cast<unsigned char>(n)) { equal = false; break; }
                }
                if (equal) fallback = Json(value);
            }
            return true;
        });
        return result.bytes.empty() ? fallback : result;
    }
};
Text first(Json value, std::initializer_list<const char*> names) {
    for (const auto name : names) { auto property = value.get(name); if (property.is('"')) return property.text(); }
    return {};
}
Text id(Json value) {
    auto extract = [](const Text& text, bool bare) -> Text {
        std::array<char16_t, 32> result{}; int32_t length = 0; uint8_t found = 0;
        vn_spotify_extract_track_id(text.data(), static_cast<int32_t>(text.size()), result.data(), static_cast<int32_t>(result.size()), &length, &found);
        return found && (!bare || static_cast<size_t>(length) == text.size()) ? Text(result.data(), length) : Text{};
    };
    for (const auto name : {"trackUri", "track_uri", "uri", "url", "externalUrl"}) { auto result = extract(value.get(name).text(), false); if (!result.empty()) return result; }
    for (const auto name : {"track_spotify_id", "trackId", "track_id", "spotifyId", "spotify_id", "id"}) { auto result = extract(value.get(name).text(), true); if (!result.empty()) return result; }
    return {};
}
int score(View candidate, View expected, int exact = 100, int contains = 72) {
    const auto normalized = vnotch::normalize_for_loose_match_impl(candidate);
    int32_t result = 0;
    vn_spotify_match_normalized_score(normalized.data(), static_cast<int32_t>(normalized.size()), expected.data(), static_cast<int32_t>(expected.size()), exact, contains, &result);
    return result;
}
Text artist(Json item) {
    if (item.is('"')) return item.text();
    return first(item, {"name", "artistName", "artist_name"});
}
Text find_name(Json item, int depth = 0) {
    if (depth > 12) return {};
    auto name = first(item, {"name"});
    if (!name.empty()) return name;
    item.each([&](Json, Json child) { name = find_name(child, depth + 1); return name.empty(); });
    return name;
}
Text artists(Json item) {
    auto result = first(item, {"artistName", "artist_name", "author", "subtitle"});
    if (!result.empty()) return result;
    result = artist(item.get("artist")); if (!result.empty()) return result;
    auto list = item.get("artists");
    if (list.is('{')) return find_name(list);
    list.each([&](Json, Json value) { result = artist(value); return vnotch::is_null_or_whitespace(result); });
    return result;
}
double number(Json value) {
    auto text = value.is('"') ? value.text() : Text{};
    std::string ascii;
    if (value.is('"')) { for (auto c : text) { if (c > 127) return 0; ascii += static_cast<char>(c); } }
    else ascii = value.bytes;
    double result = 0;
    auto parsed = std::from_chars(ascii.data(), ascii.data() + ascii.size(), result);
    return parsed.ec == std::errc{} && parsed.ptr == ascii.data() + ascii.size() && std::isfinite(result) ? result : 0;
}
int64_t duration(Json item) {
    for (const auto key : {"durationMs", "duration_ms", "durationMillis", "duration_millis", "duration", "durationSeconds", "duration_seconds", "track_length"}) {
        double value = number(item.get(key));
        if (value <= 0) continue;
        const bool ms = std::string_view(key).find("Ms") != std::string_view::npos || std::string_view(key).find("ms") != std::string_view::npos || std::string_view(key).find("millis") != std::string_view::npos || value > 10000;
        value *= ms ? 10000 : 10000000;
        return value < static_cast<double>(INT64_MAX) ? static_cast<int64_t>(value) : 0;
    }
    return 0;
}
void append_artists(Json values, Text& text, View separator = u"|") {
    values.each([&](Json, Json item) {
        const auto name = item.get("name").text();
        if (!vnotch::is_null_or_whitespace(name)) { if (!text.empty()) text += separator; text += separator == u"|" ? View(name) : vnotch::vn_trim(name); }
        if (separator == u"|") item.get("alias").each([&](Json, Json alias) {
            auto label = alias.text(); if (!vnotch::is_null_or_whitespace(label)) { if (!text.empty()) text += separator; text += label; } return true;
        });
        return true;
    });
}
int album_bonus(View candidate, View expected) {
    if (vnotch::is_null_or_whitespace(candidate) || vnotch::is_null_or_whitespace(expected)) return 0;
    auto a = vnotch::normalize_for_loose_match_impl(candidate), b = vnotch::normalize_for_loose_match_impl(expected);
    if (a.empty() || b.empty()) return 0;
    return a == b || (a.starts_with(b) && a.size() > b.size() && a[b.size()] == u' ') ? 40 : -20;
}
}

// 0 generic Spotify/Musixmatch, 1 Pathfinder ID, 2 iTunes artists,
// 3 NetEase ID, 4 Kugou ID/key, 5 QQ songmid, 6 Deezer detail artists.
VN_EXPORT int32_t vn_catalog_select_utf8(const uint8_t* bytes, int32_t length, int32_t kind,
    const char16_t* title, int32_t title_length, const char16_t* artist_ptr, int32_t artist_length,
    const char16_t* album, int32_t album_length, int64_t target_ms,
    const uint8_t* context_bytes, int32_t context_length, int64_t context_id,
    char16_t* output, int32_t capacity, int32_t* written, char16_t* key, int32_t key_capacity, int32_t* key_written,
    int64_t* numeric_id, int32_t* api_code, int32_t* best_score) noexcept {
    if (!written || !key_written || !numeric_id || !api_code || !best_score) return VN_ERR_INVALID_ARG;
    *written = *key_written = 0; *numeric_id = 0; *api_code = 0; *best_score = INT32_MIN;
    if (!bytes || length <= 0 || length > 2 * 1024 * 1024 || kind < 0 || kind > 7 || title_length < 0 || artist_length < 0 || album_length < 0 ||
        (!title && title_length) || (!artist_ptr && artist_length) || (!album && album_length) || context_length < 0 || context_length > 2 * 1024 * 1024 || (!context_bytes && context_length) || capacity < 0 || key_capacity < 0 || (!output && capacity) || (!key && key_capacity)) return VN_ERR_INVALID_ARG;
    try {
        const Json root(vnotch::json::parse({reinterpret_cast<const char*>(bytes), static_cast<size_t>(length)}, 64), kind == 0 || kind == 1 || kind == 7);
        const auto raw_title = vnotch::make_u16_view(title, title_length), raw_artist = vnotch::make_u16_view(artist_ptr, artist_length), raw_album = vnotch::make_u16_view(album, album_length);
        const auto expected_title = vnotch::normalize_for_loose_match_impl(raw_title), expected_artist = vnotch::normalize_for_loose_match_impl(raw_artist);
        Text best, best_key;
        int best_value = (kind == 2 || kind == 6) ? 0 : INT32_MIN;
        *api_code = static_cast<int32_t>(root.get("code").integer());
        auto cover_art = [](Json track) {
            Text best_url;
            int64_t best_width = -1;
            track.get("albumOfTrack").get("coverArt").get("sources").each([&](Json, Json src) {
                auto url = src.get("url").text();
                if (!url.empty()) {
                    auto w = src.get("width").integer();
                    if (w > best_width) { best_width = w; best_url = std::move(url); }
                }
                return true;
            });
            return best_url;
        };
        auto consider_spotify = [&](Json track, bool pathfinder) {
            auto candidate_id = id(track);
            if (candidate_id.empty()) return;
            auto candidate_title = first(track, pathfinder ? std::initializer_list<const char*>{"name"} : std::initializer_list<const char*>{"trackName", "track_name", "title", "name"});
            int value = score(candidate_title, expected_title);
            if (value < 72) return;
            int artist_score = 0;
            if (pathfinder) {
                auto type = track.get("__typename").text();
                if (!type.empty() && !vnotch::vn_equals_icase_ascii(type, u"Track")) return;
                Text combined;
                track.get("artists").get("items").each([&](Json, Json item) {
                    auto name = vnotch::normalize_for_loose_match_impl(item.get("profile").get("name").text());
                    if (!name.empty()) {
                        artist_score = std::max(artist_score, score(name, expected_artist, 35, 22));
                        if (!combined.empty()) combined += u' ';
                        combined += name;
                    }
                    return true;
                });
                artist_score = std::max(artist_score, score(combined, expected_artist, 35, 22));
                if (!expected_artist.empty() && artist_score == 0) return;
            } else {
                artist_score = score(artists(track), expected_artist, 35, 22);
                auto ticks = duration(track);
                if (ticks > 0 && target_ms > 0 && target_ms <= INT64_MAX / 10000) {
                    int32_t delta = 0; vn_spotify_calculate_duration_score_delta(ticks, target_ms * 10000, &delta); value += delta;
                }
            }
            value += artist_score;
            if (value > best_value) { best_value = value; best = std::move(candidate_id); if (pathfinder) best_key = cover_art(track); }
        };
        if (kind == 0) {
            auto visit = [&](auto&& self, Json value, int depth) -> void {
                if (depth > 24) return;
                if (value.is('{')) consider_spotify(value, false);
                value.each([&](Json, Json child) { self(self, child, depth + 1); return true; });
            };
            visit(visit, root, 0);
        } else if (kind == 1) {
            if (root.get("thumbnail_url").is('"')) best_key = root.get("thumbnail_url").text();
            else root.get("data").get("searchV2").get("tracksV2").get("items").each([&](Json, Json item) { consider_spotify(item.get("item").get("data"), true); return true; });
        } else if (kind == 7) {
            const auto expected_album = vnotch::normalize_for_loose_match_impl(raw_album);
            if (!expected_title.empty() && !expected_artist.empty()) root.get("data").get("searchV2").get("tracksV2").get("items").each([&](Json, Json item) {
                const auto track = item.get("item").get("data");
                const auto type = track.get("__typename").text();
                if (!type.empty() && !vnotch::vn_equals_icase_ascii(type, u"Track")) return true;
                if (vnotch::normalize_for_loose_match_impl(track.get("name").text()) != expected_title) return true;
                Text display; int count = 0; bool primary = false;
                track.get("artists").get("items").each([&](Json, Json value) {
                    auto name = value.get("profile").get("name").text();
                    const auto normalized = vnotch::normalize_for_loose_match_impl(name);
                    if (!normalized.empty()) { if (count == 0) primary = normalized == expected_artist; ++count; }
                    if (!vnotch::is_null_or_whitespace(name)) { if (!display.empty()) display += u", "; display += vnotch::vn_trim(name); }
                    return true;
                });
                if (!primary) return true;
                int value = count > 1 ? 135 : 115;
                if (!expected_album.empty() && track.get("albumOfTrack").is('{')) {
                    const auto album_name = vnotch::normalize_for_loose_match_impl(track.get("albumOfTrack").get("name").text());
                    if (album_name == expected_album) value += 50;
                    else if (!album_name.empty()) value -= 20;
                }
                if (value > best_value) { best_value = value; best = std::move(display); best_key = cover_art(track); }
                return true;
            });
        } else if (kind == 2 || kind == 6) {
            Json search_item;
            if (kind == 6 && context_length > 0) {
                const Json search(vnotch::json::parse({reinterpret_cast<const char*>(context_bytes), static_cast<size_t>(context_length)}, 64), false);
                search.get("data").each([&](Json, Json item) { if (item.get("id").integer() == context_id) { search_item = item; return false; } return true; });
                if (!search_item.is('{')) return VN_OK;
            }
            auto consider = [&](Json item) {
                Text candidate_title, candidate_artist, candidate_album, candidate_art;
                int64_t milliseconds = 0;
                if (kind == 2) {
                    candidate_title = item.get("trackName").text(); candidate_artist = item.get("artistName").text(); candidate_album = item.get("collectionName").text();
                    milliseconds = item.get("trackTimeMillis").integer();
                    candidate_art = item.get("artworkUrl100").text();
                    if (const auto pos = candidate_art.rfind(u"100x100bb"); pos != Text::npos) candidate_art.replace(pos, 9, u"600x600bb");
                } else {
                    const auto source = search_item.is('{') ? search_item : item;
                    candidate_title = source.get("title").text(); append_artists(item.get("contributors"), candidate_artist, u" & "); candidate_album = source.get("album").get("title").text();
                    const auto seconds = source.get("duration").integer(); milliseconds = seconds >= 0 && seconds <= INT64_MAX / 1000 ? seconds * 1000 : 0;
                    candidate_art = first(source.get("album"), {"cover_xl", "cover_big", "cover_medium", "cover"});
                }
                std::array<char16_t, 512> formatted{}; int32_t count = 0, value = 0;
                if (vn_media_metadata_format_spotify_artists(candidate_artist.data(), static_cast<int32_t>(candidate_artist.size()), raw_artist.data(), static_cast<int32_t>(raw_artist.size()),
                    candidate_title.data(), static_cast<int32_t>(candidate_title.size()), raw_title.data(), static_cast<int32_t>(raw_title.size()), milliseconds, target_ms,
                    formatted.data(), static_cast<int32_t>(formatted.size()), &count, &value) == VN_OK && count > 0 && value > 0) {
                    value += album_bonus(candidate_album, raw_album);
                    if (value > best_value) { best_value = value; best.assign(formatted.data(), count); best_key = std::move(candidate_art); }
                }
            };
            if (kind == 6) consider(root);
            else root.get("results").each([&](Json, Json item) { consider(item); return true; });
        } else {
            const auto list = kind == 3 ? root.get("result").get("songs") : kind == 4 ? root.get("candidates") : root.get("data").get("song").get("list");
            list.each([&](Json, Json item) {
                Text candidate_title, candidate_artists, candidate_id, candidate_key; int64_t milliseconds = 0, number_id = 0;
                if (kind == 3) {
                    number_id = item.get("id").integer(); if (number_id <= 0) return true;
                    candidate_title = item.get("name").text();
                    auto dt = item.get("dt"); milliseconds = dt.bytes.empty() ? item.get("duration").integer() : dt.integer();
                    append_artists(item.get("ar"), candidate_artists); append_artists(item.get("artists"), candidate_artists);
                } else if (kind == 4) {
                    auto value = item.get("id"); candidate_id = value.is('"') ? value.text() : Text(value.bytes.begin(), value.bytes.end());
                    candidate_key = item.get("accesskey").text(); if (candidate_id.empty() || candidate_key.empty()) return true;
                    candidate_title = item.get("song").text(); candidate_artists = item.get("singer").text();
                    for (auto& c : candidate_artists) if (c == u'、' || c == u'/') c = u'|';
                    milliseconds = item.get("duration").integer();
                } else {
                    candidate_id = item.get("songmid").text(); if (candidate_id.empty()) return true;
                    candidate_title = first(item, {"songname", "name"}); append_artists(item.get("singer"), candidate_artists);
                    const auto seconds = item.get("interval").integer(); milliseconds = seconds >= 0 && seconds <= INT32_MAX / 1000 ? seconds * 1000 : 0;
                }
                if (milliseconds < INT32_MIN || milliseconds > INT32_MAX) milliseconds = 0;
                int32_t value = 0; uint8_t accepted = 0;
                vn_lyrics_score_netease_candidate(raw_title.data(), static_cast<int32_t>(raw_title.size()), raw_artist.data(), static_cast<int32_t>(raw_artist.size()),
                    static_cast<int32_t>(std::clamp<int64_t>(target_ms / 1000, 0, INT32_MAX)), candidate_title.data(), static_cast<int32_t>(candidate_title.size()),
                    candidate_artists.data(), static_cast<int32_t>(candidate_artists.size()), static_cast<int32_t>(milliseconds), &value, &accepted);
                if (accepted && value > best_value) { best_value = value; best = std::move(candidate_id); best_key = std::move(candidate_key); *numeric_id = number_id; }
                return true;
            });
        }
        *best_score = best_value;
        int status = vnotch::copy_u16_out(best, output, capacity, written);
        int key_status = vnotch::copy_u16_out(best_key, key, key_capacity, key_written);
        return status != VN_OK ? status : key_status;
    } catch (...) { return VN_ERR_FAIL; }
}

VN_EXPORT int32_t vn_youtube_extract_api_key_utf8(const uint8_t* bytes, int32_t length, char16_t* output, int32_t capacity, int32_t* written) noexcept {
    if (!written) return VN_ERR_INVALID_ARG;
    *written = 0;
    if (!bytes || length < 0 || length > 2 * 1024 * 1024 || capacity < 0 || (!output && capacity)) return VN_ERR_INVALID_ARG;
    std::string_view input(reinterpret_cast<const char*>(bytes), static_cast<size_t>(length));
    constexpr std::string_view marker = "\"INNERTUBE_API_KEY\"";
    size_t pos = 0;
    while ((pos = input.find(marker, pos)) != input.npos) {
        pos += marker.size(); vnotch::json::whitespace(input, pos);
        if (pos == input.size() || input[pos++] != ':') continue;
        vnotch::json::whitespace(input, pos);
        if (pos == input.size() || input[pos++] != '"') continue;
        const auto begin = pos;
        while (pos < input.size() && ((input[pos] >= 'a' && input[pos] <= 'z') || (input[pos] >= 'A' && input[pos] <= 'Z') || (input[pos] >= '0' && input[pos] <= '9') || input[pos] == '_' || input[pos] == '-')) ++pos;
        if (pos == begin || pos == input.size() || input[pos] != '"') continue;
        const auto size = static_cast<int32_t>(pos - begin); *written = size;
        if (size > capacity) return VN_ERR_BUFFER_TOO_SMALL;
        for (int32_t i = 0; i < size; ++i) output[i] = static_cast<char16_t>(input[begin + i]);
        return VN_OK;
    }
    return VN_OK;
}
