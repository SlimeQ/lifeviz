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
    Output[id.xy]=(uint4)round(saturate(result)*255.0);
}
