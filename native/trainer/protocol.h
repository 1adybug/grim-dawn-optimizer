#pragma once
#define NOMINMAX
#include <windows.h>
#include <cstdint>
#include <cstddef>
#pragma pack(push, 1)
struct SharedState {
    uint32_t magic, version;
    volatile LONG requested, applied;
    volatile LONG64 heartbeat;
    volatile LONG status, fault;
    volatile LONG sequence;
    volatile LONG movementMultiplier;
    uint32_t reserved[2];
    uint64_t tick;
    double health;
    float maxHealth, mana, maxMana, runCap, attackCap, castCap, resistanceCap;
    float runSpeed;
    uint32_t reservedSpeed[2], playerId, reserved2;
    uint64_t player;
    wchar_t message[256];
    uint64_t hits[9];
    float healthPotionRecovery, manaPotionRecovery;
    LONG healthPotionCooldown, manaPotionCooldown;
};
#pragma pack(pop)
static_assert(offsetof(SharedState, heartbeat)==16);
static_assert(offsetof(SharedState, tick)==48);
static_assert(offsetof(SharedState, hits)==632);
static_assert(offsetof(SharedState,movementMultiplier)==36);
static_assert(offsetof(SharedState,healthPotionRecovery)==704);
static_assert(sizeof(SharedState)==720);
constexpr uint32_t Magic=0x43444731, Protocol=5;
constexpr size_t MappingSize=4096;
// Bits 32 and 64 are retired. Life now owns health, armor and resistance protection.
enum Feature { Life=1, Mana=2, Kill=4, MoveSpeed=8, Cooldown=16, AttackSpeed=128, CastSpeed=256, AutoHealth=512, AutoMana=1024 };
constexpr LONG AllFeatures=1951, SpeedFeatures=MoveSpeed|AttackSpeed|CastSpeed;
inline LONG validMultiplier(LONG value){return value==2||value==4||value==8||value==16?value:2;}
