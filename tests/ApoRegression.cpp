#define NOMINMAX
#include <windows.h>
#include <propsys.h>
#include <propvarutil.h>
#include <audioenginebaseapo.h>
#include <sddl.h>
#include <wrl/client.h>
#include <algorithm>
#include <atomic>
#include <cmath>
#include <iostream>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>
#include "../native/AudioBridge.h"

using Microsoft::WRL::ComPtr;

static void Require(bool condition, const char* message) {
    if (!condition) throw std::runtime_error(message);
}
static void Check(HRESULT status, const char* message) {
    if (FAILED(status)) { std::cerr << "HRESULT: " << std::hex << status << std::dec << '\n'; throw std::runtime_error(message); }
}
static void SetText(IPropertyStore* store, REFPROPERTYKEY key, const wchar_t* value) {
    PROPVARIANT text{};
    Check(InitPropVariantFromString(value, &text), "Proprieta test");
    const auto status = store->SetValue(key, text);
    PropVariantClear(&text);
    Check(status, "Scrittura proprieta test");
}
static std::wstring OwnerSid() {
    HANDLE token = nullptr;
    Require(OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token), "Token test");
    DWORD bytes = 0;
    GetTokenInformation(token, TokenUser, nullptr, 0, &bytes);
    std::vector<BYTE> buffer(bytes);
    const auto read = GetTokenInformation(token, TokenUser, buffer.data(), bytes, &bytes);
    CloseHandle(token);
    Require(read, "SID test");
    LPWSTR text = nullptr;
    Require(ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(buffer.data())->User.Sid, &text), "Formato SID test");
    std::wstring sid(text);
    LocalFree(text);
    return sid;
}
// Minimal outer object, as the Windows audio engine aggregates APOs.
class Outer final : public IUnknown {
    LONG references = 1;
public:
    ComPtr<IUnknown> inner;
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** result) override {
        if (iid == __uuidof(IUnknown)) { *result = this; AddRef(); return S_OK; }
        return inner->QueryInterface(iid, result);
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&references); }
    ULONG STDMETHODCALLTYPE Release() override { const auto count = InterlockedDecrement(&references); if (!count) delete this; return count; }
};
static void CheckAggregation(IClassFactory* factory) {
    auto outer = new Outer();
    ComPtr<IAudioProcessingObject> wrong;
    Require(factory->CreateInstance(outer, IID_PPV_ARGS(&wrong)) == CLASS_E_NOAGGREGATION, "Aggregazione senza IUnknown rifiutata");
    Check(factory->CreateInstance(outer, IID_PPV_ARGS(&outer->inner)), "Creazione APO aggregato");
    ComPtr<IAudioProcessingObject> apo;
    Check(outer->QueryInterface(IID_PPV_ARGS(&apo)), "Interfaccia APO tramite oggetto esterno");
    ComPtr<IUnknown> identity;
    Check(apo->QueryInterface(IID_PPV_ARGS(&identity)), "Identita APO aggregato");
    Require(identity.Get() == outer, "Identita delegata all'oggetto esterno");
    identity.Reset();
    apo.Reset();
    outer->inner.Reset();
    Require(outer->Release() == 0, "Rilascio APO aggregato");
}
static ComPtr<IAudioProcessingObject> CreateApo(IClassFactory* factory, const std::wstring& channel) {
    ComPtr<IAudioProcessingObject> apo;
    Check(factory->CreateInstance(nullptr, IID_PPV_ARGS(&apo)), "Creazione APO");
    Require(apo->Initialize(0, nullptr) == E_INVALIDARG, "Inizializzazione non valida rifiutata");
    ComPtr<IPropertyStore> store;
    Check(PSCreateMemoryPropertyStore(IID_PPV_ARGS(&store)), "Property store");
    SetText(store.Get(), BridgeChannelKey, channel.c_str());
    SetText(store.Get(), BridgeOwnerKey, OwnerSid().c_str());
    APOInitSystemEffects initialization{};
    initialization.APOInit.cbSize = sizeof(initialization);
    initialization.APOInit.clsid = SoundboardApoClsid;
    initialization.pAPOSystemEffectsProperties = store.Get();
    Check(apo->Initialize(sizeof(initialization), reinterpret_cast<BYTE*>(&initialization)), "Inizializzazione APO e memoria condivisa");
    return apo;
}
static void Lock(IAudioProcessingObject* apo, UINT32 channels, float rate) {
    const GUID floating = {3,0,0x10,{0x80,0,0,0xaa,0,0x38,0x9b,0x71}};
    UNCOMPRESSEDAUDIOFORMAT format{floating, channels, 4, 32, rate, channels == 1 ? 4U : 3U};
    ComPtr<IAudioMediaType> media;
    Check(CreateAudioMediaTypeFromUncompressedAudioFormat(&format, &media), "Formato media test");
    APO_CONNECTION_DESCRIPTOR input{}, output{};
    input.u32MaxFrameCount = output.u32MaxFrameCount = 480;
    input.pFormat = output.pFormat = media.Get();
    auto in = &input;
    auto out = &output;
    ComPtr<IAudioProcessingObjectConfiguration> config;
    Check(apo->QueryInterface(IID_PPV_ARGS(&config)), "Interfaccia configurazione");
    Check(config->LockForProcess(1, &in, 1, &out), "Lock formato mono/stereo");
}
static std::vector<float> Process(IAudioProcessingObject* apo, UINT32 channels, float voice, APO_BUFFER_FLAGS flags = BUFFER_VALID) {
    std::vector<float> input(480 * channels, voice), output(480 * channels, -9);
    APO_CONNECTION_PROPERTY in{reinterpret_cast<UINT_PTR>(input.data()), 480, flags, 0};
    APO_CONNECTION_PROPERTY out{reinterpret_cast<UINT_PTR>(output.data()), 0, BUFFER_INVALID, 0};
    auto inPointer = &in;
    auto outPointer = &out;
    ComPtr<IAudioProcessingObjectRT> realtime;
    Check(apo->QueryInterface(IID_PPV_ARGS(&realtime)), "Interfaccia realtime");
    realtime->APOProcess(1, &inPointer, 1, &outPointer);
    Require(out.u32ValidFrameCount == 480, "Numero frame conservato");
    if (flags == BUFFER_INVALID) Require(out.u32BufferFlags == BUFFER_INVALID, "Flag non valido conservato");
    return output;
}
static void Prime(AudioBridgeHeader* bridge) {
    auto samples = reinterpret_cast<std::int16_t*>(reinterpret_cast<BYTE*>(bridge) + sizeof(AudioBridgeHeader));
    for (LONG64 frame = 0; frame < BridgeFrames; ++frame) {
        samples[frame * 2] = 8192;
        samples[frame * 2 + 1] = 4096;
    }
    LARGE_INTEGER now;
    QueryPerformanceCounter(&now);
    bridge->playing = 0;
    bridge->baseFrame = 0;
    bridge->writtenFrame = BridgeFrames;
    bridge->endFrame = BridgeFrames;
    bridge->startQpc = now.QuadPart;
    bridge->producerQpc = now.QuadPart;
    bridge->gain = 1000;
    InterlockedIncrement64(&bridge->epoch);
    bridge->playing = 1;
}

int wmain(int argc, wchar_t** argv) {
    try {
        Require(argc >= 2, "Specificare la DLL");
        Check(CoInitializeEx(nullptr, COINIT_MULTITHREADED), "COM test");
        const auto module = LoadLibraryW(argv[1]);
        Require(module != nullptr, "Caricamento DLL");
        const auto getFactory = reinterpret_cast<LPFNGETCLASSOBJECT>(GetProcAddress(module, "DllGetClassObject"));
        const auto canUnload = reinterpret_cast<LPFNCANUNLOADNOW>(GetProcAddress(module, "DllCanUnloadNow"));
        Require(getFactory && canUnload && canUnload() == S_OK, "Esportazioni COM");
        ComPtr<IClassFactory> factory;
        Check(getFactory(SoundboardApoClsid, IID_PPV_ARGS(&factory)), "Factory COM");
        CheckAggregation(factory.Get());
        const bool serve = argc >= 4 && std::wstring(argv[2]) == L"--serve";
        const auto channel = serve ? std::wstring(argv[3]) : L"Local\\Soundboard.Audio.v1.test-" + std::to_wstring(GetCurrentProcessId());
        auto apo = CreateApo(factory.Get(), channel);
        Lock(apo.Get(), 1, 44100);
        const auto mapping = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, channel.c_str());
        Require(mapping != nullptr, "Canale IPC accessibile al proprietario");
        auto bridge = static_cast<AudioBridgeHeader*>(MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, BridgeBytes));
        Require(bridge && bridge->magic == BridgeMagic && bridge->capacity == BridgeFrames, "Layout IPC");
        if (serve) {
            std::atomic<bool> quit = false;
            std::atomic<float> meter = 0;
            std::thread processing([&] {
                while (!quit) {
                    const auto samples = Process(apo.Get(), 1, 0);
                    float peak = 0;
                    for (const auto value : samples) peak = std::max(peak, std::abs(value));
                    meter = peak;
                    Sleep(10);
                }
            });
            std::cout << "READY" << std::endl;
            std::string command;
            while (std::getline(std::cin, command) && command != "quit") {
                // .NET writes a UTF-8 BOM before the first line when the console uses code page 65001.
                if (command.rfind("\xEF\xBB\xBF", 0) == 0) command.erase(0, 3);
                if (command == "meter") std::cout << meter.load() << std::endl;
            }
            quit = true;
            processing.join();
        } else {
            Require(Process(apo.Get(), 1, 0.2f).front() == 0.2f, "Voce invariata senza produttore");
            std::cout << "PASS: voce conservata quando Soundboard non riproduce\n";
            Prime(bridge);
            Require(std::abs(Process(apo.Get(), 1, 0.2f).front() - 0.3875f) < 0.00001f, "Voce piu file mono");
            Require(std::abs(Process(apo.Get(), 1, 0, BUFFER_SILENT).front() - 0.1875f) < 0.00001f, "File su ingresso silenzioso");
            std::cout << "PASS: somma voce e file, conversione stereo-mono e ricampionamento 48k/44.1k\n";
            bridge->gain = 0;
            Require(Process(apo.Get(), 1, 0.2f).front() == 0.2f, "Volume zero conserva voce");
            bridge->gain = 1000;
            Require(Process(apo.Get(), 1, 0.95f).front() == 1, "Picchi limitati");
            bridge->playing = 0;
            Require(Process(apo.Get(), 1, 0.2f).front() == 0.2f, "Pausa conserva voce");
            Prime(bridge);
            bridge->producerQpc -= bridge->frequency * 3;
            Require(Process(apo.Get(), 1, 0.2f).front() == 0.2f, "Produttore terminato conserva voce");
            std::cout << "PASS: volume, picchi, pausa e produttore disconnesso\n";
            auto second = CreateApo(factory.Get(), channel);
            Lock(second.Get(), 2, 48000);
            Prime(bridge);
            const auto stereo = Process(second.Get(), 2, 0);
            Require(std::abs(stereo[0] - 0.25f) < 0.00001f && std::abs(stereo[1] - 0.125f) < 0.00001f, "Canali stereo");
            Require(std::abs(Process(apo.Get(), 1, 0).front() - 0.1875f) < 0.00001f, "Lettori indipendenti");
            std::cout << "PASS: lettori contemporanei ricevono lo stesso file\n";
            APO_REG_PROPERTIES* properties = nullptr;
            Check(apo->GetRegistrationProperties(&properties), "Registrazione APO");
            Require(properties && properties->clsid == SoundboardApoClsid && properties->u32NumAPOInterfaces == 1 &&
                properties->Flags == APO_FLAG_DEFAULT, "Metadati APO");
            CoTaskMemFree(properties);
        }
        UnmapViewOfFile(bridge);
        CloseHandle(mapping);
        apo.Reset();
        factory.Reset();
        Require(canUnload() == S_OK, "Rilascio COM e mapping");
        FreeLibrary(module);
        CoUninitialize();
        std::cout << "PASS: risorse COM e IPC rilasciate\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << "FAIL: " << error.what() << '\n';
        return 1;
    }
}
