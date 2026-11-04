#define WIN32_LEAN_AND_MEAN
#define DIRECTINPUT_VERSION 0x0800
#include "overlay.h"
#include <d3d9.h>
#include <d3d11.h>
#include <dinput.h>
#include <algorithm>
#include <array>
#include <atomic>
#include <mutex>
#include <vector>
#include <string>
#include "../../vendor/minhook/include/MinHook.h"
#include "../../vendor/imgui/imgui.h"
#include "../../vendor/imgui/backends/imgui_impl_win32.h"
#include "../../vendor/imgui/backends/imgui_impl_dx9.h"
#include "../../vendor/imgui/backends/imgui_impl_dx11.h"
#pragma comment(lib,"d3d9.lib")
#pragma comment(lib,"d3d11.lib")
#pragma comment(lib,"d3dcompiler.lib")
#pragma comment(lib,"dxgi.lib")
#pragma comment(lib,"dinput8.lib")
#pragma comment(lib,"dxguid.lib")
#pragma comment(lib,"user32.lib")
extern IMGUI_IMPL_API LRESULT ImGui_ImplWin32_WndProcHandler(HWND,UINT,WPARAM,LPARAM);
#ifdef OVERLAY_SMOKE
void overlaySmokeRendered(int api,void* device,IDXGISwapChain* swap);
#endif

namespace {
SharedState* state=nullptr;
std::recursive_mutex uiMutex;
ImGuiContext* context=nullptr;
HWND gameWindow=nullptr,probeWindow=nullptr;
WNDPROC previousProc=nullptr;
std::atomic<bool> capture{false};
bool opened=false,initialized=false,inputReady=false;
RECT savedCursorClip{};
bool savedCursorClipValid=false,savedCursorUnclipped=false;
int renderer=0;
LONG lastToggle=0,lastTriggers=0;
uint64_t noticeUntil=0;
uint64_t noticePlayer=0;
IDirect3DDevice9* device9=nullptr;
ID3D11Device* device11=nullptr;
ID3D11DeviceContext* context11=nullptr;
using Present9=HRESULT(WINAPI*)(IDirect3DDevice9*,const RECT*,const RECT*,HWND,const RGNDATA*);
using PresentSwap9=HRESULT(WINAPI*)(IDirect3DSwapChain9*,const RECT*,const RECT*,HWND,const RGNDATA*,DWORD);
using Reset9=HRESULT(WINAPI*)(IDirect3DDevice9*,D3DPRESENT_PARAMETERS*);
using Present11=HRESULT(WINAPI*)(IDXGISwapChain*,UINT,UINT);
using InputState=HRESULT(WINAPI*)(IDirectInputDevice8W*,DWORD,void*);
using InputData=HRESULT(WINAPI*)(IDirectInputDevice8W*,DWORD,DIDEVICEOBJECTDATA*,DWORD*,DWORD);
Present9 originalPresent9=nullptr;
PresentSwap9 originalSwap9=nullptr;
Reset9 originalReset9=nullptr;
Present11 originalPresent11=nullptr;
InputState originalInputState[2]{};
InputData originalInputData[2]{};
decltype(&SetCursorPos) originalSetCursorPos=nullptr;
decltype(&ClipCursor) originalClipCursor=nullptr;
decltype(&GetKeyboardState) originalKeyboardState=nullptr;
thread_local bool presenting=false;
enum InputOwner:BYTE { ReleasedInput,GameInput,PopupInput };
std::array<BYTE,261> windowInputOwners{}; // virtual keys plus five mouse buttons
std::atomic<ULONG> inputFocusEpoch{0};
struct BufferedDevice {
    IDirectInputDevice8W* device;
    DWORD type;
    ULONG focusEpoch;
    std::array<BYTE,256> owners{};
    std::vector<DIDEVICEOBJECTDATA> releases;
};
std::recursive_mutex bufferedInputMutex;
std::vector<BufferedDevice> bufferedDevices;

// A release belongs to whoever received the down. In particular, opening the
// popup between Ctrl/Alt down and up must not leave the game holding a modifier.
bool routeButton(BYTE& owner,bool down,bool captured){
    if(captured){
        if(!down&&owner==GameInput){owner=ReleasedInput;return true;}
        if(down&&owner==ReleasedInput)owner=PopupInput;
        if(!down)owner=ReleasedInput;
        return false;
    }
    if(owner==PopupInput){if(!down)owner=ReleasedInput;return false;}
    owner=down?GameInput:ReleasedInput;return true;
}

bool alive(){return state&&state->heartbeat>0&&GetTickCount64()-static_cast<uint64_t>(state->heartbeat)<3000&&!state->fault&&state->status>0&&state->status!=3;}
#ifdef OVERLAY_INPUT_TESTS
bool inputTestForeground=true;
bool foreground(){return inputTestForeground;}
#else
bool foreground(){DWORD pid=0;GetWindowThreadProcessId(GetForegroundWindow(),&pid);return pid==GetCurrentProcessId();}
#endif
bool capturesInput(){return capture.load(std::memory_order_relaxed)&&alive()&&foreground();}

bool queue(LONG type,LONG key,LONG value){return alive()&&enqueueOverlayAction(state,type,key,value);}
void clearPopupInput(){
    if(!context)return;
    ImGui::SetCurrentContext(context);
    auto& io=ImGui::GetIO();
    io.ClearEventsQueue();io.ClearInputKeys();io.ClearInputMouse();
    io.MouseDrawCursor=false;io.WantCaptureMouse=false;io.WantCaptureKeyboard=false;io.WantTextInput=false;
}
void closePopup(bool restoreCursor=true){
    std::lock_guard<std::recursive_mutex> lock(uiMutex);
    opened=false;capture=false;if(state)state->overlayVisible=0;
    clearPopupInput();
    // Restore the game's cursor restriction only while it still has focus.
    // Never re-confine the desktop mouse after Alt+Tab / focus loss.
    if(restoreCursor&&savedCursorClipValid&&foreground()&&originalClipCursor)
        originalClipCursor(savedCursorUnclipped?nullptr:&savedCursorClip);
    savedCursorClipValid=false;
}
void openPopup(){
    std::lock_guard<std::recursive_mutex> lock(uiMutex);
    if(!context||!inputReady||opened)return;
    clearPopupInput();
    savedCursorClipValid=GetClipCursor(&savedCursorClip)!=FALSE;
    RECT desktop{GetSystemMetrics(SM_XVIRTUALSCREEN),GetSystemMetrics(SM_YVIRTUALSCREEN),0,0};
    desktop.right=desktop.left+GetSystemMetrics(SM_CXVIRTUALSCREEN);desktop.bottom=desktop.top+GetSystemMetrics(SM_CYVIRTUALSCREEN);
    savedCursorUnclipped=EqualRect(&savedCursorClip,&desktop)!=FALSE;
    opened=true;capture=true;
    if(originalClipCursor)originalClipCursor(nullptr);
}
void togglePopup(){if(opened)closePopup();else openPopup();}
int mouseButton(UINT message,WPARAM wparam,bool& down){
    int button=-1;down=false;
    switch(message){
        case WM_LBUTTONDOWN:case WM_LBUTTONDBLCLK:down=true;[[fallthrough]];
        case WM_LBUTTONUP:button=0;break;
        case WM_RBUTTONDOWN:case WM_RBUTTONDBLCLK:down=true;[[fallthrough]];
        case WM_RBUTTONUP:button=1;break;
        case WM_MBUTTONDOWN:case WM_MBUTTONDBLCLK:down=true;[[fallthrough]];
        case WM_MBUTTONUP:button=2;break;
        case WM_XBUTTONDOWN:case WM_XBUTTONDBLCLK:down=true;[[fallthrough]];
        case WM_XBUTTONUP:button=HIWORD(wparam)==XBUTTON1?3:4;break;
    }
    return button;
}
int windowButton(UINT message,WPARAM wparam,bool& down){
    down=message==WM_KEYDOWN||message==WM_SYSKEYDOWN;
    if((down||message==WM_KEYUP||message==WM_SYSKEYUP)&&wparam<256)return static_cast<int>(wparam);
    int button=mouseButton(message,wparam,down);
    return button<0?-1:256+button;
}
void popupMouse(UINT message,WPARAM wparam,LPARAM lparam){
    auto& io=ImGui::GetIO();
    if(message==WM_MOUSEWHEEL){io.AddMouseWheelEvent(0.f,static_cast<short>(HIWORD(wparam))/static_cast<float>(WHEEL_DELTA));return;}
    if(message==WM_MOUSEHWHEEL){io.AddMouseWheelEvent(-static_cast<short>(HIWORD(wparam))/static_cast<float>(WHEEL_DELTA),0.f);return;}
    io.AddMousePosEvent(static_cast<float>(static_cast<short>(LOWORD(lparam))),static_cast<float>(static_cast<short>(HIWORD(lparam))));
    bool down=false;int button=mouseButton(message,wparam,down);
    if(button>=0)io.AddMouseButtonEvent(button,down);
    // This fixed in-game panel needs no OS mouse capture. The stock Win32
    // backend calls SetCapture on button-down; a close before button-up leaves
    // that capture (and its private held-button mask) behind in the game.
}
LRESULT CALLBACK windowProc(HWND window,UINT message,WPARAM wparam,LPARAM lparam){
    std::lock_guard<std::recursive_mutex> lock(uiMutex);
    bool down=false;int button=windowButton(message,wparam,down);
    if(context&&capturesInput()){
        ImGui::SetCurrentContext(context);
        if(message>=WM_MOUSEFIRST&&message<=WM_MOUSELAST){
            popupMouse(message,wparam,lparam);
            if(button>=0&&routeButton(windowInputOwners[button],down,true))
                return CallWindowProcW(previousProc,window,message,wparam,lparam);
            return message==WM_XBUTTONDOWN||message==WM_XBUTTONUP||message==WM_XBUTTONDBLCLK?TRUE:0;
        }
        ImGui_ImplWin32_WndProcHandler(window,message,wparam,lparam);
        if(button>=0&&routeButton(windowInputOwners[button],down,true))
            return CallWindowProcW(previousProc,window,message,wparam,lparam);
        if(message==WM_KEYDOWN&&wparam==VK_ESCAPE){closePopup();return 0;}
        if((message>=WM_MOUSEFIRST&&message<=WM_MOUSELAST)||(message>=WM_KEYFIRST&&message<=WM_KEYLAST)||message==WM_CHAR||message==WM_INPUT){
            // DefWindowProc performs WM_INPUT cleanup without delivering it to the game.
            return message==WM_INPUT?DefWindowProcW(window,message,wparam,lparam):0;
        }
    }
    if(message==WM_KILLFOCUS||message==WM_ACTIVATEAPP&&!wparam)closePopup(false);
    if(message==WM_KILLFOCUS||message==WM_SETFOCUS||message==WM_ACTIVATEAPP){
        windowInputOwners.fill(ReleasedInput);++inputFocusEpoch;
    }
    // A button used to close the popup can still be physically held. Keep its
    // repeat/up out of gameplay until it is released, regardless of close path.
    if(button>=0&&!routeButton(windowInputOwners[button],down,false))return 0;
    return CallWindowProcW(previousProc,window,message,wparam,lparam);
}

void shutdownUi(){
    closePopup();
    if(context){
        ImGui::SetCurrentContext(context);
        if(initialized){if(renderer==9)ImGui_ImplDX9_Shutdown();else if(renderer==11)ImGui_ImplDX11_Shutdown();ImGui_ImplWin32_Shutdown();}
        ImGui::DestroyContext(context);context=nullptr;
    }
    if(gameWindow&&previousProc&&reinterpret_cast<WNDPROC>(GetWindowLongPtrW(gameWindow,GWLP_WNDPROC))==windowProc)
        SetWindowLongPtrW(gameWindow,GWLP_WNDPROC,reinterpret_cast<LONG_PTR>(previousProc));
    windowInputOwners.fill(ReleasedInput);
    gameWindow=nullptr;previousProc=nullptr;initialized=false;renderer=0;device9=nullptr;
    if(context11){context11->Release();context11=nullptr;}
    if(device11){device11->Release();device11=nullptr;}
}

bool initialize(HWND window,int api,void* device){
    if(!window||window==probeWindow||!IsWindowVisible(window))return false;
    if(initialized&&gameWindow==window&&renderer==api&&(api==9?device9==device:device11==device))return true;
    shutdownUi();
    context=ImGui::CreateContext();
    auto& io=ImGui::GetIO();io.IniFilename=nullptr;io.LogFilename=nullptr;
    io.ConfigFlags|=ImGuiConfigFlags_NavEnableKeyboard|ImGuiConfigFlags_NoMouseCursorChange;
    // The desktop keeps its bundled web fonts. The injected renderer uses a
    // local Windows Chinese font, with no font download or game-file writes.
    wchar_t directory[MAX_PATH]{};GetWindowsDirectoryW(directory,MAX_PATH);
    std::wstring font=std::wstring(directory)+L"\\Fonts\\msyh.ttc";
    char fontPath[MAX_PATH*3]{};WideCharToMultiByte(CP_UTF8,0,font.c_str(),-1,fontPath,sizeof(fontPath),nullptr,nullptr);
    if(GetFileAttributesW(font.c_str())!=INVALID_FILE_ATTRIBUTES)io.Fonts->AddFontFromFileTTF(fontPath,18.f,nullptr,io.Fonts->GetGlyphRangesChineseSimplifiedCommon());
    if(io.Fonts->Fonts.empty())io.Fonts->AddFontDefault();
    ImGui::StyleColorsDark();
    auto& style=ImGui::GetStyle();style.WindowRounding=7;style.FrameRounding=4;style.WindowPadding={18,14};style.ItemSpacing={10,9};
    style.Colors[ImGuiCol_WindowBg]={.08f,.08f,.08f,.97f};
    style.Colors[ImGuiCol_TitleBg]={.09f,.09f,.09f,1};
    style.Colors[ImGuiCol_TitleBgActive]={.12f,.12f,.12f,1};
    style.Colors[ImGuiCol_FrameBg]={.17f,.17f,.17f,1};
    style.Colors[ImGuiCol_FrameBgHovered]={.23f,.23f,.23f,1};
    style.Colors[ImGuiCol_FrameBgActive]={.27f,.27f,.27f,1};
    style.Colors[ImGuiCol_CheckMark]={.3f,.85f,.5f,1};
    style.Colors[ImGuiCol_SliderGrab]=style.Colors[ImGuiCol_CheckMark];
    style.Colors[ImGuiCol_Button]={.13f,.32f,.2f,1};
    style.Colors[ImGuiCol_ButtonHovered]={.18f,.43f,.27f,1};
    style.Colors[ImGuiCol_ButtonActive]={.2f,.5f,.3f,1};
    style.Colors[ImGuiCol_NavHighlight]=style.Colors[ImGuiCol_CheckMark];
    renderer=api;gameWindow=window;
    bool win32=ImGui_ImplWin32_Init(window);
    bool graphics=false;
    if(api==9){device9=static_cast<IDirect3DDevice9*>(device);graphics=ImGui_ImplDX9_Init(device9);}
    else {device11=static_cast<ID3D11Device*>(device);device11->AddRef();device11->GetImmediateContext(&context11);graphics=ImGui_ImplDX11_Init(device11,context11);}
    if(!win32||!graphics){if(graphics){if(api==9)ImGui_ImplDX9_Shutdown();else ImGui_ImplDX11_Shutdown();}if(win32)ImGui_ImplWin32_Shutdown();shutdownUi();state->overlayStatus=-2;return false;}
    SetLastError(0);
    previousProc=reinterpret_cast<WNDPROC>(SetWindowLongPtrW(window,GWLP_WNDPROC,reinterpret_cast<LONG_PTR>(windowProc)));
    if(!previousProc){ImGui_ImplWin32_Shutdown();if(api==9)ImGui_ImplDX9_Shutdown();else ImGui_ImplDX11_Shutdown();shutdownUi();state->overlayStatus=-2;return false;}
    initialized=true;lastTriggers=state->lifeTriggers;lastToggle=state->overlayToggle;noticeUntil=0;noticePlayer=state->player;
    state->overlayStatus=inputReady?api:-3;
    return true;
}

bool frame(){
    ImGui::SetCurrentContext(context);
    LONG triggers=state->lifeTriggers;
    if(!alive()||!foreground()){
        closePopup();noticeUntil=0;lastTriggers=triggers;lastToggle=state->overlayToggle;return false;
    }
    if(lastToggle!=state->overlayToggle){
        lastToggle=state->overlayToggle;togglePopup();
    }
    auto now=GetTickCount64();
    if(noticePlayer!=state->player){noticePlayer=state->player;noticeUntil=0;lastTriggers=triggers;}
    if(triggers!=lastTriggers){if(state->lifeMode==PreventDeath&&(state->applied&Life))noticeUntil=now+3000;lastTriggers=triggers;}
    if(!opened&&noticeUntil<=now)return false;
    if(renderer==9)ImGui_ImplDX9_NewFrame();else ImGui_ImplDX11_NewFrame();
    ImGui_ImplWin32_NewFrame();
    auto& io=ImGui::GetIO();io.MouseDrawCursor=opened;
    float uiScale=std::clamp(std::min(io.DisplaySize.x/1920.f,io.DisplaySize.y/1080.f),1.f,2.f);
    io.FontGlobalScale=uiScale;
    if(opened){
        POINT mouse{};if(GetCursorPos(&mouse)&&ScreenToClient(gameWindow,&mouse))io.AddMousePosEvent(static_cast<float>(mouse.x),static_cast<float>(mouse.y));
        for(int i=0;i<3;++i)io.AddMouseButtonEvent(i,(GetAsyncKeyState(i==0?VK_LBUTTON:i==1?VK_RBUTTON:VK_MBUTTON)&0x8000)!=0);
        struct Key{int native;ImGuiKey key;};
        for(auto key:{Key{VK_TAB,ImGuiKey_Tab},Key{VK_RETURN,ImGuiKey_Enter},Key{VK_SPACE,ImGuiKey_Space},Key{VK_LEFT,ImGuiKey_LeftArrow},Key{VK_RIGHT,ImGuiKey_RightArrow},Key{VK_UP,ImGuiKey_UpArrow},Key{VK_DOWN,ImGuiKey_DownArrow}})
            io.AddKeyEvent(key.key,(GetAsyncKeyState(key.native)&0x8000)!=0);
        if(GetAsyncKeyState(VK_ESCAPE)&0x8000)closePopup();
    }
    ImGui::NewFrame();
    if(opened){
        ImVec2 size{std::min(440.f*uiScale,io.DisplaySize.x-16),std::min(650.f*uiScale,io.DisplaySize.y-16)};
        ImGui::SetNextWindowSize(size,ImGuiCond_Always);
        ImGui::SetNextWindowPos({io.DisplaySize.x-size.x-8,8},ImGuiCond_Always);
        bool visible=opened;
        if(ImGui::Begin("实战工具###gdo-combat",&visible,ImGuiWindowFlags_NoCollapse|ImGuiWindowFlags_NoResize|ImGuiWindowFlags_NoMove|ImGuiWindowFlags_NoSavedSettings)){
            ImGui::TextDisabled("Ctrl+Alt+F10 / Esc 关闭 · DirectX %d",renderer);
            if(state->status!=2)ImGui::TextWrapped("等待进入单人角色；功能偏好仍可保存。");
            ImGui::Separator();
            struct FeatureRow{int flag;const char* name;int shortcut;};
            const FeatureRow features[]={{Life,"无限生命",1},{Mana,"无限蓝量",2},{Kill,"一击必杀",3},{MoveSpeed,"移动速度",4},{AttackSpeed,"最大攻击速度",5},{CastSpeed,"最大施法速度",6},{Cooldown,"技能无冷却",7},{AutoHealth,"自动加血",8},{AutoMana,"自动回蓝",9}};
            ImGui::BeginChild("features",{0,-54*uiScale},false);
            ImGui::BeginDisabled(state->uiWrite!=state->uiRead);
            for(auto feature:features){
                ImGui::PushID(feature.flag);
                bool enabled=(state->requested&feature.flag)!=0;
                ImGui::BeginDisabled(state->status!=2);
                if(ImGui::Checkbox(feature.name,&enabled))queue(SetFeature,feature.flag,enabled?1:0);
                ImGui::EndDisabled();
                ImGui::SameLine();ImGui::TextDisabled("  Ctrl+Alt+F%d",feature.shortcut);
                if(feature.flag==Life){
                    if(ImGui::RadioButton("无限",state->lifeMode==Infinite))queue(SetLifeMode,0,Infinite);
                    ImGui::SameLine();if(ImGui::RadioButton("锁血",state->lifeMode==PreventDeath))queue(SetLifeMode,0,PreventDeath);
                    if(state->lifeMode==PreventDeath)ImGui::TextDisabled("每次触发：存档死亡次数 +1");
                }
                if(feature.flag==MoveSpeed){
                    for(int value:{2,4,8,16}){if(value!=2)ImGui::SameLine();char label[12];sprintf_s(label,"%dx",value);if(ImGui::RadioButton(label,state->movementMultiplier==value))queue(SetMovement,0,value);}
                }
                ImGui::PopID();
            }
            ImGui::EndDisabled();
            if(state->uiWrite!=state->uiRead)ImGui::TextDisabled("正在保存操作…");
            if(state->uiDropped)ImGui::TextWrapped("操作过快，请等待当前操作完成后重试。");
            ImGui::EndChild();
            ImGui::Separator();
            if(ImGui::Button("全部关闭  Ctrl+Alt+F12")){
                // Stop live effects immediately even if preference persistence fails.
                InterlockedExchange(&state->requested,0);
                InterlockedExchange(&state->overlayEmergency,1);
            }
        }
        ImGui::End();
        if(!visible)closePopup();
    }
    if(noticeUntil>now){
        ImGui::SetNextWindowPos({io.DisplaySize.x*.5f,36*uiScale},ImGuiCond_Always,{.5f,0});
        ImGui::SetNextWindowBgAlpha(.92f);
        ImGui::Begin("###gdo-life-notice",nullptr,ImGuiWindowFlags_NoDecoration|ImGuiWindowFlags_AlwaysAutoResize|ImGuiWindowFlags_NoInputs|ImGuiWindowFlags_NoNav|ImGuiWindowFlags_NoSavedSettings|ImGuiWindowFlags_NoFocusOnAppearing);
        ImGui::TextColored({.4f,1.f,.6f,1},"锁血已触发");
        ImGui::Text("你已经死亡了 %u 次",state->deathCount);
        ImGui::TextUnformatted("已拦截致命伤害，保留至少 1 点生命");ImGui::End();
    }
    state->overlayVisible=opened?1:0;
    capture=opened;
    ImGui::Render();return true;
}

void draw9(IDirect3DDevice9* device){
    std::lock_guard<std::recursive_mutex> lock(uiMutex);
    D3DDEVICE_CREATION_PARAMETERS creation{};
    if(FAILED(device->GetCreationParameters(&creation))||!initialize(creation.hFocusWindow,9,device))return;
    if(FAILED(device->TestCooperativeLevel()))return;
    IDirect3DSurface9* back=nullptr,*oldTarget=nullptr,*oldDepth=nullptr;
    if(FAILED(device->GetBackBuffer(0,0,D3DBACKBUFFER_TYPE_MONO,&back)))return;
    device->GetRenderTarget(0,&oldTarget);device->GetDepthStencilSurface(&oldDepth);
    D3DVIEWPORT9 oldViewport{};device->GetViewport(&oldViewport);
    if(SUCCEEDED(device->BeginScene())){
        device->SetRenderTarget(0,back);device->SetDepthStencilSurface(nullptr);
        if(frame()){
            ImGui_ImplDX9_RenderDrawData(ImGui::GetDrawData());
#ifdef OVERLAY_SMOKE
            overlaySmokeRendered(9,device,nullptr);
#endif
        }
        device->SetRenderTarget(0,oldTarget);device->SetDepthStencilSurface(oldDepth);device->SetViewport(&oldViewport);device->EndScene();
    }
    if(oldTarget)oldTarget->Release();if(oldDepth)oldDepth->Release();back->Release();
}
HRESULT WINAPI present9(IDirect3DDevice9* device,const RECT* source,const RECT* destination,HWND window,const RGNDATA* dirty){
    if(!presenting){presenting=true;try{draw9(device);}catch(...){closePopup();state->overlayStatus=-2;}auto result=originalPresent9(device,source,destination,window,dirty);presenting=false;return result;}
    return originalPresent9(device,source,destination,window,dirty);
}
HRESULT WINAPI presentSwap9(IDirect3DSwapChain9* swap,const RECT* source,const RECT* destination,HWND window,const RGNDATA* dirty,DWORD flags){
    if(!presenting){presenting=true;IDirect3DDevice9* device=nullptr;if(SUCCEEDED(swap->GetDevice(&device))){try{draw9(device);}catch(...){closePopup();state->overlayStatus=-2;}device->Release();}auto result=originalSwap9(swap,source,destination,window,dirty,flags);presenting=false;return result;}
    return originalSwap9(swap,source,destination,window,dirty,flags);
}
HRESULT WINAPI reset9(IDirect3DDevice9* device,D3DPRESENT_PARAMETERS* params){
    std::lock_guard<std::recursive_mutex> lock(uiMutex);
    if(initialized&&device==device9){ImGui::SetCurrentContext(context);ImGui_ImplDX9_InvalidateDeviceObjects();}
    return originalReset9(device,params);
}
HRESULT WINAPI present11(IDXGISwapChain* swap,UINT interval,UINT flags){
    if(!(flags&DXGI_PRESENT_TEST)){
        std::lock_guard<std::recursive_mutex> lock(uiMutex);
        ID3D11Device* device=nullptr;
        DXGI_SWAP_CHAIN_DESC desc{};
        if(SUCCEEDED(swap->GetDesc(&desc))&&SUCCEEDED(swap->GetDevice(__uuidof(ID3D11Device),reinterpret_cast<void**>(&device)))){
            try{
                if(initialize(desc.OutputWindow,11,device)&&frame()){
                    // Keep no back-buffer reference across Present/ResizeBuffers.
                    ID3D11Texture2D* buffer=nullptr;ID3D11RenderTargetView* target=nullptr;
                    if(SUCCEEDED(swap->GetBuffer(0,__uuidof(ID3D11Texture2D),reinterpret_cast<void**>(&buffer)))){
                        if(SUCCEEDED(device->CreateRenderTargetView(buffer,nullptr,&target))){
                            ID3D11RenderTargetView* previous[D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT]{};ID3D11DepthStencilView* depth=nullptr;
                            // Upstream backs up VS/PS/GS but explicitly clears
                            // HS/DS/CS without restoring them. Preserve those
                            // stages too: the game's state cache may reuse them.
                            ID3D11HullShader* hull=nullptr;ID3D11DomainShader* domain=nullptr;ID3D11ComputeShader* compute=nullptr;
                            ID3D11ClassInstance* hullClasses[256]{},*domainClasses[256]{},*computeClasses[256]{};
                            UINT hullCount=256,domainCount=256,computeCount=256;
                            context11->HSGetShader(&hull,hullClasses,&hullCount);context11->DSGetShader(&domain,domainClasses,&domainCount);context11->CSGetShader(&compute,computeClasses,&computeCount);
                            context11->OMGetRenderTargets(D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT,previous,&depth);
                            context11->OMSetRenderTargets(1,&target,nullptr);ImGui_ImplDX11_RenderDrawData(ImGui::GetDrawData());
#ifdef OVERLAY_SMOKE
                            overlaySmokeRendered(11,device,swap);
#endif
                            context11->OMSetRenderTargets(D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT,previous,depth);
                            context11->HSSetShader(hull,hullClasses,hullCount);context11->DSSetShader(domain,domainClasses,domainCount);context11->CSSetShader(compute,computeClasses,computeCount);
                            if(hull)hull->Release();if(domain)domain->Release();if(compute)compute->Release();
                            for(UINT i=0;i<hullCount;++i)hullClasses[i]->Release();for(UINT i=0;i<domainCount;++i)domainClasses[i]->Release();for(UINT i=0;i<computeCount;++i)computeClasses[i]->Release();
                            for(auto view:previous)if(view)view->Release();if(depth)depth->Release();target->Release();
                        }
                        buffer->Release();
                    }
                }
            }catch(...){closePopup();state->overlayStatus=-2;}
            device->Release();
        }
    }
    return originalPresent11(swap,interval,flags);
}

DWORD inputDeviceType(IDirectInputDevice8W* device){DIDEVCAPS info{};info.dwSize=sizeof(info);if(FAILED(device->GetCapabilities(&info)))return 0;DWORD type=GET_DIDEVICE_TYPE(info.dwDevType);return type==DI8DEVTYPE_KEYBOARD||type==DI8DEVTYPE_MOUSE?type:0;}
bool keyboardOrMouse(IDirectInputDevice8W* device){return inputDeviceType(device)!=0;}
template<int I> HRESULT WINAPI inputState(IDirectInputDevice8W* device,DWORD size,void* data){
    auto result=originalInputState[I](device,size,data);
    if(SUCCEEDED(result)&&data&&capturesInput()&&keyboardOrMouse(device))ZeroMemory(data,size);
    return result;
}
BufferedDevice& bufferedDevice(IDirectInputDevice8W* device,DWORD type){
    ULONG epoch=inputFocusEpoch.load(std::memory_order_relaxed);
    for(auto& entry:bufferedDevices)if(entry.device==device&&entry.type==type){
        if(entry.focusEpoch!=epoch){entry.owners.fill(ReleasedInput);entry.releases.clear();entry.focusEpoch=epoch;}
        return entry;
    }
    bufferedDevices.push_back({device,type,epoch});return bufferedDevices.back();
}
bool bufferedEvent(BufferedDevice& device,const DIDEVICEOBJECTDATA& event,bool captured){
    DWORD button=event.dwOfs;
    if(device.type==DI8DEVTYPE_MOUSE){
        if(button<DIMOFS_BUTTON0||button>DIMOFS_BUTTON7)return !captured;
        button-=DIMOFS_BUTTON0;
    }
    if(button>=device.owners.size())return !captured;
    return routeButton(device.owners[button],(event.dwData&0x80)!=0,captured);
}
template<int I> HRESULT WINAPI inputData(IDirectInputDevice8W* device,DWORD size,DIDEVICEOBJECTDATA* data,DWORD* count,DWORD flags){
    DWORD type=inputDeviceType(device);
    // Both the legacy DX3 and current DirectInput record layouts are supported.
    if(!type||!count||(size!=FIELD_OFFSET(DIDEVICEOBJECTDATA,uAppData)&&size!=sizeof(DIDEVICEOBJECTDATA)))
        return originalInputData[I](device,size,data,count,flags);
    std::lock_guard<std::recursive_mutex> lock(bufferedInputMutex);
    auto& tracked=bufferedDevice(device,type);
    bool captured=capturesInput();DWORD capacity=*count;
    if(data&&!tracked.releases.empty()){
        // A previous count-only query drained popup input. Its game-owned
        // releases remain available, including if the popup has since closed.
        DWORD probe=0;auto result=originalInputData[I](device,size,data,&probe,0);
        if(FAILED(result)){*count=probe;return result;}
        DWORD copied=std::min(capacity,static_cast<DWORD>(tracked.releases.size()));
        for(DWORD i=0;i<copied;++i)memcpy(reinterpret_cast<BYTE*>(data)+i*size,&tracked.releases[i],size);
        if(captured||!(flags&DIGDD_PEEK))tracked.releases.erase(tracked.releases.begin(),tracked.releases.begin()+copied);
        *count=copied;return result;
    }
    auto result=originalInputData[I](device,size,data,count,captured?flags&~DIGDD_PEEK:flags);
    if(FAILED(result))return result;
    if(!data){
        if(captured){
            DIDEVICEOBJECTDATA discard[64];
            for(int i=0;i<4;++i){
                DWORD entries=64;
                if(FAILED(originalInputData[I](device,sizeof(discard[0]),discard,&entries,0)))break;
                for(DWORD j=0;j<entries;++j)if(bufferedEvent(tracked,discard[j],true))tracked.releases.push_back(discard[j]);
                if(entries<64)break;
            }
            *count=static_cast<DWORD>(tracked.releases.size());
        }else *count+=static_cast<DWORD>(tracked.releases.size());
        return result;
    }
    auto previousOwners=tracked.owners;DWORD written=0;
    for(DWORD i=0;i<*count;++i){
        auto source=reinterpret_cast<BYTE*>(data)+i*size;DIDEVICEOBJECTDATA event{};memcpy(&event,source,size);
        if(bufferedEvent(tracked,event,captured)){
            if(written!=i)memmove(reinterpret_cast<BYTE*>(data)+written*size,source,size);
            ++written;
        }
    }
    if(!captured&&(flags&DIGDD_PEEK))tracked.owners=previousOwners;
    *count=written;return result;
}
BOOL WINAPI setCursorPos(int x,int y){return capturesInput()?TRUE:originalSetCursorPos(x,y);}
BOOL WINAPI clipCursor(const RECT* rect){
    if(!capturesInput())return originalClipCursor(rect);
    std::lock_guard<std::recursive_mutex> lock(uiMutex);
    if(!capturesInput())return originalClipCursor(rect);
    // Remember newer game requests (for example after a resolution change),
    // so closing restores the latest restriction rather than an old rectangle.
    savedCursorClipValid=true;savedCursorUnclipped=rect==nullptr;
    if(rect)savedCursorClip=*rect;
    return originalClipCursor(nullptr);
}
BOOL WINAPI keyboardState(PBYTE keys){auto result=originalKeyboardState(keys);if(result&&keys&&capturesInput())ZeroMemory(keys,256);return result;}

bool hook(void* target,void* detour,void** original){
    if(!target||MH_CreateHook(target,detour,original)!=MH_OK)return false;
    if(MH_EnableHook(target)!=MH_OK){MH_RemoveHook(target);return false;}return true;
}
template<class T,class U> bool hookFn(T target,U detour,T* original){return hook(reinterpret_cast<void*>(target),reinterpret_cast<void*>(detour),reinterpret_cast<void**>(original));}
bool installInput(){
    IDirectInput8W* input=nullptr;
    if(FAILED(DirectInput8Create(GetModuleHandleW(nullptr),DIRECTINPUT_VERSION,IID_IDirectInput8W,reinterpret_cast<void**>(&input),nullptr)))return false;
    void* stateTargets[2]{},*dataTargets[2]{};bool ready=true;
    for(int i=0;i<2;++i){
        IDirectInputDevice8W* device=nullptr;
        if(FAILED(input->CreateDevice(i?GUID_SysKeyboard:GUID_SysMouse,&device,nullptr))){ready=false;continue;}
        auto table=*reinterpret_cast<void***>(device);stateTargets[i]=table[9];dataTargets[i]=table[10];
        if(i&&stateTargets[1]==stateTargets[0])originalInputState[1]=originalInputState[0];
        else ready=hook(table[9],i?reinterpret_cast<void*>(inputState<1>):reinterpret_cast<void*>(inputState<0>),reinterpret_cast<void**>(&originalInputState[i]))&&ready;
        if(i&&dataTargets[1]==dataTargets[0])originalInputData[1]=originalInputData[0];
        else ready=hook(table[10],i?reinterpret_cast<void*>(inputData<1>):reinterpret_cast<void*>(inputData<0>),reinterpret_cast<void**>(&originalInputData[i]))&&ready;
        device->Release();
    }
    input->Release();
    ready=hookFn(&SetCursorPos,setCursorPos,&originalSetCursorPos)&&ready;
    ready=hookFn(&ClipCursor,clipCursor,&originalClipCursor)&&ready;
    ready=hookFn(&GetKeyboardState,keyboardState,&originalKeyboardState)&&ready;
    return ready;
}
}

bool installOverlay(SharedState* shared){
    state=shared;
    WNDCLASSW klass{};klass.lpfnWndProc=DefWindowProcW;klass.hInstance=GetModuleHandleW(nullptr);klass.lpszClassName=L"GrimDawnOptimizer.RenderProbe.v6";
    RegisterClassW(&klass);
    probeWindow=CreateWindowW(klass.lpszClassName,L"",WS_OVERLAPPEDWINDOW,0,0,128,128,nullptr,nullptr,klass.hInstance,nullptr);
    if(!probeWindow){state->overlayStatus=-1;return false;}
    inputReady=installInput();
    bool dx9=false,dx11=false;
    auto d3d=Direct3DCreate9(D3D_SDK_VERSION);
    if(d3d){
        D3DPRESENT_PARAMETERS params{};params.Windowed=TRUE;params.SwapEffect=D3DSWAPEFFECT_DISCARD;params.hDeviceWindow=probeWindow;
        IDirect3DDevice9* device=nullptr;
        if(SUCCEEDED(d3d->CreateDevice(D3DADAPTER_DEFAULT,D3DDEVTYPE_HAL,probeWindow,D3DCREATE_SOFTWARE_VERTEXPROCESSING,&params,&device))){
            auto table=*reinterpret_cast<void***>(device);
            dx9=hook(table[17],reinterpret_cast<void*>(present9),reinterpret_cast<void**>(&originalPresent9));
            hook(table[16],reinterpret_cast<void*>(reset9),reinterpret_cast<void**>(&originalReset9));
            IDirect3DSwapChain9* swap=nullptr;
            if(SUCCEEDED(device->GetSwapChain(0,&swap))){hook((*reinterpret_cast<void***>(swap))[3],reinterpret_cast<void*>(presentSwap9),reinterpret_cast<void**>(&originalSwap9));swap->Release();}
            device->Release();
        }
        d3d->Release();
    }
    DXGI_SWAP_CHAIN_DESC desc{};desc.BufferCount=1;desc.BufferDesc.Format=DXGI_FORMAT_R8G8B8A8_UNORM;desc.BufferUsage=DXGI_USAGE_RENDER_TARGET_OUTPUT;desc.OutputWindow=probeWindow;desc.SampleDesc.Count=1;desc.Windowed=TRUE;desc.SwapEffect=DXGI_SWAP_EFFECT_DISCARD;
    IDXGISwapChain* swap=nullptr;ID3D11Device* device=nullptr;ID3D11DeviceContext* immediate=nullptr;
    if(SUCCEEDED(D3D11CreateDeviceAndSwapChain(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&desc,&swap,&device,nullptr,&immediate))){
        dx11=hook((*reinterpret_cast<void***>(swap))[8],reinterpret_cast<void*>(present11),reinterpret_cast<void**>(&originalPresent11));
        immediate->Release();device->Release();swap->Release();
    }
    DestroyWindow(probeWindow);probeWindow=nullptr;UnregisterClassW(klass.lpszClassName,klass.hInstance);
    if(!dx9&&!dx11)state->overlayStatus=-1;
    return dx9||dx11;
}
