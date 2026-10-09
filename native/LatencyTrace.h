#pragma once
#include <d3d11.h>
#include <wrl/client.h>
#include <vector>
#include <string>
#include <cstdio>
#include <stdexcept>
// Optional diagnostics only. GPU query results are polled without flushing/waiting.
struct LatencyTrace {
    struct Row {
        double time=0, wait=0, capture=0, cpu=0, submit=0, gpu=-1, age=-1;
        double x=0,y=0,visualX=0,visualY=0,afterX=0,afterY=0,predictX=0,predictY=0;
        int flags=0, ready=0, presented=0;
    };
    struct Slot {
        Microsoft::WRL::ComPtr<ID3D11Query> disjoint,start,end;
        int row=-1;
    } slots[4];
    Microsoft::WRL::ComPtr<ID3D11DeviceContext> context;
    std::vector<Row> rows;
    std::wstring path;
    double started=0;
    bool active=false;
    static double Frequency() {
        static const double frequency=[] { LARGE_INTEGER f{}; QueryPerformanceFrequency(&f); return double(f.QuadPart); }();
        return frequency;
    }
    static double FromQpc(LONGLONG q) { return double(q)*1000.0/Frequency(); }
    static double Now() { LARGE_INTEGER q{}; QueryPerformanceCounter(&q); return FromQpc(q.QuadPart); }
    ~LatencyTrace() { Finish(); }
    void Start(ID3D11Device* device,ID3D11DeviceContext* ctx,const wchar_t* filename) {
        Finish();
        FILE* f=nullptr; _wfopen_s(&f,filename,L"wb");
        if(!f) throw std::runtime_error("Cannot create latency CSV");
        fclose(f); path=filename; context=ctx; rows.clear(); rows.reserve(8192);
        for(auto& slot:slots) {
            slot=Slot{};
            D3D11_QUERY_DESC desc{D3D11_QUERY_TIMESTAMP_DISJOINT,0};
            HRESULT hr=device->CreateQuery(&desc,&slot.disjoint);
            desc.Query=D3D11_QUERY_TIMESTAMP;
            if(SUCCEEDED(hr)) hr=device->CreateQuery(&desc,&slot.start);
            if(SUCCEEDED(hr)) hr=device->CreateQuery(&desc,&slot.end);
            if(FAILED(hr)) slot=Slot{}; // CPU trace is still useful if GPU queries are unavailable.
        }
        started=Now(); active=true;
    }
    void Poll() {
        if(!context) return;
        for(auto& slot:slots) if(slot.row>=0) {
            D3D11_QUERY_DATA_TIMESTAMP_DISJOINT d{}; UINT64 a=0,b=0;
            if(context->GetData(slot.disjoint.Get(),&d,sizeof(d),D3D11_ASYNC_GETDATA_DONOTFLUSH)!=S_OK) continue;
            if(context->GetData(slot.start.Get(),&a,sizeof(a),D3D11_ASYNC_GETDATA_DONOTFLUSH)!=S_OK ||
                context->GetData(slot.end.Get(),&b,sizeof(b),D3D11_ASYNC_GETDATA_DONOTFLUSH)!=S_OK) continue;
            if(!d.Disjoint && d.Frequency && b>=a) rows[slot.row].gpu=double(b-a)*1000.0/double(d.Frequency);
            slot.row=-1;
        }
    }
    int BeginFrame(int flags) {
        if(!active) return -1;
        Poll();
        if(Now()-started>=30000 || rows.size()>=30000) { Finish(); return -1; }
        Row row; row.time=Now()-started; row.flags=flags; rows.push_back(row);
        return int(rows.size())-1;
    }
    int BeginGpu(int row) {
        if(row<0) return -1;
        for(int i=0;i<4;i++) if(slots[i].disjoint && slots[i].row<0) {
            slots[i].row=row; context->Begin(slots[i].disjoint.Get()); context->End(slots[i].start.Get()); return i;
        }
        return -1;
    }
    void EndGpu(int i) { if(i>=0) { context->End(slots[i].end.Get()); context->End(slots[i].disjoint.Get()); } }
    void Finish() {
        if(!active) return;
        Poll(); active=false;
        FILE* f=nullptr; _wfopen_s(&f,path.c_str(),L"wb");
        if(f) {
            fputs("time_ms,flags,desktop_ready,submitted,wait_ms,capture_cpu_ms,draw_cpu_ms,submit_cpu_ms,gpu_draw_ms,desktop_frame_age_ms,sampled_x,sampled_y,visual_x,visual_y,hwnd_after_x,hwnd_after_y,prediction_x,prediction_y\n",f);
            for(const auto& r:rows) fprintf(f,"%.3f,%d,%d,%d,%.3f,%.3f,%.3f,%.3f,%.3f,%.3f,%.3f,%.3f,%.3f,%.3f,%.3f,%.3f,%.3f,%.3f\n",
                r.time,r.flags,r.ready,r.presented,r.wait,r.capture,r.cpu,r.submit,r.gpu,r.age,r.x,r.y,r.visualX,r.visualY,r.afterX,r.afterY,r.predictX,r.predictY);
            fclose(f);
        }
        for(auto& slot:slots) slot=Slot{};
        context.Reset();
        std::vector<Row>().swap(rows);
    }
};
