using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SkylinesAgentBridge.Support
{
    /// <summary>
    /// The game window's client rectangle in desktop (virtual-screen) coordinates.
    ///
    /// WHY THIS LIVES HERE
    /// -------------------
    /// World→screen projection answers in Unity pixels, whose origin is the window's
    /// client area. A cursor automation channel (CUA) clicks in desktop pixels, whose
    /// origin is the top-left of the primary monitor. Translating between them needs
    /// the client area's position on the desktop, which Unity does not expose — so this
    /// helper asks user32 directly.
    ///
    /// Call from the main thread (the same thread the projection runs on); the Win32
    /// calls themselves are thread-agnostic, but the process must be the game.
    ///
    /// Live-game finding (2026-11-11): <c>Process.MainWindowHandle</c> returns zero for
    /// Cities.exe in some launch configurations, so the primary path is a straight
    /// <c>EnumWindows</c> scan for a visible top-level window owned by this process.
    /// </summary>
    public static class WindowGeometry
    {
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);

        /// <summary>
        /// The client area of the game's main window in desktop coordinates:
        /// <c>x</c>, <c>y</c> = top-left of the client area on the desktop,
        /// <c>width</c>, <c>height</c> = client size in pixels.
        /// </summary>
        /// <param name="x">Desktop x of the client area's top-left, or -1.</param>
        /// <param name="y">Desktop y of the client area's top-left, or -1.</param>
        /// <param name="width">Client width in pixels, or -1.</param>
        /// <param name="height">Client height in pixels, or -1.</param>
        /// <returns>True when the window geometry could be read.</returns>
        public static bool TryGetClientRect(out int x, out int y, out int width, out int height)
        {
            x = -1;
            y = -1;
            width = -1;
            height = -1;

            try
            {
                IntPtr window = FindGameWindow();
                if (window == IntPtr.Zero)
                {
                    return false;
                }

                RECT rect;
                if (!GetClientRect(window, out rect))
                {
                    return false;
                }

                POINT origin = new POINT();
                origin.X = 0;
                origin.Y = 0;
                if (!ClientToScreen(window, ref origin))
                {
                    return false;
                }

                x = origin.X;
                y = origin.Y;
                width = rect.Right - rect.Left;
                height = rect.Bottom - rect.Top;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// A visible top-level window owned by the current (game) process. Enumerates
        /// with EnumWindows — the reliable path — and falls back to the Process API.
        /// </summary>
        private static IntPtr FindGameWindow()
        {
            int pid = Process.GetCurrentProcess().Id;
            IntPtr best = IntPtr.Zero;
            long bestArea = -1;

            List<IntPtr> candidates = new List<IntPtr>();
            EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                uint windowPid;
                GetWindowThreadProcessId(hWnd, out windowPid);
                if (windowPid == (uint)pid && IsWindowVisible(hWnd))
                {
                    candidates.Add(hWnd);
                }
                return true;
            }, IntPtr.Zero);

            foreach (IntPtr hWnd in candidates)
            {
                RECT rect;
                if (GetClientRect(hWnd, out rect))
                {
                    long area = (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top);
                    if (area > bestArea)
                    {
                        bestArea = area;
                        best = hWnd;
                    }
                }
            }

            if (best != IntPtr.Zero)
            {
                return best;
            }

            return Process.GetCurrentProcess().MainWindowHandle;
        }
    }
}
