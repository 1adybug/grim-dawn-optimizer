#define WIN32_LEAN_AND_MEAN
#include "protocol.h"
#include "potion_policy.h"
#include "overlay.h"
#pragma comment(lib,"user32.lib")
#include "../../vendor/minhook/include/MinHook.h"
#include <algorithm>
#include <cmath>
#include <cstring>
#include <cstdio>
#include <string>

#ifdef _WIN64
#define CALL
#define DETOUR
#define DUMMY
constexpr size_t Bio=0xee0,Combat=0x530,SkillManager=0x850,SkillOwner=0x30,Attacker=0x16c,Parent=8,Attr=0x20,AbsoluteRunMode=0x26b3;
constexpr size_t ModifierVtable=0x79c6b8,ModifierRecovery=0x8c,ModifierConvert=0xf0,HealIncrease=0x1ac,LookupSkillRva=0x19d40;
#else
#define CALL __thiscall
#define DETOUR __fastcall
#define DUMMY ,void* unusedEdx
constexpr size_t Bio=0xa18,Combat=0x3dc,SkillManager=0x600,SkillOwner=0x24,Attacker=0xf4,Parent=4,Attr=0x1c,AbsoluteRunMode=0x1cbb;
constexpr size_t ModifierVtable=0x6164bc,ModifierRecovery=0x7c,ModifierConvert=0xe0,HealIncrease=0x184,LookupSkillRva=0xd4a0;
#include "exports32.h"
#endif

// This profile is restricted to the locally inspected 1.3.1.1 x86/x64 builds.
// No disk patches, save parsing, attribute increments, item changes, or quest writes.
static SharedState* shared=nullptr;
static HMODULE game=nullptr, engine=nullptr;
static void** gameEngine=nullptr;
static void** rootEngine=nullptr;
static void* localPlayer=nullptr;
static LONG effects=0,movementMultiplier=2,lifeMode=Infinite;
template<class T> T sym(HMODULE m,const char* name){
#ifndef _WIN64
    for(auto& pair:ExportNames32)if(strcmp(pair.x64,name)==0){name=pair.x86;break;}
#endif
    return reinterpret_cast<T>(GetProcAddress(m,name));
}
template<class T> T& at(void* p,size_t n){return *reinterpret_cast<T*>(static_cast<char*>(p)+n);}
using Ptr=void*(CALL*)(void*);
using Float=float(CALL*)(void*);
using Bool=bool(CALL*)(void*);
using Update=void(CALL*)(void*,int);
using Cap=float(CALL*)(void*,float);
using RunGetter=float(CALL*)(void*,bool);
using SubLife=void(CALL*)(void*,float,const void*,bool,bool);
using SubMana=void(CALL*)(void*,float);
using SkillStart=void(CALL*)(void*,bool);
using SkillEnd=void(CALL*)(void*,int);
using DefProcess=void(CALL*)(void*,void*,void*,void*,float);
using ArmorCalc=float(CALL*)(void*,float,float,float,float);
using IsFoe=bool(CALL*)(void*,void*,void*,bool);
using Id=uint32_t(CALL*)(void*);
using Action=void(CALL*)(void*);
using Int=int(CALL*)(void*);
using Singleton=void*(*)();
using SkillLookup=void*(CALL*)(void*,uint32_t);
using ModifierRead=void(CALL*)(void*,void*,bool);
using ModifierReadLevel=void(CALL*)(void*,void*,int,bool,bool,bool);
using ModifierDestroy=void*(CALL*)(void*,unsigned);
static Ptr getHotSlots,getHealthSlot,getManaSlot;
static Id getSlotSkillId,getSkillLevel;
static Int getSlotCooldown,getSlotStatus,getSkillCharges;
static Action useHealthPotion,useManaPotion;
static Singleton objectManager,potionClass;
static SkillLookup lookupSkill;
static ModifierRead readModifiers;
static ModifierReadLevel readModifiersLevel;
static void* potionSlotVtable;
static bool(*isGameTimePaused)();
static Ptr getMain, getGameInfo, getFactionMgr;
static Bool isMultiplayer;
static Float maxLife,maxMana,defenseCap,reserveMana;
static Id objectId;
static IsFoe isFoe;
static SkillEnd endCooldown;
static SkillEnd refreshCooldown;
static Update originalUpdate;
static Cap originalRun,originalAbsoluteRun,originalAttack,originalCast;
static RunGetter originalGetRun;
static SubLife originalLife;
static SubMana originalMana;
static SkillStart originalCooldown;
static DefProcess originalAbs,originalDur;
static ArmorCalc originalArmor;
static void(CALL*forceSpeed)(void*);
static uint64_t hits[9]{},lastPotionAttempt[2]{},lastPotionCheck=0;

static bool live(){return shared && shared->heartbeat>0 && GetTickCount64()-static_cast<uint64_t>(shared->heartbeat)<3000;}
static bool on(LONG flag){return live() && (effects&flag)!=0;}
static bool infiniteLife(){return on(Life)&&lifeMode==Infinite;}
static float movementResult(void* p,float normal){
    if(p!=localPlayer||!on(MoveSpeed)||!std::isfinite(normal)||normal<=0)return normal;
    float boosted=normal*movementMultiplier;
    return std::isfinite(boosted)?boosted:normal;
}
static void message(const wchar_t* t){if(shared)wcsncpy_s(shared->message,t,_TRUNCATE);}
struct ModifierBuffer {
    alignas(16) unsigned char data[256]{};
    ModifierBuffer(){at<void*>(data,0)=reinterpret_cast<char*>(game)+ModifierVtable;}
    ~ModifierBuffer(){auto table=at<void**>(data,0);reinterpret_cast<ModifierDestroy>(table[0])(data,0);}
};
struct PotionInfo {void* controller=nullptr;float recovery=0;int cooldown=0;bool ready=false;};
static float potionRecovery(void* player,void* skill,bool mana){
    ModifierBuffer current,base;
    readModifiers(skill,current.data,true);
    readModifiersLevel(skill,base.data,static_cast<int>(getSkillLevel(skill)),true,true,true);
    // Mirrors Skill_ChargePotion::TargetResult in both exact supported builds.
    // Recovery fields contain flat, percentage and converted-percentage values.
    size_t start=ModifierRecovery+(mana?20:0);
    float flat=at<float>(base.data,start),percent=at<float>(base.data,start+4),extra=at<float>(base.data,start+8);
    float convert=at<float>(current.data,ModifierConvert),disabled=at<float>(current.data,ModifierConvert+4);
    if(!std::isfinite(convert)||!std::isfinite(disabled)||disabled>0)return 0;
    if(convert>0){float scale=1.f+convert*.01f;flat*=scale;extra=percent*scale;percent=0;}
    float ceiling=mana?maxMana(player):maxLife(player);
    return recoveryAmount(flat,percent,extra,ceiling,mana?0:at<float>(player,Bio+HealIncrease),mana?0:at<float>(player,Bio+HealIncrease+4));
}
static bool enginePotion(void* player,bool mana,PotionInfo& result){
    result.controller=getHotSlots(player);
    if(!result.controller)return false;
    void* slot=(mana?getManaSlot:getHealthSlot)(result.controller);
    if(!slot||at<void*>(slot,0)!=potionSlotVtable)return false;
    uint32_t id=getSlotSkillId(slot);if(!id)return false;
    void* manager=objectManager();if(!manager)return false;
    void* skill=lookupSkill(manager,id);if(!skill)return false;
    auto rtti=reinterpret_cast<Ptr>(at<void**>(skill,0)[0]);
    if(rtti(skill)!=potionClass())return false;
    result.cooldown=getSlotCooldown(slot);
    result.ready=potionChargeReady(getSlotStatus(slot),getSkillCharges(skill));
    result.recovery=potionRecovery(player,skill,mana);
    return result.recovery>0;
}
static bool(*probePotion)(void*,bool,PotionInfo&)=enginePotion;
static void servicePotions(void* player,float hp,float usableMana,bool allowActions,uint64_t now){
    if(!(effects&(AutoHealth|AutoMana))){lastPotionCheck=0;return;}
    double life=at<double>(player,Bio+0x20);
    if(!allowActions||!std::isfinite(life)||life<=0||!live())return;
    if(lastPotionCheck&&now-lastPotionCheck<250)return;
    lastPotionCheck=now;
    for(int i=0;i<2;++i){
        LONG flag=i?AutoMana:AutoHealth;if(!on(flag))continue;
        PotionInfo info;
        if(!probePotion(player,i!=0,info))continue;
        (i?shared->manaPotionRecovery:shared->healthPotionRecovery)=info.recovery;
        (i?shared->manaPotionCooldown:shared->healthPotionCooldown)=info.cooldown;
        double current=i?static_cast<double>(at<float>(player,Bio+0x3c)):at<double>(player,Bio+0x20);
        if(info.ready&&needsPotion(current,i?usableMana:hp,info.recovery)
           &&(!lastPotionAttempt[i]||now-lastPotionAttempt[i]>=500)){
            lastPotionAttempt[i]=now;
            (i?useManaPotion:useHealthPotion)(info.controller);
            ++hits[7+i];
        }
    }
}
static void maintainPlayer(bool allowActions=true){
    void* e=gameEngine?*gameEngine:nullptr;
    void* root=rootEngine?*rootEngine:nullptr;
    bool multi=false;
    if(root){void* info=getGameInfo(root);if(info)multi=isMultiplayer(info);}
    void* active=e?getMain(e):nullptr;
    void* player=multi?nullptr:active;
    LONG old=effects;
    lifeMode=shared->lifeMode==PreventDeath?PreventDeath:Infinite;
    LONG oldMultiplier=movementMultiplier;movementMultiplier=validMultiplier(shared->movementMultiplier);
    bool changedPlayer=localPlayer!=player;
    effects=(player&&live()&&!shared->fault)?(shared->requested&AllFeatures):0;
    localPlayer=player;
    shared->applied=effects;
    shared->status=multi?3:player?2:1;
    // Recalculate the engine's movement cache on enable, disable, expiry and player changes.
    if(active && (((old^effects)&SpeedFeatures) || (changedPlayer&&(effects&SpeedFeatures)) || ((effects&MoveSpeed)&&oldMultiplier!=movementMultiplier)))forceSpeed(active);
    if(changedPlayer){lastPotionCheck=0;lastPotionAttempt[0]=lastPotionAttempt[1]=0;}
    if(!player){shared->player=0;return;}
    // Only current resource fields change. Base attributes and save data are never written.
    float hp=maxLife(player),mp=availableMana(maxMana(player),reserveMana(player));
    if(infiniteLife() && hp>0 && at<double>(player,Bio+0x20)>0){
        at<double>(player,Bio+0x20)=hp;at<float>(player,Bio+0x2c)=0;
    }
    if(on(Mana) && mp>0){at<float>(player,Bio+0x3c)=mp;at<float>(player,Bio+0x44)=0;}
    if(on(Cooldown))refreshCooldown(static_cast<char*>(player)+SkillManager,0);
    servicePotions(player,hp,mp,allowActions,GetTickCount64());
    shared->player=reinterpret_cast<uint64_t>(player);
    shared->playerId=objectId(player);
    shared->health=at<double>(player,Bio+0x20);shared->maxHealth=hp;
    shared->mana=at<float>(player,Bio+0x3c);shared->maxMana=mp;
    shared->runCap=at<unsigned char>(player,AbsoluteRunMode)?originalAbsoluteRun(player,1000000.f):originalRun(player,1000000.f);
    shared->runSpeed=movementResult(player,originalGetRun(player,false));
    shared->attackCap=originalAttack(player,1000000.f);
    shared->castCap=originalCast(player,1000000.f);
    shared->resistanceCap=defenseCap(player);
}
static void DETOUR updateHook(void* self DUMMY,int ms){
    // No exceptions cross the engine ABI. A fault disables every effect for this session.
    originalUpdate(self,ms);
    if(!shared)return;
    InterlockedIncrement(&shared->sequence);
    __try{
        DWORD foregroundProcess=0;GetWindowThreadProcessId(GetForegroundWindow(),&foregroundProcess);
        bool allowActions=ms>0&&foregroundProcess==GetCurrentProcessId()&&!isGameTimePaused();
        maintainPlayer(allowActions);shared->tick=GetTickCount64();memcpy(shared->hits,hits,sizeof(hits));
    }
    __except(EXCEPTION_EXECUTE_HANDLER){effects=0;shared->applied=0;shared->fault=GetExceptionCode();message(L"运行时检查失败，所有效果已关闭；请重启游戏。");}
    InterlockedIncrement(&shared->sequence);
}
static float DETOUR getRunHook(void* p DUMMY,bool raw){
    float normal=originalGetRun(p,raw);
    // The inspected getter recomputes speed from attributes on each call. Its
    // raw=false branch applies the normal/absolute cap before returning. Scale
    // only this final result; leave both cap functions untouched to avoid nested
    // nested multiplication and preserve the original baseline when disabled.
    float boosted=movementResult(p,normal);
    if(boosted!=normal&&std::isfinite(boosted))++hits[3];
    return boosted;
}
static float DETOUR attackHook(void* p DUMMY,float v){if(p==localPlayer&&on(AttackSpeed)){++hits[4];v=1000000.f;}return originalAttack(p,v);}
static float DETOUR castHook(void* p DUMMY,float v){if(p==localPlayer&&on(CastSpeed)){++hits[5];v=1000000.f;}return originalCast(p,v);}
static void DETOUR lifeHook(void* p DUMMY,float damage,const void* type,bool a,bool b){
    if(p==localPlayer&&on(Life)&&damage>0){
        if(lifeMode==Infinite){++hits[0];return;}
        double current=at<double>(p,Bio+0x20);
        if(std::isfinite(current)&&current>0&&std::isfinite(damage)&&damage>=current-1.0){
            // Round down so a float conversion cannot cross the one-health floor.
            double allowed=std::max(0.0,current-1.0);
            float reduced=static_cast<float>(allowed);
            if(reduced>allowed)reduced=std::nextafter(reduced,0.f);
            InterlockedIncrement(&shared->lifeTriggers);++hits[0];
            if(current<1.0)at<double>(p,Bio+0x20)=1.0;
            if(reduced<=0)return;
            damage=reduced;
        }
    }
    if(p!=localPlayer&&localPlayer&&on(Kill)&&damage>0){
        void* e=gameEngine?*gameEngine:nullptr;
        void* fm=e?getFactionMgr(e):nullptr;
        // Only hostile victims whose current attacker is the local player.
        if(fm&&at<uint32_t>(p,Combat+Attacker)==objectId(localPlayer)&&isFoe(fm,localPlayer,p,false)){
            damage=static_cast<float>(std::max(0.0,at<double>(p,Bio+0x20))+1000.0);++hits[2];
        }
    }
    originalLife(p,damage,type,a,b);
}
static void DETOUR manaHook(void* p DUMMY,float amount){if(p==localPlayer&&on(Mana)&&amount>0){++hits[1];return;}originalMana(p,amount);}
static void DETOUR cooldownHook(void* s DUMMY,bool b){
    if(localPlayer&&on(Cooldown)&&at<void*>(s,SkillOwner)==static_cast<char*>(localPlayer)+SkillManager){
        // EndCooldown restores the engine's own charge accounting.
        endCooldown(s,0);++hits[6];return;
    }
    originalCooldown(s,b);
}
static void DETOUR absHook(void* attr DUMMY,void* owner,void* reduction,void* scale,float arg){
    originalAbs(attr,owner,reduction,scale,arg);
    if(owner==localPlayer&&infiniteLife()){
        float cap=defenseCap(owner)+at<float>(attr,Attr+0xc);
        if(cap>0&&std::isfinite(cap)){at<float>(attr,Attr)=std::min(100.f,cap);++hits[0];}
    }
}
static void DETOUR durHook(void* attr DUMMY,void* owner,void* reduction,void* scale,float arg){
    originalDur(attr,owner,reduction,scale,arg);
    if(owner==localPlayer&&infiniteLife()){
        float cap=defenseCap(owner)+at<float>(attr,Attr+8);
        if(cap>0&&std::isfinite(cap))at<float>(attr,Attr)=std::min(100.f,cap);
    }
}
static float DETOUR armorHook(void* cm DUMMY,float damage,float armor,float absorbed,float bonus){
    if(localPlayer&&infiniteLife()&&at<void*>(cm,Parent)==localPlayer&&damage>0){
        armor=std::max(armor,damage);absorbed=armor;bonus=std::max(0.f,bonus);
    }
    return originalArmor(cm,damage,armor,absorbed,bonus);
}

struct Hook { const char* name; void* detour; void** original; };
#define HOOK(symbol,fn,original) {symbol,reinterpret_cast<void*>(fn),reinterpret_cast<void**>(&original)}
static bool install(){
    game=GetModuleHandleW(L"Game.dll");engine=GetModuleHandleW(L"Engine.dll");
    if(!game||!engine)return false;
    auto dos=reinterpret_cast<IMAGE_DOS_HEADER*>(game);
    auto nt=reinterpret_cast<IMAGE_NT_HEADERS*>(reinterpret_cast<char*>(game)+dos->e_lfanew);
#ifdef _WIN64
    if(nt->FileHeader.TimeDateStamp!=1791489611 || nt->OptionalHeader.SizeOfImage!=0xae6000)return false;
#else
    if(nt->FileHeader.TimeDateStamp!=1791489002)return false;
#endif
    gameEngine=sym<void**>(game,"?gGameEngine@GAME@@3PEAVGameEngine@1@EA");
    rootEngine=sym<void**>(engine,"?gEngine@GAME@@3PEAVEngine@1@EA");
    getMain=sym<Ptr>(game,"?GetMainPlayer@GameEngine@GAME@@QEBAPEAVPlayer@2@XZ");
    getGameInfo=sym<Ptr>(engine,"?GetGameInfo@Engine@GAME@@QEAAPEAVGameInfo@2@XZ");
    isMultiplayer=sym<Bool>(engine,"?GetIsMultiPlayer@GameInfo@GAME@@QEBA_NXZ");
    maxLife=sym<Float>(game,"?GetLifeLimit@Character@GAME@@QEBA?BMXZ");
    maxMana=sym<Float>(game,"?GetManaLimit@Character@GAME@@QEBA?BMXZ");
    reserveMana=sym<Float>(game,"?GetReserveMana@Character@GAME@@QEBA?BMXZ");
    defenseCap=sym<Float>(game,"?GetDefenseAttributeCap@Player@GAME@@UEBAMXZ");
    objectId=sym<Id>(engine,"?GetObjectId@Object@GAME@@QEBAIXZ");
    getFactionMgr=sym<Ptr>(game,"?GetFactionManager@GameEngine@GAME@@QEAAPEAVFactionManager@2@XZ");
    isFoe=sym<IsFoe>(game,"?IsFoe@FactionManager@GAME@@QEAA_NPEAVCharacter@2@0_N@Z");
    endCooldown=sym<SkillEnd>(game,"?EndCooldown@Skill@GAME@@UEAAXH@Z");
    refreshCooldown=sym<SkillEnd>(game,"?RefreshCooldown@SkillManager@GAME@@UEAAXH@Z");
    forceSpeed=sym<void(CALL*)(void*)>(game,"?ForceSpeedUpdate@Character@GAME@@QEAAXXZ");
    originalRun=sym<Cap>(game,"?CapRunSpeed@Player@GAME@@UEBAMM@Z");
    originalAbsoluteRun=sym<Cap>(game,"?CapAbsoluteRunSpeed@Character@GAME@@QEBAMM@Z");
    getHotSlots=sym<Ptr>(game,"?GetPlayerHotSlotCtrl@Player@GAME@@QEAAAEAVPlayerHotSlotCtrl@2@XZ");
    getHealthSlot=sym<Ptr>(game,"?GetHealthPotionSlot@PlayerHotSlotCtrl@GAME@@QEBAPEAVHotSlotOption@2@XZ");
    getManaSlot=sym<Ptr>(game,"?GetManaPotionSlot@PlayerHotSlotCtrl@GAME@@QEBAPEAVHotSlotOption@2@XZ");
    getSlotSkillId=sym<Id>(game,"?GetSkillId@HotSlotOptionSkill@GAME@@UEBAIXZ");
    getSlotCooldown=sym<Int>(game,"?GetCooldownRemaining@HotSlotOptionSkill@GAME@@UEAAHXZ");
    getSlotStatus=sym<Int>(game,"?GetStatus@HotSlotOptionSkill@GAME@@UEAA?AW4HotSlotOptionStatus@2@XZ");
    getSkillCharges=sym<Int>(game,"?GetCooldownCharges@Skill@GAME@@UEBAHXZ");
    useHealthPotion=sym<Action>(game,"?ActivateHealthPotionSlot@PlayerHotSlotCtrl@GAME@@QEAAXXZ");
    useManaPotion=sym<Action>(game,"?ActivateManaPotionSlot@PlayerHotSlotCtrl@GAME@@QEAAXXZ");
    objectManager=sym<Singleton>(engine,"?Get@?$Singleton@VObjectManager@GAME@@@GAME@@SAPEAVObjectManager@2@XZ");
    potionClass=sym<Singleton>(game,"?GetStaticClassInfo@Skill_ChargePotion@GAME@@SAAEBVRTTI_ClassInfo@2@XZ");
    potionSlotVtable=sym<void*>(game,"??_7HotSlotOptionPotionSkill@GAME@@6B@");
    getSkillLevel=sym<Id>(game,"?GetCurrentLevel@Skill@GAME@@UEBA?BIXZ");
    readModifiers=sym<ModifierRead>(game,"?GetSkillModifierData@Skill@GAME@@UEBAXAEAVSkillProfile_Modifiers@2@_N@Z");
    readModifiersLevel=sym<ModifierReadLevel>(game,"?GetSkillModifierData@Skill@GAME@@UEBAXAEAVSkillProfile_Modifiers@2@H_N11@Z");
    lookupSkill=reinterpret_cast<SkillLookup>(reinterpret_cast<char*>(game)+LookupSkillRva);
    isGameTimePaused=sym<decltype(isGameTimePaused)>(engine,"?IsGameTimePaused@GAME@@YA_NXZ");
    if(!getHotSlots||!getHealthSlot||!getManaSlot||!getSlotSkillId||!getSlotCooldown||!getSlotStatus||!getSkillCharges||!useHealthPotion||!useManaPotion
       ||!objectManager||!potionClass||!potionSlotVtable||!getSkillLevel||!readModifiers||!readModifiersLevel||!isGameTimePaused)return false;
    if(!gameEngine||!rootEngine||!getMain||!getGameInfo||!isMultiplayer||!maxLife||!maxMana||!reserveMana||!defenseCap||!objectId||!getFactionMgr||!isFoe||!endCooldown||!refreshCooldown||!forceSpeed||!originalRun||!originalAbsoluteRun)return false;
    Hook hooks[]={
        HOOK("?Update@GameEngine@GAME@@QEAAXH@Z",updateHook,originalUpdate),
        HOOK("?GetRunSpeed@Character@GAME@@QEAA?BM_N@Z",getRunHook,originalGetRun),
        HOOK("?CapAttackSpeed@Player@GAME@@UEBAMM@Z",attackHook,originalAttack),
        HOOK("?CapSpellCastSpeed@Player@GAME@@UEBAMM@Z",castHook,originalCast),
        HOOK("?SubtractLife@Character@GAME@@QEAAXMAEBUPlayStatsDamageType@2@_N_N@Z",lifeHook,originalLife),
        HOOK("?SubtractMana@Character@GAME@@QEAAXM@Z",manaHook,originalMana),
        HOOK("?StartCooldown@Skill@GAME@@QEAAX_N@Z",cooldownHook,originalCooldown),
        HOOK("?Process@CombatAttributeAbsDefense@GAME@@UEAAXAEBVCharacter@2@AEBUReductionInfo@2@AEBUDamageScaleInfo@2@M@Z",absHook,originalAbs),
        HOOK("?Process@CombatAttributeDurDefense@GAME@@UEAAXAEBVCharacter@2@AEBUReductionInfo@2@AEBUDamageScaleInfo@2@M@Z",durHook,originalDur),
        HOOK("?DesignerCalculatePhysicalDamageDefense@CombatManager@GAME@@QEBAMMMMM@Z",armorHook,originalArmor)
    };
    if(MH_Initialize()!=MH_OK)return false;
    for(auto& h:hooks){
        void* target=sym<void*>(game,h.name);
        if(!target||MH_CreateHook(target,h.detour,h.original)!=MH_OK){MH_Uninitialize();return false;}
    }
    if(MH_EnableHook(MH_ALL_HOOKS)!=MH_OK){MH_Uninitialize();return false;}
    return true;
}
static DWORD WINAPI start(void*){
    wchar_t name[96];swprintf_s(name,L"Local\\GrimDawnCompanion.v%u.%lu",Protocol,GetCurrentProcessId());
    HANDLE mapping=OpenFileMappingW(FILE_MAP_ALL_ACCESS,FALSE,name);
    if(!mapping)return 1;
    shared=static_cast<SharedState*>(MapViewOfFile(mapping,FILE_MAP_ALL_ACCESS,0,0,MappingSize));
    if(!shared || shared->magic!=Magic || shared->version!=Protocol)return 2;
    if(!install()){shared->status=-1;message(L"当前 Game.dll 不匹配，未安装任何效果。");return 3;}
    shared->status=1;message(L"已连接。进入单人游戏后可启用开关。");
#ifndef TRAINER_SIMULATION
    // Graphics/input hooks are independent of the combat update ABI. A renderer
    // failure is reported separately and must not break existing effects.
    installOverlay(shared);
#endif
    // The bridge stays loaded until game exit. Hooks become pass-through when heartbeat expires.
    // Retaining trampolines avoids unloading code while an engine stack is executing inside it.
    return 0;
}
BOOL WINAPI DllMain(HINSTANCE module,DWORD reason,LPVOID){
    if(reason==DLL_PROCESS_ATTACH){DisableThreadLibraryCalls(module);HANDLE t=CreateThread(nullptr,0,start,nullptr,0,nullptr);if(t)CloseHandle(t);}
    return TRUE;
}
