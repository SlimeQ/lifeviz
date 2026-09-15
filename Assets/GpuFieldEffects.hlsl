// Scene/history colors use premultiplied BGRA uint, matching the image-effect backend.
cbuffer ImageParameters : register(b0)
{
    uint Width; uint Height; uint CellWidth; uint CellHeight;
    uint PassParity; uint SortAxis; uint Padding0; uint Padding1;
    float Feedback; float Displacement; uint FrameIndex; uint HasHistory;
};
cbuffer EffectParameters : register(b1)
{
    uint FieldWidth; uint FieldHeight; uint HistoryHead; uint HistoryCount;
    float Flow; float Persistence; float Swirl; float Spread;
    float Scale; float Motion; float Feed; float Kill;
    float Seed; uint Effect; uint SolverPass; uint FieldReady;
    float KaleidoscopeFeedback; float KaleidoscopeZoom; float KaleidoscopeRotation; float KaleidoscopeFolds;
    float KaleidoscopeCenterX; float KaleidoscopeCenterY; float EffectPadding0; float EffectPadding1;
    float ParticleEmission; float ParticleGravity; float ParticleTurbulence; float ParticlePersistence;
    float RippleImpulse; float RippleSpeed; float RippleDamping; float RippleRefraction;
    float ChromaticRed; float ChromaticGreen; float ChromaticBlue; float ChromaticDrift;
    float ContourFlow; float ContourThickness; float ContourPersistence; float ToyPadding;

};
Texture2D<uint4> Previous : register(t0);
Texture2D<uint4> Scene : register(t1);
Texture2D<float4> Field : register(t2);
Texture2DArray<uint4> TimeHistory : register(t3);
RWTexture2D<uint4> Output : register(u0);
RWTexture2D<float4> FieldOutput : register(u1);
RWTexture2DArray<uint4> HistoryOutput : register(u2);

int2 FieldPoint(int2 p) { return clamp(p, int2(0,0), int2(FieldWidth-1,FieldHeight-1)); }
float4 F(int2 p) { return Field.Load(int3(FieldPoint(p),0)); }
float4 SampleField(float2 p)
{
    int2 a = int2(floor(p)); float2 f = frac(p);
    return lerp(lerp(F(a),F(a+int2(1,0)),f.x),lerp(F(a+int2(0,1)),F(a+1),f.x),f.y);
}
float4 S(int2 p) { return float4(Scene.Load(int3(clamp(p,int2(0,0),int2(Width-1,Height-1)),0)))/255.0; }
float4 P(int2 p) { return float4(Previous.Load(int3(clamp(p,int2(0,0),int2(Width-1,Height-1)),0)))/255.0; }
float4 SamplePrevious(float2 p)
{
    int2 a=int2(floor(p)); float2 f=frac(p);
    return lerp(lerp(P(a),P(a+int2(1,0)),f.x),lerp(P(a+int2(0,1)),P(a+1),f.x),f.y);
}
float Luma(float4 c) { return dot(c.rgb,float3(0.114,0.587,0.299)); }
float Hash(uint2 p)
{
    uint h=p.x*374761393u+p.y*668265263u;
    h=(h^(h>>13))*1274126177u; return float(h^(h>>16))/4294967295.0;
}

[numthreads(8,8,1)]
void AdvanceFieldCS(uint3 id : SV_DispatchThreadID)
{
    if(id.x>=FieldWidth || id.y>=FieldHeight) return;
    int2 p=int2(id.xy); float2 uv=(float2(p)+0.5)/float2(FieldWidth,FieldHeight);
    int2 scenePoint=int2(uv*float2(Width,Height));
    float4 c=F(p), l=F(p-int2(1,0)), r=F(p+int2(1,0)), u=F(p-int2(0,1)), d=F(p+int2(0,1));
    if(SolverPass==0)
    {
        float2 v=FieldReady!=0 ? SampleField(float2(p)-c.xy).xy*0.985 : float2(0,0);
        int2 stepSize=max(int2(1,1),int2(Width/FieldWidth,Height/FieldHeight));
        float2 grad=float2(Luma(S(scenePoint+int2(stepSize.x,0)))-Luma(S(scenePoint-int2(stepSize.x,0))),
                           Luma(S(scenePoint+int2(0,stepSize.y)))-Luma(S(scenePoint-int2(0,stepSize.y))));
        // Smooth changing curl forcing also moves flat-color regions.
        float t=float(FrameIndex)*0.018;
        float2 curl=float2(sin(uv.y*12+t)*cos(uv.x*9-t),-cos(uv.y*12+t)*sin(uv.x*9-t));
        v+=Flow*(float2(-grad.y,grad.x)*2.0 + Swirl*curl*0.18);
        FieldOutput[p]=float4(clamp(v,-5.0,5.0),0,0);
    }
    else if(SolverPass==1) FieldOutput[p]=float4(c.xy,0,0.5*(r.x-l.x+d.y-u.y));
    else if(SolverPass==2) FieldOutput[p]=float4(c.xy,(l.z+r.z+u.z+d.z-c.w)*0.25,c.w);
    else if(SolverPass==3)
    {
        float2 v=c.xy-0.5*float2(r.z-l.z,d.z-u.z);
        if(p.x==0 || p.x==int(FieldWidth)-1) v.x=0;
        if(p.y==0 || p.y==int(FieldHeight)-1) v.y=0;
        FieldOutput[p]=float4(clamp(v,-5.0,5.0),0,0);
    }
    else if(SolverPass==5 || SolverPass==6)
    {
        // Damped wave equation: x=height, y=velocity, z=last scene luminance.
        // The coefficient never exceeds the stable 2-D four-neighbor limit.
        float luminance=Luma(S(scenePoint));
        float lap=l.x+r.x+u.x+d.x-4*c.x;
        float impulse=0;
        if(SolverPass==5)
        {
            impulse=(luminance-c.z)*RippleImpulse*1.8;
            // Sparse deterministic drips keep a still image gently moving.
            float drip=Hash(id.xy/3)>0.995 ? sin(float(FrameIndex)*0.13+Hash(id.xy/3)*40) : 0;
            impulse+=drip*luminance*RippleImpulse*0.035;
        }
        float velocity=clamp((c.y+lap*(0.08+RippleSpeed*0.36)+impulse)*RippleDamping,-1,1);
        FieldOutput[p]=float4(clamp(c.x+velocity,-3,3),velocity,luminance,0);
    }
    else
    {
        float luminance=Luma(S(scenePoint));
        if(FieldReady==0)
        {
            // Spatially coherent colonies, with deterministic initialization for bakes.
            float colony=(Hash(id.xy/6)>0.80 && luminance>0.08) ? 0.8*Seed : 0;
            FieldOutput[p]=float4(1-colony,colony,0,0); return;
        }
        float2 lap=-c.xy+0.2*(l.xy+r.xy+u.xy+d.xy)+0.05*(F(p-1).xy+F(p+1).xy+F(p+int2(-1,1)).xy+F(p+int2(1,-1)).xy);
        float reaction=c.x*c.y*c.y;
        float2 change=float2(lap.x-reaction+Feed*(1-c.x),0.5*lap.y+reaction-(Kill+Feed)*c.y);
        // Sparse continuous scene injection leaves space for autonomous growth.
        float injection=Seed*0.006*luminance*(Hash(id.xy/6)>0.96 ? 1:0);
        FieldOutput[p]=float4(saturate(c.xy+change*0.8+float2(-injection,injection)),0,0);
    }
}

[numthreads(8,8,1)]
void CaptureHistoryCS(uint3 id : SV_DispatchThreadID)
{
    if(id.x>=FieldWidth || id.y>=FieldHeight) return;
    float2 uv=(float2(id.xy)+0.5)/float2(FieldWidth,FieldHeight);
    HistoryOutput[uint3(id.xy,HistoryHead)]=(uint4)round(S(int2(uv*float2(Width,Height)))*255.0);
}
float4 H(int2 p,uint slice) { return float4(TimeHistory.Load(int4(FieldPoint(p),slice,0)))/255.0; }
float4 SampleHistory(float2 p,uint slice)
{
    int2 a=int2(floor(p)); float2 f=frac(p);
    return lerp(lerp(H(a,slice),H(a+int2(1,0),slice),f.x),lerp(H(a+int2(0,1),slice),H(a+1,slice),f.x),f.y);
}

float2 MirrorImagePoint(float2 p)
{
    float2 span=max(float2(Width,Height)-1,1);
    return span-abs(frac(p/(2*span))*2*span-span);
}
float4 SampleKaleidoscope(float2 p,bool previousFrame)
{
    p=MirrorImagePoint(p);
    int2 a=int2(floor(p)); float2 f=frac(p);
    if(previousFrame) return SamplePrevious(p);
    return lerp(lerp(S(a),S(a+int2(1,0)),f.x),lerp(S(a+int2(0,1)),S(a+1),f.x),f.y);
}

float4 Kaleidoscope(float2 pixel)
{
    float2 center=float2(KaleidoscopeCenterX,KaleidoscopeCenterY)*float2(Width-1,Height-1);
    // Work in pixels so folds have the same geometry on wide and tall canvases.
    float2 offset=pixel-center;
    float radius=length(offset);
    float angle=radius>0.0001 ? atan2(offset.y,offset.x) : 0;
    float sector=6.28318530718/max(KaleidoscopeFolds,2);
    float folded=abs(frac(angle/sector+0.5)*sector-sector*0.5);
    float2 ray=float2(cos(folded),sin(folded))*radius;
    float4 current=SampleKaleidoscope(center+ray,false);
    if(HasHistory==0 || KaleidoscopeFeedback<=0) return current;
    float twist=KaleidoscopeRotation*0.01745329252;
    float2 retainedRay=float2(cos(folded-twist),sin(folded-twist))*radius/max(KaleidoscopeZoom,0.9);
    return lerp(current,SampleKaleidoscope(center+retainedRay,true),KaleidoscopeFeedback);
}

float2 SceneGradient(int2 p,int radius)
{
    return float2(Luma(S(p+int2(radius,0)))-Luma(S(p-int2(radius,0))),
                  Luma(S(p+int2(0,radius)))-Luma(S(p-int2(0,radius))));
}
float2 CurlCurrent(float2 uv,float t)
{
    return float2(sin(uv.y*13+t)*cos(uv.x*9-t*0.7),-cos(uv.y*13+t)*sin(uv.x*9-t*0.7));
}
float4 TrailSample(float2 p,bool nearest)
{
    if(HasHistory==0 || any(p<0) || any(p>float2(Width-1,Height-1))) return 0;
    return nearest ? P(int2(round(p))) : SamplePrevious(p);
}
float4 FadeTrail(float4 color,float persistence)
{
    // Spend at least one alpha byte each step so quantized trails can fully disappear.
    float alpha=max(0,color.a*persistence-1.0/255);
    return color*(alpha/max(color.a,1.0/255));
}
float4 Erosion(int2 p,float2 uv)
{
    float unit=max(float(Height)/144,1);
    float2 velocity=CurlCurrent(uv,float(FrameIndex)*0.023)*ParticleTurbulence*3.5;
    velocity.y+=ParticleGravity*3;
    // Nearest sampling keeps grains discrete, with open boundaries for departing grains.
    float4 grains=FadeTrail(TrailSample(float2(p)-velocity*unit,true),ParticlePersistence);
    int cell=max(1,int(round(unit*2)));
    uint2 tile=uint2(p)/uint(cell);
    float4 color=S(int2(tile)*cell+(cell>>1));
    float edge=saturate(length(SceneGradient(p,max(1,int(unit))))*3);
    float available=saturate(edge+Luma(color)*0.18);
    float scatter=Hash(tile+uint2(FrameIndex*17,FrameIndex*31));
    float emission=scatter<ParticleEmission*available*0.25 ? 1 : 0;
    float4 fresh=color*emission;
    return fresh+grains*(1-fresh.a);
}
float4 Chromatic(int2 p,float4 fresh)
{
    if(HasHistory==0) return fresh;
    float unit=max(float(Height)/144,1)*ChromaticDrift*2;
    float4 red=TrailSample(float2(p)-float2(unit,0),false);
    float4 green=TrailSample(float2(p)-float2(0,-unit*0.65),false);
    float4 blue=TrailSample(float2(p)-float2(-unit,unit*0.4),false);
    // Backend storage is BGRA. A separate coverage history is unnecessary:
    // max per-channel coverage bounds each premultiplied color component.
    float3 retention=float3(ChromaticBlue,ChromaticGreen,ChromaticRed);
    float3 channels=lerp(fresh.rgb,float3(blue.x,green.y,red.z),retention);
    float3 coverage=lerp(fresh.aaa,float3(blue.a,green.a,red.a),retention);
    if(fresh.a==0)
    {
        float3 faded=max(0,coverage-1.0/255);
        channels*=faded/max(coverage,1.0/255);
        coverage=faded;
    }
    return float4(channels,max(coverage.x,max(coverage.y,coverage.z)));
}
float4 Contours(int2 p,float2 uv)
{
    float unit=max(float(Height)/144,1);
    int radius=max(1,int(round((1+ContourThickness*4)*unit)));
    float2 gradient=SceneGradient(p,radius);
    float edge=smoothstep(0.025,0.3,length(gradient));
    float4 color=S(p);
    // Preserve source coverage, brighten edge pigment, and transport previous threads.
    float4 thread=float4(min(color.rgb*1.4+color.a*0.15,color.aaa),color.a)*edge;
    float2 curl=CurlCurrent(uv,float(FrameIndex)*0.018);
    float2 tangent=float2(-gradient.y,gradient.x);
    float2 velocity=(curl*2.5+tangent*2)*ContourFlow*unit;
    float4 old=FadeTrail(TrailSample(float2(p)-velocity,false),ContourPersistence);
    return thread+old*(1-thread.a);
}

[numthreads(8,8,1)]
void EffectOutputCS(uint3 id : SV_DispatchThreadID)
{
    if(id.x>=Width || id.y>=Height) return;
    float2 uv=(float2(id.xy)+0.5)/float2(Width,Height);
    float2 fp=uv*float2(FieldWidth,FieldHeight)-0.5;
    float4 fresh=S(int2(id.xy)), result=fresh;
    if(Effect==2 && HasHistory!=0) // FluidInk
    {
        float2 velocity=SampleField(fp).xy*float2(Width,Height)/float2(FieldWidth,FieldHeight);
        result=lerp(fresh,SamplePrevious(float2(id.xy)-velocity),Persistence);
    }
    else if(Effect==3 && HistoryCount>1 && Spread>0) // TimeDisplacement
    {
        float phase=float(FrameIndex)*Motion*0.035;
        float wave=0.5+0.25*(sin(uv.x*Scale*6.283+phase)+sin(uv.y*Scale*6.283-phase*0.7));
        float age=wave*Spread*float(HistoryCount-1);
        uint lo=(uint)floor(age), hi=min(lo+1,HistoryCount-1);
        uint a=(HistoryHead+64-lo)%64, b=(HistoryHead+64-hi)%64;
        float4 ca=lo==0 ? fresh : SampleHistory(fp,a);
        float4 cb=hi==0 ? fresh : SampleHistory(fp,b);
        result=lerp(ca,cb,frac(age));
    }
    else if(Effect==4) // ReactionDiffusion; preserve scene coverage and color provenance.
    {
        float concentration=SampleField(fp).y;
        float growth=smoothstep(0.03,0.38,concentration);
        float edge=4*growth*(1-growth);
        float3 pigment=lerp(fresh.rgb*0.12,fresh.rgb,growth);
        result=float4(lerp(pigment,float3(0.95,0.65,0.25)*fresh.a,edge*0.65),fresh.a);
    }
    else if(Effect==5) result=Kaleidoscope(float2(id.xy));
    else if(Effect==6) result=Erosion(int2(id.xy),uv);
    else if(Effect==7 && RippleRefraction>0)
    {
        float2 gradient=float2(SampleField(fp+float2(1,0)).x-SampleField(fp-float2(1,0)).x,
                               SampleField(fp+float2(0,1)).x-SampleField(fp-float2(0,1)).x);
        float2 offset=gradient*RippleRefraction*18*float2(Width,Height)/float2(FieldWidth,FieldHeight);
        result=SampleKaleidoscope(float2(id.xy)+offset,false);
    }
    else if(Effect==8) result=Chromatic(int2(id.xy),fresh);
    else if(Effect==9) result=Contours(int2(id.xy),uv);
    Output[id.xy]=(uint4)round(saturate(result)*255.0);
}
