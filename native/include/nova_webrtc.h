#pragma once
#include <stdint.h>
#ifdef _WIN32
# ifdef NOVA_WEBRTC_EXPORTS
#  define NOVA_API __declspec(dllexport)
# else
#  define NOVA_API __declspec(dllimport)
# endif
# define NOVA_CALL __cdecl
#else
# define NOVA_API __attribute__((visibility("default")))
# define NOVA_CALL
#endif
#ifdef __cplusplus
extern "C" {
#endif
#define NOVA_WEBRTC_ABI_VERSION 1
typedef void (NOVA_CALL *nova_event_cb)(void*, int32_t, const uint8_t*, int32_t, const char*);
NOVA_API int32_t NOVA_CALL nova_webrtc_abi_version(void);
NOVA_API int32_t NOVA_CALL nova_peer_create(const char* const* ice, int32_t ice_count, int32_t queue_limit, nova_event_cb callback, void* user, int32_t* peer);
NOVA_API int32_t NOVA_CALL nova_peer_create_ex(const char* const* ice, int32_t ice_count, int32_t queue_limit, int32_t relay_only, nova_event_cb callback, void* user, int32_t* peer);
NOVA_API int32_t NOVA_CALL nova_peer_get_selected_candidate_pair(int32_t peer, char* local, int32_t local_size, char* remote, int32_t remote_size);
NOVA_API int32_t NOVA_CALL nova_peer_create_channel(int32_t peer, const char* label, int32_t* channel);
NOVA_API int32_t NOVA_CALL nova_peer_set_remote_description(int32_t peer, const char* sdp, const char* type);
NOVA_API int32_t NOVA_CALL nova_peer_add_candidate(int32_t peer, const char* candidate, const char* mid);
NOVA_API int32_t NOVA_CALL nova_peer_create_offer(int32_t peer);
NOVA_API int32_t NOVA_CALL nova_peer_create_answer(int32_t peer);
NOVA_API int32_t NOVA_CALL nova_peer_send(int32_t channel, const uint8_t* data, int32_t length);
NOVA_API int32_t NOVA_CALL nova_peer_close_channel(int32_t peer);
NOVA_API int32_t NOVA_CALL nova_peer_close(int32_t peer);
#ifdef __cplusplus
}
#endif
