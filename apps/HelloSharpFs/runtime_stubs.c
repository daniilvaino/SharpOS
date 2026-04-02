#include <stdint.h>
static unsigned char g_sharp_heap[1024 * 1024];
static uint64_t g_sharp_heap_offset = 0;
static void* sharp_alloc(uint64_t size){ uint64_t aligned = (size + 15ULL) & ~15ULL; if (g_sharp_heap_offset + aligned > (uint64_t)sizeof(g_sharp_heap)) return (void*)0; void* p = (void*)(g_sharp_heap + g_sharp_heap_offset); g_sharp_heap_offset += aligned; return p; }
void* RhNewString(void* pEEType, int length){ if (length < 0) return (void*)0; uint64_t chars = ((uint64_t)length + 1ULL) * 2ULL; uint64_t bytes = 8ULL + 4ULL + chars; unsigned char* obj = (unsigned char*)sharp_alloc(bytes); if (!obj) return (void*)0; *((void**)obj) = pEEType; *((int*)(obj + 8)) = length; unsigned short* data = (unsigned short*)(obj + 12); for (int i = 0; i <= length; i++) data[i] = 0; return (void*)obj; }
