using System.Runtime.InteropServices;

namespace lifeviz;

internal enum LiveVjAction { None, Hold, Release, Slam }

// Input-only state: connection/foreground/target changes require another neutral sample.
internal sealed class LiveVjButtonEdges
{
    internal const ushort Hold = 0x1000;    // Xbox A
    internal const ushort Release = 0x2000; // Xbox B
    internal const ushort Slam = 0x4000;    // Xbox X
    private const ushort Mapped = Hold | Release | Slam;
    private bool _armed;
    private ushort _previous;

    public void Reset()
    {
        _armed = false;
        _previous = 0;
    }

    public LiveVjAction Observe(ushort buttons)
    {
        ushort current = (ushort)(buttons & Mapped);
        if (!_armed)
        {
            _armed = current == 0;
            return LiveVjAction.None;
        }

        ushort newlyPressed = (ushort)(current & ~_previous);
        _previous = current;
        // Release has priority, so simultaneous controls cannot accidentally slam or re-hold.
        if ((newlyPressed & Release) != 0) return LiveVjAction.Release;
        if ((newlyPressed & Slam) != 0) return LiveVjAction.Slam;
        return (newlyPressed & Hold) != 0 ? LiveVjAction.Hold : LiveVjAction.None;
    }
}

internal sealed class LiveVjController
{
    private int _controller = -1;
    private bool _available = OperatingSystem.IsWindows();

    [StructLayout(LayoutKind.Sequential)]
    private struct State
    {
        public uint PacketNumber;
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;
    }

    [DllImport("xinput1_4.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint XInputGetState(uint index, out State state);

    // False indicates either no device or one non-armed sample after a device disconnects.
    public bool TryRead(out ushort buttons)
    {
        buttons = 0;
        if (!_available) return false;
        try
        {
            if (_controller >= 0)
            {
                if (XInputGetState((uint)_controller, out State state) == 0)
                {
                    buttons = state.Buttons;
                    return true;
                }
                _controller = -1;
                return false;
            }

            for (uint i = 0; i < 4; i++)
            {
                if (XInputGetState(i, out State state) != 0) continue;
                _controller = (int)i;
                buttons = state.Buttons;
                return true;
            }
        }
        catch (DllNotFoundException) { _available = false; }
        catch (EntryPointNotFoundException) { _available = false; }
        return false;
    }
}
