#pragma once
#include <algorithm>
// Inputs and output use monitor-local physical pixels; required is already clamped.
inline RECT BackdropRegion(const RECT& required,const RECT& cached,bool valid,LONG width,LONG height,bool dragging=false) {
    if(dragging) return RECT{0,0,width,height};
    // Release the full-output drag cache once the pointer is released.
    if(cached.left==0 && cached.top==0 && cached.right==width && cached.bottom==height) valid=false;
    RECT guarded{std::max(0L,required.left-64),std::max(0L,required.top-64),
        std::min(width,required.right+64),std::min(height,required.bottom+64)};
    if(valid && cached.left<=guarded.left && cached.top<=guarded.top &&
        cached.right>=guarded.right && cached.bottom>=guarded.bottom) return cached;
    return RECT{std::max(0L,required.left-256),std::max(0L,required.top-256),
        std::min(width,required.right+256),std::min(height,required.bottom+256)};
}
