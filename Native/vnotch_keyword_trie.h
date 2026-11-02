#pragma once
#include <array>
#include <cstdint>
#include <string_view>

namespace vnotch {
// ASCII field names only. Non-ASCII prefixes remain available to the caller's boundary rules.
// A dense transition table consumes each character once, independently of the number of rules.
template<size_t Capacity>
struct KeywordTrie {
    struct Node { std::array<uint16_t, 39> next{}; bool terminal = false; };
    std::array<Node, Capacity> nodes{};
    uint16_t used = 1;
    bool reversed = false;
    static constexpr int Symbol(char16_t ch) noexcept {
        if (ch >= u'A' && ch <= u'Z') ch += u'a' - u'A';
        if (ch >= u'a' && ch <= u'z') return ch - u'a';
        if (ch >= u'0' && ch <= u'9') return 26 + ch - u'0';
        return ch == u'_' ? 36 : ch == u'-' ? 37 : ch == u'.' ? 38 : -1;
    }
    constexpr bool Contains(std::u16string_view text) const noexcept {
        uint16_t node = 0;
        for (size_t i = 0; i < text.size(); ++i) {
            const int symbol = Symbol(text[reversed ? text.size() - i - 1 : i]);
            if (symbol < 0 || !(node = nodes[node].next[symbol])) return false;
        }
        return nodes[node].terminal;
    }
    template<typename Accept>
    constexpr bool MatchesSuffix(std::u16string_view text, Accept accept) const noexcept {
        uint16_t node = 0;
        for (size_t i = 0; i < text.size(); ++i) {
            const int symbol = Symbol(text[text.size() - i - 1]);
            if (symbol < 0 || !(node = nodes[node].next[symbol])) return false;
            if (nodes[node].terminal && accept(i + 1)) return true;
        }
        return false;
    }
};
template<size_t N>
consteval auto MakeKeywordTrie(const std::u16string_view (&words)[N], bool reverse = false) {
    KeywordTrie<N * 32 + 1> trie{};
    trie.reversed = reverse;
    for (const auto word : words) {
        uint16_t node = 0;
        for (size_t i = 0; i < word.size(); ++i) {
            const int symbol = trie.Symbol(word[reverse ? word.size() - i - 1 : i]);
            if (symbol < 0) throw "Unsupported keyword character";
            auto& next = trie.nodes[node].next[symbol];
            if (!next) {
                if (trie.used >= trie.nodes.size()) throw "Keyword trie capacity exceeded";
                next = trie.used++;
            }
            node = next;
        }
        trie.nodes[node].terminal = true;
    }
    return trie;
}
}
