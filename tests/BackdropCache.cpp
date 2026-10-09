#define NOMINMAX
#include <windows.h>
#include <cassert>
#include <cstdio>
#include "../native/BackdropCache.h"
int main() {
    RECT needed{500,300,930,560}, empty{};
    RECT cache=BackdropRegion(needed,empty,false,1920,1080);
    assert(cache.left==244 && cache.top==44 && cache.right==1186 && cache.bottom==816);
    for(int dx=-150;dx<=150;dx++) {
        RECT moved{needed.left+dx,needed.top,needed.right+dx,needed.bottom};
        RECT reuse=BackdropRegion(moved,cache,true,1920,1080);
        assert(EqualRect(&reuse,&cache)); // Moving glass must not move the cached desktop origin.
    }
    RECT moved{750,300,1180,560};
    RECT recentered=BackdropRegion(moved,cache,true,1920,1080);
    assert(recentered.left==494 && recentered.right==1436);
    RECT dragging=BackdropRegion(needed,cache,true,1920,1080,true);
    assert(dragging.left==0 && dragging.top==0 && dragging.right==1920 && dragging.bottom==1080);
    RECT fastMove{1400,700,1830,960};
    RECT fastCache=BackdropRegion(fastMove,dragging,true,1920,1080,true);
    assert(EqualRect(&dragging,&fastCache));
    RECT released=BackdropRegion(needed,dragging,true,1920,1080,false);
    assert(EqualRect(&released,&cache));
    for(int x=0;x<1920;x+=37) for(int y=0;y<1080;y+=31) {
        RECT region{x,y,std::min(1920,x+600),std::min(1080,y+400)};
        RECT result=BackdropRegion(region,cache,true,1920,1080);
        assert(result.left>=0 && result.top>=0 && result.right<=1920 && result.bottom<=1080);
        assert(result.left<=region.left && result.top<=region.top && result.right>=region.right && result.bottom>=region.bottom);
        RECT stable=BackdropRegion(region,result,true,1920,1080);
        assert(EqualRect(&stable,&result)); // Clamped edges must not cause repeated recentering.
    }
    puts("PASS: anchored backdrop, fast full-output drag, release shrink, recentering, resize and monitor edges.");
}
