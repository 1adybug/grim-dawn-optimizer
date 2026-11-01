// Standalone GPU regression. Owns only its test window/device; never discovers,
// opens, launches or injects into the game. Run separately for DX9/11 and fullscreen.
#define OVERLAY_SMOKE
#include "overlay.cpp"
#include <cassert>
#include <iostream>
#include <fstream>
#include <d3dcompiler.h>

static SharedState smokeState{};
static int smokeFrame=0;
static bool popupPixels=false,noticePixels=false,resetPixels=false;
static int lateDraws=0;
static const wchar_t* screenshotPath=nullptr;
static void examine(const unsigned char* data,int pitch,int width,int height,bool rgba){
    int changed=0;
    int left=smokeFrame==30?width-430:width/2-220;
    int right=smokeFrame==30?width-16:width/2+220;
    for(int y=40;y<std::min(120,height);++y)for(int x=std::max(0,left);x<std::min(right,width);++x){
        auto pixel=data+y*pitch+x*4;
        if(abs(pixel[0]-(rgba?25:45))>12||abs(pixel[1]-35)>12||abs(pixel[2]-(rgba?45:25))>12)++changed;
    }
    if(smokeFrame==30){popupPixels=changed>200;assert(capturesInput());}
    if(smokeFrame==95){noticePixels=changed>200;assert(!capturesInput());}
    if(smokeFrame==180)resetPixels=changed>200;
    if(screenshotPath&&(smokeFrame==30||smokeFrame==95)){
        BITMAPFILEHEADER file{};BITMAPINFOHEADER info{};
        info.biSize=sizeof(info);info.biWidth=width;info.biHeight=-height;info.biPlanes=1;info.biBitCount=32;info.biCompression=BI_RGB;
        file.bfType=0x4d42;file.bfOffBits=sizeof(file)+sizeof(info);file.bfSize=file.bfOffBits+width*height*4;
        std::wstring outputPath=screenshotPath;if(smokeFrame==30)outputPath+=L".popup.bmp";
        std::ofstream output(outputPath,std::ios::binary);output.write(reinterpret_cast<char*>(&file),sizeof(file));output.write(reinterpret_cast<char*>(&info),sizeof(info));
        std::vector<unsigned char> row(width*4);
        for(int y=0;y<height;++y){memcpy(row.data(),data+y*pitch,row.size());if(rgba)for(int x=0;x<width;++x)std::swap(row[x*4],row[x*4+2]);output.write(reinterpret_cast<char*>(row.data()),row.size());}
    }
}
void overlaySmokeRendered(int api,void* device,IDXGISwapChain* swap){
    if(smokeFrame>=290)++lateDraws;
    if(smokeFrame!=30&&smokeFrame!=95&&smokeFrame!=180)return;
    if(api==9){
        auto d=static_cast<IDirect3DDevice9*>(device);IDirect3DSurface9* target=nullptr,*copy=nullptr;
        assert(SUCCEEDED(d->GetRenderTarget(0,&target)));D3DSURFACE_DESC desc{};target->GetDesc(&desc);
        assert(SUCCEEDED(d->CreateOffscreenPlainSurface(desc.Width,desc.Height,desc.Format,D3DPOOL_SYSTEMMEM,&copy,nullptr)));
        assert(SUCCEEDED(d->GetRenderTargetData(target,copy)));D3DLOCKED_RECT rect{};assert(SUCCEEDED(copy->LockRect(&rect,nullptr,D3DLOCK_READONLY)));
        examine(static_cast<unsigned char*>(rect.pBits),rect.Pitch,desc.Width,desc.Height,false);copy->UnlockRect();copy->Release();target->Release();
    }else{
        auto d=static_cast<ID3D11Device*>(device);ID3D11Texture2D* target=nullptr,*copy=nullptr;ID3D11DeviceContext* immediate=nullptr;d->GetImmediateContext(&immediate);
        assert(SUCCEEDED(swap->GetBuffer(0,__uuidof(ID3D11Texture2D),reinterpret_cast<void**>(&target))));D3D11_TEXTURE2D_DESC desc{};target->GetDesc(&desc);
        desc.Usage=D3D11_USAGE_STAGING;desc.BindFlags=0;desc.CPUAccessFlags=D3D11_CPU_ACCESS_READ;desc.MiscFlags=0;
        assert(SUCCEEDED(d->CreateTexture2D(&desc,nullptr,&copy)));immediate->CopyResource(copy,target);D3D11_MAPPED_SUBRESOURCE mapped{};
        assert(SUCCEEDED(immediate->Map(copy,0,D3D11_MAP_READ,0,&mapped)));
        examine(static_cast<unsigned char*>(mapped.pData),mapped.RowPitch,desc.Width,desc.Height,true);immediate->Unmap(copy,0);
        copy->Release();target->Release();immediate->Release();
    }
}

int wmain(int argc,wchar_t** argv){
    _set_abort_behavior(0,_WRITE_ABORT_MSG|_CALL_REPORTFAULT);
    _set_error_mode(_OUT_TO_STDERR);
    int api=argc>1?_wtoi(argv[1]):11;bool fullscreen=argc>2&&wcscmp(argv[2],L"fullscreen")==0;
    screenshotPath=argc>3?argv[3]:nullptr;
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    WNDCLASSW klass{};klass.lpfnWndProc=DefWindowProcW;klass.hInstance=GetModuleHandleW(nullptr);klass.lpszClassName=L"GDO.Overlay.RenderTest";RegisterClassW(&klass);
    int width=fullscreen?GetSystemMetrics(SM_CXSCREEN):1280,height=fullscreen?GetSystemMetrics(SM_CYSCREEN):800;
    HWND window=CreateWindowW(klass.lpszClassName,L"Grim Dawn Optimizer — DirectX overlay test",WS_POPUP,0,0,width,height,nullptr,nullptr,klass.hInstance,nullptr);
    ShowWindow(window,SW_SHOW);SetForegroundWindow(window);SetFocus(window);
    assert(MH_Initialize()==MH_OK);
    smokeState.magic=Magic;smokeState.version=Protocol;smokeState.heartbeat=GetTickCount64();smokeState.status=2;smokeState.applied=Life;smokeState.lifeMode=PreventDeath;smokeState.movementMultiplier=2;
    assert(installOverlay(&smokeState));
    SetForegroundWindow(window);SetFocus(window);
    IDirect3D9* d3d=nullptr;IDirect3DDevice9* d9=nullptr;IDXGISwapChain* swap=nullptr;ID3D11Device* d11=nullptr;ID3D11DeviceContext* immediate=nullptr;
    ID3D11ComputeShader* smokeCompute=nullptr;
    D3DPRESENT_PARAMETERS params{};
    if(api==9){
        d3d=Direct3DCreate9(D3D_SDK_VERSION);assert(d3d);D3DDISPLAYMODE mode{};d3d->GetAdapterDisplayMode(D3DADAPTER_DEFAULT,&mode);
        if(fullscreen){width=mode.Width;height=mode.Height;SetWindowPos(window,HWND_TOP,0,0,width,height,SWP_SHOWWINDOW);SetForegroundWindow(window);}
        params.Windowed=!fullscreen;params.SwapEffect=D3DSWAPEFFECT_DISCARD;params.hDeviceWindow=window;params.BackBufferWidth=width;params.BackBufferHeight=height;params.BackBufferFormat=fullscreen?mode.Format:D3DFMT_UNKNOWN;
        auto created=d3d->CreateDevice(D3DADAPTER_DEFAULT,D3DDEVTYPE_HAL,window,D3DCREATE_SOFTWARE_VERTEXPROCESSING,&params,&d9);
        if(FAILED(created))std::cout<<"DX9 CreateDevice failed: 0x"<<std::hex<<created<<std::dec<<" mode="<<width<<"x"<<height<<" format="<<params.BackBufferFormat<<" foreground="<<foreground()<<std::endl;
        assert(SUCCEEDED(created));
        IDirect3DSwapChain9* chain=nullptr;assert(SUCCEEDED(d9->GetSwapChain(0,&chain)));D3DPRESENT_PARAMETERS actual{};chain->GetPresentParameters(&actual);chain->Release();assert(actual.Windowed==!fullscreen);
    }else{
        DXGI_SWAP_CHAIN_DESC desc{};desc.BufferCount=1;desc.BufferDesc.Width=width;desc.BufferDesc.Height=height;desc.BufferDesc.Format=DXGI_FORMAT_R8G8B8A8_UNORM;desc.BufferUsage=DXGI_USAGE_RENDER_TARGET_OUTPUT;desc.OutputWindow=window;desc.SampleDesc.Count=1;desc.Windowed=TRUE;desc.SwapEffect=DXGI_SWAP_EFFECT_DISCARD;desc.Flags=DXGI_SWAP_CHAIN_FLAG_ALLOW_MODE_SWITCH;
        assert(SUCCEEDED(D3D11CreateDeviceAndSwapChain(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&desc,&swap,&d11,nullptr,&immediate)));
        if(fullscreen)assert(SUCCEEDED(swap->SetFullscreenState(TRUE,nullptr)));
        BOOL actual=FALSE;swap->GetFullscreenState(&actual,nullptr);assert((actual!=FALSE)==fullscreen);
        const char shader[]="[numthreads(1,1,1)] void main(uint3 id : SV_DispatchThreadID) {}";
        ID3DBlob* blob=nullptr;assert(SUCCEEDED(D3DCompile(shader,sizeof(shader),nullptr,nullptr,nullptr,"main","cs_5_0",0,0,&blob,nullptr)));
        assert(SUCCEEDED(d11->CreateComputeShader(blob->GetBufferPointer(),blob->GetBufferSize(),nullptr,&smokeCompute)));blob->Release();
    }
    for(smokeFrame=0;smokeFrame<320;++smokeFrame){
        MSG message{};while(PeekMessageW(&message,nullptr,0,0,PM_REMOVE)){TranslateMessage(&message);DispatchMessageW(&message);}
        if(smokeFrame==200)Sleep(3100);
        smokeState.heartbeat=GetTickCount64();
        if(smokeFrame==20||smokeFrame==60)InterlockedIncrement(&smokeState.overlayToggle);
        if(smokeFrame==80||smokeFrame==170)InterlockedIncrement(&smokeState.lifeTriggers);
        if(smokeFrame==160){
            if(api==9)assert(SUCCEEDED(d9->Reset(&params)));
            else assert(SUCCEEDED(swap->ResizeBuffers(0,width,height,DXGI_FORMAT_UNKNOWN,DXGI_SWAP_CHAIN_FLAG_ALLOW_MODE_SWITCH)));
        }
        if(smokeFrame>=310)smokeState.heartbeat-=4000;
        if(api==9){
            d9->Clear(0,nullptr,D3DCLEAR_TARGET,D3DCOLOR_XRGB(25,35,45),1,0);
            D3DVIEWPORT9 viewport{16,20,static_cast<DWORD>(width-32),static_cast<DWORD>(height-40),0,1};d9->SetViewport(&viewport);
            auto presented=d9->Present(nullptr,nullptr,nullptr,nullptr);
            if(FAILED(presented))std::cout<<"DX9 Present failed at frame "<<smokeFrame<<": 0x"<<std::hex<<presented<<std::dec<<" foreground="<<foreground()<<std::endl;
            assert(SUCCEEDED(presented));
            D3DVIEWPORT9 restored{};d9->GetViewport(&restored);assert(memcmp(&viewport,&restored,sizeof(viewport))==0);
        }
        else{
            ID3D11Texture2D* buffer=nullptr;ID3D11RenderTargetView* target=nullptr;
            assert(SUCCEEDED(swap->GetBuffer(0,__uuidof(ID3D11Texture2D),reinterpret_cast<void**>(&buffer))));assert(SUCCEEDED(d11->CreateRenderTargetView(buffer,nullptr,&target)));
            const float clear[]={25.f/255,35.f/255,45.f/255,1};immediate->ClearRenderTargetView(target,clear);target->Release();buffer->Release();
            immediate->CSSetShader(smokeCompute,nullptr,0);
            assert(SUCCEEDED(swap->Present(0,0)));
            ID3D11ComputeShader* restored=nullptr;immediate->CSGetShader(&restored,nullptr,nullptr);assert(restored==smokeCompute);restored->Release();
        }
        Sleep(16);
    }
    std::cout<<"GPU evidence: popup="<<popupPixels<<" notice="<<noticePixels<<" reset="<<resetPixels<<" late="<<lateDraws<<" status="<<smokeState.overlayStatus<<" input="<<inputReady<<" foreground="<<foreground()<<std::endl;
    assert(popupPixels&&noticePixels&&resetPixels&&lateDraws==0);
    assert(!capturesInput()&&!smokeState.overlayVisible);
    shutdownUi();MH_DisableHook(MH_ALL_HOOKS);MH_Uninitialize();
    if(swap){swap->SetFullscreenState(FALSE,nullptr);smokeCompute->Release();swap->Release();immediate->Release();d11->Release();}
    if(d9){d9->Release();d3d->Release();}
    DestroyWindow(window);UnregisterClassW(klass.lpszClassName,klass.hInstance);
    std::cout<<"PASS: DirectX "<<api<<(fullscreen?" exclusive fullscreen":" windowed")<<" ("<<sizeof(void*)*8<<" bit): framebuffer popup/notice pixels, input capture only for popup, device reset/resize, notice expiry, heartbeat fail-open.\n";
}
