// Headless adapters only. Circuit state, wiring, compilation, cache generation,
// and both simulation paths are the production source files linked by the project.
using System;
using System.Linq;
using DLS.Simulation;
namespace UnityEngine
{
    public static class Random { public static float value => 0; }
    public static class Mathf { public static int Min(params int[] values) => values.Min(); }
}
namespace NUnit.Framework.Interfaces { }
namespace DLS.Description
{
    public struct PinBitCount { public int BitCount; }
    public struct PinDescription { public int ID; public PinBitCount BitCount; }
    public class ChipDescription
    {
        public string Name;
        public ChipType ChipType;
        public bool CanBeCached, ShouldBeCached;
        public PinDescription[] InputPins = Array.Empty<PinDescription>();
        public PinDescription[] OutputPins = Array.Empty<PinDescription>();
        public SubChipDescription[] SubChips = Array.Empty<SubChipDescription>();
        public WireDescription[] Wires = Array.Empty<WireDescription>();
    }
    public struct SubChipDescription { public string Name; public int ID; public uint[] InternalData; }
    public struct WireDescription { public PinAddress SourcePinAddress, TargetPinAddress; }
    public static class ChipTypeHelper { public static bool IsBusOriginType(ChipType type) => type == ChipType.Bus; }
}
namespace DLS.Game
{
    public class ChipLibrary
    {
        public DLS.Description.ChipDescription GetChipDescription(string name) => throw new NotSupportedException();
    }
    public class DevPinInstance { public Pin Pin; }
    public class Pin { public DLS.Description.PinAddress Address; public PinStateValue State, PlayerInputState; }
    public class Project
    {
        public static Project ActiveProject = new();
        public Description description = new();
        public double simAvgTicksPerSec;
        public void NotifyRomContentsEditedRuntime(SimChip chip) { }
    }
    public class Description { public long StepsRanSinceCreated; }
}
namespace DLS.Simulation
{
    public class SimAudio
    {
        public void InitFrame() { }
        public void NotifyAllNotesRegistered(double deltaTime) { }
        public void RegisterNote(int frequency, uint volume) { }
    }
    public static class SimKeyboardHelper
    {
        public static void RefreshInputState() { }
        public static bool KeyIsHeld(uint key) => false;
    }
}
