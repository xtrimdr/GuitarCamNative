using WildTrack.Audio.Vst;
using WildTrack.Projects;

namespace WildTrack.App.Audio;

/// <summary>
/// Where the live engine finds each VST3 effect's running process when it hands a chain its slots
/// (<see cref="ChannelDsp.Specs(Channel, double, double, Func{InsertSlot, VstBridge?}?)"/>): the window keeps them
/// (<c>MainWindow.VstFx.cs</c>), by owner (a track or a bus) and slot, and plugs its lookup in here. UI thread.
/// </summary>
public static class LiveVst
{
    /// <summary>The process of an owner's slot, or null while it loads or if it couldn't open.</summary>
    public static Func<string, string, VstBridge?>? Find { get; set; }

    /// <summary>The lookup for one owner's slots.</summary>
    public static Func<InsertSlot, VstBridge?> Of(string owner) => s => Find?.Invoke(owner, s.Id);
}
