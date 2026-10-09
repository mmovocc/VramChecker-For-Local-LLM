#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_3.h>
#include <dcomp.h>
#include <dwmapi.h>
#include <d3dcompiler.h>
#include <wrl/client.h>
#include <vector>
#include <string>
#include <stdexcept>
#include <algorithm>
#include <cstdio>
#include <cmath>
#include <cstring>
#include <memory>
#include <map>
#include <mutex>
#include "SamplingClock.h"
#include "BackdropCache.h"
#include "MotionPrediction.h"
#include "LatencyTrace.h"
using Microsoft::WRL::ComPtr;
static thread_local std::wstring lastError;
static void Check(HRESULT hr, const char* operation) {
    if (FAILED(hr)) { char text[256]; sprintf_s(text, "%s (0x%08X)", operation, unsigned(hr)); throw std::runtime_error(text); }
}
static void Error(const std::exception& e) {
    const char* text=e.what();
    int n=MultiByteToWideChar(CP_ACP,0,text,-1,nullptr,0);
    if(n<=1) { lastError=L"Unknown error"; return; }
    lastError.resize(size_t(n));
    MultiByteToWideChar(CP_ACP,0,text,-1,&lastError[0],n);
    lastError.resize(size_t(n-1));
}
// DwmFlush only paces presentation. It fails transiently while composition is
// unavailable (lock screen, mode change); that must not tear down the device.
static void WaitForComposition() { DwmFlush(); }
// Bytecode is device independent. Window re-creation (hide/show, monitor change)
// and multiple glass windows reuse it instead of recompiling at O3 each time.
static ComPtr<ID3DBlob> CompileCached(const std::string& source,const char* entry,const char* profile) {
    static std::mutex gate;
    static std::map<std::string,ComPtr<ID3DBlob>> cache;
    std::string key=std::string(entry)+'\n'+profile+'\n'+source;
    {
        std::lock_guard<std::mutex> lock(gate);
        auto found=cache.find(key);
        if(found!=cache.end()) return found->second;
    }
    ComPtr<ID3DBlob> code, errors;
    HRESULT hr=D3DCompile(source.data(),source.size(),"LiquidGlass.hlsl",nullptr,nullptr,entry,profile,D3DCOMPILE_OPTIMIZATION_LEVEL3,0,&code,&errors);
    if(FAILED(hr)) throw std::runtime_error(errors ? (const char*)errors->GetBufferPointer() : "Shader compile failed");
    std::lock_guard<std::mutex> lock(gate);
    return cache.emplace(std::move(key),code).first->second;
}
struct Constants {
    float windowSize[2], windowOrigin[2], desktopSize[2], desktopOrigin[2];
    float pointer[2], radius, refraction, blur, tint, material, hasDesktop;
};
static_assert(sizeof(Constants)%16==0,"Constant buffer must be a multiple of 16 bytes");
struct Renderer {
    HWND hwnd, host; HMONITOR monitor = nullptr;
    bool syncActive=false, lastDragging=false;
    double lastDragTime=-1000;
    int placement[5]{};
    double desktopFrameTime=0;
    MotionPrediction prediction;
    LatencyTrace trace;
    RECT desktopRect{}; int width=0, height=0, overlayW=0, overlayH=0;
    bool haveFrame=false; ULONGLONG nextRetry=0;
    RECT cropRect{};
    bool renderDirty=true, frameHeld=false;
    Constants previousConstants{};
    bool haveConstants=false;
    ComPtr<ID3D11Texture2D> heldDesktop;
    std::vector<RECT> dirtyRects;
    std::vector<DXGI_OUTDUPL_MOVE_RECT> moveRects;
    unsigned long long draws=0, skips=0, copies=0, copiedPixels=0;
    unsigned long long geometryBuilds=0;
    float cachedRadius=-1;
    bool geometryDirty=true;
    int nextInterval=16, idlePolls=0;
    int refreshHz=60;
    std::string shader;
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<IDCompositionDevice> composition;
    ComPtr<IDCompositionTarget> target;
    ComPtr<IDCompositionVisual> visual;
    ComPtr<IDCompositionTarget> hostTarget;
    ComPtr<IDCompositionVisual> hostVisual;
    ComPtr<IDCompositionSurface> hostSurface;
    ComPtr<IDCompositionSurface> emptySurface;
    ComPtr<IDXGIOutput1> output;
    ComPtr<IDXGIOutputDuplication> duplication;
    ComPtr<ID3D11Texture2D> desktop, overlay, backbuffer;
    ComPtr<ID3D11ShaderResourceView> desktopView, overlayView;
    ComPtr<ID3D11RenderTargetView> renderTarget;
    ComPtr<ID3D11VertexShader> vs;
    ComPtr<ID3D11PixelShader> ps;
    ComPtr<ID3D11PixelShader> geometryShader;
    ComPtr<ID3D11Texture2D> geometryTexture[2];
    ComPtr<ID3D11RenderTargetView> geometryTarget[2];
    ComPtr<ID3D11ShaderResourceView> geometryView[2];
    ComPtr<ID3D11Buffer> constants;
    ComPtr<ID3D11SamplerState> sampler;
    ComPtr<ID3D11RasterizerState> rasterizer;
    Renderer(HWND h, HWND screenHost, const wchar_t* shaderPath) : hwnd(h), host(screenHost) {
        FILE* f=nullptr; _wfopen_s(&f,shaderPath,L"rb");
        if (!f) throw std::runtime_error("Cannot open LiquidGlass.hlsl");
        long n=fseek(f,0,SEEK_END)==0 ? ftell(f) : -1;
        if(n>0) { rewind(f); shader.resize(size_t(n)); if(fread(&shader[0],1,size_t(n),f)!=size_t(n)) n=-1; }
        fclose(f);
        if(n<=0) throw std::runtime_error("Cannot read LiquidGlass.hlsl");
        // The HWND owner excludes the window before starting this render thread.
        Initialize();
    }
    ~Renderer() { trace.Finish(); ShowWindowAsync(host,SW_HIDE); ReleaseCaptureFrame(); if(context) { context->ClearState(); context->Flush(); } }
    ComPtr<ID3DBlob> Compile(const char* entry,const char* profile) { return CompileCached(shader,entry,profile); }
    LONG OutputWidth() const { return desktopRect.right-desktopRect.left; }
    LONG OutputHeight() const { return desktopRect.bottom-desktopRect.top; }
    // The full-output drag cache stays screen-anchored, so positions can be re-read freely.
    bool FullOutputCached() const {
        return haveFrame && cropRect.left==0 && cropRect.top==0 && cropRect.right==OutputWidth() && cropRect.bottom==OutputHeight();
    }
    void Initialize() {
        monitor=MonitorFromWindow(hwnd,MONITOR_DEFAULTTONEAREST);
        ComPtr<IDXGIFactory1> factory;
        Check(CreateDXGIFactory1(IID_PPV_ARGS(&factory)),"CreateDXGIFactory1");
        ComPtr<IDXGIAdapter1> selected;
        for(UINT a=0; !selected; ++a) {
            ComPtr<IDXGIAdapter1> adapter;
            if(factory->EnumAdapters1(a,&adapter)==DXGI_ERROR_NOT_FOUND) break;
            for(UINT o=0;;++o) {
                ComPtr<IDXGIOutput> candidate;
                if(adapter->EnumOutputs(o,&candidate)==DXGI_ERROR_NOT_FOUND) break;
                DXGI_OUTPUT_DESC desc{}; candidate->GetDesc(&desc);
                if(desc.Monitor==monitor) {
                    selected=adapter; candidate.As(&output); desktopRect=desc.DesktopCoordinates;
                    DEVMODEW mode{}; mode.dmSize=sizeof(mode);
                    if(EnumDisplaySettingsExW(desc.DeviceName,ENUM_CURRENT_SETTINGS,&mode,0) && mode.dmDisplayFrequency>1)
                        refreshHz=int(mode.dmDisplayFrequency);
                    break;
                }
            }
        }
        if(!selected || !output) throw std::runtime_error("No desktop output found for this window");
        D3D_FEATURE_LEVEL levels[]={D3D_FEATURE_LEVEL_11_0};
        Check(D3D11CreateDevice(selected.Get(),D3D_DRIVER_TYPE_UNKNOWN,nullptr,D3D11_CREATE_DEVICE_BGRA_SUPPORT,levels,1,D3D11_SDK_VERSION,&device,nullptr,&context),"D3D11CreateDevice");
        auto vertex=Compile("VSMain","vs_5_0"), pixel=Compile("PSMain","ps_5_0");
        Check(device->CreateVertexShader(vertex->GetBufferPointer(),vertex->GetBufferSize(),nullptr,&vs),"CreateVertexShader");
        Check(device->CreatePixelShader(pixel->GetBufferPointer(),pixel->GetBufferSize(),nullptr,&ps),"CreatePixelShader");
        auto geometry=Compile("PSGeometry","ps_5_0");
        Check(device->CreatePixelShader(geometry->GetBufferPointer(),geometry->GetBufferSize(),nullptr,&geometryShader),"Create geometry shader");
        D3D11_BUFFER_DESC cb{}; cb.ByteWidth=sizeof(Constants); cb.Usage=D3D11_USAGE_DEFAULT; cb.BindFlags=D3D11_BIND_CONSTANT_BUFFER;
        Check(device->CreateBuffer(&cb,nullptr,&constants),"CreateBuffer");
        D3D11_SAMPLER_DESC sd{}; sd.Filter=D3D11_FILTER_MIN_MAG_MIP_LINEAR; sd.AddressU=sd.AddressV=sd.AddressW=D3D11_TEXTURE_ADDRESS_CLAMP; sd.MaxLOD=D3D11_FLOAT32_MAX;
        Check(device->CreateSamplerState(&sd,&sampler),"CreateSamplerState");
        D3D11_RASTERIZER_DESC rd{}; rd.FillMode=D3D11_FILL_SOLID; rd.CullMode=D3D11_CULL_NONE; rd.DepthClipEnable=TRUE;
        Check(device->CreateRasterizerState(&rd,&rasterizer),"CreateRasterizerState");
        ComPtr<IDXGIDevice> dxgi; device.As(&dxgi);
        Check(DCompositionCreateDevice(dxgi.Get(),IID_PPV_ARGS(&composition)),"DCompositionCreateDevice");
        Check(composition->CreateTargetForHwnd(hwnd,TRUE,&target),"CreateTargetForHwnd");
        Check(composition->CreateVisual(&visual),"CreateVisual");
        Check(target->SetRoot(visual.Get()),"SetRoot");
        Check(composition->CreateTargetForHwnd(host,TRUE,&hostTarget),"Create fixed host target");
        Check(composition->CreateVisual(&hostVisual),"Create fixed host visual");
        Check(hostTarget->SetRoot(hostVisual.Get()),"Set fixed host root");
        // An explicit transparent surface avoids an empty no-redirection HWND
        // falling back to a window background when the mouse capture changes.
        Check(composition->CreateSurface(1,1,DXGI_FORMAT_B8G8R8A8_UNORM,DXGI_ALPHA_MODE_PREMULTIPLIED,&emptySurface),"Create transparent surface");
        D3D11_TEXTURE2D_DESC clearDesc{}; clearDesc.Width=clearDesc.Height=clearDesc.MipLevels=clearDesc.ArraySize=1;
        clearDesc.Format=DXGI_FORMAT_B8G8R8A8_UNORM; clearDesc.SampleDesc.Count=1;
        UINT zero=0; D3D11_SUBRESOURCE_DATA clearData{&zero,4,0};
        ComPtr<ID3D11Texture2D> clearTexture;
        Check(device->CreateTexture2D(&clearDesc,&clearData,&clearTexture),"Create transparent pixel");
        ComPtr<IDXGISurface> clearUpdate; POINT clearOffset{};
        Check(emptySurface->BeginDraw(nullptr,IID_PPV_ARGS(&clearUpdate),&clearOffset),"Begin transparent surface");
        ComPtr<ID3D11Texture2D> clearTarget;
        HRESULT clearResult=clearUpdate.As(&clearTarget);
        if(SUCCEEDED(clearResult)) context->CopySubresourceRegion(clearTarget.Get(),0,UINT(clearOffset.x),UINT(clearOffset.y),0,clearTexture.Get(),0,nullptr);
        Check(emptySurface->EndDraw(),"End transparent surface");
        Check(clearResult,"Transparent surface texture");
        Check(visual->SetContent(emptySurface.Get()),"Initialize transparent owner");
        Check(hostVisual->SetContent(emptySurface.Get()),"Initialize transparent host");
        RECT rc; GetClientRect(hwnd,&rc); Resize(std::max(1L,rc.right),std::max(1L,rc.bottom));
    }
    void Resize(int w,int h) {
        if(w==width && h==height) return;
        context->OMSetRenderTargets(0,nullptr,nullptr);
        renderTarget.Reset(); backbuffer.Reset();
        // One offscreen render target and one composition surface for BOTH states.
        // No flip-buffer reattachment or separate Present transaction at drag release.
        D3D11_TEXTURE2D_DESC desc{}; desc.Width=w; desc.Height=h;
        desc.MipLevels=desc.ArraySize=1; desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;
        desc.SampleDesc.Count=1; desc.BindFlags=D3D11_BIND_RENDER_TARGET;
        Check(device->CreateTexture2D(&desc,nullptr,&backbuffer),"Create glass render texture");
        Check(device->CreateRenderTargetView(backbuffer.Get(),nullptr,&renderTarget),"CreateRenderTargetView");
        width=w; height=h;
        hostSurface.Reset();
        for(int i=0;i<2;i++) { geometryView[i].Reset(); geometryTarget[i].Reset(); geometryTexture[i].Reset(); }
        geometryDirty=true;
        renderDirty=true;
    }
    void SetOverlay(const void* data,int w,int h,int stride) {
        if(w<1||h<1||stride<w*4) throw std::runtime_error("Invalid overlay dimensions");
        if(!overlay || w!=overlayW || h!=overlayH) {
            overlayView.Reset(); overlay.Reset();
            D3D11_TEXTURE2D_DESC td{}; td.Width=w; td.Height=h; td.MipLevels=td.ArraySize=1; td.Format=DXGI_FORMAT_B8G8R8A8_UNORM;
            td.SampleDesc.Count=1; td.BindFlags=D3D11_BIND_SHADER_RESOURCE;
            Check(device->CreateTexture2D(&td,nullptr,&overlay),"Create overlay");
            Check(device->CreateShaderResourceView(overlay.Get(),nullptr,&overlayView),"Create overlay SRV"); overlayW=w; overlayH=h;
        }
        ID3D11ShaderResourceView* nullViews[2]={}; context->PSSetShaderResources(0,2,nullViews);
        context->UpdateSubresource(overlay.Get(),0,nullptr,data,stride,0);
        renderDirty=true;
    }
    void ReleaseCaptureFrame() {
        heldDesktop.Reset();
        if(frameHeld && duplication) { HRESULT hr=duplication->ReleaseFrame(); if(FAILED(hr)) LoseDuplication(hr,200); }
        frameHeld=false;
    }
    // Recreate only the duplication session; the device and surfaces stay valid.
    void LoseDuplication(HRESULT hr,ULONGLONG retryMs) {
        wchar_t msg[96]; swprintf_s(msg,L"Desktop duplication lost: 0x%08X",unsigned(hr)); lastError=msg;
        duplication.Reset(); frameHeld=false; haveFrame=false; renderDirty=true;
        nextRetry=GetTickCount64()+retryMs;
    }
    static bool Overlaps(const RECT& a,const RECT& b) {
        return a.left<b.right && b.left<a.right && a.top<b.bottom && b.top<a.bottom;
    }
    bool RegionChanged(const DXGI_OUTDUPL_FRAME_INFO& info,const RECT& region) {
        if(info.TotalMetadataBufferSize==0) return info.LastPresentTime.QuadPart!=0;
        UINT bytes=0;
        dirtyRects.resize(info.TotalMetadataBufferSize/sizeof(RECT)+1);
        if(FAILED(duplication->GetFrameDirtyRects(UINT(dirtyRects.size()*sizeof(RECT)),dirtyRects.data(),&bytes))) return true;
        for(UINT i=0;i<bytes/sizeof(RECT);++i) if(Overlaps(dirtyRects[i],region)) return true;
        moveRects.resize(info.TotalMetadataBufferSize/sizeof(DXGI_OUTDUPL_MOVE_RECT)+1);
        if(FAILED(duplication->GetFrameMoveRects(UINT(moveRects.size()*sizeof(DXGI_OUTDUPL_MOVE_RECT)),moveRects.data(),&bytes))) return true;
        for(UINT i=0;i<bytes/sizeof(DXGI_OUTDUPL_MOVE_RECT);++i) {
            const auto& m=moveRects[i];
            RECT src{m.SourcePoint.x,m.SourcePoint.y,m.SourcePoint.x+m.DestinationRect.right-m.DestinationRect.left,m.SourcePoint.y+m.DestinationRect.bottom-m.DestinationRect.top};
            if(Overlaps(m.DestinationRect,region)||Overlaps(src,region)) return true;
        }
        return false;
    }
    void CopyCrop(ID3D11Texture2D* source,const RECT& region) {
        D3D11_TEXTURE2D_DESC td{}; source->GetDesc(&td);
        td.Width=region.right-region.left; td.Height=region.bottom-region.top;
        D3D11_TEXTURE2D_DESC old{}; if(desktop) desktop->GetDesc(&old);
        if(!desktop || old.Width!=td.Width || old.Height!=td.Height || old.Format!=td.Format) {
            desktopView.Reset(); desktop.Reset(); td.BindFlags=D3D11_BIND_SHADER_RESOURCE; td.MiscFlags=0; td.CPUAccessFlags=0; td.Usage=D3D11_USAGE_DEFAULT;
            Check(device->CreateTexture2D(&td,nullptr,&desktop),"Create cropped desktop texture");
            Check(device->CreateShaderResourceView(desktop.Get(),nullptr,&desktopView),"Create desktop SRV");
        }
        D3D11_BOX box{UINT(region.left),UINT(region.top),0,UINT(region.right),UINT(region.bottom),1};
        context->CopySubresourceRegion(desktop.Get(),0,0,0,0,source,0,&box);
        cropRect=region; haveFrame=true; renderDirty=true;
        copies++; copiedPixels+=static_cast<unsigned long long>(td.Width)*td.Height;
    }
    void Capture(const RECT& region) {
        bool needCrop=!haveFrame || !EqualRect(&region,&cropRect);
        // The acquired frame is valid until ReleaseFrame. Re-crop it before release
        // when moving across a static desktop; never read a released DXGI resource.
        if(needCrop && heldDesktop) { CopyCrop(heldDesktop.Get(),region); needCrop=false; }
        ReleaseCaptureFrame();
        if(!duplication) {
            if(GetTickCount64()<nextRetry) return;
            HRESULT hr=output->DuplicateOutput(device.Get(),&duplication);
            if(FAILED(hr)) { wchar_t msg[128]; swprintf_s(msg,L"DuplicateOutput failed: 0x%08X",unsigned(hr)); lastError=msg; nextRetry=GetTickCount64()+1000; return; }
        }
        DXGI_OUTDUPL_FRAME_INFO info{}; ComPtr<IDXGIResource> resource;
        HRESULT hr=duplication->AcquireNextFrame(0,&info,&resource);
        if(hr==DXGI_ERROR_WAIT_TIMEOUT) {
            // A new crop cannot be reconstructed from the old small texture. A fresh
            // duplication session supplies a complete first frame even on an idle desktop.
            if(needCrop) { duplication.Reset(); nextRetry=0; }
            return;
        }
        // ACCESS_LOST: desktop switch or mode change. INVALID_CALL: the session was
        // invalidated underneath us (seen after sleep/resume). Both recover in place.
        if(hr==DXGI_ERROR_ACCESS_LOST || hr==DXGI_ERROR_INVALID_CALL) { LoseDuplication(hr,200); return; }
        Check(hr,"AcquireNextFrame");
        frameHeld=true;
        try {
            Check(resource.As(&heldDesktop),"Desktop texture");
            if(info.LastPresentTime.QuadPart) desktopFrameTime=LatencyTrace::FromQpc(info.LastPresentTime.QuadPart);
            if(needCrop || RegionChanged(info,region)) CopyCrop(heldDesktop.Get(),region);
        } catch(...) { ReleaseCaptureFrame(); throw; }
    }
    int Render(float radius,float refraction,float blur,float tint,int maxFps,int economy,int sampleHz,int flags,float predictionMs) {
        bool dragging=(flags&1)!=0, wantSynced=(flags&2)!=0;
        if(dragging) lastDragTime=LatencyTrace::Now();
        if(dragging || !wantSynced) placement[4]=0;
        // Keep the original visual alive while the UI thread shows the empty host.
        // Transfer the surface only after that HWND is actually visible.
        if(wantSynced && !IsWindowVisible(host)) ShowWindowAsync(host,SW_SHOWNOACTIVATE);
        bool synced=wantSynced && IsWindowVisible(host);
        int row=trace.BeginFrame(flags);
        if(synced!=syncActive || dragging!=lastDragging) renderDirty=true;
        if(!wantSynced && IsWindowVisible(host)) renderDirty=true;
        if(!synced || !dragging || predictionMs<=0) prediction.Reset();
        if(MonitorFromWindow(hwnd,MONITOR_DEFAULTTONEAREST)!=monitor) return 2;
        RECT rc{}; if(!GetClientRect(hwnd,&rc) || rc.right<1||rc.bottom<1||IsIconic(hwnd)) return 0;
        Resize(rc.right,rc.bottom);
        double waitStart=LatencyTrace::Now();
        // The display supplies the drag clock; the managed loop does not add a
        // second full-period timer after this wait.
        if((flags&4)!=0) WaitForComposition();
        if(row>=0) trace.rows[row].wait=LatencyTrace::Now()-waitStart;
        ID3D11ShaderResourceView* nullViews[4]={}; context->PSSetShaderResources(0,4,nullViews);
        POINT origin{0,0}; if(!ClientToScreen(hwnd,&origin)) return 0; POINT mouse{}; GetCursorPos(&mouse);
        // Bound the shader's refraction + blur kernel + chromatic fringe footprint.
        LONG pad=LONG(std::ceil(std::abs(refraction)+std::abs(blur)*2.35f+3));
        LONG dw=OutputWidth(), dh=OutputHeight();
        LONG left=origin.x-desktopRect.left, top=origin.y-desktopRect.top;
        RECT region{std::clamp(left-pad,0L,dw-1),std::clamp(top-pad,0L,dh-1),std::clamp(left+width+pad,1L,dw),std::clamp(top+height+pad,1L,dh)};
        // Keep the cache anchored to the desktop while the glass moves inside it.
        // Recenter before exhausting the margin, leaving room for the next move.
        // A fast drag can cross a small cache in one frame. Keep the entire output
        // screen-anchored during dragging; shrink back after release.
        region=BackdropRegion(region,cropRect,haveFrame,dw,dh,dragging || LatencyTrace::Now()-lastDragTime<250);
        // One worker deadline controls capture, shading and presentation together.
        double captureStart=LatencyTrace::Now();
        Capture(region);
        if(row>=0) {
            auto& r=trace.rows[row]; r.capture=LatencyTrace::Now()-captureStart; r.ready=haveFrame?1:0;
            r.age=desktopFrameTime>0 ? LatencyTrace::Now()-desktopFrameTime : -1;
        }
        if(!overlayView) return 0;
        // Capture/texture setup may take time. The full-output drag cache permits
        // another position read without waiting for another desktop image.
        bool fullOutput=FullOutputCached();
        if(dragging && fullOutput) {
            if(!ClientToScreen(hwnd,&origin)) return 0;
            GetCursorPos(&mouse);
        }
        Constants c{}; c.windowSize[0]=float(width); c.windowSize[1]=float(height);
        c.windowOrigin[0]=float(origin.x); c.windowOrigin[1]=float(origin.y);
        c.desktopSize[0]=float(std::max(1L,cropRect.right-cropRect.left)); c.desktopSize[1]=float(std::max(1L,cropRect.bottom-cropRect.top));
        c.desktopOrigin[0]=float(desktopRect.left+cropRect.left); c.desktopOrigin[1]=float(desktopRect.top+cropRect.top);
        c.pointer[0]=float(mouse.x-origin.x); c.pointer[1]=float(mouse.y-origin.y);
        c.radius=radius; c.refraction=refraction; c.blur=blur; c.tint=tint; c.hasDesktop=haveFrame?1.f:0.f;
        c.material=float((flags>>3)&3); // 0 legacy, 1 dark smoked, 2 light (flag bits 8/16)
        bool interaction=!haveConstants || std::memcmp(c.windowSize,previousConstants.windowSize,16)!=0 ||
            std::memcmp(c.pointer,previousConstants.pointer,8)!=0 || GetAsyncKeyState(VK_LBUTTON)<0;
        int activeInterval=sampleHz>0 ? IntervalForRate(sampleHz) : std::max(1,1000/std::max(10,maxFps));
        if(!renderDirty && haveConstants && std::memcmp(&c,&previousConstants,sizeof(c))==0) {
            skips++; idlePolls++;
            // Keep fixed-rate capture polling, but reuse identical pixels instead
            // of running the shader and submitting another composition transaction.
            nextInterval=sampleHz<=0 && idlePolls>8 ? 100 : activeInterval;
            return haveFrame?0:1;
        }
        idlePolls=0;
        nextInterval=sampleHz>0 || interaction || !economy ? activeInterval : std::max(activeInterval,33);
        double drawStart=LatencyTrace::Now();
        int gpu=trace.BeginGpu(row);
        context->UpdateSubresource(constants.Get(),0,nullptr,&c,0,0);
        D3D11_VIEWPORT vp{0,0,float(width),float(height),0,1}; context->RSSetViewports(1,&vp); context->RSSetState(rasterizer.Get());
        context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        context->VSSetShader(vs.Get(),nullptr,0);
        context->PSSetConstantBuffers(0,1,constants.GetAddressOf()); context->PSSetSamplers(0,1,sampler.GetAddressOf());
        if(geometryDirty || cachedRadius!=radius) {
            for(int i=0;i<2;i++) if(!geometryTexture[i]) {
                D3D11_TEXTURE2D_DESC td{}; td.Width=width; td.Height=height; td.ArraySize=td.MipLevels=1;
                td.Format=DXGI_FORMAT_R16G16B16A16_FLOAT; td.SampleDesc.Count=1; td.BindFlags=D3D11_BIND_SHADER_RESOURCE|D3D11_BIND_RENDER_TARGET;
                Check(device->CreateTexture2D(&td,nullptr,&geometryTexture[i]),"Create geometry cache");
                Check(device->CreateRenderTargetView(geometryTexture[i].Get(),nullptr,&geometryTarget[i]),"Geometry RTV");
                Check(device->CreateShaderResourceView(geometryTexture[i].Get(),nullptr,&geometryView[i]),"Geometry SRV");
            }
            ID3D11RenderTargetView* rt[]={geometryTarget[0].Get(),geometryTarget[1].Get()};
            context->OMSetRenderTargets(2,rt,nullptr); context->PSSetShader(geometryShader.Get(),nullptr,0); context->Draw(3,0);
            geometryDirty=false; cachedRadius=radius; geometryBuilds++;
        }
        context->OMSetRenderTargets(1,renderTarget.GetAddressOf(),nullptr); context->PSSetShader(ps.Get(),nullptr,0);
        if(synced || (dragging && fullOutput)) {
            POINT latest{};
            if(ClientToScreen(hwnd,&latest)) {
                GetCursorPos(&mouse);
                double ox=0,oy=0;
                if(synced && dragging && predictionMs>0) prediction.Offset(LatencyTrace::Now(),latest.x,latest.y,predictionMs,ox,oy);
                c.windowOrigin[0]=float(latest.x+ox); c.windowOrigin[1]=float(latest.y+oy);
                c.pointer[0]=float(mouse.x)-c.windowOrigin[0]; c.pointer[1]=float(mouse.y)-c.windowOrigin[1];
                if(row>=0) { trace.rows[row].predictX=ox; trace.rows[row].predictY=oy; }
                context->UpdateSubresource(constants.Get(),0,nullptr,&c,0,0);
            }
        }
        ID3D11ShaderResourceView* views[]={desktopView.Get(),overlayView.Get(),geometryView[0].Get(),geometryView[1].Get()}; context->PSSetShaderResources(0,4,views);
        context->Draw(3,0);
        if(row>=0) {
            auto& r=trace.rows[row]; r.cpu=LatencyTrace::Now()-drawStart;
            r.x=c.windowOrigin[0]; r.y=c.windowOrigin[1];
            r.visualX=c.windowOrigin[0]; r.visualY=c.windowOrigin[1];
        }
        double submitStart=LatencyTrace::Now();
        bool surfaceChanged=!hostSurface;
        if(!hostSurface)
            Check(composition->CreateSurface(width,height,DXGI_FORMAT_B8G8R8A8_UNORM,DXGI_ALPHA_MODE_PREMULTIPLIED,&hostSurface),"Create synchronized surface");
        ComPtr<IDXGISurface> update; POINT offset{};
        Check(hostSurface->BeginDraw(nullptr,IID_PPV_ARGS(&update),&offset),"Begin synchronized draw");
        try {
            ComPtr<ID3D11Texture2D> texture; Check(update.As(&texture),"Synchronized draw texture");
            context->OMSetRenderTargets(0,nullptr,nullptr);
            D3D11_BOX box{0,0,0,UINT(width),UINT(height),1};
            context->CopySubresourceRegion(texture.Get(),0,UINT(offset.x),UINT(offset.y),0,backbuffer.Get(),0,&box);
        } catch(...) { hostSurface->EndDraw(); throw; }
        Check(hostSurface->EndDraw(),"End synchronized draw");
        trace.EndGpu(gpu);
        if(synced) {
            POINT hostOrigin{}; ClientToScreen(host,&hostOrigin);
            // Surface pixels and their screen position use exactly the same origin,
            // and are applied atomically by this single composition transaction.
            if(!syncActive || !haveConstants || c.windowOrigin[0]!=previousConstants.windowOrigin[0])
                Check(hostVisual->SetOffsetX(c.windowOrigin[0]-hostOrigin.x),"Synchronized X");
            if(!syncActive || !haveConstants || c.windowOrigin[1]!=previousConstants.windowOrigin[1])
                Check(hostVisual->SetOffsetY(c.windowOrigin[1]-hostOrigin.y),"Synchronized Y");
            if(!syncActive || surfaceChanged) {
                Check(visual->SetContent(emptySurface.Get()),"Hide HWND visual");
                Check(hostVisual->SetContent(hostSurface.Get()),"Show synchronized visual");
            }
        } else {
            if(syncActive || surfaceChanged) {
                Check(visual->SetContent(hostSurface.Get()),"Show glass surface on HWND");
                Check(hostVisual->SetContent(emptySurface.Get()),"Detach fixed host surface");
            }
        }
        Check(composition->Commit(),"Commit glass surface and ownership");
        if(synced && !dragging && (!syncActive || lastDragging || !haveConstants ||
            std::memcmp(c.windowSize,previousConstants.windowSize,16)!=0)) {
            // Keep the same HWND and visual at release. Only permit the UI to
            // tighten its hit region after the final unpredicted position is shown.
            Check(composition->WaitForCommitCompletion(),"Wait for final placement");
            WaitForComposition();
            placement[0]=int(c.windowOrigin[0]); placement[1]=int(c.windowOrigin[1]);
            placement[2]=width; placement[3]=height; placement[4]=1;
        }
        if(!wantSynced && IsWindowVisible(host)) {
            // Commit is asynchronous. Do not let HWND visibility overtake the
            // transaction restoring the original visual on the composition thread.
            Check(composition->WaitForCommitCompletion(),"Wait for glass handoff");
            WaitForComposition();
            ShowWindowAsync(host,SW_HIDE);
        }
        syncActive=synced;
        lastDragging=dragging;
        if(row>=0) {
            auto& r=trace.rows[row]; r.submit=LatencyTrace::Now()-submitStart; r.presented=1;
            POINT after{}; ClientToScreen(hwnd,&after); r.afterX=after.x; r.afterY=after.y;
            if(!synced) { r.visualX=after.x; r.visualY=after.y; }
        }
        previousConstants=c; haveConstants=true; renderDirty=false; draws++;
        return haveFrame?0:1;
    }
    void Compare(const wchar_t* referencePath,const wchar_t* optimizedPath) {
        auto code=Compile("PSReference","ps_5_0"); ComPtr<ID3D11PixelShader> reference;
        Check(device->CreatePixelShader(code->GetBufferPointer(),code->GetBufferSize(),nullptr,&reference),"Reference shader");
        ID3D11ShaderResourceView* views[]={desktopView.Get(),overlayView.Get(),geometryView[0].Get(),geometryView[1].Get()};
        context->PSSetShaderResources(0,4,views);
        context->OMSetRenderTargets(1,renderTarget.GetAddressOf(),nullptr);
        context->PSSetShader(reference.Get(),nullptr,0); context->Draw(3,0); Save(referencePath,false);
        context->PSSetShader(ps.Get(),nullptr,0); context->Draw(3,0); Save(optimizedPath,false);
        renderDirty=true;
    }
    // Explicit diagnostic: save our GPU output, since desktop capture excludes this HWND.
    void Save(const wchar_t* path,bool source) {
        ID3D11Texture2D* texture=source?desktop.Get():backbuffer.Get(); if(!texture) throw std::runtime_error("No frame available");
        D3D11_TEXTURE2D_DESC td{}; texture->GetDesc(&td); td.Usage=D3D11_USAGE_STAGING; td.BindFlags=0; td.MiscFlags=0; td.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
        ComPtr<ID3D11Texture2D> staging; Check(device->CreateTexture2D(&td,nullptr,&staging),"Create staging");
        context->CopyResource(staging.Get(),texture); D3D11_MAPPED_SUBRESOURCE map{}; Check(context->Map(staging.Get(),0,D3D11_MAP_READ,0,&map),"Map screenshot");
        FILE* f=nullptr; _wfopen_s(&f,path,L"wb");
        if(!f) { context->Unmap(staging.Get(),0); throw std::runtime_error("Cannot write diagnostic image"); }
        BITMAPFILEHEADER fh{}; fh.bfType=0x4D42; fh.bfOffBits=sizeof(fh)+sizeof(BITMAPINFOHEADER); fh.bfSize=fh.bfOffBits+td.Width*td.Height*4;
        BITMAPINFOHEADER bh{}; bh.biSize=sizeof(bh); bh.biWidth=td.Width; bh.biHeight=-LONG(td.Height); bh.biPlanes=1; bh.biBitCount=32; bh.biCompression=BI_RGB;
        fwrite(&fh,sizeof(fh),1,f); fwrite(&bh,sizeof(bh),1,f);
        for(UINT y=0;y<td.Height;++y) fwrite((char*)map.pData+y*map.RowPitch,td.Width*4,1,f);
        fclose(f); context->Unmap(staging.Get(),0);
    }
};
#define API extern "C" __declspec(dllexport)
API const wchar_t* GlassLastError() { return lastError.c_str(); }
API void* GlassCreate(HWND hwnd,HWND host,const wchar_t* shader) { try { return new Renderer(hwnd,host,shader); } catch(const std::exception& e) { Error(e); return nullptr; } }
API void GlassDestroy(Renderer* r) { delete r; }
API int GlassOverlay(Renderer* r,const void* p,int w,int h,int stride) { try { r->SetOverlay(p,w,h,stride); return 0; } catch(const std::exception& e) { Error(e); return -1; } }
API int GlassRender(Renderer* r,float radius,float refraction,float blur,float tint,int maxFps,int economy,int sampleHz,int flags,float predictionMs) { try { return r->Render(radius,refraction,blur,tint,maxFps,economy,sampleHz,flags,predictionMs); } catch(const std::exception& e) { Error(e); return -1; } }
API int GlassTrace(Renderer* r,const wchar_t* path) { try { r->trace.Start(r->device.Get(),r->context.Get(),path); return 0; } catch(const std::exception& e) { Error(e); return -1; } }
API int GlassRefreshRate(Renderer* r) { return r->refreshHz; }
API void GlassPlacement(Renderer* r,int* values) { std::memcpy(values,r->placement,sizeof(r->placement)); }
API int GlassInterval(Renderer* r) { return r->nextInterval; }
API int GlassCompare(Renderer* r,const wchar_t* referencePath,const wchar_t* optimizedPath) { try { r->Compare(referencePath,optimizedPath); return 0; } catch(const std::exception& e) { Error(e); return -1; } }
API int GlassSave(Renderer* r,const wchar_t* path,int source) { try { r->Save(path,source!=0); return 0; } catch(const std::exception& e) { Error(e); return -1; } }
API void GlassCounters(Renderer* r,unsigned long long* values) {
    values[0]=r->draws; values[1]=r->skips; values[2]=r->copies; values[3]=r->copiedPixels;
    values[4]=static_cast<unsigned long long>(r->cropRect.right-r->cropRect.left)*(r->cropRect.bottom-r->cropRect.top)*4;
    values[5]=r->geometryBuilds;
}
