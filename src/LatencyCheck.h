#pragma once
#include <thread>
struct EnvelopePoint { double t, value; };
inline void addEnvelope(std::vector<EnvelopePoint>& points,const BYTE* bytes,UINT32 frames,const WAVEFORMATEX* f,DWORD flags,UINT64 qpc,bool pcm16=false) {
 if(flags&AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR)return;
 const UINT32 hop=f->nSamplesPerSec/1000;
 for(UINT32 i=0;i+hop<=frames;i+=hop){double sum=0;
  if(!(flags&AUDCLNT_BUFFERFLAGS_SILENT))for(UINT32 j=0;j<hop;j++)for(UINT32 c=0;c<f->nChannels;c++){
   const size_t k=size_t(i+j)*f->nChannels+c;
   sum+=pcm16?std::abs(reinterpret_cast<const short*>(bytes)[k]/32768.0):std::abs(reinterpret_cast<const float*>(bytes)[k]);
  }
  points.push_back({qpc*1e-7+(i+hop*.5)/f->nSamplesPerSec,sum/(hop*f->nChannels)});
 }
}
struct LatencyResult { int ms=-1;double corr=0;bool reliable=false; };
inline LatencyResult analyzeLatency(std::vector<EnvelopePoint> input,std::vector<EnvelopePoint> output){
 if(input.size()<1000||output.size()<1000){printf("MEASUREMENT insufficient samples\n");fflush(stdout);return {};}
 double begin=std::max(input.front().t,output.front().t)+1,end=std::min(input.back().t,output.back().t)-1;
 auto at=[](const std::vector<EnvelopePoint>&p,double t,size_t&c){while(c+1<p.size()&&p[c+1].t<t)c++;if(c+1>=p.size()||p[c+1].t-p[c].t>.005)return 0.0;double a=(t-p[c].t)/(p[c+1].t-p[c].t);return p[c].value+a*(p[c+1].value-p[c].value);};
 std::vector<double>x,y;size_t a=0,b=0;for(double t=begin;t<end;t+=.001){x.push_back(at(input,t,a));y.push_back(at(output,t,b));}
 // Smooth five milliseconds to reduce packet timestamp jitter before correlation.
 auto smooth=[](std::vector<double>&v){auto source=v;for(size_t i=2;i+2<v.size();i++)v[i]=(source[i-2]+source[i-1]+source[i]+source[i+1]+source[i+2])/5;};smooth(x);smooth(y);
 if(x.size()<2000){printf("MEASUREMENT insufficient overlap\n");fflush(stdout);return {};}
 double mean=0;for(auto v:x)mean+=v;mean/=x.size();
 std::vector<double> correlations(801);double best=-2;int lagBest=-1;
 for(int lag=-100;lag<=700;lag++){double sx=0,sy=0,sxx=0,syy=0,sxy=0;int count=0;for(int i=800;i<int(x.size())-800;i++){double u=x[i],v=y[i+lag];sx+=u;sy+=v;sxx+=u*u;syy+=v*v;sxy+=u*v;count++;}double den=std::sqrt(std::max(0.0,(sxx-sx*sx/count)*(syy-sy*sy/count)));double r=den>1e-20?(sxy-sx*sy/count)/den:0;correlations[lag+100]=r;if(r>best){best=r;lagBest=lag;}}
 double other=-2;for(int lag=-100;lag<=700;lag++)if(std::abs(lag-lagBest)>25)other=std::max(other,correlations[lag+100]);
 printf("MEASUREMENT digitalDelayMs=%d correlation=%.6f otherPeak=%.6f inputMeanAbs=%.8f reliable=%s (excludes source Apple DAC and final analog output)\n",lagBest,best,other,mean,(best>=.65&&best-other>=.05&&mean>1e-5)?"yes":"no");fflush(stdout);
 return {lagBest,best,best>=.65&&best-other>=.05&&mean>1e-5};
}
struct LoopbackCheck {
 Microsoft::WRL::ComPtr<IAudioClient> client;Microsoft::WRL::ComPtr<IAudioCaptureClient> reader;WAVEFORMATEX* format=nullptr;
 ~LoopbackCheck(){if(client)client->Stop();CoTaskMemFree(format);}
 void open(IMMDeviceEnumerator*enumerator,const wchar_t*id){Microsoft::WRL::ComPtr<IMMDevice>d;check(enumerator->GetDevice(id,&d),"LoopbackDevice");check(d->Activate(__uuidof(IAudioClient),CLSCTX_ALL,nullptr,&client),"LoopbackActivate");check(client->GetMixFormat(&format),"LoopbackFormat");check(client->Initialize(AUDCLNT_SHAREMODE_SHARED,AUDCLNT_STREAMFLAGS_LOOPBACK,2000000,0,format,nullptr),"LoopbackInitialize");check(client->GetService(IID_PPV_ARGS(&reader)),"LoopbackService");check(client->Start(),"LoopbackStart");}
 void drain(std::vector<EnvelopePoint>&points){UINT32 packet=0;check(reader->GetNextPacketSize(&packet),"LoopbackPacket");while(packet){BYTE*data;UINT32 frames;DWORD flags;UINT64 qpc,pos;check(reader->GetBuffer(&data,&frames,&flags,&pos,&qpc),"LoopbackBuffer");addEnvelope(points,data,frames,format,flags,qpc);check(reader->ReleaseBuffer(frames),"LoopbackRelease");check(reader->GetNextPacketSize(&packet),"LoopbackNext");}}
};
