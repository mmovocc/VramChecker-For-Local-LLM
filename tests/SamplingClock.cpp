#include "../native/SamplingClock.h"
#include <cassert>
#include <cstdio>
#include <initializer_list>
int main() {
    for(int hz : {1,5,10,15,24,30,60,120,144}) {
        SamplingClock clock; int samples=0;
        for(int ms=0;ms<10000;ms++) if(clock.Due(ms/1000.0,hz)) samples++;
        assert(samples>=hz*10-1 && samples<=hz*10+1);
    }
    SamplingClock clock;
    assert(clock.Due(0,5)); assert(!clock.Due(.01,5));
    assert(clock.Due(.02,60));
    assert(clock.Due(100,60)); assert(!clock.Due(100,60));
    assert(clock.Due(100,0)); assert(clock.Due(100,0));
    assert(IntervalForRate(1)==1000); assert(IntervalForRate(5)==200);
    assert(IntervalForRate(15)==67); assert(IntervalForRate(30)==33);
    assert(IntervalForRate(60)==17); assert(IntervalForRate(144)==7);
    std::puts("PASS: rate intervals, immediate changes, no catch-up bursts, automatic mode.");
}
