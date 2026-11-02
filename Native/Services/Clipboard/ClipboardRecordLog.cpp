#include "../../vnotch_native.h"
#include "../../vnotch_handle_registry.h"
#include <bcrypt.h>
#include <array>
#include <memory>
#include <mutex>
#include <stdexcept>

#pragma comment(lib, "bcrypt.lib")

namespace {
constexpr uint32_t kPageSize = 4096, kHeaderSize = 3 * kPageSize, kMaxPayload = 256 * 1024 * 1024;
constexpr uint32_t CommitOffset(uint64_t slot) noexcept { return kPageSize * (1 + static_cast<uint32_t>(slot)); }
constexpr uint64_t kMaxLogSize = static_cast<uint64_t>(kMaxPayload) * 4 + kHeaderSize;
constexpr char kMagic[8]{'V','N','C','L','O','G','1',0};
constexpr uint32_t kRecordMagic = 0x31524e56;
void Require(bool success) { if (!success) throw std::runtime_error("Clipboard record I/O or authentication failed"); }
void Check(NTSTATUS status) { Require(status >= 0); }

struct Handle {
    HANDLE value = INVALID_HANDLE_VALUE;
    Handle() = default;
    explicit Handle(HANDLE handle) noexcept : value(handle) {}
    Handle(const Handle&) = delete;
    ~Handle() { Close(); }
    void Close() noexcept { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); value = INVALID_HANDLE_VALUE; }
    explicit operator bool() const noexcept { return value && value != INVALID_HANDLE_VALUE; }
};
struct SecretBuffer {
    std::vector<uint8_t> bytes;
    ~SecretBuffer() { if (!bytes.empty()) SecureZeroMemory(bytes.data(), bytes.size()); }
};
struct Algorithm { BCRYPT_ALG_HANDLE value = nullptr; ~Algorithm() { if (value) BCryptCloseAlgorithmProvider(value, 0); } };
struct Key { BCRYPT_KEY_HANDLE value = nullptr; ~Key() { if (value) BCryptDestroyKey(value); } };
struct DpapiBlob {
    DATA_BLOB value{};
    ~DpapiBlob() { if (value.pbData) { SecureZeroMemory(value.pbData, value.cbData); LocalFree(value.pbData); } }
};
struct Mapping {
    Handle handle;
    uint8_t* data = nullptr;
    Mapping(HANDLE file, uint64_t size, bool write)
        : handle(CreateFileMappingW(file, nullptr, write ? PAGE_READWRITE : PAGE_READONLY,
            static_cast<DWORD>(size >> 32), static_cast<DWORD>(size), nullptr)) {
        Require(static_cast<bool>(handle));
        data = static_cast<uint8_t*>(MapViewOfFile(handle.value, write ? FILE_MAP_WRITE : FILE_MAP_READ, 0, 0, static_cast<SIZE_T>(size)));
        Require(data != nullptr);
    }
    ~Mapping() { if (data) UnmapViewOfFile(data); }
};

#pragma pack(push, 1)
struct Commit {
    uint64_t generation, offset;
    uint32_t size, reserved, checksum, padding;
};
struct Header { char magic[8]; uint32_t version, size; uint8_t file_id[16]; Commit commits[2]; };
struct Record {
    uint32_t magic, length;
    uint64_t generation;
    uint8_t nonce[12], tag[16];
    uint32_t reserved;
};
struct Aad { uint8_t file_id[16]; uint32_t magic, length; uint64_t generation; };
#pragma pack(pop)
static_assert(sizeof(Commit) == 32 && sizeof(Header) == 96 && sizeof(Record) == 48 && sizeof(Aad) == 32);

uint32_t Checksum(const Commit& commit) noexcept {
    uint32_t hash = 2166136261u;
    const auto* bytes = reinterpret_cast<const uint8_t*>(&commit);
    for (size_t i = 0; i < offsetof(Commit, checksum); ++i) hash = (hash ^ bytes[i]) * 16777619u;
    return hash;
}
uint64_t FileSize(HANDLE file) {
    LARGE_INTEGER size{}; Require(GetFileSizeEx(file, &size) && size.QuadPart >= 0);
    return static_cast<uint64_t>(size.QuadPart);
}
std::wstring FullPath(const wchar_t* value, int32_t length) {
    Require(value && length > 0 && length < 32768);
    std::wstring input(value, length); Require(input.find(L'\0') == std::wstring::npos);
    std::array<wchar_t, 512> path_buffer;
    DWORD written = GetFullPathNameW(input.c_str(), static_cast<DWORD>(path_buffer.size()), path_buffer.data(), nullptr);
    Require(written > 0 && written < 32768);
    std::wstring result;
    if (written < path_buffer.size()) result.assign(path_buffer.data(), written);
    else {
        std::vector<wchar_t> large(static_cast<size_t>(written) + 1);
        written = GetFullPathNameW(input.c_str(), static_cast<DWORD>(large.size()), large.data(), nullptr);
        Require(written > 0 && written < large.size());
        result.assign(large.data(), written);
    }
    while (result.size() > 3 && (result.back() == L'\\' || result.back() == L'/')) result.pop_back();
    return result;
}
bool IsLegacyBytes(const uint8_t* bytes, size_t read) {
    if (read >= sizeof(kMagic) && std::memcmp(bytes, kMagic, sizeof(kMagic)) == 0) return false;
    size_t i = read >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
    while (i < read && (bytes[i] == ' ' || bytes[i] == '\r' || bytes[i] == '\n' || bytes[i] == '\t')) ++i;
    Require(i < read && bytes[i] == '{'); // Corrupt binary records must never be treated as a migration.
    return true;
}
bool IsLegacy(HANDLE file, uint64_t size) {
    Require(size > 0);
    std::array<uint8_t, 64> bytes{};
    LARGE_INTEGER start{}; Require(SetFilePointerEx(file, start, nullptr, FILE_BEGIN));
    DWORD read = 0; Require(ReadFile(file, bytes.data(), static_cast<DWORD>(std::min<uint64_t>(bytes.size(), size)), &read, nullptr));
    return IsLegacyBytes(bytes.data(), read);
}
Header ReadHeader(const uint8_t* data, uint64_t size) {
    Require(size >= kHeaderSize && size <= kMaxLogSize);
    Header header{}; std::memcpy(&header, data, offsetof(Header, commits));
    Require(std::memcmp(header.magic, kMagic, sizeof(kMagic)) == 0 && header.version == 1 && header.size == kHeaderSize);
    for (uint64_t i = 0; i < 2; ++i) std::memcpy(&header.commits[i], data + CommitOffset(i), sizeof(Commit));
    return header;
}
Header ReadHeader(const Mapping& map, uint64_t size) { return ReadHeader(map.data, size); }
Commit Latest(const Header& header, uint64_t size) {
    Commit best{};
    for (const auto& candidate : header.commits) {
        if (!candidate.generation || candidate.checksum != Checksum(candidate)) continue;
        if (candidate.generation > best.generation) best = candidate;
    }
    // A valid commit pointing outside the file is corruption, not an uncommitted tail.
    Require(best.generation && best.offset >= kHeaderSize && best.size >= sizeof(Record) &&
        best.offset <= size && best.size <= size - best.offset);
    return best;
}

struct Context {
    std::wstring root;
    std::mutex gate;
    Algorithm algorithm;
    SecretBuffer key_object;
    Key key; // Destroy the CNG key before wiping its backing object.
    bool encrypted_seen = false;
    explicit Context(std::wstring directory) : root(std::move(directory)) {}

    bool HasEncryptedRecords() const {
        std::vector<std::wstring> pending{root};
        while (!pending.empty()) {
            auto directory = std::move(pending.back()); pending.pop_back();
            WIN32_FIND_DATAW item{};
            struct FindGuard { HANDLE value; ~FindGuard() { if (value != INVALID_HANDLE_VALUE) FindClose(value); } };
            FindGuard find{FindFirstFileW((directory + L"\\*").c_str(), &item)};
            if (find.value == INVALID_HANDLE_VALUE) {
                Require(GetLastError() == ERROR_FILE_NOT_FOUND || GetLastError() == ERROR_PATH_NOT_FOUND);
                continue;
            }
            do {
                if (item.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) continue;
                const std::wstring_view name(item.cFileName);
                if (name == L"." || name == L"..") continue;
                const std::wstring path = directory + L"\\" + item.cFileName;
                if (item.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) {
                    if (directory != root || name == L"items" || name == L"staging") pending.push_back(path);
                    continue;
                }
                if (name != L"entry.json" && name != L"content.json") continue;
                Handle file(CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                    nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr));
                Require(static_cast<bool>(file));
                std::array<char, 8> prefix{}; DWORD read = 0;
                Require(ReadFile(file.value, prefix.data(), static_cast<DWORD>(prefix.size()), &read, nullptr));
                if (read == prefix.size() && std::memcmp(prefix.data(), kMagic, sizeof(kMagic)) == 0) return true;
            } while (FindNextFileW(find.value, &item));
            Require(GetLastError() == ERROR_NO_MORE_FILES);
        }
        return false;
    }

    std::wstring Path(const wchar_t* path, int32_t length) const {
        auto full = FullPath(path, length);
        Require(full.size() > root.size() && (root.back() == L'\\' || full[root.size()] == L'\\') &&
            CompareStringOrdinal(root.data(), static_cast<int>(root.size()), full.data(), static_cast<int>(root.size()), TRUE) == CSTR_EQUAL);
        return full;
    }

    void EnsureKey(bool allow_create) {
        if (key.value) return;
        const std::wstring path = root + L"\\.history-key.dpapi";
        Handle file;
        DWORD creation = 0;
        for (int retry = 0; retry < 100; ++retry) {
            file.value = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
            if (file) { creation = ERROR_ALREADY_EXISTS; break; }
            DWORD failure = GetLastError();
            if (failure == ERROR_FILE_NOT_FOUND) {
                Require(allow_create && !encrypted_seen && !HasEncryptedRecords());
                file.value = CreateFileW(path.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_HIDDEN, nullptr);
                if (file) { creation = ERROR_SUCCESS; break; }
                failure = GetLastError();
            }
            if (failure != ERROR_SHARING_VIOLATION && failure != ERROR_FILE_EXISTS) break;
            Sleep(10);
        }
        Require(static_cast<bool>(file));
        SecretBuffer secret; secret.bytes.resize(32);
        static uint8_t entropy_bytes[]{'V','N','o','t','c','h','.','H','i','s','t','o','r','y','.','1'};
        DATA_BLOB entropy{sizeof(entropy_bytes), entropy_bytes};
        if (creation != ERROR_ALREADY_EXISTS) {
            Check(BCryptGenRandom(nullptr, secret.bytes.data(), 32, BCRYPT_USE_SYSTEM_PREFERRED_RNG));
            DATA_BLOB plain{32, secret.bytes.data()}; DpapiBlob protected_key;
            Require(CryptProtectData(&plain, L"VNotch clipboard history", &entropy, nullptr, nullptr,
                CRYPTPROTECT_UI_FORBIDDEN, &protected_key.value));
            DWORD written = 0;
            Require(WriteFile(file.value, protected_key.value.pbData, protected_key.value.cbData, &written, nullptr) &&
                written == protected_key.value.cbData && FlushFileBuffers(file.value));
        } else {
            const uint64_t size = FileSize(file.value);
            if (size == 0) {
                // A crash before DPAPI key publication cannot have committed an encrypted record.
                Require(allow_create && !HasEncryptedRecords());
                file.Close(); Require(DeleteFileW(path.c_str()));
                EnsureKey(true); return;
            }
            Require(size <= 65536);
            std::vector<uint8_t> bytes(static_cast<size_t>(size)); DWORD read = 0;
            Require(ReadFile(file.value, bytes.data(), static_cast<DWORD>(bytes.size()), &read, nullptr) && read == bytes.size());
            DATA_BLOB protected_key{read, bytes.data()}; DpapiBlob plain;
            Require(CryptUnprotectData(&protected_key, nullptr, &entropy, nullptr, nullptr, CRYPTPROTECT_UI_FORBIDDEN, &plain.value));
            Require(plain.value.cbData == 32); std::copy_n(plain.value.pbData, 32, secret.bytes.data());
        }
        // Retryable initialization: release partially-created CNG objects before another attempt.
        if (algorithm.value) { BCryptCloseAlgorithmProvider(algorithm.value, 0); algorithm.value = nullptr; }
        if (!key_object.bytes.empty()) SecureZeroMemory(key_object.bytes.data(), key_object.bytes.size());
        key_object.bytes.clear();
        Check(BCryptOpenAlgorithmProvider(&algorithm.value, BCRYPT_AES_ALGORITHM, nullptr, 0));
        Check(BCryptSetProperty(algorithm.value, BCRYPT_CHAINING_MODE,
            reinterpret_cast<PUCHAR>(const_cast<wchar_t*>(BCRYPT_CHAIN_MODE_GCM)), sizeof(BCRYPT_CHAIN_MODE_GCM), 0));
        DWORD object_size = 0, written = 0;
        Check(BCryptGetProperty(algorithm.value, BCRYPT_OBJECT_LENGTH, reinterpret_cast<PUCHAR>(&object_size), sizeof(object_size), &written, 0));
        BCRYPT_AUTH_TAG_LENGTHS_STRUCT tags{};
        Check(BCryptGetProperty(algorithm.value, BCRYPT_AUTH_TAG_LENGTH, reinterpret_cast<PUCHAR>(&tags), sizeof(tags), &written, 0));
        Require(tags.dwMinLength <= 16 && tags.dwMaxLength >= 16 && (!tags.dwIncrement || (16 - tags.dwMinLength) % tags.dwIncrement == 0));
        key_object.bytes.resize(object_size);
        Check(BCryptGenerateSymmetricKey(algorithm.value, &key.value, key_object.bytes.data(), object_size, secret.bytes.data(), 32, 0));
    }

    void Crypt(bool encrypt, const Header& header, Record& record, const uint8_t* input, uint8_t* output) {
        Aad aad{}; std::copy_n(header.file_id, 16, aad.file_id);
        aad.magic = record.magic; aad.length = record.length; aad.generation = record.generation;
        BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO auth; BCRYPT_INIT_AUTH_MODE_INFO(auth);
        auth.pbNonce = record.nonce; auth.cbNonce = sizeof(record.nonce);
        auth.pbTag = record.tag; auth.cbTag = sizeof(record.tag);
        auth.pbAuthData = reinterpret_cast<PUCHAR>(&aad); auth.cbAuthData = sizeof(aad);
        ULONG written = 0;
        const auto operation = encrypt ? BCryptEncrypt : BCryptDecrypt;
        Check(operation(key.value, const_cast<PUCHAR>(input), record.length, &auth, nullptr, 0, output, record.length, &written, 0));
        Require(written == record.length);
    }
    void ReadRecordBytes(const uint8_t* bytes, const Header& header, const Commit& commit, SecretBuffer& plain) {
        Record record{}; std::memcpy(&record, bytes, sizeof(record));
        Require(record.magic == kRecordMagic && record.generation == commit.generation && record.length > 0 &&
            record.length <= kMaxPayload && static_cast<uint64_t>(record.length) + sizeof(Record) == commit.size);
        plain.bytes.resize(record.length);
        Crypt(false, header, record, bytes + sizeof(Record), plain.bytes.data());
    }
    void ReadRecord(const Mapping& map, const Header& header, const Commit& commit, SecretBuffer& plain) {
        ReadRecordBytes(map.data + commit.offset, header, commit, plain);
    }
    void Append(HANDLE file, Header header, const Commit& previous, const uint8_t* plain, int32_t length) {
        Require(previous.generation < UINT64_MAX);
        const uint64_t offset = previous.generation
            ? (previous.offset + previous.size + kPageSize - 1) & ~uint64_t{kPageSize - 1} : kHeaderSize;
        const uint64_t end = offset + sizeof(Record) + static_cast<uint32_t>(length);
        const uint64_t mapped_size = (end + kPageSize - 1) & ~uint64_t{kPageSize - 1};
        Require(mapped_size <= kMaxLogSize);
        Record record{}; record.magic = kRecordMagic; record.length = length; record.generation = previous.generation + 1;
        Check(BCryptGenRandom(nullptr, record.nonce, sizeof(record.nonce), BCRYPT_USE_SYSTEM_PREFERRED_RNG));
        std::vector<uint8_t> cipher(static_cast<size_t>(length)); Crypt(true, header, record, plain, cipher.data());
        Mapping map(file, mapped_size, true);
        if (!previous.generation) {
            std::memset(map.data, 0, kHeaderSize);
            std::memcpy(map.data, &header, offsetof(Header, commits));
        }
        std::memcpy(map.data + offset, &record, sizeof(record));
        std::memcpy(map.data + offset + sizeof(record), cipher.data(), cipher.size());
        // Persist ciphertext before publishing its commit. The second slot preserves the prior commit during a torn write.
        Require(FlushViewOfFile(map.data, static_cast<SIZE_T>(mapped_size)) && FlushFileBuffers(file));
        Commit next{record.generation, offset, static_cast<uint32_t>(sizeof(record) + cipher.size()), 0, 0, 0};
        next.checksum = Checksum(next);
        const uint32_t commit_offset = CommitOffset(next.generation & 1);
        std::memcpy(map.data + commit_offset, &next, sizeof(next));
        Require(FlushViewOfFile(map.data + commit_offset, kPageSize) && FlushFileBuffers(file));
    }
    void Replace(const std::wstring& path, const uint8_t* plain, int32_t length) {
        std::array<uint8_t, 16> random{};
        Check(BCryptGenRandom(nullptr, random.data(), static_cast<ULONG>(random.size()), BCRYPT_USE_SYSTEM_PREFERRED_RNG));
        std::wstring temp = path + L".";
        for (const auto byte : random) { temp += L"0123456789abcdef"[byte >> 4]; temp += L"0123456789abcdef"[byte & 15]; }
        temp += L".tmp";
        try {
            {
                Handle file(CreateFileW(temp.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr));
                Require(static_cast<bool>(file));
                Header header{}; std::copy_n(kMagic, sizeof(kMagic), header.magic); header.version = 1; header.size = kHeaderSize;
                Check(BCryptGenRandom(nullptr, header.file_id, sizeof(header.file_id), BCRYPT_USE_SYSTEM_PREFERRED_RNG));
                Append(file.value, header, {}, plain, length);
            }
            Require(MoveFileExW(temp.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH));
        } catch (...) { DeleteFileW(temp.c_str()); throw; }
    }
};
vnotch::HandleRegistry<Context> contexts;
}

VN_EXPORT int32_t __stdcall vn_clipboard_record_open(const wchar_t* root, int32_t length, int64_t* handle) noexcept {
    if (!handle) return VN_ERR_INVALID_ARG; *handle = 0;
    try { *handle = contexts.Add(std::make_unique<Context>(FullPath(root, length))); return VN_OK; }
    catch (...) { return VN_ERR_FAIL; }
}
VN_EXPORT int32_t __stdcall vn_clipboard_record_close(int64_t handle) noexcept { contexts.Remove(handle); return VN_OK; }
VN_EXPORT int32_t __stdcall vn_clipboard_record_migrate(int64_t handle, const wchar_t* path, int32_t length) noexcept {
    try {
        auto context = contexts.Acquire(handle); if (!context) return VN_ERR_INVALID_ARG;
        std::lock_guard lock(context->gate);
        const auto full = context->Path(path, length);
        Handle file(CreateFileW(full.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_DELETE, nullptr,
            OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr));
        Require(static_cast<bool>(file)); const uint64_t size = FileSize(file.value);
        if (!IsLegacy(file.value, size)) { context->encrypted_seen = true; return VN_OK; }
        Require(size <= kMaxPayload);
        SecretBuffer plain; plain.bytes.resize(static_cast<size_t>(size));
        LARGE_INTEGER start{}; Require(SetFilePointerEx(file.value, start, nullptr, FILE_BEGIN));
        DWORD read = 0;
        Require(ReadFile(file.value, plain.bytes.data(), static_cast<DWORD>(size), &read, nullptr) && read == size);
        file.Close();
        context->EnsureKey(true); context->Replace(full, plain.bytes.data(), static_cast<int32_t>(size));
        return VN_OK;
    } catch (...) { return VN_ERR_FAIL; }
}
// Returns 1 for a legacy JSON document; the facade retains its existing JSON reader.
VN_EXPORT int32_t __stdcall vn_clipboard_record_read(int64_t handle, const wchar_t* path, int32_t length,
    uint8_t* output, int32_t capacity, int32_t* written) noexcept {
    if (!written || capacity < 0 || (capacity > 0 && !output)) return VN_ERR_INVALID_ARG; *written = 0;
    try {
        auto context = contexts.Acquire(handle); if (!context) return VN_ERR_INVALID_ARG;
        std::lock_guard lock(context->gate);
        const auto full = context->Path(path, length);
        Handle file(CreateFileW(full.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr));
        if (!file && (GetLastError() == ERROR_FILE_NOT_FOUND || GetLastError() == ERROR_PATH_NOT_FOUND)) return 2;
        Require(static_cast<bool>(file)); const uint64_t size = FileSize(file.value);
        // Read cached header pages once. Mapping/unmapping a tiny JSON document costs
        // more than copying its ciphertext; retain mappings for large payloads only.
        std::array<uint8_t, kHeaderSize> pages;
        DWORD read = 0;
        const auto prefix_size = static_cast<DWORD>(std::min<uint64_t>(size, pages.size()));
        Require(ReadFile(file.value, pages.data(), prefix_size, &read, nullptr) && read == prefix_size);
        if (IsLegacyBytes(pages.data(), read)) return 1;
        context->encrypted_seen = true;
        const auto header = ReadHeader(pages.data(), size); const auto commit = Latest(header, size);
        Require(commit.size > sizeof(Record) && commit.size - sizeof(Record) <= kMaxPayload);
        *written = static_cast<int32_t>(commit.size - sizeof(Record));
        if (capacity < *written) return VN_ERR_BUFFER_TOO_SMALL;
        context->EnsureKey(false); SecretBuffer plain;
        if (commit.size <= 256 * 1024) {
            std::vector<uint8_t> large;
            uint8_t* record = pages.data();
            if (commit.size > pages.size()) { large.resize(commit.size); record = large.data(); }
            LARGE_INTEGER position{}; position.QuadPart = static_cast<LONGLONG>(commit.offset);
            Require(SetFilePointerEx(file.value, position, nullptr, FILE_BEGIN));
            Require(ReadFile(file.value, record, commit.size, &read, nullptr) && read == commit.size);
            context->ReadRecordBytes(record, header, commit, plain);
        } else {
            Mapping map(file.value, size, false); context->ReadRecord(map, header, commit, plain);
        }
        std::copy(plain.bytes.begin(), plain.bytes.end(), output); return VN_OK;
    } catch (...) { return VN_ERR_FAIL; }
}
VN_EXPORT int32_t __stdcall vn_clipboard_record_append(int64_t handle, const wchar_t* path, int32_t path_length,
    const uint8_t* plain, int32_t length) noexcept {
    if (!plain || length <= 0 || static_cast<uint32_t>(length) > kMaxPayload) return VN_ERR_INVALID_ARG;
    try {
        auto context = contexts.Acquire(handle); if (!context) return VN_ERR_INVALID_ARG;
        std::lock_guard lock(context->gate);
        const auto full = context->Path(path, path_length);
        Handle file(CreateFileW(full.c_str(), GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr));
        if (!file) {
            Require(GetLastError() == ERROR_FILE_NOT_FOUND); context->EnsureKey(true);
            context->Replace(full, plain, length); return VN_OK;
        }
        const uint64_t size = FileSize(file.value);
        if (IsLegacy(file.value, size)) { file.Close(); context->EnsureKey(true); context->Replace(full, plain, length); return VN_OK; }
        context->encrypted_seen = true;
        context->EnsureKey(false);
        Require(size <= kMaxLogSize);
        Header header{}; Commit commit{};
        {
            Mapping map(file.value, size, false); header = ReadHeader(map, size); commit = Latest(header, size);
            SecretBuffer verified; context->ReadRecord(map, header, commit, verified);
        }
        if (size > std::max<uint64_t>(256 * 1024, static_cast<uint64_t>(length) * 4 + kHeaderSize)) {
            file.Close(); context->Replace(full, plain, length);
        } else context->Append(file.value, header, commit, plain, length);
        return VN_OK;
    } catch (...) { return VN_ERR_FAIL; }
}
