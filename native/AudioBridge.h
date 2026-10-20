#pragma once
#include <windows.h>
#include <cstddef>
#include <cstdint>

inline constexpr GUID SoundboardApoClsid = {0xd1f6bc85,0x2934,0x4d97,{0xa1,0xd0,0x0c,0x26,0x3b,0x77,0x66,0xc2}};
inline constexpr PROPERTYKEY BridgeChannelKey = {SoundboardApoClsid, 2};
inline constexpr PROPERTYKEY BridgeOwnerKey = {SoundboardApoClsid, 3};
inline constexpr std::uint32_t BridgeMagic = 0x31414253;
inline constexpr std::int64_t BridgeFrames = 8192;

struct alignas(64) AudioBridgeHeader {
    std::uint32_t magic;
    std::uint32_t version;
    volatile LONG playing;
    volatile LONG gain;
    volatile LONG64 epoch;
    volatile LONG64 startQpc;
    volatile LONG64 baseFrame;
    volatile LONG64 writtenFrame;
    volatile LONG64 endFrame;
    volatile LONG64 producerQpc;
    volatile LONG64 consumerQpc;
    LONG64 frequency;
    LONG64 capacity;
    unsigned char reserved[40];
};

static_assert(sizeof(AudioBridgeHeader) == 128);
static_assert(offsetof(AudioBridgeHeader, consumerQpc) == 64);
inline constexpr SIZE_T BridgeBytes = sizeof(AudioBridgeHeader) + BridgeFrames * 4;

inline LONG64 ReadBridge(volatile LONG64* value) noexcept {
    return InterlockedCompareExchange64(value, 0, 0);
}
