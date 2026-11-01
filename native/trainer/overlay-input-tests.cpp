// No game/GPU or physical input. Calls the actual window procedure and input
// detours with an owned hidden HWND and deterministic original-function stubs.
#define OVERLAY_INPUT_TESTS
#include "overlay.cpp"
#include "../../vendor/imgui/imgui_internal.h"
#include <iostream>

namespace {
int failures=0,gameMessages=0,stateCalls=0,dataCalls=0;
bool gameKeys[256]{},gameLeftButton=false;
bool queuedInput=false;
std::vector<DIDEVICEOBJECTDATA> deviceEvents;
DWORD receivedFlags=0;
HRESULT inputResult=DI_OK;
bool cursorCalled=false,clipCalled=false,keyboardCalled=false;
const RECT* receivedClip=nullptr;
void check(bool ok,const char* name){if(!ok){++failures;std::cerr<<"FAIL: "<<name<<'\n';}}
LRESULT CALLBACK gameProc(HWND hwnd,UINT message,WPARAM wparam,LPARAM lparam){
    if(message>=WM_MOUSEFIRST&&message<=WM_MOUSELAST)++gameMessages;
    if((message==WM_KEYDOWN||message==WM_SYSKEYDOWN)&&wparam<256)gameKeys[wparam]=true;
    if((message==WM_KEYUP||message==WM_SYSKEYUP)&&wparam<256)gameKeys[wparam]=false;
    if(message==WM_LBUTTONDOWN)gameLeftButton=true;
    if(message==WM_LBUTTONUP)gameLeftButton=false;
    return DefWindowProcW(hwnd,message,wparam,lparam);
}
struct FakeDevice {void** table;DWORD type;};
HRESULT WINAPI caps(IDirectInputDevice8W* device,DIDEVCAPS* out){out->dwDevType=reinterpret_cast<FakeDevice*>(device)->type;return DI_OK;}
HRESULT WINAPI readState(IDirectInputDevice8W*,DWORD size,void* bytes){++stateCalls;if(bytes)memset(bytes,0x80,size);return inputResult;}
HRESULT WINAPI readData(IDirectInputDevice8W*,DWORD size,DIDEVICEOBJECTDATA* data,DWORD* count,DWORD flags){
    if(queuedInput){
        ++dataCalls;receivedFlags=flags;
        if(!count)return E_POINTER;
        DWORD available=static_cast<DWORD>(deviceEvents.size());
        if(!data){*count=available;return inputResult;}
        DWORD copied=std::min(*count,available);*count=copied;
        for(DWORD i=0;i<copied;++i)memcpy(reinterpret_cast<BYTE*>(data)+i*size,&deviceEvents[i],size);
        if(!(flags&DIGDD_PEEK))deviceEvents.erase(deviceEvents.begin(),deviceEvents.begin()+copied);
        return inputResult;
    }
    ++dataCalls;receivedFlags=flags;if(count&&*count){if(data){memset(data,0,size);data->dwData=0x80;}*count=1;}return inputResult;
}
void deviceEvent(DWORD offset,DWORD value){DIDEVICEOBJECTDATA event{};event.dwOfs=offset;event.dwData=value;event.dwTimeStamp=1234;event.dwSequence=static_cast<DWORD>(deviceEvents.size()+1);deviceEvents.push_back(event);}
BOOL WINAPI moveCursor(int x,int y){cursorCalled=x==17&&y==29;return TRUE;}
BOOL WINAPI setClip(const RECT* rect){clipCalled=true;receivedClip=rect;return TRUE;}
BOOL WINAPI readKeyboard(PBYTE keys){keyboardCalled=true;memset(keys,0x80,256);return TRUE;}
void checkForwarding(IDirectInputDevice8W* mouse,const char* label){
    unsigned char bytes[32]{};inputResult=DI_OK;stateCalls=dataCalls=0;
    check(inputState<0>(mouse,sizeof(bytes),bytes)==DI_OK&&stateCalls==1&&bytes[0]==0x80&&bytes[31]==0x80,label);
    DIDEVICEOBJECTDATA data{};DWORD count=1;
    check(inputData<0>(mouse,sizeof(data),&data,&count,DIGDD_PEEK)==DI_OK&&dataCalls==1&&count==1&&data.dwData==0x80&&receivedFlags==DIGDD_PEEK,"closed mouse preserves buffered data / PEEK");
    cursorCalled=clipCalled=keyboardCalled=false;RECT rect{1,2,3,4};BYTE keys[256]{};
    check(setCursorPos(17,29)&&cursorCalled,"closed cursor positioning forwards");
    check(clipCursor(&rect)&&clipCalled&&receivedClip==&rect,"closed cursor clipping forwards unchanged");
    check(keyboardState(keys)&&keyboardCalled&&keys[0]==0x80&&keys[255]==0x80,"closed keyboard forwards unchanged");
}
}

int main(){
    SharedState fixture{};fixture.heartbeat=GetTickCount64();fixture.status=2;state=&fixture;
    WNDCLASSW klass{};klass.lpfnWndProc=gameProc;klass.hInstance=GetModuleHandleW(nullptr);klass.lpszClassName=L"GDO.Overlay.InputRegression";
    RegisterClassW(&klass);
    HWND window=CreateWindowW(klass.lpszClassName,L"",WS_OVERLAPPEDWINDOW,0,0,640,480,nullptr,nullptr,klass.hInstance,nullptr);
    check(window!=nullptr,"owned hidden window");gameWindow=window;previousProc=gameProc;
    SetWindowLongPtrW(window,GWLP_WNDPROC,reinterpret_cast<LONG_PTR>(windowProc));
    context=ImGui::CreateContext();ImGui_ImplWin32_Init(window);inputReady=true;
    originalInputState[0]=readState;originalInputData[0]=readData;originalSetCursorPos=moveCursor;originalClipCursor=setClip;originalKeyboardState=readKeyboard;
    void* table[]={nullptr,nullptr,nullptr,reinterpret_cast<void*>(caps)};
    FakeDevice mouse{table,DI8DEVTYPE_MOUSE},keyboard{table,DI8DEVTYPE_KEYBOARD},joystick{table,DI8DEVTYPE_JOYSTICK};
    auto mouseDevice=reinterpret_cast<IDirectInputDevice8W*>(&mouse);
    checkForwarding(mouseDevice,"never-opened mouse state forwards unchanged");

    // A person keeps Ctrl/Alt held until after the hotkey opens the popup.
    // The game must receive the releases for downs it already processed.
    SendMessageW(window,WM_KEYDOWN,VK_CONTROL,0);
    SendMessageW(window,WM_SYSKEYDOWN,VK_MENU,0);
    SendMessageW(window,WM_LBUTTONDOWN,MK_LBUTTON,MAKELPARAM(90,90));
    check(gameKeys[VK_CONTROL]&&gameKeys[VK_MENU]&&gameLeftButton,"game sees input held before opening");
    openPopup();
    SendMessageW(window,WM_KEYUP,VK_CONTROL,0);
    SendMessageW(window,WM_SYSKEYUP,VK_MENU,0);
    SendMessageW(window,WM_LBUTTONUP,0,MAKELPARAM(90,90));
    togglePopup();
    check(!gameKeys[VK_CONTROL]&&!gameKeys[VK_MENU],"hotkey opening never strands game Ctrl/Alt down");
    check(!gameLeftButton,"opening while game mouse held delivers its release");
    for(int closeMethod=0;closeMethod<3;++closeMethod){
        SendMessageW(window,WM_KEYDOWN,VK_CONTROL,0);SendMessageW(window,WM_SYSKEYDOWN,VK_MENU,0);
        openPopup();
        SendMessageW(window,WM_KEYUP,VK_CONTROL,0);SendMessageW(window,WM_SYSKEYUP,VK_MENU,0);
        SendMessageW(window,WM_KEYDOWN,'W',0);
        if(closeMethod==0)togglePopup();else if(closeMethod==1)closePopup();else SendMessageW(window,WM_KEYDOWN,VK_ESCAPE,0);
        check(!gameKeys[VK_CONTROL]&&!gameKeys[VK_MENU],"toggle / X / Esc leave no game-held modifiers");
        SendMessageW(window,WM_KEYDOWN,'W',0); // held-key repeat after close
        check(!gameKeys['W'],"popup-held movement key does not repeat into gameplay after close");
        SendMessageW(window,WM_KEYUP,'W',0);SendMessageW(window,WM_KEYUP,VK_ESCAPE,0);
        SendMessageW(window,WM_KEYDOWN,'W',0);check(gameKeys['W'],"fresh movement key resumes after release");
        SendMessageW(window,WM_KEYUP,'W',0);
    }

    // Buffered DirectInput has the same ownership boundary as Win32 messages.
    // Discarding every buffered event also strands keys held before opening.
    queuedInput=true;auto keyboardDevice=reinterpret_cast<IDirectInputDevice8W*>(&keyboard);
    DIDEVICEOBJECTDATA events[8]{};DWORD eventCount=8;
    deviceEvent(DIK_LCONTROL,0x80);deviceEvent(DIK_LMENU,0x80);
    inputData<0>(keyboardDevice,sizeof(events[0]),events,&eventCount,0);
    check(eventCount==2,"game receives buffered modifier downs before opening");
    openPopup();deviceEvent(DIK_LCONTROL,0);deviceEvent(DIK_LMENU,0);deviceEvent(DIK_W,0x80);deviceEvent(DIK_W,0);
    eventCount=8;inputData<0>(keyboardDevice,sizeof(events[0]),events,&eventCount,DIGDD_PEEK);
    check(eventCount==2&&events[0].dwOfs==DIK_LCONTROL&&events[1].dwOfs==DIK_LMENU&&events[0].dwData==0&&events[1].dwData==0,
        "popup forwards buffered releases of game-held Ctrl/Alt only");
    check(deviceEvents.empty(),"captured PEEK consumes popup buffered input");
    closePopup();
    deviceEvent(DIMOFS_BUTTON0,0x80);eventCount=8;inputData<0>(mouseDevice,sizeof(events[0]),events,&eventCount,0);
    openPopup();deviceEvent(DIMOFS_BUTTON0,0);deviceEvent(DIMOFS_BUTTON1,0x80);deviceEvent(DIMOFS_BUTTON1,0);deviceEvent(DIMOFS_X,20);
    eventCount=8;inputData<0>(mouseDevice,sizeof(events[0]),events,&eventCount,0);
    check(eventCount==1&&events[0].dwOfs==DIMOFS_BUTTON0&&events[0].dwData==0,"popup delivers prior game mouse release without clicks or motion");
    closePopup();
    deviceEvent(DIK_LCONTROL,0x80);eventCount=8;inputData<0>(keyboardDevice,sizeof(events[0]),events,&eventCount,0);
    openPopup();deviceEvent(DIK_LCONTROL,0);deviceEvent(DIK_W,0x80);
    eventCount=INFINITE;inputData<0>(keyboardDevice,sizeof(events[0]),nullptr,&eventCount,DIGDD_PEEK);
    check(eventCount==1&&deviceEvents.empty(),"count-only queries drain popup input and retain game-owned releases");
    closePopup();eventCount=8;inputData<0>(keyboardDevice,sizeof(events[0]),events,&eventCount,DIGDD_PEEK);
    check(eventCount==1&&events[0].dwOfs==DIK_LCONTROL&&events[0].dwData==0&&events[0].dwTimeStamp==1234,"queued release preserves metadata after close / PEEK");
    eventCount=8;inputData<0>(keyboardDevice,sizeof(events[0]),events,&eventCount,0);
    check(eventCount==1&&events[0].dwOfs==DIK_LCONTROL,"PEEK leaves queued game release available to consume");
    deviceEvent(DIK_W,0x80);deviceEvent(DIK_W,0);deviceEvent(DIK_A,0x80);
    eventCount=8;inputData<0>(keyboardDevice,sizeof(events[0]),events,&eventCount,DIGDD_PEEK);
    check(eventCount==1&&events[0].dwOfs==DIK_A&&deviceEvents.size()==3,"closed PEEK filters popup-held repeat / release without consuming device input");
    eventCount=8;inputData<0>(keyboardDevice,sizeof(events[0]),events,&eventCount,0);
    check(eventCount==1&&events[0].dwOfs==DIK_A&&deviceEvents.empty(),"closed consumption retains popup ownership across PEEK");
    deviceEvent(DIK_A,0);eventCount=8;inputData<0>(keyboardDevice,sizeof(events[0]),events,&eventCount,0);
    // DX3 records have the same first 16 bytes, without the pointer-sized tail.
    deviceEvent(DIK_RCONTROL,0x80);eventCount=8;
    inputData<0>(keyboardDevice,FIELD_OFFSET(DIDEVICEOBJECTDATA,uAppData),events,&eventCount,0);
    openPopup();deviceEvent(DIK_RCONTROL,0);eventCount=8;
    inputData<0>(keyboardDevice,FIELD_OFFSET(DIDEVICEOBJECTDATA,uAppData),events,&eventCount,0);
    check(eventCount==1&&events[0].dwOfs==DIK_RCONTROL&&events[0].dwData==0,"legacy DX3 buffered records retain game-owned releases");
    deviceEvent(DIK_W,0x80);eventCount=8;inputData<0>(keyboardDevice,sizeof(events[0]),events,&eventCount,0);
    SendMessageW(window,WM_KILLFOCUS,0,0);SendMessageW(window,WM_SETFOCUS,0,0);
    deviceEvent(DIK_W,0x80);deviceEvent(DIK_W,0);eventCount=8;
    inputData<0>(keyboardDevice,sizeof(events[0]),events,&eventCount,0);
    check(eventCount==2,"focus transition discards stale popup ownership before fresh game input");
    queuedInput=false;

    opened=true;capture=true;
    SendMessageW(window,WM_LBUTTONDOWN,MK_LBUTTON,MAKELPARAM(100,100));
    check(GetCapture()==nullptr,"popup button-down never acquires Windows mouse capture");
    ImGui::GetIO().MouseDown[0]=true;ImGui::GetIO().MouseDrawCursor=true;
    ImGui::GetIO().KeyCtrl=true;ImGui::GetIO().KeysData[ImGuiKey_W-ImGuiKey_NamedKey_BEGIN].Down=true;
    closePopup();
    check(GetCapture()==nullptr,"closing while mouse held leaves no Windows mouse capture");
    check(!ImGui::GetIO().MouseDown[0]&&!ImGui::GetIO().MouseDrawCursor&&context->InputEventsQueue.empty(),"closing clears queued input / held buttons / software cursor");
    check(!ImGui::GetIO().KeyCtrl&&!ImGui::GetIO().KeysData[ImGuiKey_W-ImGuiKey_NamedKey_BEGIN].Down,"closing clears held movement keys / modifiers");
    checkForwarding(mouseDevice,"closed-after-click mouse state forwards unchanged");
    int before=gameMessages;SendMessageW(window,WM_LBUTTONUP,0,MAKELPARAM(100,100));SendMessageW(window,WM_LBUTTONDOWN,MK_LBUTTON,MAKELPARAM(110,100));SendMessageW(window,WM_LBUTTONUP,0,MAKELPARAM(110,100));
    check(gameMessages==before+2,"popup-owned release stays blocked; next game click / release reach original procedure");

    openPopup();SendMessageW(window,WM_LBUTTONDOWN,MK_LBUTTON,MAKELPARAM(100,100));
    togglePopup();check(!opened&&!capturesInput()&&GetCapture()==nullptr&&context->InputEventsQueue.empty(),"F10 toggle uses complete close cleanup");
    checkForwarding(mouseDevice,"F10 restores mouse state");
    SetCapture(window);openPopup();closePopup();
    check(GetCapture()==window,"existing game-owned mouse capture is preserved");ReleaseCapture();
    openPopup();RECT latestClip{10,20,510,420};clipCursor(&latestClip);clipCalled=false;closePopup();
    check(clipCalled&&receivedClip&&EqualRect(receivedClip,&latestClip),"close restores latest game cursor restriction");
    openPopup();clipCursor(&latestClip);inputTestForeground=false;clipCalled=false;
    SendMessageW(window,WM_KILLFOCUS,0,0);check(!clipCalled&&!savedCursorClipValid,"focus loss does not re-confine desktop cursor");
    inputTestForeground=true;
    openPopup();clipCursor(&latestClip);clipCalled=false;
    SendMessageW(window,WM_KILLFOCUS,0,0);check(!clipCalled,"focus-loss message never re-confines cursor even before foreground state updates");

    opened=true;capture=true;unsigned char bytes[32]{};
    check(inputState<0>(mouseDevice,sizeof(bytes),bytes)==DI_OK&&bytes[0]==0&&bytes[31]==0,"open popup masks mouse input");
    check(inputState<0>(reinterpret_cast<IDirectInputDevice8W*>(&keyboard),sizeof(bytes),bytes)==DI_OK&&bytes[0]==0,"open popup masks keyboard input");
    check(inputState<0>(reinterpret_cast<IDirectInputDevice8W*>(&joystick),sizeof(bytes),bytes)==DI_OK&&bytes[0]==0x80,"unrelated input device forwards");
    DIDEVICEOBJECTDATA blockedData{};DWORD blockedCount=1;
    check(inputData<0>(mouseDevice,sizeof(blockedData),&blockedData,&blockedCount,DIGDD_PEEK)==DI_OK&&blockedCount==0&&receivedFlags==0,"open popup drains buffered mouse input instead of replaying it after close");
    inputResult=DIERR_INPUTLOST;
    check(inputState<0>(mouseDevice,sizeof(bytes),bytes)==DIERR_INPUTLOST&&bytes[0]==0x80,"failed input read preserves original result and bytes");inputResult=DI_OK;
    SendMessageW(window,WM_KEYDOWN,VK_ESCAPE,0);
    check(!capturesInput()&&!opened,"Escape ends capture");checkForwarding(mouseDevice,"Escape restores mouse state");
    opened=true;capture=true;SendMessageW(window,WM_KILLFOCUS,0,0);check(!capturesInput(),"focus loss ends capture");
    opened=true;capture=true;fixture.heartbeat=GetTickCount64()-4000;checkForwarding(mouseDevice,"expired heartbeat restores mouse input without rendering");
    fixture.heartbeat=GetTickCount64();inputTestForeground=false;checkForwarding(mouseDevice,"background window forwards input");
    inputTestForeground=true;fixture.status=3;checkForwarding(mouseDevice,"multiplayer forwards input");
    fixture.status=2;opened=false;capture=false;noticeUntil=GetTickCount64()+3000;checkForwarding(mouseDevice,"notice-only mode forwards input");

    // Restore only resources owned by this test, even on a regression failure.
    if(GetCapture()==window)ReleaseCapture();
    SetWindowLongPtrW(window,GWLP_WNDPROC,reinterpret_cast<LONG_PTR>(previousProc));
    ImGui_ImplWin32_Shutdown();ImGui::DestroyContext(context);context=nullptr;
    DestroyWindow(window);UnregisterClassW(klass.lpszClassName,klass.hInstance);
    if(failures)return 1;
    std::cout<<"PASS: Win32 / buffered DirectInput game-held releases; toggle / X / Esc, popup-held repeat, PEEK / count-only / DX3; mouse/keyboard forwarding, focus, expiry, background, multiplayer, notices ("<<sizeof(void*)*8<<" bit).\n";
}
