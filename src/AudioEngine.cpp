#define NOMINMAX
#include <windows.h>
#include <mmdeviceapi.h>
#include <audioclient.h>
#include <avrt.h>
#include <ksmedia.h>
#include <wrl/client.h>
#include <atomic>
#include <array>
#include <vector>
#include <thread>
#include <algorithm>
#include <cmath>
#include <cstdio>
#include <stdexcept>
#include <filesystem>
#include <fstream>
#include <mutex>
using Microsoft::WRL::ComPtr;
static void check(HRESULT h,const char* at){if(FAILED(h)){char b[180];sprintf_s(b,"%s: 0x%08lx",at,h);throw std::runtime_error(b);}}
#include "LatencyCheck.h"
struct Handle{HANDLE h=nullptr;~Handle(){if(h)CloseHandle(h);}operator HANDLE()const{return h;}};
struct Endpoint{
 ComPtr<IAudioClient3> client;WAVEFORMATEX* format=nullptr;Handle event;UINT32 period=0,capacity=0;bool started=false,pcm16=false,polling=false;
 ~Endpoint(){if(started)client->Stop();CoTaskMemFree(format);}
 void activate(IMMDeviceEnumerator* e,const wchar_t*id){ComPtr<IMMDevice>d;check(e->GetDevice(id,&d),"Device");check(d->Activate(__uuidof(IAudioClient3),CLSCTX_ALL,nullptr,&client),"Activate");}
 void shared(IMMDeviceEnumerator*e,const wchar_t*id){activate(e,id);check(client->GetMixFormat(&format),"MixFormat");bool fp=format->wFormatTag==WAVE_FORMAT_IEEE_FLOAT||(format->wFormatTag==WAVE_FORMAT_EXTENSIBLE&&reinterpret_cast<WAVEFORMATEXTENSIBLE*>(format)->SubFormat==KSDATAFORMAT_SUBTYPE_IEEE_FLOAT);if(!fp||format->wBitsPerSample!=32||(format->nChannels<1||format->nChannels>32))throw std::runtime_error("Float mix format required");UINT32 def,step,max;check(client->GetSharedModeEnginePeriod(format,&def,&step,&period,&max),"SharedPeriod");check(client->InitializeSharedAudioStream(AUDCLNT_STREAMFLAGS_EVENTCALLBACK,period,format,nullptr),"SharedInitialize");finish();}
 void exclusiveInput(IMMDeviceEnumerator*e,const wchar_t*id){activate(e,id);format=reinterpret_cast<WAVEFORMATEX*>(CoTaskMemAlloc(sizeof(WAVEFORMATEXTENSIBLE)));if(!format)throw std::bad_alloc();auto*f=reinterpret_cast<WAVEFORMATEXTENSIBLE*>(format);*f={};f->Format={WAVE_FORMAT_EXTENSIBLE,2,48000,192000,4,16,22};f->Samples.wValidBitsPerSample=16;f->dwChannelMask=3;f->SubFormat=KSDATAFORMAT_SUBTYPE_PCM;check(client->IsFormatSupported(AUDCLNT_SHAREMODE_EXCLUSIVE,format,nullptr),"InputFormat");REFERENCE_TIME def=0,min=0;check(client->GetDevicePeriod(&def,&min),"InputPeriod");HRESULT h=client->Initialize(AUDCLNT_SHAREMODE_EXCLUSIVE,0,400000,min,format,nullptr);polling=true;if(h==AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED){UINT32 aligned=0;check(client->GetBufferSize(&aligned),"AlignedBuffer");client.Reset();activate(e,id);min=static_cast<REFERENCE_TIME>(std::llround(1e7*aligned/48000));h=client->Initialize(AUDCLNT_SHAREMODE_EXCLUSIVE,AUDCLNT_STREAMFLAGS_EVENTCALLBACK,min,min,format,nullptr);}check(h,"InputInitialize");pcm16=true;finish();period=144;}
 void finish(){if(polling){event.h=CreateWaitableTimerExW(nullptr,nullptr,CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,TIMER_ALL_ACCESS);if(!event.h)event.h=CreateWaitableTimerW(nullptr,FALSE,nullptr);LARGE_INTEGER due;due.QuadPart=-10000;if(!event.h||!SetWaitableTimer(event,&due,1,nullptr,nullptr,FALSE))throw std::runtime_error("Capture timer");}else{event.h=CreateEventW(nullptr,FALSE,FALSE,nullptr);if(!event.h)throw std::runtime_error("Audio event");check(client->SetEventHandle(event),"SetEvent");}check(client->GetBufferSize(&capacity),"BufferSize");}
 void start(){check(client->Start(),"Start");started=true;}
};
struct Priority{
 HANDLE token=nullptr;
 Priority(AVRT_PRIORITY p){DWORD index=0;token=AvSetMmThreadCharacteristicsW(L"Pro Audio",&index);if(token)AvSetMmThreadPriority(token,p);}
 ~Priority(){if(token)AvRevertMmThreadCharacteristics(token);}
};
struct Stereo{float l=0,r=0;};
struct State{
 static constexpr size_t ringSize=192000,envSize=16384;
 std::vector<Stereo> ring=std::vector<Stereo>(ringSize);
 std::atomic<unsigned long long>written{0},consumed{0},packets{0},captureLostFrames{0},captureGaps{0},underflows{0},resyncs{0},renderLate{0},duplicates{0};
 std::atomic<double> queueMs{0},reserveMs{4},corr{0},inputPeak{0};std::atomic<int> latencyMs{-1};
 std::atomic<bool> failed{false},collect{false};std::array<EnvelopePoint,envSize>env{};std::atomic<size_t>envWritten{0};
 std::mutex errorLock;std::string error;
 void fail(const char*message,HANDLE stop){std::lock_guard<std::mutex>lock(errorLock);error=message;failed=true;SetEvent(stop);}
};
static bool waitStart(HANDLE stop,HANDLE go){HANDLE h[]={stop,go};return WaitForMultipleObjects(2,h,FALSE,INFINITE)==WAIT_OBJECT_0+1;}
static void captureLoop(Endpoint&capture,State&s,HANDLE stop,HANDLE go,bool exclusive){
 CoInitializeEx(nullptr,COINIT_MULTITHREADED);
 try{
  Priority priority(AVRT_PRIORITY_CRITICAL);ComPtr<IAudioCaptureClient>reader;check(capture.client->GetService(IID_PPV_ARGS(&reader)),"CaptureService");if(!waitStart(stop,go)){CoUninitialize();return;}capture.start();
  HANDLE waits[]={stop,capture.event};UINT64 lastEnd=0;bool havePosition=false;auto write=s.written.load();
  while(WaitForSingleObject(stop,0)!=WAIT_OBJECT_0){DWORD wake=WaitForMultipleObjects(2,waits,FALSE,2000);if(wake==WAIT_OBJECT_0)break;if(wake!=WAIT_OBJECT_0+1)throw std::runtime_error("Input stopped responding");
   UINT32 packet=exclusive?1:0;if(capture.polling)check(capture.client->GetCurrentPadding(&packet),"InputAvailable");else if(!exclusive)check(reader->GetNextPacketSize(&packet),"CapturePacket");
    while(packet){BYTE*data=nullptr;UINT32 frames=0;DWORD flags=0;UINT64 pos=0,qpc=0;HRESULT h=reader->GetBuffer(&data,&frames,&flags,&pos,&qpc);if(h==AUDCLNT_S_BUFFER_EMPTY||h==AUDCLNT_E_BUFFER_ERROR)break;check(h,"CaptureBuffer");if(frames==0)break;
    if(havePosition&&pos<lastEnd){s.duplicates++;check(reader->ReleaseBuffer(frames),"DuplicateRelease");break;}
    if(havePosition&&pos>lastEnd){s.captureGaps++;s.captureLostFrames.fetch_add(pos-lastEnd);}
    lastEnd=pos+frames;havePosition=true;
    const float*fp=reinterpret_cast<const float*>(data);const short*pcm=reinterpret_cast<const short*>(data);
    double peak=0;
    for(UINT32 i=0;i<frames;i++){Stereo sample;if(!(flags&AUDCLNT_BUFFERFLAGS_SILENT)){sample.l=capture.pcm16?pcm[i*2]/32768.0f:fp[i*2];sample.r=capture.pcm16?pcm[i*2+1]/32768.0f:fp[i*2+1];}s.ring[(write+i)%State::ringSize]=sample;peak=std::max(peak,double(std::max(std::abs(sample.l),std::abs(sample.r))));}
    double previousPeak=s.inputPeak.load(std::memory_order_relaxed);while(peak>previousPeak&&!s.inputPeak.compare_exchange_weak(previousPeak,peak,std::memory_order_relaxed)){}
    if(s.collect.load(std::memory_order_relaxed)&&!(flags&AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR)){size_t ew=s.envWritten.load(std::memory_order_relaxed);const UINT32 hop=capture.format->nSamplesPerSec/1000;for(UINT32 i=0;i+hop<=frames&&ew<State::envSize;i+=hop){double sum=0;for(UINT32 j=0;j<hop;j++){const auto&a=s.ring[(write+i+j)%State::ringSize];sum+=std::abs(a.l)+std::abs(a.r);}s.env[ew++]={qpc*1e-7+(i+hop*.5)/capture.format->nSamplesPerSec,sum/(hop*2)};}s.envWritten.store(ew,std::memory_order_release);}
    write+=frames;s.written.store(write,std::memory_order_release);s.packets++;check(reader->ReleaseBuffer(frames),"CaptureRelease");if(capture.polling){check(capture.client->GetCurrentPadding(&packet),"InputNext");}else{if(exclusive)break;check(reader->GetNextPacketSize(&packet),"CaptureNext");}
   }
  }
 }catch(const std::exception&e){s.fail(e.what(),stop);}CoUninitialize();
}
static void renderLoop(Endpoint&render,Endpoint&capture,State&s,HANDLE stop,HANDLE go,bool exclusive,double minimumReserve){
 CoInitializeEx(nullptr,COINIT_MULTITHREADED);
 try{
  Priority priority(AVRT_PRIORITY_HIGH);ComPtr<IAudioRenderClient>writer;check(render.client->GetService(IID_PPV_ARGS(&writer)),"RenderService");if(!waitStart(stop,go)){CoUninitialize();return;}
  BYTE*initial=nullptr;UINT32 goal=std::min(render.capacity,render.period);check(writer->GetBuffer(goal,&initial),"Prime");check(writer->ReleaseBuffer(goal,AUDCLNT_BUFFERFLAGS_SILENT),"PrimeRelease");render.start();
  const double inRate=capture.format->nSamplesPerSec,outRate=render.format->nSamplesPerSec,nominal=inRate/outRate;double read=0,filteredError=0,reserve=exclusive?std::max(6.0,minimumReserve):15.0,learnedFloor=exclusive?minimumReserve:15.0;bool primed=false;Stereo previous{};ULONGLONG lastWake=GetTickCount64(),lastAdapt=lastWake;HANDLE waits[]={stop,render.event};
  while(WaitForSingleObject(stop,0)!=WAIT_OBJECT_0){DWORD wake=WaitForMultipleObjects(2,waits,FALSE,2000);if(wake==WAIT_OBJECT_0)break;if(wake!=WAIT_OBJECT_0+1)throw std::runtime_error("Output stopped responding");ULONGLONG now=GetTickCount64();if(primed&&now-lastWake>static_cast<ULONGLONG>(1000.0*render.period/outRate*1.8)){s.renderLate++;}lastWake=now;
   UINT32 padding=0;check(render.client->GetCurrentPadding(&padding),"Padding");UINT32 count=goal>padding?goal-padding:0;if(!count)continue;auto write=s.written.load(std::memory_order_acquire);double available=double(write)-read,need=count*nominal+2;
   if(available>inRate*.100){read=double(write)-(need+reserve*inRate/1000);available=double(write)-read;s.resyncs++;filteredError=0;}
   if(!primed&&available>=need+reserve*inRate/1000)primed=true;
   double marginMs=1000*(available-need)/inRate;
   if(primed&&marginMs>=0&&marginMs<.25){reserve=std::min(15.0,reserve+1);learnedFloor=std::max(learnedFloor,reserve);lastAdapt=now;}
   if(exclusive&&primed&&now-lastAdapt>=30000&&reserve>learnedFloor){reserve=std::max(learnedFloor,reserve-.5);lastAdapt=now;}
   s.reserveMs=reserve;s.queueMs=1000*available/inRate;
   const double target=need+reserve*inRate/1000;filteredError=filteredError*.97+(available-target)*.03;double ratio=nominal*(1+std::clamp(filteredError/inRate*.5,-.002,.002));
   if(primed&&available<count*ratio+2){s.underflows++;reserve=std::min(20.0,reserve+2);learnedFloor=std::max(learnedFloor,reserve);lastAdapt=now;primed=false;filteredError=0;}
   BYTE*data=nullptr;check(writer->GetBuffer(count,&data),"RenderBuffer");auto*dest=reinterpret_cast<float*>(data);
   for(UINT32 i=0;i<count;i++){Stereo sample{};if(primed){auto pos=static_cast<unsigned long long>(read);double t=read-double(pos);const auto&a=s.ring[pos%State::ringSize];const auto&b=s.ring[(pos+1)%State::ringSize];sample.l=float(a.l+t*(b.l-a.l));sample.r=float(a.r+t*(b.r-a.r));read+=ratio;}else if(i<64){float fade=1-i/64.0f;sample.l=previous.l*fade;sample.r=previous.r*fade;}if(render.format->nChannels==1)dest[i]=.5f*(sample.l+sample.r);else{dest[i*render.format->nChannels]=sample.l;dest[i*render.format->nChannels+1]=sample.r;for(UINT32 ch=2;ch<render.format->nChannels;ch++)dest[i*render.format->nChannels+ch]=0;}if(i+1==count)previous=sample;}
   check(writer->ReleaseBuffer(count,0),"RenderRelease");s.consumed.store(static_cast<unsigned long long>(read),std::memory_order_release);
  }
 }catch(const std::exception&e){s.fail(e.what(),stop);}CoUninitialize();
}
static void measureLoop(State&s,HANDLE stop,const wchar_t*output){
 CoInitializeEx(nullptr,COINIT_MULTITHREADED);
 try{ComPtr<IMMDeviceEnumerator>e;check(CoCreateInstance(__uuidof(MMDeviceEnumerator),nullptr,CLSCTX_ALL,IID_PPV_ARGS(&e)),"MeasurementEnumerator");LoopbackCheck loop;loop.open(e.Get(),output);std::vector<EnvelopePoint>out;s.envWritten=0;s.collect=true;auto begin=GetTickCount64();while(GetTickCount64()-begin<12000&&WaitForSingleObject(stop,5)!=WAIT_OBJECT_0)loop.drain(out);s.collect=false;Sleep(10);size_t n=std::min(s.envWritten.load(std::memory_order_acquire),State::envSize);std::vector<EnvelopePoint>in(s.env.begin(),s.env.begin()+n);if(WaitForSingleObject(stop,0)!=WAIT_OBJECT_0){auto result=analyzeLatency(std::move(in),std::move(out));s.latencyMs=result.reliable?result.ms:-1;s.corr=result.corr;}}
 catch(const std::exception&e){s.collect=false;fprintf(stderr,"Measurement: %s\n",e.what());}CoUninitialize();
}
static std::string escaped(const std::string&value){std::string out;for(char c:value){if(c=='"'||c=='\\')out+='\\';if(c>=32)out+=c;}return out;}
static void statusFile(const std::filesystem::path&path,State&s,unsigned long long uptime,bool exclusive){
 std::string error;{std::lock_guard<std::mutex> guard(s.errorLock);error=s.error;}auto temp=path;temp+=L".tmp";std::ofstream f(temp,std::ios::binary|std::ios::trunc);f<<"{\"state\":\""<<(s.failed?"error":"running")<<"\",\"pid\":"<<GetCurrentProcessId()<<",\"uptimeSeconds\":"<<uptime<<",\"inputExclusive\":"<<(exclusive?"true":"false")<<",\"otherPcAudio\":true,\"packets\":"<<s.packets.load()<<",\"captureFrames\":"<<s.written.load()<<",\"consumedFrames\":"<<s.consumed.load()<<",\"inputPeak\":"<<s.inputPeak.exchange(0)<<",\"captureGaps\":"<<s.captureGaps.load()<<",\"captureLostFrames\":"<<s.captureLostFrames.load()<<",\"bufferShortages\":"<<s.underflows.load()<<",\"resyncs\":"<<s.resyncs.load()<<",\"lateOutput\":"<<s.renderLate.load()<<",\"duplicates\":"<<s.duplicates.load()<<",\"reserveMs\":"<<s.reserveMs.load()<<",\"queueMs\":"<<s.queueMs.load()<<",\"latencyMs\":"<<s.latencyMs.load()<<",\"correlation\":"<<s.corr.load()<<",\"measuring\":"<<(s.collect?"true":"false")<<",\"error\":\""<<escaped(error)<<"\"}";f.close();MoveFileExW(temp.c_str(),path.c_str(),MOVEFILE_REPLACE_EXISTING);
}
int wmain(int argc,wchar_t**argv){
 if(argc<2)return 2;CoInitializeEx(nullptr,COINIT_MULTITHREADED);int code=0;
 const wchar_t*input=nullptr,*output=L"default";std::wstring selectedInput;for(int a=2;a+1<argc;a++)if(wcscmp(argv[a],L"--input-id")==0)selectedInput=argv[a+1];if(selectedInput.empty()){fprintf(stderr,"Select an input device first\n");return 2;}input=selectedInput.c_str();
 try{Handle singleton;singleton.h=CreateMutexW(nullptr,FALSE,L"Local\\CodexLineMonitorSingle");if(GetLastError()==ERROR_ALREADY_EXISTS)throw std::runtime_error("Already running");Handle stop,go,measure;stop.h=CreateEventW(nullptr,TRUE,FALSE,L"Local\\CodexLineMonitorStop");go.h=CreateEventW(nullptr,TRUE,FALSE,L"Local\\CodexLineMonitorGo");measure.h=CreateEventW(nullptr,FALSE,FALSE,L"Local\\CodexLineMonitorMeasure");if(!stop.h||!go.h||!measure.h)throw std::runtime_error("Control event");ResetEvent(stop);ResetEvent(go);bool exclusive=argc>2&&wcscmp(argv[2],L"--exclusive-input")==0,autoMeasure=argc>3&&wcscmp(argv[3],L"--measure")==0;ComPtr<IMMDeviceEnumerator>enumerator;check(CoCreateInstance(__uuidof(MMDeviceEnumerator),nullptr,CLSCTX_ALL,IID_PPV_ARGS(&enumerator)),"Enumerator");double minimumReserve=4;for(int a=2;a+1<argc;a++)if(wcscmp(argv[a],L"--min-reserve-ms")==0){double v=_wtof(argv[a+1]);if(std::isfinite(v))minimumReserve=std::clamp(v,4.0,20.0);}std::wstring selectedOutput=output;for(int a=2;a+1<argc;a++)if(wcscmp(argv[a],L"--output-id")==0)selectedOutput=argv[a+1];bool followDefault=selectedOutput==L"default";if(followDefault){ComPtr<IMMDevice>d;check(enumerator->GetDefaultAudioEndpoint(eRender,eConsole,&d),"DefaultOutput");LPWSTR id=nullptr;check(d->GetId(&id),"DefaultId");selectedOutput=id;CoTaskMemFree(id);}output=selectedOutput.c_str();Endpoint capture,render;if(exclusive)capture.exclusiveInput(enumerator.Get(),input);else capture.shared(enumerator.Get(),input);if(capture.format->nChannels!=2)throw std::runtime_error("Input must be stereo");render.shared(enumerator.Get(),output);State state;
  printf("ENGINE v2 input=%s rate=%lu capturePeriodMs=%.3f output=SHARED outputPeriodMs=%.3f independent MMCSS threads\n",exclusive?"EXCLUSIVE":"SHARED",capture.format->nSamplesPerSec,1000.0*capture.period/capture.format->nSamplesPerSec,1000.0*render.period/render.format->nSamplesPerSec);fflush(stdout);
  std::thread captureThread(captureLoop,std::ref(capture),std::ref(state),stop.h,go.h,exclusive),renderThread(renderLoop,std::ref(render),std::ref(capture),std::ref(state),stop.h,go.h,exclusive,minimumReserve),measurementThread;
  struct JoinAll{HANDLE stop;std::thread&a,&b,&c;~JoinAll(){SetEvent(stop);if(a.joinable())a.join();if(b.joinable())b.join();if(c.joinable())c.join();}} cleanup{stop,captureThread,renderThread,measurementThread};
  std::filesystem::path ready=argv[1],status=ready.parent_path()/L"state.json";std::ofstream(ready).close();HANDLE initWait[]={stop,go};if(WaitForMultipleObjects(2,initWait,FALSE,15000)!=WAIT_OBJECT_0+1)throw std::runtime_error("Start cancelled or timed out");auto begin=GetTickCount64(),lastLog=begin;std::atomic<bool> measurementRunning{false};HANDLE waits[]={stop,measure};
  while(WaitForSingleObject(stop,0)!=WAIT_OBJECT_0){DWORD wake=WaitForMultipleObjects(2,waits,FALSE,1000);auto seconds=(GetTickCount64()-begin)/1000;if(followDefault){ComPtr<IMMDevice>d;LPWSTR id=nullptr;if(SUCCEEDED(enumerator->GetDefaultAudioEndpoint(eRender,eConsole,&d))&&SUCCEEDED(d->GetId(&id))){bool changed=selectedOutput!=id;CoTaskMemFree(id);if(changed)state.fail("Default output changed",stop);}}bool request=wake==WAIT_OBJECT_0+1||(autoMeasure&&seconds>=165);if(request&&!measurementRunning.load()){autoMeasure=false;if(measurementThread.joinable())measurementThread.join();measurementRunning=true;measurementThread=std::thread([&]{measureLoop(state,stop,output);measurementRunning=false;});}statusFile(status,state,seconds,exclusive);if(GetTickCount64()-lastLog>=10000){printf("health seconds=%llu frames=%llu captureGaps=%llu shortages=%llu resyncs=%llu lateOutput=%llu reserveMs=%.2f queueMs=%.2f\n",seconds,state.written.load(),state.captureGaps.load(),state.underflows.load(),state.resyncs.load(),state.renderLate.load(),state.reserveMs.load(),state.queueMs.load());fflush(stdout);lastLog=GetTickCount64();}}
  code=state.failed?1:0;statusFile(status,state,(GetTickCount64()-begin)/1000,exclusive);if(state.failed)fprintf(stderr,"ERROR %s\n",state.error.c_str());
 }catch(const std::exception&e){fprintf(stderr,"ERROR %s\n",e.what());code=1;}CoUninitialize();return code;
}
