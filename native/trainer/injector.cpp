#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <tlhelp32.h>
#include <cstdio>
#include <cwchar>

static uintptr_t remoteModule(DWORD pid,const wchar_t* module){
    HANDLE s=CreateToolhelp32Snapshot(TH32CS_SNAPMODULE|TH32CS_SNAPMODULE32,pid);
    if(s==INVALID_HANDLE_VALUE)return 0;
    MODULEENTRY32W row{};row.dwSize=sizeof(row);uintptr_t found=0;
    for(BOOL ok=Module32FirstW(s,&row);ok;ok=Module32NextW(s,&row))if(_wcsicmp(row.szModule,module)==0){found=reinterpret_cast<uintptr_t>(row.modBaseAddr);break;}
    CloseHandle(s);return found;
}
int wmain(int argc,wchar_t** argv){
    if(argc!=3){fwprintf(stderr,L"Usage: injector PID DLL\n");return 1;}
    DWORD pid=wcstoul(argv[1],nullptr,10);const wchar_t* path=argv[2];
    const wchar_t* filename=wcsrchr(path,L'\\');filename=filename?filename+1:path;
    if(remoteModule(pid,filename)){puts("already-loaded");return 0;}
    HANDLE process=OpenProcess(PROCESS_CREATE_THREAD|PROCESS_QUERY_INFORMATION|PROCESS_VM_OPERATION|PROCESS_VM_WRITE|PROCESS_VM_READ,FALSE,pid);
    if(!process){fprintf(stderr,"OpenProcess failed: %lu\n",GetLastError());return 2;}
    auto load=GetProcAddress(GetModuleHandleW(L"kernel32.dll"),"LoadLibraryW");
    HMODULE owner=nullptr;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,reinterpret_cast<LPCWSTR>(load),&owner);
    wchar_t modulePath[MAX_PATH];GetModuleFileNameW(owner,modulePath,MAX_PATH);
    const wchar_t* ownerName=wcsrchr(modulePath,L'\\');ownerName=ownerName?ownerName+1:modulePath;
    uintptr_t base=remoteModule(pid,ownerName);
    if(!base){CloseHandle(process);return 3;}
    auto target=reinterpret_cast<LPTHREAD_START_ROUTINE>(base+reinterpret_cast<uintptr_t>(load)-reinterpret_cast<uintptr_t>(owner));
    SIZE_T bytes=(wcslen(path)+1)*sizeof(wchar_t),written=0;
    void* remote=VirtualAllocEx(process,nullptr,bytes,MEM_COMMIT|MEM_RESERVE,PAGE_READWRITE);
    if(!remote||!WriteProcessMemory(process,remote,path,bytes,&written)||written!=bytes){if(remote)VirtualFreeEx(process,remote,0,MEM_RELEASE);CloseHandle(process);return 4;}
    HANDLE thread=CreateRemoteThread(process,nullptr,0,target,remote,0,nullptr);
    if(!thread){VirtualFreeEx(process,remote,0,MEM_RELEASE);CloseHandle(process);return 5;}
    DWORD wait=WaitForSingleObject(thread,10000);
    if(wait==WAIT_OBJECT_0)VirtualFreeEx(process,remote,0,MEM_RELEASE);
    CloseHandle(thread);CloseHandle(process);
    if(wait!=WAIT_OBJECT_0){puts("Load timed out. Restart the game before retrying.");return 6;}
    if(!remoteModule(pid,filename)){puts("The game could not load the bridge.");return 7;}
    puts("loaded");return 0;
}
