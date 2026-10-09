#pragma once
inline int IntervalForRate(int hz) {
    return hz > 0 ? ((1000 + hz / 2) / hz > 0 ? (1000 + hz / 2) / hz : 1) : 0;
}
// Monotonic, phase-preserving capture pacing. Never performs catch-up bursts.
struct SamplingClock {
    double deadline=0;
    int rate=-1;
    bool Due(double now,int hz) {
        if(rate!=hz) { rate=hz; deadline=now; }
        if(hz<=0) return true;
        if(now+1e-9<deadline) return false;
        deadline+=1.0/hz;
        if(deadline<=now) deadline=now+1.0/hz;
        return true;
    }
};
