#include <cassert>
#include <cstdio>
#include "../native/MotionPrediction.h"
int main() {
    MotionPrediction p; double x=0,y=0;
    p.Offset(0,100,100,4,x,y); assert(x==0 && y==0);
    p.Offset(10,110,100,4,x,y); assert(x>0 && x<=4 && y==0);
    p.Offset(20,110,100,4,x,y); assert(x==0 && y==0); // stopped
    p.Offset(30,120,100,4,x,y); assert(x>0);
    p.Offset(40,110,100,4,x,y); assert(x==0 && y==0); // reversal
    p.Offset(50,100,100,4,x,y); assert(x<0);
    p.Offset(200,500,100,4,x,y); assert(x==0 && y==0); // old sample
    p.Offset(210,900,100,8,x,y); assert(std::hypot(x,y)<=12.00001); // bounded speed
    p.Reset(); p.Offset(300,300,300,4,x,y); assert(x==0 && y==0);
    p.Offset(310,310,310,0,x,y); assert(x==0 && y==0);
    puts("PASS: prediction startup, stop, reversal, stale sample, displacement cap and disabled mode.");
}
