// Built separately by Tools/TranslationRuntime: the main native DLL loads it on demand.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include "LiteTranslationAbi.h"
#include "LiteTranslationChunks.h"
#include <ctranslate2/translator.h>
#include <sentencepiece_processor.h>
#include <algorithm>
#include <array>
#include <chrono>
#include <filesystem>
#include <fstream>
#include <map>
#include <memory>
#include <mutex>
#include <stdexcept>
#include <string_view>

namespace {
constexpr std::string_view Languages =
    " af am ar ast az ba be bg bn br bs ca ceb cs cy da de el en es et fa ff fi fr fy ga gd gl gu ha he hi hr ht hu hy id ig ilo is it ja jv ka kk km kn ko lb lg ln lo lt lv mg mk ml mn mr ms my ne nl no ns oc or pa pl ps pt ro ru sd si sk sl so sq sr ss su sv sw ta th tl tn tr uk ur uz vi wo xh yi yo zh zu ";
constexpr size_t ChunkTokens = 192;
constexpr size_t BatchChunks = 4;
constexpr size_t OutputTokens = 512;
constexpr size_t MaxInputChars = 16384;

std::string Utf8(std::wstring_view input) {
    if (input.empty()) return {};
    int n = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, input.data(), static_cast<int>(input.size()), nullptr, 0, nullptr, nullptr);
    if (n <= 0) throw std::runtime_error("Invalid Unicode");
    std::string output(n, '\0');
    WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, input.data(), static_cast<int>(input.size()), output.data(), n, nullptr, nullptr);
    return output;
}

std::wstring Utf16(std::string_view input) {
    if (input.empty()) return {};
    int n = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, input.data(), static_cast<int>(input.size()), nullptr, 0);
    if (n <= 0) throw std::runtime_error("Invalid Unicode");
    std::wstring output(n, L'\0');
    MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, input.data(), static_cast<int>(input.size()), output.data(), n);
    return output;
}

bool Cancelled(VnTranslationCancelled callback, intptr_t state) noexcept {
    return callback && callback(state) != 0;
}

struct Engine {
    sentencepiece::SentencePieceProcessor tokenizer;
    std::unique_ptr<ctranslate2::Translator> translator;
    ULONGLONG last_used = GetTickCount64();
    bool natural = true;
    std::vector<std::future<ctranslate2::TranslationResult>> pending;

    int WaitPending(VnTranslationCancelled cancel, intptr_t state) {
        for (auto& work : pending) {
            while (work.valid() && work.wait_for(std::chrono::milliseconds(5)) != std::future_status::ready)
                if (Cancelled(cancel, state)) return VN_TRANSLATION_CANCELLED;
        }
        return Cancelled(cancel, state) ? VN_TRANSLATION_CANCELLED : 0;
    }

    Engine(std::wstring_view root, int threads) {
        std::filesystem::path folder(root);
        std::ifstream input(folder / L"sentencepiece.bpe.model", std::ios::binary);
        if (!input) throw std::runtime_error("Tokenizer missing");
        std::string serialized((std::istreambuf_iterator<char>(input)), {});
        if (!tokenizer.LoadFromSerializedProto(serialized).ok()) throw std::runtime_error("Invalid tokenizer");
        ctranslate2::ReplicaPoolConfig config;
        config.num_threads_per_replica = threads > 0 ? std::clamp(threads, 1, 16) : 4;
        config.max_queued_batches = 1;
        translator = std::make_unique<ctranslate2::Translator>(Utf8(root), ctranslate2::Device::CPU,
            ctranslate2::ComputeType::INT8_FLOAT32, std::vector<int>{0}, false, config);
    }

    int Translate(std::wstring_view input, const std::string& language,
        VnTranslationCancelled cancel, intptr_t state, std::wstring& result) {
        if (Cancelled(cancel, state)) return VN_TRANSLATION_CANCELLED;
        if (Languages.find(" " + language + " ") == std::string_view::npos) return VN_TRANSLATION_LANGUAGE;
        if (input.size() > MaxInputChars) return VN_TRANSLATION_LENGTH;
        // A cancelled beam-search batch owns no caller memory or callback. Let the
        // bounded in-flight job finish before queuing another one on this model.
        if (!pending.empty()) {
            int rc = WaitPending(cancel, state);
            if (rc != 0) return rc;
            pending.clear();
        }
        ctranslate2::TranslationOptions options;
        options.beam_size = natural ? 4 : 1;
        options.return_scores = false;
        options.return_end_token = true;
        options.max_input_length = 0; // Never silently truncate a selection.
        options.max_decoding_length = OutputTokens;
        if (options.beam_size == 1)
            options.callback = [cancel, state](const ctranslate2::GenerationStepResult&) { return Cancelled(cancel, state); };
        // Beam search has no token callback in CTranslate2. Poll its future without
        // blocking cancellation; keep at most four active hypotheses in either mode.
        const size_t batchChunks = natural ? 1 : BatchChunks;
        // Deduplicate identical chunks within this request batch; never retain selected text.
        struct Part { std::wstring before; size_t index; };
        std::vector<std::vector<std::string>> sources;
        std::vector<Part> parts;
        std::wstring separator;
        sources.reserve(BatchChunks);
        result.reserve(input.size());
        auto flush = [&]() -> int {
            if (sources.empty()) return 0;
            if (Cancelled(cancel, state)) return VN_TRANSLATION_CANCELLED;
            std::vector<ctranslate2::TranslationResult> translated;
            if (natural) {
                pending = translator->translate_batch_async(sources, options);
                int rc = WaitPending(cancel, state);
                if (rc != 0) return rc;
                translated.reserve(pending.size());
                for (auto& work : pending) translated.push_back(work.get());
                pending.clear();
            } else translated = translator->translate_batch(sources, options);
            if (Cancelled(cancel, state)) return VN_TRANSLATION_CANCELLED;
            if (translated.size() != sources.size()) return VN_TRANSLATION_MODEL;
            std::vector<std::wstring> decoded;
            decoded.reserve(translated.size());
            for (auto& item : translated) {
                if (item.hypotheses.empty()) return VN_TRANSLATION_MODEL;
                auto& output = item.hypotheses[0];
                if (output.empty() || output.back() != "</s>") return VN_TRANSLATION_LENGTH;
                output.pop_back();
                std::string text;
                if (!tokenizer.Decode(output, &text).ok() || text.empty()) return VN_TRANSLATION_MODEL;
                decoded.push_back(Utf16(text));
            }
            for (const auto& part : parts) {
                result.append(part.before);
                result.append(decoded[part.index]);
            }
            sources.clear();
            parts.clear();
            return 0;
        };
        // Separators stay attached to their original chunk, including blank/indented lines.
        size_t position = 0;
        while (position < input.size()) {
            if (Cancelled(cancel, state)) return VN_TRANSLATION_CANCELLED;
            size_t end = input.find_first_of(L"\r\n", position);
            if (end == std::wstring_view::npos) end = input.size();
            auto line = input.substr(position, end - position);
            size_t first = line.find_first_not_of(L" \t");
            if (first == std::wstring_view::npos) separator.append(line);
            else {
                size_t last = line.find_last_not_of(L" \t") + 1;
                separator.append(line.substr(0, first));
                std::vector<std::string> tokens;
                if (!tokenizer.Encode(Utf8(line.substr(first, last - first)), &tokens).ok()) return VN_TRANSLATION_MODEL;
                for (size_t begin = 0; begin < tokens.size();) {
                    if (Cancelled(cancel, state)) return VN_TRANSLATION_CANCELLED;
                    size_t stop = vnotch::translation::FindChunkEnd(tokens, begin, ChunkTokens);
                    std::vector<std::string> source;
                    source.reserve(stop - begin + 2);
                    // SMaLL-100 specifies the TARGET on the source, with no forced decoder prefix.
                    source.emplace_back("__" + language + "__");
                    source.insert(source.end(), tokens.begin() + begin, tokens.begin() + stop);
                    source.emplace_back("</s>");
                    size_t index = static_cast<size_t>(std::find(sources.begin(), sources.end(), source) - sources.begin());
                    if (index == sources.size()) {
                        if (sources.size() == batchChunks) {
                            int rc = flush();
                            if (rc != 0) return rc;
                        }
                        index = sources.size();
                        sources.push_back(std::move(source));
                    }
                    parts.push_back({std::move(separator), index});
                    separator.clear();
                    begin = stop;
                    if (begin < tokens.size() && language != "zh" && language != "ja") separator.push_back(L' ');
                }
                separator.append(line.substr(last));
            }
            position = end;
            while (position < input.size() && (input[position] == L'\r' || input[position] == L'\n'))
                separator.push_back(input[position++]);
        }
        int rc = flush();
        if (rc != 0) return rc;
        result.append(separator);
        return Cancelled(cancel, state) ? VN_TRANSLATION_CANCELLED : 0;
    }
};

std::mutex gate;
std::map<int64_t, std::unique_ptr<Engine>> engines;
int64_t next_handle = 0;
}

extern "C" __declspec(dllexport) int32_t __stdcall vn_lite_open(
    const wchar_t* root, int32_t length, int32_t threads, int64_t* handle) noexcept {
    if (!root || length <= 0 || !handle) return -1;
    *handle = 0;
    try {
        auto engine = std::make_unique<Engine>(std::wstring_view(root, length), threads);
        // Exercise decoding too: prepacking INT8 weights must not delay the first selection.
        std::wstring warmup;
        int rc = engine->Translate(L"Hello.", "fr", nullptr, 0, warmup);
        if (rc != 0) return rc;
        engine->last_used = GetTickCount64();
        std::lock_guard lock(gate);
        *handle = ++next_handle;
        engines.emplace(*handle, std::move(engine));
        return 0;
    } catch (...) { return VN_TRANSLATION_MODEL; }
}

extern "C" __declspec(dllexport) int32_t __stdcall vn_lite_translate(
    int64_t handle, const wchar_t* text, int32_t length, const wchar_t* target, int32_t target_length,
    VnTranslationCancelled cancel, intptr_t state, wchar_t* output, int32_t capacity, int32_t* written) noexcept {
    if (!text || length < 0 || !target || target_length <= 0 || target_length > 8 || !written || capacity < 0 || (!output && capacity)) return -1;
    *written = 0;
    try {
        std::lock_guard lock(gate);
        auto entry = engines.find(handle);
        if (entry == engines.end()) return -1;
        std::wstring translated;
        int rc = entry->second->Translate(std::wstring_view(text, length), Utf8(std::wstring_view(target, target_length)), cancel, state, translated);
        entry->second->last_used = GetTickCount64();
        if (rc != 0) return rc;
        *written = static_cast<int32_t>(translated.size());
        if (translated.size() > static_cast<size_t>(capacity)) return -2;
        std::copy(translated.begin(), translated.end(), output);
        return 0;
    } catch (...) { return -3; }
}

extern "C" __declspec(dllexport) int32_t __stdcall vn_lite_close(int64_t handle) noexcept {
    try {
        std::lock_guard lock(gate);
        engines.erase(handle);
        return 0;
    } catch (...) { return -3; }
}

extern "C" __declspec(dllexport) int32_t __stdcall vn_lite_set_natural(int64_t handle, uint8_t enabled) noexcept {
    try {
        std::lock_guard lock(gate);
        auto entry = engines.find(handle);
        if (entry == engines.end()) return -1;
        entry->second->natural = enabled != 0;
        return 0;
    } catch (...) { return -3; }
}

extern "C" __declspec(dllexport) int32_t __stdcall vn_lite_release_idle(
    int64_t handle, int64_t idle_ms, uint8_t* released) noexcept {
    if (!released || idle_ms < 0) return -1;
    *released = 0;
    if (idle_ms == 0) return 0; // The existing retention setting uses zero for "keep loaded".
    try {
        // A cleanup timer must never wait behind a foreground translation.
        std::unique_lock lock(gate, std::try_to_lock);
        if (!lock.owns_lock()) return 0;
        auto entry = engines.find(handle);
        if (entry == engines.end()) return -1;
        for (auto& work : entry->second->pending)
            if (work.valid() && work.wait_for(std::chrono::milliseconds(0)) != std::future_status::ready) return 0;
        if (GetTickCount64() - entry->second->last_used >= static_cast<ULONGLONG>(idle_ms)) {
            engines.erase(entry);
            *released = 1;
        }
        return 0;
    } catch (...) { return -3; }
}
