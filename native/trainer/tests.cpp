// Regression checks against mock engine functions. Never opens or injects into the game.
#define TRAINER_SIMULATION
#include "bridge.cpp"
#include <cassert>
#include <initializer_list>
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
static void* activePlayer=playerMemory;
static uint32_t playerDeaths=12,otherDeaths=3;
static int deathIncrements=0,saveRequests=0;
static bool statsFault=false;
static void* DETOUR mockPlayStats(void* self DUMMY){
    if(statsFault)RaiseException(EXCEPTION_ACCESS_VIOLATION,0,0,nullptr);
    return self==playerMemory?&playerDeaths:&otherDeaths;
}
static uint32_t DETOUR mockDeaths(void* self DUMMY){return *static_cast<uint32_t*>(self);}
static void DETOUR mockIncrementDeaths(void* self DUMMY){++*static_cast<uint32_t*>(self);++deathIncrements;}
static void DETOUR mockSave(void* self DUMMY){assert(self==activePlayer);++saveRequests;}
static float receivedDamage=0,receivedMana=0,armorArgs[4]{};
static float playerCap=1.35f,absoluteCap=1.5f,baseRun=.8f,cachedRun=.8f;
static bool multiplayer=false,bypassCaps=false;
static int refreshes=0,speedRefreshes=0;
static int potionUses[2]{};
static float potionAmounts[2]={500,30};
static int potionCooldowns[2]{};
static int potionCharges[2]={1,1};
static bool potionReady[2]={true,true},potionSupported=true;
static bool mockPotion(void* player,bool mana,PotionInfo& result){
    int i=mana?1:0;result.controller=player;result.recovery=potionAmounts[i];result.cooldown=potionCooldowns[i];
    int status=!potionReady[i]?4:potionCharges[i]<=0?2:potionCooldowns[i]>0?3:1;
    result.ready=potionChargeReady(status,potionCharges[i]);return potionSupported;
}
static void DETOUR mockUseHealth(void* self DUMMY){assert(potionCharges[0]>0);--potionCharges[0];++potionUses[0];potionCooldowns[0]=1000;}
static void DETOUR mockUseMana(void* self DUMMY){assert(potionCharges[1]>0);--potionCharges[1];++potionUses[1];potionCooldowns[1]=1000;}
static void* DETOUR mockMain(void* self DUMMY){return activePlayer;}
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
static std::initializer_list<float> updateDamagePackets;
static void DETOUR mockUpdate(void* self DUMMY,int){
    for(float damage:updateDamagePackets)lifeHook(activePlayer TEST_EDX,damage,nullptr,false,false);
}
static void simulateDamageUpdate(std::initializer_list<float> packets){
    updateDamagePackets=packets;
    updateHook(fakeEngine TEST_EDX,16);
    updateDamagePackets={};
}
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
    getPlayStats=bindMock<Ptr>(mockPlayStats);getDeaths=bindMock<Id>(mockDeaths);
    incrementDeaths=bindMock<Action>(mockIncrementDeaths);forceSave=bindMock<Action>(mockSave);
    originalRun=bindMock<Cap>(mockCap);originalAbsoluteRun=bindMock<Cap>(mockAbsoluteCap);originalGetRun=bindMock<RunGetter>(mockRun);
    originalAttack=bindMock<Cap>(mockCap);originalCast=bindMock<Cap>(mockCap);
    forceSpeed=bindMock<decltype(forceSpeed)>(mockForce);refreshCooldown=bindMock<SkillEnd>(mockEnd);endCooldown=bindMock<SkillEnd>(mockEnd);
    originalLife=bindMock<SubLife>(mockLife);originalMana=bindMock<SubMana>(mockMana);
    originalUpdate=bindMock<Update>(mockUpdate);isGameTimePaused=[](){return false;};
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
    assert(state.deathCount==12&&playerDeaths==12&&deathIncrements==0&&saveRequests==0); // Infinite never counts.
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

    // Normal damage still reaches the engine, including a hit that leaves 1 HP.
    state.lifeMode=PreventDeath;state.requested=Life;
    at<double>(playerMemory,Bio+0x20)=100;maintainPlayer();
    assert(at<double>(playerMemory,Bio+0x20)==100);
    LONG triggers=state.lifeTriggers;
    lifeHook(playerMemory TEST_EDX,99,nullptr,false,false);
    assert(receivedDamage==99&&state.lifeTriggers==triggers&&playerDeaths==12);

    // The exact lethal boundary restores life before another game update runs.
    at<float>(playerMemory,Bio+0x2c)=42;
    receivedDamage=-1;
    lifeHook(playerMemory TEST_EDX,100,nullptr,false,false);
    assert(receivedDamage==-1&&at<double>(playerMemory,Bio+0x20)==10000);
    assert(at<float>(playerMemory,Bio+0x2c)==0&&state.lifeTriggers==triggers+1);
    assert(playerDeaths==13&&state.deathCount==13&&deathIncrements==1&&saveRequests==1);

    // Even five individually lethal packets in one update create one death.
    simulateDamageUpdate({20000,20000,20000,20000,20000});
    assert(receivedDamage==-1&&at<double>(playerMemory,Bio+0x20)==10000);
    assert(playerDeaths==14&&state.deathCount==14&&deathIncrements==2&&saveRequests==2);
    assert(state.lifeTriggers==triggers+2);

    // Ordinary follow-up hits in later updates still pass through without counting.
    simulateDamageUpdate({10,10,10,10});
    assert(receivedDamage==10&&state.lifeTriggers==triggers+2&&playerDeaths==14);
    lifeHook(enemyMemory TEST_EDX,200,nullptr,false,false);assert(receivedDamage==200&&state.lifeTriggers==triggers+2);
    lifeHook(playerMemory TEST_EDX,-20,nullptr,false,false);assert(receivedDamage==-20);

    // A new lethal event can trigger from fractional but still positive life.
    at<double>(playerMemory,Bio+0x20)=.5;
    simulateDamageUpdate({1});
    assert(at<double>(playerMemory,Bio+0x20)==10000&&state.lifeTriggers==triggers+3);
    assert(playerDeaths==15&&state.deathCount==15&&deathIncrements==3&&saveRequests==3);
    LONG countedTriggers=state.lifeTriggers;
    for(float damage:{0.f,-20.f,10.f,std::numeric_limits<float>::infinity(),std::numeric_limits<float>::quiet_NaN()})
        lifeHook(playerMemory TEST_EDX,damage,nullptr,false,false);
    assert(playerDeaths==15&&saveRequests==3&&state.lifeTriggers==countedTriggers);
    at<float>(attr,Attr)=15;absHook(attr TEST_EDX,playerMemory,nullptr,nullptr,0);assert(at<float>(attr,Attr)==15);
    armorHook(cm TEST_EDX,1000,100,70,0);assert(armorArgs[1]==100&&armorArgs[2]==70);
    at<double>(playerMemory,Bio+0x20)=0;maintainPlayer();assert(at<double>(playerMemory,Bio+0x20)==0);
    lifeHook(playerMemory TEST_EDX,200,nullptr,false,false);assert(receivedDamage==200);
    at<double>(playerMemory,Bio+0x20)=100;state.heartbeat=GetTickCount64()-4000;
    lifeHook(playerMemory TEST_EDX,200,nullptr,false,false);assert(receivedDamage==200);
    state.heartbeat=GetTickCount64();multiplayer=true;maintainPlayer();
    lifeHook(playerMemory TEST_EDX,200,nullptr,false,false);assert(receivedDamage==200);
    multiplayer=false;state.requested=0;maintainPlayer();
    lifeHook(playerMemory TEST_EDX,200,nullptr,false,false);assert(receivedDamage==200);
    assert(playerDeaths==15&&saveRequests==3);
    state.lifeMode=Infinite;

    // Totals come from the active character, including real deaths and switches.
    playerDeaths=23;maintainPlayer();assert(state.deathCount==23);
    activePlayer=enemyMemory;maintainPlayer();assert(state.deathCount==3&&state.player==reinterpret_cast<uint64_t>(enemyMemory));
    activePlayer=nullptr;maintainPlayer();assert(state.deathCount==0&&state.player==0);
    activePlayer=playerMemory;state.lifeMode=PreventDeath;state.requested=Life;
    at<double>(playerMemory,Bio+0x20)=100;maintainPlayer();assert(state.deathCount==23);
    simulateDamageUpdate({200});assert(playerDeaths==24&&state.deathCount==24&&saveRequests==4);
    playerDeaths=UINT32_MAX;
    simulateDamageUpdate({20000});assert(playerDeaths==UINT32_MAX&&state.deathCount==UINT32_MAX&&saveRequests==4);
    playerDeaths=24;maintainPlayer();statsFault=true;
    simulateDamageUpdate({20000});
    assert(state.fault==EXCEPTION_ACCESS_VIOLATION&&state.applied==0&&effects==0&&playerDeaths==24&&saveRequests==4);
    lifeHook(playerMemory TEST_EDX,200,nullptr,false,false);assert(receivedDamage==200&&saveRequests==4);
    statsFault=false;state.fault=0;state.lifeMode=Infinite;state.requested=0;maintainPlayer();

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
    potionCharges[0]=potionCharges[1]=1;
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

    // Both resources may consume a second charge while the first replenishes.
    int healthUses=potionUses[0],manaUses=potionUses[1];
    state.requested=AutoHealth|AutoMana;maintainPlayer(false);
    at<double>(playerMemory,Bio+0x20)=9000;at<float>(playerMemory,Bio+0x3c)=200;
    potionCharges[0]=potionCharges[1]=2;potionCooldowns[0]=potionCooldowns[1]=0;
    lastPotionCheck=0;lastPotionAttempt[0]=lastPotionAttempt[1]=0;
    servicePotions(playerMemory,10000,260,true,4000);
    assert(potionUses[0]==healthUses+1&&potionUses[1]==manaUses+1);
    assert(potionCharges[0]==1&&potionCharges[1]==1&&potionCooldowns[0]>0&&potionCooldowns[1]>0);
    servicePotions(playerMemory,10000,260,true,4250);
    assert(potionUses[0]==healthUses+1&&potionUses[1]==manaUses+1); // Retry limit still applies.
    servicePotions(playerMemory,10000,260,true,4500);
    assert(potionUses[0]==healthUses+2&&potionUses[1]==manaUses+2);
    assert(potionCharges[0]==0&&potionCharges[1]==0);
    servicePotions(playerMemory,10000,260,true,5000);
    potionCooldowns[0]=potionCooldowns[1]=0;
    servicePotions(playerMemory,10000,260,true,5500);
    assert(potionUses[0]==healthUses+2&&potionUses[1]==manaUses+2); // No charge, even at zero CD.
    potionCharges[0]=potionCharges[1]=1;potionCooldowns[0]=potionCooldowns[1]=1000;
    at<double>(playerMemory,Bio+0x20)=9800;at<float>(playerMemory,Bio+0x3c)=250;
    servicePotions(playerMemory,10000,260,true,6000);
    assert(potionUses[0]==healthUses+2&&potionUses[1]==manaUses+2); // Recheck the deficit after healing.
    at<double>(playerMemory,Bio+0x20)=9500;at<float>(playerMemory,Bio+0x3c)=230;
    potionReady[0]=potionReady[1]=false;
    servicePotions(playerMemory,10000,260,true,6500);
    assert(potionUses[0]==healthUses+2&&potionUses[1]==manaUses+2); // Other unavailable states remain blocked.
    potionReady[0]=potionReady[1]=true;
    state.requested=AutoHealth;maintainPlayer(false);
    servicePotions(playerMemory,10000,260,true,7000);
    assert(potionUses[0]==healthUses+3&&potionUses[1]==manaUses+2);
    state.requested=AutoMana;maintainPlayer(false);
    servicePotions(playerMemory,10000,260,true,7500);
    assert(potionUses[0]==healthUses+3&&potionUses[1]==manaUses+3); // Independently use a replenished charge.
    assert(potionChargeReady(1,1)&&potionChargeReady(3,1)&&potionChargeReady(3,2));
    assert(!potionChargeReady(1,0)&&!potionChargeReady(3,0)&&!potionChargeReady(1,-1));
    for(int status:{0,2,4,5,6,7,8,9,-1})assert(!potionChargeReady(status,2));

    state.requested=MoveSpeed;maintainPlayer();assert(cachedRun==3.2f);
    multiplayer=true;maintainPlayer();assert(state.applied==0 && cachedRun==.8f && state.status==3);
    multiplayer=false;maintainPlayer();assert(cachedRun==3.2f);
    state.heartbeat=GetTickCount64()-4000;maintainPlayer();assert(state.applied==0 && cachedRun==.8f && !on(Life));
    state.heartbeat=GetTickCount64();state.requested=32|64;maintainPlayer();assert(state.applied==0); // Retired controls.
    assert(speedRefreshes>0 && sizeof(SharedState)==1136 && offsetof(SharedState,hits)==632 && offsetof(SharedState,deathCount)==1132 && Protocol==7);
    state.uiWrite=state.uiRead=0;
    for(int i=0;i<32;++i)assert(enqueueOverlayAction(&state,SetMovement,0,4));
    assert(!enqueueOverlayAction(&state,DisableAll,0,0)&&state.uiDropped==1);
    assert(state.uiActions[0].type==SetMovement&&state.uiActions[31].value==4);
    state.uiRead=32;assert(enqueueOverlayAction(&state,DisableAll,0,0)&&state.uiActions[0].type==DisableAll);
    std::cout<<"PASS: resources/reserved mana; movement multipliers; independent speeds/cache restore; lock life instant refill, one death per update, native totals/save requests, switches/overflow/fault; normal damage/healing; no revival; auto potions and charges; local-only/expiry/multiplayer; retired flags; v7 ABI ("<<sizeof(void*)*8<<" bit).\n";
}
