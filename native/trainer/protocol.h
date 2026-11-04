#pragma once
#define NOMINMAX
#include <windows.h>
#include <cstdint>
#include <cstddef>
#pragma pack(push, 1)
struct OverlayAction { LONG type, key, value; };
constexpr uint32_t OverlayQueueSize=32;
struct SharedState {
    uint32_t magic, version;
    volatile LONG requested, applied;
    volatile LONG64 heartbeat;
    volatile LONG status, fault;
    volatile LONG sequence;
    volatile LONG movementMultiplier;
    volatile LONG lifeMode, lifeTriggers;
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
    volatile LONG overlayToggle, overlayStatus, overlayVisible;
    volatile LONG uiWrite, uiRead, uiDropped;
    OverlayAction uiActions[OverlayQueueSize];
    volatile LONG overlayEmergency;
    uint32_t deathCount;
};
#pragma pack(pop)
static_assert(offsetof(SharedState, heartbeat)==16);
static_assert(offsetof(SharedState, tick)==48);
static_assert(offsetof(SharedState, hits)==632);
static_assert(offsetof(SharedState,movementMultiplier)==36);
static_assert(offsetof(SharedState,healthPotionRecovery)==704);
static_assert(offsetof(SharedState,overlayToggle)==720);
static_assert(offsetof(SharedState,uiWrite)==732);
static_assert(offsetof(SharedState,uiActions)==744);
static_assert(offsetof(SharedState,overlayEmergency)==1128);
static_assert(offsetof(SharedState,deathCount)==1132);
static_assert(sizeof(SharedState)==1136);
static_assert(offsetof(SharedState,lifeMode)==40);
static_assert(offsetof(SharedState,lifeTriggers)==44);
constexpr uint32_t Magic=0x43444731, Protocol=7;
constexpr size_t MappingSize=4096;
// Bits 32 and 64 are retired. Life now owns health, armor and resistance protection.
enum Feature { Life=1, Mana=2, Kill=4, MoveSpeed=8, Cooldown=16, AttackSpeed=128, CastSpeed=256, AutoHealth=512, AutoMana=1024 };
constexpr LONG AllFeatures=1951, SpeedFeatures=MoveSpeed|AttackSpeed|CastSpeed;
inline LONG validMultiplier(LONG value){return value==2||value==4||value==8||value==16?value:2;}
enum LifeMode { Infinite=0, PreventDeath=1 };
enum OverlayActionType { SetFeature=1, SetLifeMode=2, SetMovement=3, DisableAll=4 };
inline bool enqueueOverlayAction(SharedState* state,LONG type,LONG key,LONG value){
    uint32_t write=static_cast<uint32_t>(state->uiWrite),read=static_cast<uint32_t>(state->uiRead);
    if(write-read>=OverlayQueueSize){InterlockedIncrement(&state->uiDropped);return false;}
    state->uiActions[write%OverlayQueueSize]={type,key,value};
    MemoryBarrier();InterlockedIncrement(&state->uiWrite);return true;
}
