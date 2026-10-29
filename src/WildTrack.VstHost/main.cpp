// wtvsthost.exe: hosts one VST3 plugin for WildTrack in its own process, so a plugin that hangs or crashes never takes
// a recording down with it (the user's call). The app starts one per plugin instance.
//
//   wtvsthost --scan <file.vst3>
//       prints one JSON line per audio class of the file (with its main audio and event inputs, to tell instruments
//       from effects) and exits (scanning runs out of process too).
//   wtvsthost --host <file.vst3> --cid <32 hex> --shm <name> --sr 48000 [--offline] [--parent <pid>]
//       loads that class and processes blocks through the shared memory <name> (shared.h), woken by the event
//       <name>.req and answering on <name>.done. Commands come on stdin, one per line:
//         state                 -> "state <base64>" (the component's and the controller's state)
//         load-state <base64>   -> "loaded" or "error <message>"
//         editor [owner hwnd]   opens (or brings up) the plugin's window
//         editor-close          closes it
//         quit
//       and it says on stdout: "ready <latency> <outputs> <instrument>", "error <message>", "dirty" (a parameter
//       changed in its window), "editor-open", "editor-closed".
//   It exits when stdin closes or the app (--parent) goes away.

#include <windows.h>

#include "shared.h"

#include "pluginterfaces/gui/iplugview.h"
#include "pluginterfaces/vst/ivstaudioprocessor.h"
#include "pluginterfaces/vst/ivsteditcontroller.h"
#include "pluginterfaces/vst/ivstevents.h"
#include "pluginterfaces/vst/ivstprocesscontext.h"
#include "public.sdk/source/common/memorystream.h"
#include "public.sdk/source/vst/hosting/eventlist.h"
#include "public.sdk/source/vst/hosting/hostclasses.h"
#include "public.sdk/source/vst/hosting/module.h"
#include "public.sdk/source/vst/hosting/parameterchanges.h"
#include "public.sdk/source/vst/hosting/plugprovider.h"
#include "public.sdk/source/vst/hosting/processdata.h"

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstring>
#include <cstdio>
#include <iostream>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

using namespace Steinberg;
using namespace Steinberg::Vst;

namespace {

// --- output to the app: whole lines, flushed, from any thread --------------------------------------------------------

std::mutex outLock;

void Say(const std::string& line)
{
    std::lock_guard<std::mutex> g(outLock);
    std::fwrite(line.data(), 1, line.size(), stdout);
    std::fputc('\n', stdout);
    std::fflush(stdout);
}

std::string Utf8(const wchar_t* w)
{
    int n = WideCharToMultiByte(CP_UTF8, 0, w, -1, nullptr, 0, nullptr, nullptr);
    std::string s(n > 0 ? n - 1 : 0, '\0');
    if (n > 1) WideCharToMultiByte(CP_UTF8, 0, w, -1, s.data(), n, nullptr, nullptr);
    return s;
}

std::wstring Wide(const std::string& s)
{
    int n = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), -1, nullptr, 0);
    std::wstring w(n > 0 ? n - 1 : 0, L'\0');
    if (n > 1) MultiByteToWideChar(CP_UTF8, 0, s.c_str(), -1, w.data(), n);
    return w;
}

std::string Json(const std::string& s)
{
    std::string o = "\"";
    for (unsigned char c : s)
    {
        if (c == '"' || c == '\\') { o += '\\'; o += static_cast<char>(c); }
        else if (c < 0x20) { char b[8]; std::snprintf(b, sizeof b, "\\u%04x", c); o += b; }
        else o += static_cast<char>(c);
    }
    return o + "\"";
}

// --- base64, for the plugin's state on the command line ---------------------------------------------------------------

const char* B64 = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

std::string ToBase64(const std::vector<uint8_t>& d)
{
    std::string o;
    o.reserve((d.size() + 2) / 3 * 4);
    for (size_t i = 0; i < d.size(); i += 3)
    {
        uint32_t v = d[i] << 16 | (i + 1 < d.size() ? d[i + 1] << 8 : 0) | (i + 2 < d.size() ? d[i + 2] : 0);
        o += B64[v >> 18 & 63];
        o += B64[v >> 12 & 63];
        o += i + 1 < d.size() ? B64[v >> 6 & 63] : '=';
        o += i + 2 < d.size() ? B64[v & 63] : '=';
    }
    return o;
}

std::vector<uint8_t> FromBase64(const std::string& s)
{
    std::vector<uint8_t> o;
    uint32_t v = 0;
    int bits = 0;
    for (char c : s)
    {
        const char* p = std::strchr(B64, c);
        if (c == '=' || !p || !c) continue;
        v = v << 6 | static_cast<uint32_t>(p - B64);
        bits += 6;
        if (bits >= 8) { bits -= 8; o.push_back(static_cast<uint8_t>(v >> bits & 0xff)); }
    }
    return o;
}

// --- scan --------------------------------------------------------------------------------------------------------------

/// What a class takes, to tell an instrument from an effect without trusting its sub-categories alone: its main audio
/// inputs (a sidechain doesn't count) and its event (note) inputs, from a component made just to ask. Empty if it
/// can't be made: the app then goes by the sub-categories.
std::string Buses(const VST3::Hosting::PluginFactory& factory, const VST3::Hosting::ClassInfo& info, FUnknown* context)
{
    auto component = factory.createInstance<IComponent>(info.ID());
    if (!component || component->initialize(context) != kResultOk) return "";
    int32 mainIns = 0;
    for (int32 i = 0, n = component->getBusCount(kAudio, kInput); i < n; i++)
    {
        BusInfo bus {};
        if (component->getBusInfo(kAudio, kInput, i, bus) == kResultOk && bus.busType == kMain) mainIns++;
    }
    int32 eventIns = component->getBusCount(kEvent, kInput);
    component->terminate();
    return ",\"ins\":" + std::to_string(mainIns) + ",\"events\":" + std::to_string(eventIns);
}

int Scan(const std::string& path)
{
    std::string error;
    auto module = VST3::Hosting::Module::create(path, error);
    if (!module) { Say("error " + error); return 1; }
    static HostApplication hostApp;
    PluginContextFactory::instance().setPluginContext(&hostApp);
    auto factory = module->getFactory();
    factory.setHostContext(&hostApp);
    std::string factoryVendor = factory.info().vendor();
    for (auto& info : factory.classInfos())
    {
        if (info.category() != kVstAudioEffectClass) continue;
        std::string vendor = info.vendor().empty() ? factoryVendor : info.vendor();
        Say("{\"cid\":" + Json(info.ID().toString(false)) + ",\"name\":" + Json(info.name()) + ",\"vendor\":" + Json(vendor) +
            ",\"sub\":" + Json(info.subCategoriesString()) + ",\"version\":" + Json(info.version()) + Buses(factory, info, &hostApp) + "}");
    }
    PluginContextFactory::instance().setPluginContext(nullptr);
    return 0;
}

// --- host ---------------------------------------------------------------------------------------------------------------

constexpr UINT WM_COMMAND_LINE = WM_APP + 1;

/// What the plugin's window changes reaches the processor through here (performEdit -> the next process call), and
/// the app hears "dirty" so it saves the new state.
class ComponentHandler : public IComponentHandler
{
public:
    std::mutex lock;
    std::vector<std::pair<ParamID, ParamValue>> pending;
    std::atomic<ULONGLONG> lastDirty {0};

    tresult PLUGIN_API beginEdit(ParamID) override { return kResultOk; }
    tresult PLUGIN_API performEdit(ParamID id, ParamValue value) override
    {
        {
            std::lock_guard<std::mutex> g(lock);
            pending.emplace_back(id, value);
        }
        Dirty();
        return kResultOk;
    }
    tresult PLUGIN_API endEdit(ParamID) override { Dirty(); return kResultOk; }
    tresult PLUGIN_API restartComponent(int32) override { Dirty(); return kResultOk; }

    /// At most one "dirty" every half second: a knob being turned sends hundreds of edits.
    void Dirty()
    {
        ULONGLONG now = GetTickCount64();
        if (now - lastDirty.load() < 500) return;
        lastDirty = now;
        Say("dirty");
    }

    tresult PLUGIN_API queryInterface(const TUID iid, void** obj) override
    {
        if (FUnknownPrivate::iidEqual(iid, IComponentHandler::iid) || FUnknownPrivate::iidEqual(iid, FUnknown::iid))
        {
            *obj = this;
            return kResultOk;
        }
        *obj = nullptr;
        return kNoInterface;
    }
    uint32 PLUGIN_API addRef() override { return 1; }
    uint32 PLUGIN_API release() override { return 1; }
};

struct Host;

/// The plugin's window: a top-level window of this process (owned by WildTrack's, so it stays above it).
class Editor : public IPlugFrame
{
public:
    Host* host = nullptr;
    HWND hwnd = nullptr;
    IPtr<IPlugView> view;

    tresult PLUGIN_API resizeView(IPlugView* v, ViewRect* r) override
    {
        if (!hwnd || !r) return kInvalidArgument;
        RECT wr {0, 0, r->getWidth(), r->getHeight()};
        AdjustWindowRectEx(&wr, GetWindowLong(hwnd, GWL_STYLE), FALSE, GetWindowLong(hwnd, GWL_EXSTYLE));
        SetWindowPos(hwnd, nullptr, 0, 0, wr.right - wr.left, wr.bottom - wr.top, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
        v->onSize(r);
        return kResultOk;
    }
    tresult PLUGIN_API queryInterface(const TUID iid, void** obj) override
    {
        if (FUnknownPrivate::iidEqual(iid, IPlugFrame::iid) || FUnknownPrivate::iidEqual(iid, FUnknown::iid))
        {
            *obj = this;
            return kResultOk;
        }
        *obj = nullptr;
        return kNoInterface;
    }
    uint32 PLUGIN_API addRef() override { return 1; }
    uint32 PLUGIN_API release() override { return 1; }
};

struct Host
{
    VST3::Hosting::Module::Ptr module;
    IPtr<PlugProvider> provider;
    IPtr<IComponent> component;
    IPtr<IEditController> controller;
    FUnknownPtr<IAudioProcessor> processor;
    std::string name;

    gcvst::Shared* shared = nullptr;
    HANDLE request = nullptr, done = nullptr;
    HostProcessData data;
    EventList events {gcvst::MaxEvents + 128};
    ParameterChanges params {256};
    ProcessContext context {};
    ComponentHandler handler;
    Editor editor;
    bool active[128] {};
    std::atomic<bool> quit {false};
    bool debug = GetEnvironmentVariableW(L"GCVST_DEBUG", nullptr, 0) > 0;
    int64_t blocks = 0;
    DWORD mainThread = 0;
    HWND messages = nullptr;

    bool Load(const std::string& path, const std::string& cid, int32 sampleRate, bool offline)
    {
        std::string error;
        module = VST3::Hosting::Module::create(path, error);
        if (!module) return Fail(error.empty() ? "no pude abrir el archivo del plugin" : error);
        auto uid = VST3::UID::fromString(cid, false);
        auto factory = module->getFactory();
        for (auto& info : factory.classInfos())
        {
            if (info.category() != kVstAudioEffectClass || (uid && info.ID() != *uid)) continue;
            provider = owned(new PlugProvider(factory, info, true));
            name = info.name();
            break;
        }
        if (!provider || !provider->initialize()) return Fail("no pude crear el plugin");
        component = provider->getComponentPtr();
        controller = provider->getControllerPtr();
        processor = FUnknownPtr<IAudioProcessor>(component);
        if (!component || !processor) return Fail("el plugin no procesa audio");

        // the main stereo output (and input, for an effect) and the first event input, if it takes notes
        int32 audioIns = component->getBusCount(kAudio, kInput), audioOuts = component->getBusCount(kAudio, kOutput);
        int32 eventIns = component->getBusCount(kEvent, kInput);
        if (audioOuts < 1) return Fail("el plugin no tiene salida de audio");
        std::vector<SpeakerArrangement> ins(audioIns, SpeakerArr::kStereo), outs(audioOuts, SpeakerArr::kStereo);
        processor->setBusArrangements(ins.empty() ? nullptr : ins.data(), audioIns, outs.data(), audioOuts);
        component->activateBus(kAudio, kOutput, 0, true);
        if (audioIns > 0) component->activateBus(kAudio, kInput, 0, true);
        if (eventIns > 0) component->activateBus(kEvent, kInput, 0, true);

        ProcessSetup setup {offline ? kOffline : kRealtime, kSample32, gcvst::MaxBlock, static_cast<SampleRate>(sampleRate)};
        if (processor->setupProcessing(setup) != kResultOk) return Fail("el plugin no acepta 48 kHz en 32 bits");
        data.prepare(*component, gcvst::MaxBlock, kSample32);
        data.inputEvents = &events;
        data.inputParameterChanges = &params;
        data.processContext = &context;
        if (component->setActive(true) != kResultOk) return Fail("el plugin no se pudo activar");
        processor->setProcessing(true);
        if (controller) controller->setComponentHandler(&handler);

        BusInfo bus {};
        component->getBusInfo(kAudio, kOutput, 0, bus);
        if (debug)
            for (int32 i = 0; i < audioOuts; i++)
            {
                BusInfo b {};
                component->getBusInfo(kAudio, kOutput, i, b);
                std::fprintf(stderr, "out bus %d: %d ch, type %d, flags %u\n", i, b.channelCount, b.busType, b.flags);
            }
        shared->latency = static_cast<int32_t>(processor->getLatencySamples());
        shared->outputs = bus.channelCount;
        shared->instrument = eventIns > 0 ? 1 : 0;
        shared->status = gcvst::Ready;
        context.sampleRate = sampleRate;
        Say("ready " + std::to_string(shared->latency) + " " + std::to_string(bus.channelCount) + " " + std::to_string(shared->instrument));
        return true;
    }

    bool Fail(const std::string& message)
    {
        if (shared)
        {
            strncpy_s(shared->error, message.c_str(), _TRUNCATE);
            shared->status = gcvst::Failed;
        }
        Say("error " + message);
        return false;
    }

    /// The audio thread: every request the app published, in order, as soon as it is signaled.
    void Run()
    {
        SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_TIME_CRITICAL);
        while (!quit)
        {
            WaitForSingleObject(request, 100);
            while (!quit && shared->done < shared->requested)
            {
                int64_t seq = shared->done + 1;
                Process(shared->slots[seq & 1]);
                InterlockedExchange64(&shared->done, seq);
                SetEvent(done);
            }
        }
    }

    void Process(gcvst::Slot& s)
    {
        int32 n = std::max(1, std::min<int32>(s.frames, gcvst::MaxBlock));
        events.clear();
        params.clearQueue();
        // a jump or a stop: every note it holds goes off first
        if (s.flags & gcvst::AllNotesOff)
            for (int16 p = 0; p < 128; p++)
                if (active[p]) { AddNote(false, 0, p, 0); active[p] = false; }
        for (int32 i = 0; i < std::min(s.numEvents, gcvst::MaxEvents); i++)
        {
            auto& e = s.events[i];
            if (e.pitch < 0 || e.pitch > 127) continue;
            bool on = e.type == gcvst::NoteOn;
            AddNote(on, std::clamp(e.offset, 0, n - 1), static_cast<int16>(e.pitch), e.velocity);
            active[e.pitch] = on;
        }
        // what the plugin's window changed since the last block, without waiting for the window's thread
        if (handler.lock.try_lock())
        {
            for (auto& [id, value] : handler.pending)
            {
                int32 index = 0;
                if (auto* queue = params.addParameterData(id, index)) queue->addPoint(0, value, index);
            }
            handler.pending.clear();
            handler.lock.unlock();
        }
        context.state = ProcessContext::kTempoValid | ProcessContext::kProjectTimeMusicValid | ProcessContext::kTimeSigValid |
            ((s.flags & gcvst::Playing) ? ProcessContext::kPlaying : 0);
        context.tempo = s.tempo > 0 ? s.tempo : 120;
        context.projectTimeSamples = s.timeSamples;
        context.projectTimeMusic = s.timeSamples / context.sampleRate * context.tempo / 60.0;
        context.timeSigNumerator = 4;
        context.timeSigDenominator = 4;

        // an effect gets the block's input
        if (data.numInputs > 0)
            for (int32 c = 0; c < data.inputs[0].numChannels; c++)
                std::memcpy(data.inputs[0].channelBuffers32[c], s.in[std::min(c, 1)], n * sizeof(float));
        data.numSamples = n;
        tresult result = processor->process(data);
        if (debug && (blocks++ % 50 == 0 || s.numEvents > 0))
        {
            float peak = 0;
            for (int32 c = 0; c < data.outputs[0].numChannels; c++)
                for (int32 i = 0; i < n; i++) peak = std::max(peak, std::abs(data.outputs[0].channelBuffers32[c][i]));
            std::fprintf(stderr, "block %lld: %d frames, %d events, process %d, peak %g, silence %llx\n", (long long)s.seq, n, s.numEvents, result, peak,
                (unsigned long long)data.outputs[0].silenceFlags);
        }
        // the main output to the app; a mono plugin is heard on both sides
        auto& out = data.outputs[0];
        for (int32 c = 0; c < 2; c++)
        {
            if (out.numChannels > 0) std::memcpy(s.out[c], out.channelBuffers32[std::min(c, out.numChannels - 1)], n * sizeof(float));
            else std::memset(s.out[c], 0, n * sizeof(float));
        }
    }

    void AddNote(bool on, int32 offset, int16 pitch, float velocity)
    {
        Event e {};
        e.busIndex = 0;
        e.sampleOffset = offset;
        if (on)
        {
            e.type = Event::kNoteOnEvent;
            e.noteOn.channel = 0;
            e.noteOn.pitch = pitch;
            e.noteOn.velocity = velocity;
            e.noteOn.noteId = -1;
        }
        else
        {
            e.type = Event::kNoteOffEvent;
            e.noteOff.channel = 0;
            e.noteOff.pitch = pitch;
            e.noteOff.velocity = 0;
            e.noteOff.noteId = -1;
        }
        events.addEvent(e);
    }

    // --- state (the window's thread) ---

    std::string GetState()
    {
        MemoryStream a, b;
        component->getState(&a);
        if (controller) controller->getState(&b);
        std::vector<uint8_t> blob;
        auto put = [&blob](MemoryStream& m) {
            uint32_t len = static_cast<uint32_t>(m.getSize());
            for (int i = 0; i < 4; i++) blob.push_back(static_cast<uint8_t>(len >> (8 * i)));
            blob.insert(blob.end(), m.getData(), m.getData() + len);
        };
        put(a);
        put(b);
        return ToBase64(blob);
    }

    bool SetState(const std::string& b64)
    {
        auto blob = FromBase64(b64);
        size_t at = 0;
        auto take = [&blob, &at](MemoryStream& m) {
            if (at + 4 > blob.size()) return false;
            uint32_t len = blob[at] | blob[at + 1] << 8 | blob[at + 2] << 16 | blob[at + 3] << 24;
            at += 4;
            if (at + len > blob.size()) return false;
            int32 written = 0;
            m.write(blob.data() + at, static_cast<int32>(len), &written);
            m.seek(0, IBStream::kIBSeekSet, nullptr);
            at += len;
            return true;
        };
        MemoryStream a, b;
        if (!take(a) || !take(b)) return false;
        if (component->setState(&a) != kResultOk) return false;
        if (controller)
        {
            a.seek(0, IBStream::kIBSeekSet, nullptr);
            controller->setComponentState(&a);
            if (b.getSize() > 0) controller->setState(&b);
        }
        return true;
    }

    // --- the plugin's window ---

    void OpenEditor(HWND owner)
    {
        if (editor.hwnd)
        {
            ShowWindow(editor.hwnd, SW_SHOWNORMAL);
            SetForegroundWindow(editor.hwnd);
            return;
        }
        if (!controller) { Say("error el plugin no tiene ventana"); return; }
        editor.view = owned(controller->createView(ViewType::kEditor));
        if (!editor.view || editor.view->isPlatformTypeSupported(kPlatformTypeHWND) != kResultTrue)
        {
            editor.view = nullptr;
            Say("error el plugin no tiene ventana propia");
            return;
        }
        ViewRect r {};
        editor.view->getSize(&r);
        bool resizable = editor.view->canResize() == kResultTrue;
        DWORD style = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX | (resizable ? WS_THICKFRAME | WS_MAXIMIZEBOX : 0);
        RECT wr {0, 0, std::max<int32>(r.getWidth(), 100), std::max<int32>(r.getHeight(), 60)};
        AdjustWindowRectEx(&wr, style, FALSE, 0);
        std::wstring title = Wide(name) + L" · WildTrack";
        editor.hwnd = CreateWindowExW(0, L"WtVstEditor", title.c_str(), style, CW_USEDEFAULT, CW_USEDEFAULT,
            wr.right - wr.left, wr.bottom - wr.top, owner, nullptr, GetModuleHandleW(nullptr), this);
        editor.host = this;
        editor.view->setFrame(&editor);
        if (editor.view->attached(editor.hwnd, kPlatformTypeHWND) != kResultOk)
        {
            DestroyWindow(editor.hwnd);
            Say("error no pude abrir la ventana del plugin");
            return;
        }
        ShowWindow(editor.hwnd, SW_SHOWNORMAL);
        SetForegroundWindow(editor.hwnd);
        Say("editor-open");
    }

    void CloseEditor()
    {
        if (!editor.hwnd) return;
        DestroyWindow(editor.hwnd); // WM_DESTROY detaches the view
    }

    void Command(const std::string& line)
    {
        auto space = line.find(' ');
        std::string cmd = line.substr(0, space), arg = space == std::string::npos ? "" : line.substr(space + 1);
        if (cmd == "state") Say("state " + GetState());
        else if (cmd == "load-state") Say(SetState(arg) ? "loaded" : "error no pude cargar el estado guardado del plugin");
        else if (cmd == "editor") OpenEditor(arg.empty() ? nullptr : reinterpret_cast<HWND>(static_cast<intptr_t>(std::stoll(arg))));
        else if (cmd == "editor-close") CloseEditor();
        else if (cmd == "quit") PostQuitMessage(0);
    }

    void Shutdown()
    {
        quit = true;
        CloseEditor();
        if (processor) processor->setProcessing(false);
        if (component) component->setActive(false);
        if (controller) controller->setComponentHandler(nullptr);
        data.unprepare();
        processor = nullptr;
        controller = nullptr;
        component = nullptr;
        provider = nullptr;
        module = nullptr;
    }
};

Host* gHost = nullptr;

LRESULT CALLBACK EditorProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
{
    if (msg == WM_NCCREATE)
        SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(reinterpret_cast<CREATESTRUCTW*>(lp)->lpCreateParams));
    auto* host = reinterpret_cast<Host*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
    switch (msg)
    {
    case WM_SIZE:
        if (host && host->editor.view && host->editor.view->canResize() == kResultTrue)
        {
            ViewRect r {0, 0, LOWORD(lp), HIWORD(lp)};
            host->editor.view->onSize(&r);
        }
        break;
    case WM_DESTROY:
        if (host && host->editor.view)
        {
            host->editor.view->setFrame(nullptr);
            host->editor.view->removed();
            host->editor.view = nullptr;
            host->editor.hwnd = nullptr;
            Say("editor-closed");
        }
        break;
    }
    return DefWindowProcW(hwnd, msg, wp, lp);
}

LRESULT CALLBACK MessageProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
{
    if (msg == WM_COMMAND_LINE)
    {
        std::unique_ptr<std::string> line(reinterpret_cast<std::string*>(lp));
        if (gHost) gHost->Command(*line);
        return 0;
    }
    return DefWindowProcW(hwnd, msg, wp, lp);
}

/// A crash inside the plugin: say so in the shared memory (the app silences it) and leave.
LONG WINAPI OnCrash(EXCEPTION_POINTERS*)
{
    if (gHost && gHost->shared)
    {
        strncpy_s(gHost->shared->error, "el plugin se cerró de golpe", _TRUNCATE);
        gHost->shared->status = gcvst::Crashed;
    }
    Say("error el plugin se cerró de golpe");
    return EXCEPTION_EXECUTE_HANDLER;
}

int HostMain(const std::string& path, const std::string& cid, const std::wstring& shm, int32 sampleRate, bool offline, DWORD parent)
{
    SetUnhandledExceptionFilter(OnCrash);
    Host host;
    gHost = &host;
    host.mainThread = GetCurrentThreadId();

    HANDLE map = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, shm.c_str());
    if (!map) { Say("error no encontré la memoria compartida"); return 2; }
    host.shared = static_cast<gcvst::Shared*>(MapViewOfFile(map, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(gcvst::Shared)));
    host.request = OpenEventW(SYNCHRONIZE | EVENT_MODIFY_STATE, FALSE, (shm + L".req").c_str());
    host.done = OpenEventW(SYNCHRONIZE | EVENT_MODIFY_STATE, FALSE, (shm + L".done").c_str());
    if (!host.shared || !host.request || !host.done || host.shared->magic != gcvst::Magic || host.shared->version != gcvst::Version)
    {
        Say("error la memoria compartida no es de esta versión");
        return 2;
    }

    CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    WNDCLASSW wc {};
    wc.lpfnWndProc = EditorProc;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.hCursor = LoadCursor(nullptr, IDC_ARROW);
    wc.lpszClassName = L"WtVstEditor";
    RegisterClassW(&wc);
    WNDCLASSW mc {};
    mc.lpfnWndProc = MessageProc;
    mc.hInstance = GetModuleHandleW(nullptr);
    mc.lpszClassName = L"WtVstMessages";
    RegisterClassW(&mc);
    host.messages = CreateWindowExW(0, L"WtVstMessages", L"", 0, 0, 0, 0, 0, HWND_MESSAGE, nullptr, mc.hInstance, nullptr);

    static HostApplication hostApp;
    PluginContextFactory::instance().setPluginContext(&hostApp);
    if (!host.Load(path, cid, sampleRate, offline)) return 3;

    std::thread audio([&host] { host.Run(); });
    // stdin: one command per line, handed to the window's thread (VST3 wants state and views there)
    HWND messages = host.messages;
    std::thread input([messages] {
        std::string line;
        while (std::getline(std::cin, line))
        {
            if (!line.empty() && line.back() == '\r') line.pop_back();
            PostMessageW(messages, WM_COMMAND_LINE, 0, reinterpret_cast<LPARAM>(new std::string(line)));
        }
        PostMessageW(messages, WM_COMMAND_LINE, 0, reinterpret_cast<LPARAM>(new std::string("quit")));
    });
    input.detach();
    // the app went away (it crashed, or was killed): so does this
    if (parent)
        if (HANDLE p = OpenProcess(SYNCHRONIZE, FALSE, parent))
            std::thread([p, messages] {
                WaitForSingleObject(p, INFINITE);
                PostMessageW(messages, WM_COMMAND_LINE, 0, reinterpret_cast<LPARAM>(new std::string("quit")));
            }).detach();

    MSG msg;
    while (GetMessageW(&msg, nullptr, 0, 0) > 0)
    {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }
    host.quit = true;
    SetEvent(host.request);
    audio.join();
    host.Shutdown();
    PluginContextFactory::instance().setPluginContext(nullptr);
    return 0;
}

} // namespace

int wmain(int argc, wchar_t** argv)
{
    std::string scan, path, cid;
    std::wstring shm;
    int32 sampleRate = 48000;
    bool offline = false;
    DWORD parent = 0;
    for (int i = 1; i < argc; i++)
    {
        std::wstring a = argv[i];
        auto next = [&]() { return i + 1 < argc ? std::wstring(argv[++i]) : std::wstring(); };
        if (a == L"--scan") scan = Utf8(next().c_str());
        else if (a == L"--host") path = Utf8(next().c_str());
        else if (a == L"--cid") cid = Utf8(next().c_str());
        else if (a == L"--shm") shm = next();
        else if (a == L"--sr") sampleRate = std::stoi(next());
        else if (a == L"--offline") offline = true;
        else if (a == L"--parent") parent = static_cast<DWORD>(std::stoul(next()));
    }
    // a plugin's message box or crash dialog must not hang an invisible process
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX);
    if (!scan.empty()) return Scan(scan);
    if (!path.empty() && !shm.empty()) return HostMain(path, cid, shm, sampleRate, offline, parent);
    Say("error uso: wtvsthost --scan archivo.vst3 | --host archivo.vst3 --cid id --shm nombre [--sr 48000] [--offline] [--parent pid]");
    return 1;
}
