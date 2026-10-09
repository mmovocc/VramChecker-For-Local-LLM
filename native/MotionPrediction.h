#pragma once
#include <algorithm>
#include <cmath>
struct MotionPrediction {
    double lastTime=0, x=0, y=0, vx=0, vy=0;
    bool initialized=false;
    void Reset() { initialized=false; vx=vy=0; }
    void Offset(double now,double px,double py,double horizon,double& ox,double& oy) {
        ox=oy=0;
        if(!initialized) { initialized=true; lastTime=now; x=px; y=py; return; }
        double dt=now-lastTime, dx=px-x, dy=py-y;
        lastTime=now; x=px; y=py;
        if(dt<.25 || dt>80 || (dx==0 && dy==0) || std::hypot(dx,dy)>500) { vx=vy=0; return; }
        double nx=dx/dt, ny=dy/dt;
        // A reversal invalidates the old velocity instead of predicting past the stop.
        if(nx*vx+ny*vy<0) { vx=nx; vy=ny; return; }
        vx=.65*nx+.35*vx; vy=.65*ny+.35*vy;
        double h=std::clamp(horizon,0.0,8.0);
        ox=vx*h; oy=vy*h;
        double length=std::hypot(ox,oy);
        if(length>12) { ox*=12/length; oy*=12/length; }
    }
};
