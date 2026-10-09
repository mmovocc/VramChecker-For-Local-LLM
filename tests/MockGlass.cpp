// Isolated test DLL; never ship this as the real renderer.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#define API extern "C" __declspec(dllexport)
static volatile LONG draws=0, wrongThread=0, creates=0, destroys=0, rate=0, uploads=0, fail=0;
static LONG refresh=60;
struct Renderer { DWORD owner; };
static void Owner(Renderer* r) { if(r->owner!=GetCurrentThreadId()) InterlockedIncrement(&wrongThread); }
API void* GlassCreate(HWND,HWND,const wchar_t*) { InterlockedIncrement(&creates); return new Renderer{GetCurrentThreadId()}; }
API void GlassDestroy(Renderer* r) { Owner(r); InterlockedIncrement(&destroys); delete r; }
API const wchar_t* GlassLastError() { return L"injected render failure"; }
API int GlassOverlay(Renderer* r,const void*,int,int,int) { Owner(r); InterlockedIncrement(&uploads); return 0; }
API int GlassRender(Renderer* r,float,float,float,float,int,int,int hz,int flags,float) {
    Owner(r); InterlockedExchange(&rate,hz); InterlockedIncrement(&draws);
    Sleep((flags&4) ? DWORD((1000+refresh-1)/refresh) : 8); // Native display pacing during boost.
    return InterlockedExchange(&fail,0) ? -1 : 0;
}
API int GlassInterval(Renderer* r) { Owner(r); return 100; }
API int GlassRefreshRate(Renderer* r) { Owner(r); return refresh; }
API void GlassPlacement(Renderer* r,int* v) { Owner(r); for(int i=0;i<5;i++) v[i]=0; }
API void MockRefresh(int hz) { refresh=hz; }
API int GlassSave(Renderer* r,const wchar_t*,int) { Owner(r); return 0; }
API int GlassCompare(Renderer* r,const wchar_t*,const wchar_t*) { Owner(r); return 0; }
API int GlassTrace(Renderer* r,const wchar_t*) { Owner(r); return 0; }
API void GlassCounters(Renderer* r,unsigned long long* v) { Owner(r); for(int i=0;i<6;i++) v[i]=0; v[0]=draws; }
API int MockValue(int key) {
    switch(key) { case 0:return draws; case 1:return wrongThread; case 2:return creates;
    case 3:return destroys; case 4:return rate; case 5:return uploads; } return -1;
}
API void MockFail() { InterlockedExchange(&fail,1); }
