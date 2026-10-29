// The shared memory between WildTrack (WildTrack.Audio/Vst/VstBridge.cs) and wtvsthost.exe. Both sides must agree
// on every offset: VstBridge.cs mirrors them as constants, and the static_asserts below keep this file honest.
//
// One block goes like this: the app fills slots[seq & 1] (events, input, transport) and then publishes
// requested = seq and signals <name>.req; the host processes it into the same slot's out and publishes done = seq and
// signals <name>.done. Live, the app never waits: it reads block seq - 1 while the host works on seq (one block of
// latency, compensated by sending the notes early). Offline (the export) it waits for each block.
#pragma once
#include <cstddef>
#include <cstdint>

namespace gcvst {

constexpr int32_t Magic = 0x53564347; // "GCVS"
constexpr int32_t Version = 1;
constexpr int32_t MaxBlock = 8192;
constexpr int32_t MaxEvents = 512;

enum EventType : int32_t { NoteOn = 0, NoteOff = 1 };
// Reset: the plugin forgets its tails and delay lines before the block (an effect, when the transport starts)
enum SlotFlags : int32_t { AllNotesOff = 1, Playing = 2, Reset = 4 };
enum Status : int32_t { Loading = 0, Ready = 1, Failed = 2, Crashed = 3 };

#pragma pack(push, 8)
struct Event
{
    int32_t offset;   // frame inside the block
    int32_t type;     // EventType
    int32_t pitch;    // 0..127
    float velocity;   // 0..1
};

struct Slot
{
    int64_t seq;          // the block this slot holds
    int32_t frames;       // 1..MaxBlock
    int32_t numEvents;
    int64_t timeSamples;  // the timeline position the block's events are relative to
    double tempo;
    int32_t flags;        // SlotFlags
    int32_t pad;
    Event events[MaxEvents];
    float in[2][MaxBlock];
    float out[2][MaxBlock];
};

struct Shared
{
    int32_t magic;
    int32_t version;
    int32_t sampleRate;
    int32_t maxBlock;
    volatile int32_t status;  // Status
    int32_t latency;          // the plugin's own latency in frames
    int32_t outputs;          // channels of its main output
    int32_t instrument;       // 1 = takes notes
    volatile int64_t requested;
    volatile int64_t done;
    char error[512];          // UTF-8, when status is Failed or Crashed
    Slot slots[2];
};
#pragma pack(pop)

static_assert(sizeof(Event) == 16);
static_assert(offsetof(Slot, events) == 40);
static_assert(offsetof(Slot, in) == 8232);
static_assert(offsetof(Slot, out) == 73768);
static_assert(sizeof(Slot) == 139304);
static_assert(offsetof(Shared, requested) == 32);
static_assert(offsetof(Shared, done) == 40);
static_assert(offsetof(Shared, error) == 48);
static_assert(offsetof(Shared, slots) == 560);
static_assert(sizeof(Shared) == 279168);

} // namespace gcvst
