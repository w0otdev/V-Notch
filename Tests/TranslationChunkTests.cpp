#include "../Native/Services/Translation/LiteTranslationChunks.h"
#include <array>
#include <cstdio>
#include <string_view>
#include <vector>
using namespace std::literals;
using vnotch::translation::FindChunkEnd;

int main() {
    int checks = 0;
    auto check = [&](bool ok) { ++checks; if (!ok) { std::printf("Chunk check %d failed\n", checks); return false; } return true; };
    constexpr std::array sentence = {"▁First"sv, "▁sentence"sv, "."sv, "▁Next"sv, "▁sentence"sv, "▁continues"sv, "."sv};
    if (!check(FindChunkEnd(sentence, 0, 5) == 3)) return 1;
    if (!check(FindChunkEnd(sentence, 3, 5) == sentence.size())) return 1;
    constexpr std::array quote = {"▁Hello"sv, "."sv, "”"sv, "▁Next"sv, "▁words"sv};
    if (!check(FindChunkEnd(quote, 0, 4) == 3)) return 1;
    constexpr std::array decimal = {"▁Cost"sv, "▁3"sv, "."sv, "14"sv, "▁euros"sv, "▁today"sv, "."sv};
    if (!check(FindChunkEnd(decimal, 0, 5) == 5)) return 1;
    constexpr std::array abbreviation = {"▁Call"sv, "▁Dr"sv, "."sv, "▁Smith"sv, "▁tomorrow"sv, "."sv};
    if (!check(FindChunkEnd(abbreviation, 0, 4) == 4)) return 1;
    constexpr std::array initial = {"▁Tell"sv, "▁A"sv, "."sv, "▁Smith"sv, "▁today"sv, "."sv};
    if (!check(FindChunkEnd(initial, 0, 4) == 4)) return 1;
    constexpr std::array cjk = {"今日は"sv, "晴れ"sv, "。"sv, "明日"sv, "も"sv, "晴れ"sv};
    if (!check(FindChunkEnd(cjk, 0, 4) == 3)) return 1;
    constexpr std::array arabic = {"▁مرحبا"sv, "؟"sv, "▁مرحبا"sv, "▁بك"sv};
    if (!check(FindChunkEnd(arabic, 0, 3) == 2)) return 1;
    constexpr std::array indic = {"▁नमस्ते"sv, "।"sv, "▁फिर"sv, "▁मिलेंगे"sv};
    if (!check(FindChunkEnd(indic, 0, 3) == 2)) return 1;
    constexpr std::array clause = {"▁First"sv, "▁part"sv, ","sv, "▁another"sv, "▁part"sv, "▁continues"sv};
    if (!check(FindChunkEnd(clause, 0, 4) == 3)) return 1;
    constexpr std::array subword = {"▁a"sv, "▁long"sv, "sub"sv, "word"sv, "▁next"sv};
    if (!check(FindChunkEnd(subword, 0, 3) == 3)) return 1;
    if (!check(FindChunkEnd(sentence, sentence.size(), 5) == sentence.size())) return 1;
    if (!check(FindChunkEnd(sentence, 0, 0) == 0)) return 1;
    // Every token is assigned once, in order, even with adversarial punctuation/subwords.
    std::vector<std::string_view> tokens;
    for (int i = 0; i < 100; ++i) tokens.insert(tokens.end(), sentence.begin(), sentence.end());
    for (size_t budget = 1; budget < 40; ++budget) {
        size_t position = 0, consumed = 0;
        while (position < tokens.size()) {
            size_t end = FindChunkEnd(tokens, position, budget);
            if (!check(end > position && end <= tokens.size() && end - position <= budget)) return 1;
            consumed += end - position;
            position = end;
        }
        if (!check(consumed == tokens.size())) return 1;
    }
    std::printf("%d sentence/chunk checks passed\n", checks);
}
