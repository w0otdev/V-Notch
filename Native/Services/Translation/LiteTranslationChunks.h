#pragma once
#include <algorithm>
#include <array>
#include <string_view>

namespace vnotch::translation {
inline constexpr std::string_view WordStart = "▁";
constexpr std::string_view PieceText(std::string_view piece) noexcept {
    return piece.starts_with(WordStart) ? piece.substr(WordStart.size()) : piece;
}
constexpr bool IsCloser(std::string_view piece) noexcept {
    return piece == "\"" || piece == "'" || piece == "”" || piece == "’" || piece == ")" ||
        piece == "]" || piece == "}" || piece == "»" || piece == "」" || piece == "』" || piece == "）";
}
constexpr bool IsSentenceEnd(std::string_view piece) noexcept {
    return piece.ends_with(".") || piece.ends_with("!") || piece.ends_with("?") ||
        piece.ends_with("。") || piece.ends_with("！") || piece.ends_with("？") ||
        piece.ends_with("؟") || piece.ends_with("۔") || piece.ends_with("।") || piece.ends_with("॥");
}
template<class Tokens>
constexpr bool IsAbbreviation(const Tokens& tokens, size_t begin, size_t last) noexcept {
    size_t first = last;
    while (first > begin && !std::string_view(tokens[first]).starts_with(WordStart)) --first;
    std::array<char, 32> word{};
    size_t length = 0;
    for (size_t i = first; i <= last; ++i) {
        for (char c : PieceText(tokens[i])) {
            if (length == word.size()) return false;
            word[length++] = c >= 'A' && c <= 'Z' ? c + ('a' - 'A') : c;
        }
    }
    if (length && word[length - 1] == '.') --length;
    const std::string_view value(word.data(), length);
    if (length == 1 && value[0] >= 'a' && value[0] <= 'z') return true; // Initial, e.g. A. Smith.
    constexpr std::array names = {"mr", "mrs", "ms", "dr", "prof", "sr", "jr", "st", "vs",
        "etc", "e.g", "i.e", "u.s", "u.k", "no", "fig", "approx", "dept", "inc", "ltd"};
    for (auto name : names) if (value == name) return true;
    return false;
}

// Prefer a complete sentence, then a clause, then the existing word boundary. Never
// drop tokens or grow the input budget. Punctuation without a following word boundary
// is not a Latin sentence break (decimal numbers, URLs, versions and abbreviations).
template<class Tokens>
constexpr size_t FindChunkEnd(const Tokens& tokens, size_t begin, size_t budget) noexcept {
    if (begin >= tokens.size() || budget == 0) return begin;
    const size_t hard = begin + std::min(budget, tokens.size() - begin);
    if (hard == tokens.size()) return hard;
    size_t sentence = begin, clause = begin;
    for (size_t end = begin + 1; end <= hard; ++end) {
        std::string_view piece = tokens[end - 1];
        size_t after = end;
        while (after < tokens.size() && IsCloser(tokens[after])) ++after;
        if (after > hard) continue;
        bool spaced = after == tokens.size() || std::string_view(tokens[after]).starts_with(WordStart);
        bool unspacedEnd = piece.ends_with("。") || piece.ends_with("！") || piece.ends_with("？") ||
            piece.ends_with("؟") || piece.ends_with("۔") || piece.ends_with("।") || piece.ends_with("॥");
        if (IsSentenceEnd(piece) && (spaced || unspacedEnd) &&
            (!piece.ends_with(".") || !IsAbbreviation(tokens, begin, end - 1))) sentence = after;
        else if (end >= begin + budget / 2 && (spaced || piece.ends_with("，") || piece.ends_with("；")) &&
            (piece.ends_with(",") || piece.ends_with(";") || piece.ends_with(":") ||
             piece.ends_with("，") || piece.ends_with("；") || piece.ends_with("،") || piece.ends_with("؛"))) clause = after;
    }
    if (sentence > begin) return sentence;
    if (clause > begin) return clause;
    size_t word = hard;
    while (word > begin + budget / 2 && !std::string_view(tokens[word]).starts_with(WordStart)) --word;
    return word > begin + budget / 2 ? word : hard;
}
}
