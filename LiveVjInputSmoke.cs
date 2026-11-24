using System;

namespace lifeviz;

internal static class LiveVjInputSmoke
{
    public static int Run()
    {
        var input = new LiveVjButtonEdges();
        void Check(ushort buttons, LiveVjAction expected, string context)
        {
            LiveVjAction actual = input.Observe(buttons);
            if (actual != expected)
                throw new InvalidOperationException($"Live VJ {context}: expected {expected}; got {actual}.");
        }

        Check(LiveVjButtonEdges.Slam, LiveVjAction.None, "attach with a held button");
        Check(LiveVjButtonEdges.Slam, LiveVjAction.None, "held button on attach does not arm");
        Check(0, LiveVjAction.None, "neutral arms the input");
        Check(LiveVjButtonEdges.Hold, LiveVjAction.Hold, "A presses hold");
        Check(LiveVjButtonEdges.Hold, LiveVjAction.None, "A does not repeat");
        Check((ushort)(LiveVjButtonEdges.Hold | LiveVjButtonEdges.Release), LiveVjAction.Release, "B can release while A remains down");
        Check(LiveVjButtonEdges.Hold, LiveVjAction.None, "lifting B does not re-hold A");
        input.Reset();
        Check(LiveVjButtonEdges.Hold, LiveVjAction.None, "hold cannot leak across focus/device reset");
        Check(0x0001, LiveVjAction.None, "unmapped button neither triggers nor blocks arming");
        Check((ushort)(LiveVjButtonEdges.Hold | LiveVjButtonEdges.Release | LiveVjButtonEdges.Slam), LiveVjAction.Release, "simultaneous press favors release");
        Check(LiveVjButtonEdges.Slam, LiveVjAction.None, "simultaneous suppressed slam does not become delayed");
        Check(0, LiveVjAction.None, "re-arm ordinary edges");
        Check(LiveVjButtonEdges.Slam, LiveVjAction.Slam, "independent slam works");
        return 0;
    }
}
