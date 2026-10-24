#include "../vnotch_common.h"
#include <objbase.h>
#include <mmdeviceapi.h>
#include <string>
#include <cstdint>

#pragma comment(lib, "ole32.lib")

// Default audio endpoint switching through the undocumented IPolicyConfig COM interface.
// Only the vtable layout matters; unused methods keep opaque pointer parameters.

namespace vnotch::services::audio {

constexpr size_t kMaxDeviceIdChars = 512;

// {870af99c-171d-4f9e-af0d-e63df40c2bc9}
constexpr CLSID kPolicyConfigClient = {0x870af99c, 0x171d, 0x4f9e, {0xaf, 0x0d, 0xe6, 0x3d, 0xf4, 0x0c, 0x2b, 0xc9}};
// {f8679f50-850a-41cf-9c72-430f290290c8}
constexpr IID kPolicyConfigIid = {0xf8679f50, 0x850a, 0x41cf, {0x9c, 0x72, 0x43, 0x0f, 0x29, 0x02, 0x90, 0xc8}};

struct IPolicyConfig : IUnknown {
    virtual HRESULT STDMETHODCALLTYPE GetMixFormat(PCWSTR, void*) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetDeviceFormat(PCWSTR, INT, void*) = 0;
    virtual HRESULT STDMETHODCALLTYPE ResetDeviceFormat(PCWSTR) = 0;
    virtual HRESULT STDMETHODCALLTYPE SetDeviceFormat(PCWSTR, void*, void*) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetProcessingPeriod(PCWSTR, INT, void*, void*) = 0;
    virtual HRESULT STDMETHODCALLTYPE SetProcessingPeriod(PCWSTR, void*) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetShareMode(PCWSTR, void*) = 0;
    virtual HRESULT STDMETHODCALLTYPE SetShareMode(PCWSTR, void*) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetPropertyValue(PCWSTR, INT, void*, void*) = 0;
    virtual HRESULT STDMETHODCALLTYPE SetPropertyValue(PCWSTR, INT, void*, void*) = 0;
    virtual HRESULT STDMETHODCALLTYPE SetDefaultEndpoint(PCWSTR, ERole) = 0;
    virtual HRESULT STDMETHODCALLTYPE SetEndpointVisibility(PCWSTR, INT) = 0;
};

struct ComScope {
    bool initialized;
    ComScope() noexcept : initialized(SUCCEEDED(CoInitializeEx(nullptr, COINIT_MULTITHREADED))) {}
    ComScope(const ComScope&) = delete;
    ComScope& operator=(const ComScope&) = delete;
    ~ComScope() {
        if (initialized) CoUninitialize();
    }
};

} // namespace vnotch::services::audio

extern "C" {

// Sets the default endpoint for the console, multimedia and communications roles. Each role's HRESULT is
// returned; VN_OK means the multimedia role changed (the other roles may still have failed).
VN_EXPORT int32_t __stdcall vn_audio_set_default_endpoint(
    const char16_t* device_id, int32_t device_id_length,
    int32_t* out_console_hr, int32_t* out_multimedia_hr, int32_t* out_communications_hr) noexcept {
    namespace audio = vnotch::services::audio;
    if (!device_id || device_id_length <= 0 || static_cast<size_t>(device_id_length) >= audio::kMaxDeviceIdChars ||
        !out_console_hr || !out_multimedia_hr || !out_communications_hr)
        return VN_ERR_INVALID_ARG;
    *out_console_hr = *out_multimedia_hr = *out_communications_hr = static_cast<int32_t>(E_FAIL);

    std::wstring id(reinterpret_cast<const wchar_t*>(device_id), static_cast<size_t>(device_id_length));
    audio::ComScope com;
    audio::IPolicyConfig* policy = nullptr;
    const HRESULT created = CoCreateInstance(audio::kPolicyConfigClient, nullptr, CLSCTX_ALL, audio::kPolicyConfigIid,
                                             reinterpret_cast<void**>(&policy));
    if (FAILED(created) || policy == nullptr) {
        *out_console_hr = *out_multimedia_hr = *out_communications_hr = static_cast<int32_t>(created);
        return VN_ERR_FAIL;
    }
    *out_console_hr = static_cast<int32_t>(policy->SetDefaultEndpoint(id.c_str(), eConsole));
    *out_multimedia_hr = static_cast<int32_t>(policy->SetDefaultEndpoint(id.c_str(), eMultimedia));
    *out_communications_hr = static_cast<int32_t>(policy->SetDefaultEndpoint(id.c_str(), eCommunications));
    policy->Release();
    return *out_multimedia_hr >= 0 ? VN_OK : VN_ERR_FAIL;
}

} // extern "C"
