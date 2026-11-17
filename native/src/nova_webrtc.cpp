#include "nova_webrtc.h"
#include <rtc/rtc.h>
#include <algorithm>
#include <cstring>
#include <memory>
#include <mutex>
#include <unordered_map>
#include <vector>

namespace {
struct Peer { int pc=-1, dc=-1, limit=0; nova_event_cb cb=nullptr; void* user=nullptr; std::mutex gate; };
std::mutex peers_gate;
std::unordered_map<int,std::shared_ptr<Peer>> peers;
std::shared_ptr<Peer> get(int id) { std::lock_guard l(peers_gate); auto i=peers.find(id); return i==peers.end()?nullptr:i->second; }
void NOVA_CALL state_cb(int id, rtcState s, void*) { if(auto p=get(id); p&&p->cb) p->cb(p->user,1,(const uint8_t*)&s,sizeof(s),nullptr); }
void NOVA_CALL ice_state_cb(int id, rtcIceState s, void*) { if(auto p=get(id); p&&p->cb) p->cb(p->user,8,(const uint8_t*)&s,sizeof(s),nullptr); }
void NOVA_CALL desc_cb(int id,const char* sdp,const char* type,void*) { if(auto p=get(id);p&&p->cb)p->cb(p->user,2,(const uint8_t*)sdp,(int32_t)strlen(sdp),type); }
void NOVA_CALL cand_cb(int id,const char* c,const char* mid,void*) { if(auto p=get(id);p&&p->cb)p->cb(p->user,3,(const uint8_t*)c,(int32_t)strlen(c),mid); }
void NOVA_CALL open_cb(int id,void* u) { auto p=(Peer*)u; if(p->cb)p->cb(p->user,4,nullptr,0,nullptr); }
void NOVA_CALL msg_cb(int id,const char* data,int size,void* u) { auto p=(Peer*)u; if(size>=0 && size<=p->limit && p->cb)p->cb(p->user,5,(const uint8_t*)data,size,nullptr); }
void NOVA_CALL close_cb(int,void* u) { auto p=(Peer*)u; if(p->cb)p->cb(p->user,6,nullptr,0,nullptr); }
void NOVA_CALL setup_channel(const std::shared_ptr<Peer>& p,int dc) {
 std::lock_guard l(p->gate); p->dc=dc; rtcSetUserPointer(dc,p.get()); rtcSetOpenCallback(dc,open_cb); rtcSetClosedCallback(dc,close_cb); rtcSetMessageCallback(dc,msg_cb);
}
void NOVA_CALL remote_channel_cb(int,int dc,void* user) {
 auto p=static_cast<Peer*>(user); if(!p)return; setup_channel(std::shared_ptr<Peer>(p,[](Peer*){}),dc);
 if(p->cb)p->cb(p->user,7,reinterpret_cast<const uint8_t*>(&dc),sizeof(dc),nullptr);
}
int close_channel(const std::shared_ptr<Peer>& p) {
 std::lock_guard l(p->gate); if(p->dc<0)return 0; int dc=p->dc;p->dc=-1;int result=rtcClose(dc);int deleted=rtcDeleteDataChannel(dc);return result<0?result:deleted;
}
}
extern "C" {
int32_t nova_webrtc_abi_version(){return NOVA_WEBRTC_ABI_VERSION;}
int32_t nova_peer_create(const char* const* ice,int32_t count,int32_t limit,nova_event_cb cb,void* user,int32_t* out){
 return nova_peer_create_ex(ice,count,limit,0,cb,user,out);
}
int32_t nova_peer_create_ex(const char* const* ice,int32_t count,int32_t limit,int32_t relay_only,nova_event_cb cb,void* user,int32_t* out){
 if(!out||count<0||count>32||(count>0&&!ice)||limit<16384||limit>1048576||(relay_only!=0&&relay_only!=1))return -1;
 std::vector<const char*> servers; if(count>0)servers.assign(ice,ice+count); rtcConfiguration cfg{}; cfg.iceServers=servers.data();cfg.iceServersCount=count;cfg.maxMessageSize=limit;cfg.disableAutoNegotiation=true;cfg.iceTransportPolicy=relay_only?RTC_TRANSPORT_POLICY_RELAY:RTC_TRANSPORT_POLICY_ALL;
 int id=rtcCreatePeerConnection(&cfg);if(id<0)return id;auto p=std::make_shared<Peer>();p->pc=id;p->limit=limit;p->cb=cb;p->user=user;
 {std::lock_guard l(peers_gate);peers[id]=p;} rtcSetUserPointer(id,p.get());rtcSetStateChangeCallback(id,state_cb);rtcSetIceStateChangeCallback(id,ice_state_cb);rtcSetLocalDescriptionCallback(id,desc_cb);rtcSetLocalCandidateCallback(id,cand_cb);rtcSetDataChannelCallback(id,remote_channel_cb);*out=id;return 0;
}
int32_t nova_peer_get_selected_candidate_pair(int32_t id,char* local,int32_t local_size,char* remote,int32_t remote_size){auto p=get(id);if(!p||!local||!remote||local_size<=0||remote_size<=0)return -1;return rtcGetSelectedCandidatePair(p->pc,local,local_size,remote,remote_size);}
int32_t nova_peer_create_channel(int32_t id,const char* label,int32_t* out){auto p=get(id);if(!p||!label||!out)return -1;int dc=rtcCreateDataChannel(p->pc,label);if(dc<0)return dc;setup_channel(p,dc);*out=dc;return 0;}
int32_t nova_peer_set_remote_description(int32_t id,const char* s,const char* t){auto p=get(id);return p&&s&&t?rtcSetRemoteDescription(p->pc,s,t):-1;}
int32_t nova_peer_add_candidate(int32_t id,const char* c,const char* m){auto p=get(id);return p&&c?rtcAddRemoteCandidate(p->pc,c,m?m:""):-1;}
int32_t nova_peer_create_offer(int32_t id){auto p=get(id);return p?rtcSetLocalDescription(p->pc,"offer"):-1;}
int32_t nova_peer_create_answer(int32_t id){auto p=get(id);return p?rtcSetLocalDescription(p->pc,"answer"):-1;}
int32_t nova_peer_send(int32_t id,const uint8_t* b,int32_t n){if(!b||n<=0)return -1; if(rtcGetBufferedAmount(id)>4*1024*1024)return -4;return rtcSendMessage(id,(const char*)b,n);}
int32_t nova_peer_close_channel(int32_t id){auto p=get(id);return p?close_channel(p):-1;}
int32_t nova_peer_close(int32_t id){auto p=get(id);if(!p)return 0;close_channel(p);{std::lock_guard l(peers_gate);peers.erase(id);}rtcClose(p->pc);return rtcDeletePeerConnection(p->pc);}
}
