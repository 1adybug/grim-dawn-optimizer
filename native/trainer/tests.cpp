// Regression checks against mock engine functions. Never opens or injects into the game.
#include "bridge.cpp"
#include <cassert>
#include <iostream>
#include <limits>
#ifdef _WIN64
#define TEST_EDX
#else
#define TEST_EDX ,nullptr
#endif
template<class T,class U> static T bindMock(U fn){return reinterpret_cast<T>(fn);}
static unsigned char playerMemory[0x6000]{},enemyMemory[0x6000]{};
static void* fakeEngine=reinterpret_cast<void*>(0x1234);
static float receivedDamage=0,receivedMana=0,armorArgs[4]{};
static float playerCap=1.35f,absoluteCap=1.5f,baseRun=.8f,cachedRun=.8f;
static bool multiplayer=false,bypassCaps=false;
static int refreshes=0,speedRefreshes=0;
static int potionUses[2]{};
static float potionAmounts[2]={500,30};
static int potionCooldowns[2]{};
static bool potionReady[2]={true,true},potionSupported=true;
static bool mockPotion(void* player,bool mana,PotionInfo& result){
    int i=mana?1:0;result.controller=player;result.recovery=potionAmounts[i];result.cooldown=potionCooldowns[i];result.ready=potionReady[i]&&potionCooldowns[i]<=0;return potionSupported;
}
static void DETOUR mockUseHealth(void* self DUMMY){++potionUses[0];potionCooldowns[0]=1000;}
static void DETOUR mockUseMana(void* self DUMMY){++potionUses[1];potionCooldowns[1]=1000;}
static void* DETOUR mockMain(void* self DUMMY){return playerMemory;}
static void* DETOUR mockInfo(void* self DUMMY){return fakeEngine;}
static bool DETOUR mockMultiplayer(void* self DUMMY){return multiplayer;}
static float DETOUR mockMaxLife(void* self DUMMY){return 10000.f;}
static float DETOUR mockMaxMana(void* self DUMMY){return 300.f;}
static float DETOUR mockReserve(void* self DUMMY){return 40.f;}
static float DETOUR mockDefense(void* self DUMMY){return 95.f;}
static uint32_t DETOUR mockId(void* self DUMMY){return 42;}
static float DETOUR mockCap(void* self DUMMY,float n){return std::min(n,playerCap);}
static float DETOUR mockAbsoluteCap(void* self DUMMY,float n){return std::min(n,absoluteCap);}
static float DETOUR mockRun(void* self DUMMY,bool raw){
    // Models both bypasses found in the inspected GetRunSpeed implementation.
    if(raw||bypassCaps)return baseRun;
    return at<unsigned char>(self,AbsoluteRunMode)?originalAbsoluteRun(self,baseRun):originalRun(self,baseRun);
}
static void DETOUR mockForce(void* self DUMMY){++speedRefreshes;cachedRun=getRunHook(self TEST_EDX,false);}
static void DETOUR mockLife(void* self DUMMY,float amount,const void*,bool,bool){receivedDamage=amount;}
static void DETOUR mockMana(void* self DUMMY,float amount){receivedMana=amount;}
static void* DETOUR mockFaction(void* self DUMMY){return fakeEngine;}
static bool DETOUR mockFoe(void* self DUMMY,void*,void*,bool){return true;}
static void DETOUR mockProcess(void* self DUMMY,void*,void*,void*,float){}
static void DETOUR mockEnd(void* self DUMMY,int time){assert(time==0);++refreshes;}
static float DETOUR mockArmor(void* self DUMMY,float damage,float armor,float absorbed,float bonus){armorArgs[0]=damage;armorArgs[1]=armor;armorArgs[2]=absorbed;armorArgs[3]=bonus;return 0;}
int main(){
    static SharedState state{};shared=&state;shared->heartbeat=GetTickCount64();
    gameEngine=&fakeEngine;rootEngine=&fakeEngine;
    getMain=bindMock<Ptr>(mockMain);getGameInfo=bindMock<Ptr>(mockInfo);isMultiplayer=bindMock<Bool>(mockMultiplayer);
    maxLife=bindMock<Float>(mockMaxLife);maxMana=bindMock<Float>(mockMaxMana);reserveMana=bindMock<Float>(mockReserve);
    defenseCap=bindMock<Float>(mockDefense);objectId=bindMock<Id>(mockId);
    originalRun=bindMock<Cap>(mockCap);originalAbsoluteRun=bindMock<Cap>(mockAbsoluteCap);originalGetRun=bindMock<RunGetter>(mockRun);
    originalAttack=bindMock<Cap>(mockCap);originalCast=bindMock<Cap>(mockCap);
    forceSpeed=bindMock<decltype(forceSpeed)>(mockForce);refreshCooldown=bindMock<SkillEnd>(mockEnd);endCooldown=bindMock<SkillEnd>(mockEnd);
    originalLife=bindMock<SubLife>(mockLife);originalMana=bindMock<SubMana>(mockMana);
    getFactionMgr=bindMock<Ptr>(mockFaction);isFoe=bindMock<IsFoe>(mockFoe);
    originalAbs=bindMock<DefProcess>(mockProcess);originalDur=bindMock<DefProcess>(mockProcess);originalArmor=bindMock<ArmorCalc>(mockArmor);
    at<double>(playerMemory,Bio+0x20)=1000;at<float>(playerMemory,Bio+0x2c)=20;
    at<float>(playerMemory,Bio+0x3c)=120;at<float>(playerMemory,Bio+0x44)=30;
    unsigned char before[sizeof(playerMemory)];memcpy(before,playerMemory,sizeof(before));
    state.movementMultiplier=4;state.requested=Life|Mana|MoveSpeed|Cooldown;maintainPlayer();
    assert(state.applied==(Life|Mana|MoveSpeed|Cooldown));
    assert(at<double>(playerMemory,Bio+0x20)==10000);
    assert(at<float>(playerMemory,Bio+0x3c)==260 && state.maxMana==260 && refreshes==1);
    for(size_t i=0;i<sizeof(playerMemory);++i){
        bool current=(i>=Bio+0x20&&i<Bio+0x28)||(i>=Bio+0x2c&&i<Bio+0x30)||(i>=Bio+0x3c&&i<Bio+0x40)||(i>=Bio+0x44&&i<Bio+0x48);
        assert(current || before[i]==playerMemory[i]); // No base-stat or item memory writes.
    }
    receivedDamage=-1;lifeHook(playerMemory TEST_EDX,50,nullptr,false,false);assert(receivedDamage==-1);
    lifeHook(enemyMemory TEST_EDX,50,nullptr,false,false);assert(receivedDamage==50);
    receivedMana=-1;manaHook(playerMemory TEST_EDX,20);assert(receivedMana==-1);
    manaHook(enemyMemory TEST_EDX,20);assert(receivedMana==20);

    // Scale the original final result exactly once, past the native speed cap.
    assert(getRunHook(playerMemory TEST_EDX,false)==3.2f);
    assert(getRunHook(playerMemory TEST_EDX,true)==3.2f && cachedRun==3.2f && state.runSpeed==3.2f);
    assert(getRunHook(enemyMemory TEST_EDX,true)==.8f);
    baseRun=2.f;
    assert(getRunHook(playerMemory TEST_EDX,false)==playerCap*4.f);
    assert(getRunHook(playerMemory TEST_EDX,true)==8.f);
    at<unsigned char>(playerMemory,AbsoluteRunMode)=1;
    assert(getRunHook(playerMemory TEST_EDX,false)==6.f);
    assert(getRunHook(playerMemory TEST_EDX,true)==8.f);
    at<unsigned char>(playerMemory,AbsoluteRunMode)=0;
    bypassCaps=true;assert(getRunHook(playerMemory TEST_EDX,false)==8.f);bypassCaps=false;
    baseRun=0;assert(getRunHook(playerMemory TEST_EDX,false)==0);
    baseRun=-1;assert(getRunHook(playerMemory TEST_EDX,true)==-1);
    baseRun=std::numeric_limits<float>::infinity();assert(std::isinf(getRunHook(playerMemory TEST_EDX,true)));
    baseRun=std::numeric_limits<float>::quiet_NaN();assert(std::isnan(getRunHook(playerMemory TEST_EDX,true)));
    baseRun=std::numeric_limits<float>::max();assert(getRunHook(playerMemory TEST_EDX,true)==baseRun);
    baseRun=.8f;

    for(LONG factor:{2,4,8,16}){
        state.movementMultiplier=factor;maintainPlayer();
        assert(cachedRun==baseRun*factor && getRunHook(playerMemory TEST_EDX,true)==baseRun*factor);
    }
    state.movementMultiplier=123;maintainPlayer();assert(cachedRun==baseRun*2);
    state.movementMultiplier=4;maintainPlayer();
    for(int i=0;i<20;++i){maintainPlayer();forceSpeed(playerMemory);assert(cachedRun==3.2f && state.runSpeed==3.2f);} // No 25x feedback.
    baseRun=.4f;maintainPlayer();forceSpeed(playerMemory);assert(cachedRun==1.6f); // Dynamic slow/buff changes.
    baseRun=.8f;

    // Eight independent speed combinations, including engine cache recalculation.
    for(int combination=0;combination<8;++combination){
        state.requested=((combination&1)?MoveSpeed:0)|((combination&2)?AttackSpeed:0)|((combination&4)?CastSpeed:0);
        maintainPlayer();
        assert(getRunHook(playerMemory TEST_EDX,true)==((combination&1)?3.2f:.8f));
        assert(cachedRun==((combination&1)?3.2f:.8f));
        assert(attackHook(playerMemory TEST_EDX,.8f)==((combination&2)?1.35f:.8f));
        assert(castHook(playerMemory TEST_EDX,.8f)==((combination&4)?1.35f:.8f));
        assert(getRunHook(enemyMemory TEST_EDX,true)==.8f);
        assert(attackHook(enemyMemory TEST_EDX,.8f)==.8f && castHook(enemyMemory TEST_EDX,.8f)==.8f);
    }
    state.requested=0;maintainPlayer();
    receivedDamage=-1;lifeHook(playerMemory TEST_EDX,50,nullptr,false,false);assert(receivedDamage==50);
    receivedMana=-1;manaHook(playerMemory TEST_EDX,20);assert(receivedMana==20);assert(cachedRun==.8f);
    state.requested=Kill;maintainPlayer();at<double>(enemyMemory,Bio+0x20)=1234;
    at<uint32_t>(enemyMemory,Combat+Attacker)=99;lifeHook(enemyMemory TEST_EDX,1,nullptr,false,false);assert(receivedDamage==1);
    at<uint32_t>(enemyMemory,Combat+Attacker)=42;lifeHook(enemyMemory TEST_EDX,1,nullptr,false,false);assert(receivedDamage==2234);

    // One Life flag now controls HP, armor, absorption and duration resistance.
    state.requested=Life;maintainPlayer();unsigned char attr[128]{};
    at<float>(attr,Attr)=15;at<float>(attr,Attr+0xc)=5;
    absHook(attr TEST_EDX,playerMemory,nullptr,nullptr,0);assert(at<float>(attr,Attr)==100);
    at<float>(attr,Attr)=15;at<float>(attr,Attr+8)=3;
    durHook(attr TEST_EDX,playerMemory,nullptr,nullptr,0);assert(at<float>(attr,Attr)==98);
    at<float>(attr,Attr)=15;absHook(attr TEST_EDX,enemyMemory,nullptr,nullptr,0);assert(at<float>(attr,Attr)==15);
    unsigned char cm[256]{};at<void*>(cm,Parent)=playerMemory;
    armorHook(cm TEST_EDX,1000,100,70,0);assert(armorArgs[1]>=1000 && armorArgs[2]==armorArgs[1]);
    state.requested=0;maintainPlayer();
    at<float>(attr,Attr)=15;absHook(attr TEST_EDX,playerMemory,nullptr,nullptr,0);assert(at<float>(attr,Attr)==15);
    armorHook(cm TEST_EDX,1000,100,70,0);assert(armorArgs[1]==100 && armorArgs[2]==70);

    assert(availableMana(300,40)==260 && availableMana(300,400)==0 && availableMana(300,-1)==300);
    assert(availableMana(300,std::numeric_limits<float>::quiet_NaN())==0);
    assert(!needsPotion(260,260,30) && !needsPotion(231,260,30) && needsPotion(230,260,30));
    assert(!needsPotion(0,0,30) && !needsPotion(100,260,0) && !needsPotion(100,260,std::numeric_limits<float>::infinity()));
    assert(recoveryAmount(100,20,0,1000,0,0)==300);
    assert(recoveryAmount(100,20,0,1000,50,50)==550);
    assert(recoveryAmount(100,0,20,1000,50,50)==450);
    assert(recoveryAmount(30,10,0,300,0,0)==60); // Recovery uses total mana; loss uses usable mana.
    assert(recoveryAmount(-100,20,0,1000,0,0)==100);
    probePotion=mockPotion;useHealthPotion=bindMock<Action>(mockUseHealth);useManaPotion=bindMock<Action>(mockUseMana);
    state.requested=AutoHealth|AutoMana;at<double>(playerMemory,Bio+0x20)=10000;at<float>(playerMemory,Bio+0x3c)=260;
    maintainPlayer(false);lastPotionCheck=0;
    servicePotions(playerMemory,10000,260,true,1000);assert(potionUses[0]==0&&potionUses[1]==0); // Reserved mana is not missing mana.
    at<double>(playerMemory,Bio+0x20)=9500;at<float>(playerMemory,Bio+0x3c)=230;
    servicePotions(playerMemory,10000,260,true,1300);assert(potionUses[0]==1&&potionUses[1]==1); // Exact >= boundary.
    servicePotions(playerMemory,10000,260,true,1600);assert(potionUses[0]==1&&potionUses[1]==1); // Native cooldown.
    potionCooldowns[0]=potionCooldowns[1]=0;
    lastPotionCheck=0;
    servicePotions(playerMemory,10000,260,true,1700);assert(potionUses[0]==1&&potionUses[1]==1); // Rate limit.
    servicePotions(playerMemory,10000,260,false,2000);assert(potionUses[0]==1&&potionUses[1]==1); // Pause/background.
    potionReady[0]=potionReady[1]=false;servicePotions(playerMemory,10000,260,true,2100);assert(potionUses[0]==1&&potionUses[1]==1);
    potionReady[0]=potionReady[1]=true;at<double>(playerMemory,Bio+0x20)=0;
    servicePotions(playerMemory,10000,260,true,2400);assert(potionUses[0]==1&&potionUses[1]==1); // Dead character.
    at<double>(playerMemory,Bio+0x20)=9500;state.requested=AutoMana;maintainPlayer(false);lastPotionCheck=0;
    servicePotions(playerMemory,10000,260,true,2700);assert(potionUses[0]==1&&potionUses[1]==2); // Independent toggles.
    potionCooldowns[1]=0;state.requested=0;maintainPlayer(false);
    servicePotions(playerMemory,10000,260,true,3000);assert(potionUses[0]==1&&potionUses[1]==2); // Disabled.
    state.requested=AutoHealth|AutoMana;multiplayer=true;maintainPlayer(false);
    servicePotions(playerMemory,10000,260,true,3400);assert(potionUses[0]==1&&potionUses[1]==2); // Multiplayer.
    multiplayer=false;state.heartbeat=GetTickCount64()-4000;maintainPlayer(false);
    servicePotions(playerMemory,10000,260,true,3700);assert(potionUses[0]==1&&potionUses[1]==2); // Expired tool heartbeat.
    state.heartbeat=GetTickCount64();

    state.requested=MoveSpeed;maintainPlayer();assert(cachedRun==3.2f);
    multiplayer=true;maintainPlayer();assert(state.applied==0 && cachedRun==.8f && state.status==3);
    multiplayer=false;maintainPlayer();assert(cachedRun==3.2f);
    state.heartbeat=GetTickCount64()-4000;maintainPlayer();assert(state.applied==0 && cachedRun==.8f && !on(Life));
    state.heartbeat=GetTickCount64();state.requested=32|64;maintainPlayer();assert(state.applied==0); // Retired controls.
    assert(speedRefreshes>0 && sizeof(SharedState)==720 && offsetof(SharedState,hits)==632 && Protocol==5);
    std::cout<<"PASS: resources/reserved mana; 2/4/8/16x movement and live selection; no compounding; dynamic baseline; independent speeds; cache restore; finite movement; merged Life; auto potions: >= threshold, usable mana, cooldown, pause, retry, dead/off; local-only/expiry/multiplayer; retired flags; v5 ABI ("<<sizeof(void*)*8<<" bit).\n";
}
