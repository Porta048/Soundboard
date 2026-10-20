#define NOMINMAX
#include <windows.h>
#include <propsys.h>
#include <propvarutil.h>
#include <audioenginebaseapo.h>
#include <sddl.h>
#include <TraceLoggingProvider.h>
#include <algorithm>
#include <cmath>
#include <cstring>
#include <new>
#include <string>
#include "AudioBridge.h"

static volatile LONG objects = 0;
static volatile LONG serverLocks = 0;
// ETW provider "Soundboard.Apo": reports the failing step inside audiodg, where files and consoles are unavailable.
TRACELOGGING_DEFINE_PROVIDER(apoTrace, "Soundboard.Apo", (0x6b1f3c52, 0x8e4a, 0x4d27, 0x9c, 0x3b, 0x51, 0xa0, 0x7e, 0x2d, 0x94, 0xf6));

static HRESULT Report(const char* step, HRESULT status, UINT32 channels = 0, float rate = 0) noexcept {
    TraceLoggingWrite(apoTrace, "Step", TraceLoggingString(step, "step"), TraceLoggingHResult(status, "status"),
        TraceLoggingUInt32(channels, "channels"), TraceLoggingFloat32(rate, "rate"));
    return status;
}

static constexpr GUID FloatFormat = {3,0,0x10,{0x80,0,0,0xaa,0,0x38,0x9b,0x71}};

static bool ReadFormat(IAudioMediaType* media, UNCOMPRESSEDAUDIOFORMAT& format) noexcept {
    return media && SUCCEEDED(media->GetUncompressedAudioFormat(&format)) &&
        format.guidFormatType == FloatFormat && format.dwBytesPerSampleContainer == 4 &&
        format.dwValidBitsPerSample == 32 && format.dwSamplesPerFrame >= 1 &&
        format.dwSamplesPerFrame <= 8 && std::isfinite(format.fFramesPerSecond) &&
        format.fFramesPerSecond >= 8000 && format.fFramesPerSecond <= 192000;
}

static bool SameFormat(const UNCOMPRESSEDAUDIOFORMAT& first, const UNCOMPRESSEDAUDIOFORMAT& second) noexcept {
    return first.dwSamplesPerFrame == second.dwSamplesPerFrame &&
        first.fFramesPerSecond == second.fFramesPerSecond && first.dwChannelMask == second.dwChannelMask;
}

static HRESULT SupportedFormat(IAudioMediaType* opposite, IAudioMediaType* requested, IAudioMediaType** supported) noexcept {
    if (!supported) return E_POINTER;
    *supported = nullptr;
    UNCOMPRESSEDAUDIOFORMAT format{}, other{};
    if (!ReadFormat(requested, format)) return Report("FormatRequested", APOERR_FORMAT_NOT_SUPPORTED, format.dwSamplesPerFrame, format.fFramesPerSecond);
    if (opposite && (!ReadFormat(opposite, other) || !SameFormat(format, other))) return Report("FormatOpposite", APOERR_FORMAT_NOT_SUPPORTED, other.dwSamplesPerFrame, other.fFramesPerSecond);
    Report("FormatAccepted", S_OK, format.dwSamplesPerFrame, format.fFramesPerSecond);
    requested->AddRef();
    *supported = requested;
    return S_OK;
}

// Inner IUnknown: the audio engine creates APOs aggregated, so identity and lifetime belong to the outer object.
struct INonDelegatingUnknown {
    virtual HRESULT STDMETHODCALLTYPE NonDelegatingQueryInterface(REFIID iid, void** result) = 0;
    virtual ULONG STDMETHODCALLTYPE NonDelegatingAddRef() = 0;
    virtual ULONG STDMETHODCALLTYPE NonDelegatingRelease() = 0;
};

class SoundboardApo final : public INonDelegatingUnknown, public IAudioProcessingObject, public IAudioProcessingObjectRT,
    public IAudioProcessingObjectConfiguration, public IAudioSystemEffects2 {
    volatile LONG references = 1;
    IUnknown* outer;
    HANDLE mapping = nullptr;
    AudioBridgeHeader* bridge = nullptr;
    bool initialized = false;
    bool locked = false;
    UINT32 channels = 0;
    UINT32 maximumFrames = 0;
    double frameStep = 1;
    double cursor = 0;
    LONG64 epoch = -1;

public:
    explicit SoundboardApo(IUnknown* owner) noexcept
        : outer(owner ? owner : reinterpret_cast<IUnknown*>(static_cast<INonDelegatingUnknown*>(this))) { InterlockedIncrement(&objects); }
    ~SoundboardApo() {
        Report("Destroy", S_OK);
        if (bridge) { VirtualUnlock(bridge, BridgeBytes); UnmapViewOfFile(bridge); }
        if (mapping) CloseHandle(mapping);
        InterlockedDecrement(&objects);
    }
    HRESULT STDMETHODCALLTYPE NonDelegatingQueryInterface(REFIID iid, void** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (iid == __uuidof(IUnknown)) { *result = static_cast<INonDelegatingUnknown*>(this); NonDelegatingAddRef(); return S_OK; }
        if (iid == __uuidof(IAudioProcessingObject)) *result = static_cast<IAudioProcessingObject*>(this);
        else if (iid == __uuidof(IAudioProcessingObjectRT)) *result = static_cast<IAudioProcessingObjectRT*>(this);
        else if (iid == __uuidof(IAudioProcessingObjectConfiguration)) *result = static_cast<IAudioProcessingObjectConfiguration*>(this);
        else if (iid == __uuidof(IAudioSystemEffects)) *result = static_cast<IAudioSystemEffects*>(this);
        else if (iid == __uuidof(IAudioSystemEffects2)) *result = static_cast<IAudioSystemEffects2*>(this);
        else { TraceLoggingWrite(apoTrace, "MissingInterface", TraceLoggingGuid(iid, "iid")); return E_NOINTERFACE; }
        AddRef();
        return S_OK;
    }
    ULONG STDMETHODCALLTYPE NonDelegatingAddRef() override { return InterlockedIncrement(&references); }
    ULONG STDMETHODCALLTYPE NonDelegatingRelease() override {
        const auto remaining = InterlockedDecrement(&references);
        if (!remaining) delete this;
        return remaining;
    }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** result) override { return outer->QueryInterface(iid, result); }
    ULONG STDMETHODCALLTYPE AddRef() override { return outer->AddRef(); }
    ULONG STDMETHODCALLTYPE Release() override { return outer->Release(); }
    HRESULT STDMETHODCALLTYPE Reset() override { epoch = -1; return S_OK; }
    HRESULT STDMETHODCALLTYPE GetEffectsList(LPGUID* effects, UINT* count, HANDLE) override {
        Report("GetEffectsList", S_OK);
        if (!effects || !count) return E_POINTER;
        *count = 0;
        *effects = static_cast<GUID*>(CoTaskMemAlloc(sizeof(GUID)));
        if (!*effects) return E_OUTOFMEMORY;
        **effects = SoundboardApoClsid;
        *count = 1;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetLatency(HNSTIME* value) override {
        if (!value) return E_POINTER;
        *value = 0;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetRegistrationProperties(APO_REG_PROPERTIES** result) override {
        Report("GetRegistrationProperties", S_OK);
        if (!result) return E_POINTER;
        *result = nullptr;
        auto properties = static_cast<APO_REG_PROPERTIES*>(CoTaskMemAlloc(sizeof(APO_REG_PROPERTIES)));
        if (!properties) return E_OUTOFMEMORY;
        ZeroMemory(properties, sizeof(APO_REG_PROPERTIES));
        properties->clsid = SoundboardApoClsid;
        // Same registration as working stream effects: separate input and output buffers, one declared interface
        properties->Flags = APO_FLAG_DEFAULT;
        wcscpy_s(properties->szFriendlyName, L"Soundboard microphone mixer");
        properties->u32MajorVersion = 1;
        properties->u32MinInputConnections = properties->u32MaxInputConnections = 1;
        properties->u32MinOutputConnections = properties->u32MaxOutputConnections = 1;
        properties->u32MaxInstances = 0xffffffff;
        properties->u32NumAPOInterfaces = 1;
        properties->iidAPOInterfaceList[0] = __uuidof(IAudioProcessingObject);
        *result = properties;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE Initialize(UINT32 bytes, BYTE* data) override {
        if (initialized) return APOERR_ALREADY_INITIALIZED;
        if (!data || bytes < sizeof(APOInitSystemEffects)) return Report("InitializeSize", E_INVALIDARG, bytes);
        const auto settings = reinterpret_cast<APOInitSystemEffects*>(data);
        if (bytes >= sizeof(APOInitSystemEffects2) && reinterpret_cast<APOInitSystemEffects2*>(data)->InitializeForDiscoveryOnly) {
            initialized = true;
            return S_OK;
        }
        if (!settings->pAPOSystemEffectsProperties) return Report("PropertyStore", E_INVALIDARG);
        PROPVARIANT channel{}, owner{};
        const auto store = settings->pAPOSystemEffectsProperties;
        HRESULT result = store->GetValue(BridgeChannelKey, &channel);
        if (SUCCEEDED(result)) result = store->GetValue(BridgeOwnerKey, &owner);
        if (SUCCEEDED(result) && (channel.vt != VT_LPWSTR || owner.vt != VT_LPWSTR ||
            !channel.pwszVal || !owner.pwszVal)) result = E_INVALIDARG;
        if (SUCCEEDED(result)) {
            try { result = OpenBridge(channel.pwszVal, owner.pwszVal); }
            catch (const std::bad_alloc&) { result = E_OUTOFMEMORY; }
        }
        PropVariantClear(&channel);
        PropVariantClear(&owner);
        if (SUCCEEDED(result)) initialized = true;
        else {
            if (bridge) { VirtualUnlock(bridge, BridgeBytes); UnmapViewOfFile(bridge); bridge = nullptr; }
            if (mapping) { CloseHandle(mapping); mapping = nullptr; }
        }
        return Report("Initialize", result);
    }
    HRESULT OpenBridge(const wchar_t* name, const wchar_t* sidText) {
        if (wcslen(name) > 220 || (wcsncmp(name, L"Global\\Soundboard.Audio.v1.", 27) != 0 &&
            wcsncmp(name, L"Local\\Soundboard.Audio.v1.", 26) != 0)) return Report("ChannelName", E_INVALIDARG);
        PSID sid = nullptr;
        if (!ConvertStringSidToSidW(sidText, &sid)) return Report("OwnerSid", E_INVALIDARG);
        LocalFree(sid);
        const std::wstring descriptor = std::wstring(L"D:P(A;;GA;;;SY)(A;;GA;;;LS)(A;;GA;;;") + sidText + L")";
        PSECURITY_DESCRIPTOR security = nullptr;
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(descriptor.c_str(), SDDL_REVISION_1, &security, nullptr)) return Report("SecurityDescriptor", HRESULT_FROM_WIN32(GetLastError()));
        SECURITY_ATTRIBUTES attributes{sizeof(SECURITY_ATTRIBUTES), security, FALSE};
        mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, &attributes, PAGE_READWRITE, 0, static_cast<DWORD>(BridgeBytes), name);
        const auto error = GetLastError();
        LocalFree(security);
        if (!mapping) return Report("CreateFileMapping", HRESULT_FROM_WIN32(error));
        bridge = static_cast<AudioBridgeHeader*>(MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, BridgeBytes));
        if (!bridge) return Report("MapViewOfFile", HRESULT_FROM_WIN32(GetLastError()));
        if (!VirtualLock(bridge, BridgeBytes)) return Report("VirtualLock", HRESULT_FROM_WIN32(GetLastError()));
        if (error != ERROR_ALREADY_EXISTS) {
            ZeroMemory(bridge, BridgeBytes);
            LARGE_INTEGER frequency;
            QueryPerformanceFrequency(&frequency);
            bridge->frequency = frequency.QuadPart;
            bridge->capacity = BridgeFrames;
            bridge->version = 1;
            bridge->gain = 800;
            MemoryBarrier();
            bridge->magic = BridgeMagic;
        }
        else {
            for (UINT32 attempt = 0; !bridge->magic && attempt < 100; ++attempt) Sleep(1);
            MemoryBarrier();
        }
        if (bridge->magic != BridgeMagic || bridge->version != 1 || bridge->capacity != BridgeFrames) return Report("BridgeLayout", E_INVALIDARG);
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE IsInputFormatSupported(IAudioMediaType* opposite, IAudioMediaType* requested, IAudioMediaType** supported) override {
        return SupportedFormat(opposite, requested, supported);
    }
    HRESULT STDMETHODCALLTYPE IsOutputFormatSupported(IAudioMediaType* opposite, IAudioMediaType* requested, IAudioMediaType** supported) override {
        return SupportedFormat(opposite, requested, supported);
    }
    HRESULT STDMETHODCALLTYPE GetInputChannelCount(UINT32* count) override {
        Report("GetInputChannelCount", S_OK, channels);
        if (!count) return E_POINTER;
        if (!initialized) return APOERR_NOT_INITIALIZED;
        *count = channels;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE LockForProcess(UINT32 inputs, APO_CONNECTION_DESCRIPTOR** input, UINT32 outputs, APO_CONNECTION_DESCRIPTOR** output) override {
        if (!initialized || !bridge) return Report("LockNotInitialized", APOERR_NOT_INITIALIZED);
        if (locked) return APOERR_APO_LOCKED;
        if (inputs != 1 || outputs != 1 || !input || !output || !input[0] || !output[0]) return Report("LockConnections", E_INVALIDARG, inputs);
        UNCOMPRESSEDAUDIOFORMAT inFormat{}, outFormat{};
        if (!ReadFormat(input[0]->pFormat, inFormat) || !ReadFormat(output[0]->pFormat, outFormat) || !SameFormat(inFormat, outFormat)) return Report("LockFormat", APOERR_FORMAT_NOT_SUPPORTED, inFormat.dwSamplesPerFrame, inFormat.fFramesPerSecond);
        channels = inFormat.dwSamplesPerFrame;
        frameStep = 48000.0 / inFormat.fFramesPerSecond;
        maximumFrames = std::min(input[0]->u32MaxFrameCount, output[0]->u32MaxFrameCount);
        if (!maximumFrames) return Report("LockFrames", E_INVALIDARG);
        epoch = -1;
        locked = true;
        return Report("LockForProcess", S_OK, channels, inFormat.fFramesPerSecond);
    }
    HRESULT STDMETHODCALLTYPE UnlockForProcess() override { locked = false; return S_OK; }
    UINT32 STDMETHODCALLTYPE CalcInputFrames(UINT32 frames) override { return frames; }
    UINT32 STDMETHODCALLTYPE CalcOutputFrames(UINT32 frames) override { return frames; }
    void STDMETHODCALLTYPE APOProcess(UINT32 inputs, APO_CONNECTION_PROPERTY** input, UINT32 outputs, APO_CONNECTION_PROPERTY** output) override {
        if (outputs != 1 || !output || !output[0]) return;
        auto out = output[0];
        if (!locked || inputs != 1 || !input || !input[0] || input[0]->u32ValidFrameCount > maximumFrames ||
            input[0]->u32BufferFlags == BUFFER_INVALID || !out->pBuffer || !input[0]->pBuffer) {
            out->u32ValidFrameCount = 0;
            out->u32BufferFlags = BUFFER_INVALID;
            return;
        }
        const auto in = input[0];
        const auto frames = in->u32ValidFrameCount;
        auto destination = reinterpret_cast<float*>(out->pBuffer);
        const auto bytes = frames * channels * sizeof(float);
        if (in->u32BufferFlags == BUFFER_SILENT) std::memset(destination, 0, bytes);
        else if (out->pBuffer != in->pBuffer) std::memcpy(destination, reinterpret_cast<void*>(in->pBuffer), bytes);
        out->u32ValidFrameCount = frames;
        out->u32BufferFlags = in->u32BufferFlags;
        LARGE_INTEGER now;
        QueryPerformanceCounter(&now);
        InterlockedExchange64(&bridge->consumerQpc, now.QuadPart);
        const auto frequency = bridge->frequency;
        const auto heartbeat = ReadBridge(&bridge->producerQpc);
        if (frequency <= 0 || heartbeat <= 0 || now.QuadPart < heartbeat ||
            now.QuadPart - heartbeat > frequency || !InterlockedCompareExchange(&bridge->playing, 0, 0)) return;
        const auto generation = ReadBridge(&bridge->epoch);
        const auto start = ReadBridge(&bridge->startQpc);
        const auto base = ReadBridge(&bridge->baseFrame);
        const auto written = ReadBridge(&bridge->writtenFrame);
        const auto end = ReadBridge(&bridge->endFrame);
        if (start <= 0 || start > now.QuadPart || base < 0 || written < base || end < written || end > (1LL << 50)) return;
        const double clockFrame = base + (now.QuadPart - start) * (48000.0 / frequency);
        if (epoch != generation || std::abs(cursor - clockFrame) > 4800) { epoch = generation; cursor = clockFrame; }
        const auto samples = reinterpret_cast<const std::int16_t*>(reinterpret_cast<const BYTE*>(bridge) + sizeof(AudioBridgeHeader));
        const float gain = std::clamp(InterlockedCompareExchange(&bridge->gain, 0, 0), 0L, 1000L) / 32768000.0f;
        bool mixed = false;
        for (UINT32 frame = 0; frame < frames; ++frame, cursor += frameStep) {
            if (cursor < base || cursor >= end || cursor >= written || cursor < written - BridgeFrames) continue;
            const auto source = static_cast<LONG64>(cursor);
            const auto offset = (source % BridgeFrames) * 2;
            const auto next = source + 1 < written ? ((source + 1) % BridgeFrames) * 2 : offset;
            const float fraction = static_cast<float>(cursor - source);
            const float left = (samples[offset] + fraction * (samples[next] - samples[offset])) * gain;
            const float right = (samples[offset + 1] + fraction * (samples[next + 1] - samples[offset + 1])) * gain;
            for (UINT32 channel = 0; channel < std::min(channels, 2U); ++channel) {
                const float value = channels == 1 ? (left + right) * 0.5f : channel == 0 ? left : right;
                destination[frame * channels + channel] = std::clamp(destination[frame * channels + channel] + value, -1.0f, 1.0f);
            }
            mixed = true;
        }
        if (mixed) out->u32BufferFlags = BUFFER_VALID;
    }
};

class Factory final : public IClassFactory {
    volatile LONG references = 1;
public:
    Factory() { InterlockedIncrement(&objects); }
    ~Factory() { InterlockedDecrement(&objects); }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (iid != __uuidof(IUnknown) && iid != __uuidof(IClassFactory)) return E_NOINTERFACE;
        *result = this; AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&references); }
    ULONG STDMETHODCALLTYPE Release() override { const auto count = InterlockedDecrement(&references); if (!count) delete this; return count; }
    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID iid, void** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (outer && iid != __uuidof(IUnknown)) return CLASS_E_NOAGGREGATION;
        auto instance = new (std::nothrow) SoundboardApo(outer);
        if (!instance) return E_OUTOFMEMORY;
        const auto status = instance->NonDelegatingQueryInterface(iid, result);
        instance->NonDelegatingRelease();
        return status;
    }
    HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) override { if (lock) InterlockedIncrement(&serverLocks); else InterlockedDecrement(&serverLocks); return S_OK; }
};

BOOL WINAPI DllMain(HINSTANCE, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) TraceLoggingRegister(apoTrace);
    else if (reason == DLL_PROCESS_DETACH) TraceLoggingUnregister(apoTrace);
    return TRUE;
}
STDAPI DllGetClassObject(REFCLSID clsid, REFIID iid, void** result) {
    if (!result) return E_POINTER;
    *result = nullptr;
    if (clsid != SoundboardApoClsid) return CLASS_E_CLASSNOTAVAILABLE;
    auto factory = new (std::nothrow) Factory();
    if (!factory) return E_OUTOFMEMORY;
    const auto status = factory->QueryInterface(iid, result);
    factory->Release();
    return status;
}
STDAPI DllCanUnloadNow() {
    return !InterlockedCompareExchange(&objects, 0, 0) && !InterlockedCompareExchange(&serverLocks, 0, 0) ? S_OK : S_FALSE;
}
