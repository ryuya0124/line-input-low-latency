#include <windows.h>
#include <mmdeviceapi.h>
#include <propsys.h>
#include <wrl/client.h>
#include <cstdio>
using Microsoft::WRL::ComPtr;
int wmain(int argc,wchar_t**argv){if(argc<3)return 2;CoInitializeEx(nullptr,COINIT_MULTITHREADED);ComPtr<IMMDeviceEnumerator>e;HRESULT h=CoCreateInstance(__uuidof(MMDeviceEnumerator),nullptr,CLSCTX_ALL,IID_PPV_ARGS(&e));ComPtr<IMMDevice>d;if(SUCCEEDED(h))h=e->GetDevice(argv[2],&d);ComPtr<IPropertyStore>s;bool write=wcscmp(argv[1],L"--set-listen")==0;if(SUCCEEDED(h))h=d->OpenPropertyStore(write?STGM_READWRITE:STGM_READ,&s);PROPERTYKEY key={{0x24dbb0fc,0x9311,0x4b3d,{0x9c,0xf0,0x18,0xff,0x15,0x56,0x39,0xd4}},1};PROPVARIANT v={};if(SUCCEEDED(h)){if(write){if(argc<4)return 2;v.vt=VT_BOOL;v.boolVal=wcscmp(argv[3],L"1")==0?VARIANT_TRUE:VARIANT_FALSE;h=s->SetValue(key,v);if(SUCCEEDED(h))h=s->Commit();}else{h=s->GetValue(key,&v);if(SUCCEEDED(h))printf("{\"enabled\":%s,\"type\":%u}\n",v.vt==VT_BOOL&&v.boolVal?"true":"false",v.vt);}}PropVariantClear(&v);if(FAILED(h)){fprintf(stderr,"HRESULT 0x%08lx\n",h);return 1;}return 0;}
