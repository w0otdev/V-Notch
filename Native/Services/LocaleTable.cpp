#include "vnotch_json_view.h"
#include <memory>

namespace {
using Text = std::u16string_view;
struct Slice { size_t offset, length; };
struct Entry { Slice key, value; size_t hash; };
struct Locale {
    std::u16string arena;
    std::vector<Entry> entries;
    // Power-of-two open-addressed table. Zero denotes a free slot.
    std::vector<size_t> slots = std::vector<size_t>(32);
    Text view(Slice s) const { return Text(arena).substr(s.offset, s.length); }
    Slice append(Text text) {
        Slice slice{arena.size(), text.size()};
        arena.append(text);
        return slice;
    }
    size_t locate(Text key, size_t hash) const {
        size_t pos = hash & (slots.size() - 1);
        while (slots[pos]) {
            const auto& entry = entries[slots[pos] - 1];
            if (entry.hash == hash && view(entry.key) == key) break;
            pos = (pos + 1) & (slots.size() - 1);
        }
        return pos;
    }
    void store(Text key, Text value) {
        if (key.empty() || key.front() == u'_' || key == value) return;
        if (entries.size() * 2 >= slots.size()) {
            slots.assign(slots.size() * 2, 0);
            for (size_t i = 0; i < entries.size(); ++i)
                slots[locate(view(entries[i].key), entries[i].hash)] = i + 1;
        }
        size_t hash = std::hash<Text>{}(key);
        size_t pos = locate(key, hash);
        if (slots[pos]) {
            if (!vnotch::vn_trim(value).empty()) entries[slots[pos] - 1].value = append(value);
        } else {
            entries.push_back({append(key), append(value), hash});
            slots[pos] = entries.size();
        }
    }
};

struct Match { Text text, key; };
struct Index {
    std::vector<const Locale*> tables;
    std::vector<Match> matches;
};
struct Translation { const char16_t* data; int length; };
int compare_text(Text a, Text b) noexcept {
    return CompareStringOrdinal(reinterpret_cast<const wchar_t*>(a.data()), static_cast<int>(a.size()),
        reinterpret_cast<const wchar_t*>(b.data()), static_cast<int>(b.size()), TRUE) - CSTR_EQUAL;
}
}

extern "C" {
VN_EXPORT Locale* vn_locale_create() noexcept {
    try { return new Locale(); } catch (...) { return nullptr; }
}
VN_EXPORT void vn_locale_destroy(Locale* table) noexcept { delete table; }

VN_EXPORT HRESULT vn_locale_merge(Locale* table, const char* utf8, int length) noexcept {
    if (!table || !utf8 || length <= 0) return E_INVALIDARG;
    try {
        auto root = vnotch::json::parse({utf8, static_cast<size_t>(length)}, 8);
        if (!root.is('{')) return E_INVALIDARG;
        // A broken community overlay must not publish half a locale.
        Locale merged = *table;
        root.each([&](auto key, auto value) {
            if (value.is('"')) merged.store(key.text(), value.text());
            return true;
        });
        *table = std::move(merged);
        return S_OK;
    } catch (const std::bad_alloc&) { return E_OUTOFMEMORY; }
      catch (...) { return E_INVALIDARG; }
}

VN_EXPORT const char16_t* vn_locale_get(Locale* table, const char16_t* key, int length, int* size) noexcept {
    if (!table || !key || length < 0 || !size) return nullptr;
    Text text(key, length);
    auto id = table->slots[table->locate(text, std::hash<Text>{}(text))];
    if (!id) return nullptr;
    Text value = table->view(table->entries[id - 1].value);
    *size = static_cast<int>(value.size());
    return value.data();
}

VN_EXPORT const char16_t* vn_locale_key(Locale* table, int index, int* size) noexcept {
    if (!table || !size || static_cast<size_t>(index) >= table->entries.size()) return nullptr;
    Text key = table->view(table->entries[index].key);
    *size = static_cast<int>(key.size());
    return key.data();
}

// Locale tables are immutable once published. The managed index keeps each owner alive.
VN_EXPORT Index* vn_translation_index(Locale* const* tables, int count) noexcept {
    try {
        auto index = std::make_unique<Index>();
        if (count < 0 || (count && !tables)) return nullptr;
        for (int i = 0; i < count; ++i) {
            if (!tables[i]) return nullptr;
            auto& table = *tables[i];
            index->tables.push_back(&table);
            for (auto& entry : table.entries) {
                Text value = table.view(entry.value);
                if (!vnotch::vn_trim(value).empty()) index->matches.push_back({value, table.view(entry.key)});
            }
        }
        std::sort(index->matches.begin(), index->matches.end(), [](const Match& a, const Match& b) {
            int order = compare_text(a.text, b.text);
            return order == 0 ? a.key < b.key : order < 0;
        });
        index->matches.erase(std::unique(index->matches.begin(), index->matches.end(), [](const Match& a, const Match& b) {
            return a.key == b.key && compare_text(a.text, b.text) == 0;
        }), index->matches.end());
        return index.release();
    } catch (...) { return nullptr; }
}
VN_EXPORT void vn_translation_destroy(Index* index) noexcept { delete index; }
// Output views borrow immutable arena storage. -1 asks the caller for a larger buffer.
VN_EXPORT int vn_translation_find(Index* index, const char16_t* text, int length,
    Translation* output, int capacity) noexcept {
    if (!index || !text || length <= 0 || !output || capacity <= 0) return 0;
    Text query(text, length);
    auto& matches = index->matches;
    auto match = std::lower_bound(matches.begin(), matches.end(), query,
        [](const Match& m, Text q) { return compare_text(m.text, q) < 0; });
    int count = 0;
    for (; match != matches.end() && compare_text(match->text, query) == 0; ++match) {
        size_t hash = std::hash<Text>{}(match->key);
        for (const Locale* table : index->tables) {
            size_t id = table->slots[table->locate(match->key, hash)];
            if (!id) continue;
            Text value = table->view(table->entries[id - 1].value);
            if (vnotch::vn_trim(value).empty()) continue;
            if (std::any_of(output, output + count, [value](const Translation& existing) {
                return compare_text({existing.data, static_cast<size_t>(existing.length)}, value) == 0;
            })) continue;
            if (count == capacity) return -1;
            output[count++] = {value.data(), static_cast<int>(value.size())};
        }
    }
    return count;
}
}
