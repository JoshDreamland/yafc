using System;
using System.Numerics;
using System.Runtime.InteropServices;
using SDL2;

namespace YAFC.UI
{
    // Main window is resizable and hardware-accelerated
    public abstract class WindowMain : Window
    {
        protected void Create(string title, int display)
        {
            if (visible)
                return;
            pixelsPerUnit = CalculateUnitsToPixels(display);
            var minwidth = MathUtils.Round(85f * pixelsPerUnit);
            var minheight = MathUtils.Round(60f * pixelsPerUnit); 
            window = SDL.SDL_CreateWindow(title,
                SDL.SDL_WINDOWPOS_CENTERED_DISPLAY(display),
                SDL.SDL_WINDOWPOS_CENTERED_DISPLAY(display),
                minwidth, minheight,
                SDL.SDL_WindowFlags.SDL_WINDOW_RESIZABLE | (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? 0 : SDL.SDL_WindowFlags.SDL_WINDOW_OPENGL)
            );
            SDL.SDL_SetWindowMinimumSize(window, minwidth, minheight);
            WindowResize();
            surface = new MainWindowDrawingSurface(this);
            base.Create();
        }

        protected override void BuildContents(ImGui gui)
        {
            BuildContent(gui);
            gui.SetContextRect(new Rect(default, size));
        }

        protected abstract void BuildContent(ImGui gui);

        protected override void OnRepaint()
        {
            rootGui.Rebuild();
            base.OnRepaint();
        }

        internal override void WindowResize()
        {
            SDL.SDL_GetWindowSize(window, out var windowWidth, out var windowHeight);
            contentSize = new Vector2(windowWidth/pixelsPerUnit, windowHeight/pixelsPerUnit);
            base.WindowResize();
        }

        protected WindowMain(Padding padding) : base(padding) {}
    }
}