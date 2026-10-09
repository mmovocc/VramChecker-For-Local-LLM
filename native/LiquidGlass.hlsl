// Original glass material. Pixel coordinates are physical pixels throughout.
// t0 is a live desktop texture with our HWND excluded; t1 is premultiplied UI.
Texture2D Desktop : register(t0);
Texture2D Overlay : register(t1);
Texture2D GeometryCache : register(t2);
Texture2D LightingCache : register(t3);
SamplerState LinearClamp : register(s0);
cbuffer Scene : register(b0)
{
    float2 WindowSize; float2 WindowOrigin;
    float2 DesktopSize; float2 DesktopOrigin;
    float2 Pointer; float Radius; float Refraction;
    float Blur; float Tint; float Material; float HasDesktop; // Material: 0 legacy, 1 dark smoked, 2 light
};
struct Vertex { float4 position : SV_POSITION; float2 uv : TEXCOORD0; };
Vertex VSMain(uint id : SV_VertexID)
{
    Vertex v;
    v.uv = float2((id << 1) & 2, id & 2);
    v.position = float4(v.uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return v;
}
// Superelliptic corner, normalized approximately to a pixel distance.
float Shape(float2 p)
{
    float r = min(Radius, min(WindowSize.x, WindowSize.y) * .45);
    float2 q = abs(p - WindowSize * .5) - (WindowSize * .5 - 7 - r);
    float2 a = max(q, 0);
    float n = pow(pow(a.x, 4) + pow(a.y, 4) + .0001, .25);
    float grad = length(pow(a / max(n, .001), 3));
    return (n - r) / max(grad, .72) + min(max(q.x, q.y), 0);
}
float3 SampleDesktop(float2 p)
{
    // WindowOrigin is the committed glass position (optionally predicted).
    // DesktopOrigin stays at the cached screen region until it is recentered.
    float2 uv = (WindowOrigin + p - DesktopOrigin) / DesktopSize;
    return Desktop.SampleLevel(LinearClamp, uv, 0).rgb;
}
float3 SoftBackdrop(float2 p, float amount)
{
    float3 c = SampleDesktop(p) * .28;
    c += SampleDesktop(p + float2(amount, 0)) * .12;
    c += SampleDesktop(p - float2(amount, 0)) * .12;
    c += SampleDesktop(p + float2(0, amount)) * .12;
    c += SampleDesktop(p - float2(0, amount)) * .12;
    c += SampleDesktop(p + float2(amount, amount) * .707) * .06;
    c += SampleDesktop(p + float2(-amount, amount) * .707) * .06;
    c += SampleDesktop(p + float2(amount, -amount) * .707) * .06;
    c += SampleDesktop(p - float2(amount, amount) * .707) * .06;
    return c;
}
struct CachedMaterial { float4 geometry : SV_Target0; float4 lighting : SV_Target1; };
CachedMaterial PSGeometry(Vertex v)
{
    float2 p=v.uv*WindowSize;
    float d=Shape(p), depth=max(-d,0);
    float2 normal=normalize(float2(Shape(p+float2(.5,0))-Shape(p-float2(.5,0)),
                                   Shape(p+float2(0,.5))-Shape(p-float2(0,.5)))+.00001);
    CachedMaterial m;
    m.geometry=float4(normal,d,(1-sin(saturate(depth/23)*1.5707963))*(1-exp(-depth*1.4)));
    m.lighting=float4(exp(-depth/9),exp(-pow((depth-1)/.7,2)),exp(-pow((depth-3.5)/2.6,2)),exp(-depth/30));
    return m;
}
float3 FastBackdrop(float2 p,float amount)
{
    float3 result=SampleDesktop(p);
    // Five taps matching the original kernel's per-axis second moment.
    float a=amount*1.095;
    if(amount>=.05) result=result*.4 + (SampleDesktop(p+float2(a,0))+SampleDesktop(p-float2(a,0))+
           SampleDesktop(p+float2(0,a))+SampleDesktop(p-float2(0,a)))*.15;
    return result;
}
float4 Shade(Vertex v, bool reference)
{
    float2 p = v.uv * WindowSize;
    CachedMaterial material;
    if(reference) material=PSGeometry(v);
    else { material.geometry=GeometryCache.Load(int3(int2(v.position.xy),0)); material.lighting=LightingCache.Load(int3(int2(v.position.xy),0)); }
    float d = material.geometry.z;
    float coverage = 1 - smoothstep(-.7, .7, d);
    if (coverage < .001)
    {
        float shadow = exp(-max(d, 0) * .48) * .12;
        return float4(0, 0, 0, shadow);
    }
    float2 normal = material.geometry.xy;
    float depth = max(-d, 0);
    float rim = material.lighting.x;
    // Rounded lens cross-section: strongly bends the image near the rim.
    float displacement = Refraction * material.geometry.w;
    float2 bent = p - normal * displacement;
    float3 col = reference ? SoftBackdrop(bent, Blur * (.65 + rim * 1.7)) : FastBackdrop(bent, Blur * (.65 + rim * 1.7));
    float fringe = rim * .85;
    if(reference || fringe>.01) {
        col.r = lerp(col.r, SampleDesktop(bent - normal * fringe).r, .55);
        col.b = lerp(col.b, SampleDesktop(bent + normal * fringe).b, .55);
    } else {
        // Keep the original sharp/blurred blend when the RGB offsets are subpixel.
        float3 sharp=SampleDesktop(bent);
        col.rb=lerp(col.rb,sharp.rb,.55);
    }
    float4 ui = Overlay.SampleLevel(LinearClamp, v.uv, 0);
    // Stable studio light, with a subtle interactive reflection.
    float2 light = normalize(float2(-.7, -.9) + (Pointer / WindowSize - .5) * .28);
    float facing = dot(normal, light);
    float rimLine = material.lighting.y;
    float inner = material.lighting.z;
    if (Material > .5)
    {
        // Dot-matrix UI materials: the foreground supplies its own ink, so no adaptive text inversion.
        bool dark = Material < 1.5;
        float3 frost = dark ? float3(.078, .075, .075) : float3(.965, .958, .948);
        if (HasDesktop < .5) col = frost;
        col = dark ? lerp(col * .5, frost, .74 + .2 * Tint) : lerp(col, frost, .56 + .3 * Tint); // backdrop weight: dark ~13%, light ~44%
        if (dark)
        {
            // One warm ember glow from the top-left corner (#7C4428 fading into the smoke).
            float2 q = p / max(WindowSize.x, WindowSize.y);
            col += float3(.486, .267, .157) * exp(-dot(q, q) * 5.5) * .30;
        }
        float edge = dark ? .55 : .8;
        col += rimLine * edge * (.14 + .46 * pow(saturate(facing), 3));
        col += inner * (dark ? .05 : .08) * pow(saturate(-facing), 3);
        col *= 1 - (dark ? .2 : .08) * rim * saturate(-facing);
        float3 result = ui.rgb + saturate(col) * (1 - ui.a);
        return float4(result * coverage, coverage);
    }
    if (HasDesktop < .5) col = float3(.87, .9, .94);
    col = lerp(col, float3(.95, .97, 1), Tint);
    float luminance = dot(col, float3(.2126,.7152,.0722));
    col = lerp(luminance.xxx, col, 1.06);
    col += rimLine * (.16 + .58 * pow(saturate(facing), 3));
    col += inner * .12 * pow(saturate(-facing), 3);
    col *= 1 - .13 * rim * saturate(-facing);
    col += .035 * material.lighting.w * saturate(facing);
    // Light veil underneath the information area; foreground is never refracted.
    col = lerp(col, float3(1,1,1), .12);
    // Neutral text follows the backdrop luminance; colored chart segments stay intact.
    float3 straight = ui.rgb / max(ui.a, .001);
    float chroma = max(straight.r,max(straight.g,straight.b)) - min(straight.r,min(straight.g,straight.b));
    float textPixel = (1 - step(.09,chroma)) * (1 - step(.72, dot(straight,float3(.2126,.7152,.0722))));
    float darkBackdrop = 1 - smoothstep(.28,.55,dot(col,float3(.2126,.7152,.0722)));
    ui.rgb = lerp(ui.rgb, (1 - straight * .4) * ui.a, textPixel * darkBackdrop);
    float3 result = ui.rgb + saturate(col) * (1 - ui.a);
    return float4(result * coverage, coverage);
}
float4 PSMain(Vertex v) : SV_TARGET { return Shade(v,false); }
float4 PSReference(Vertex v) : SV_TARGET { return Shade(v,true); }
