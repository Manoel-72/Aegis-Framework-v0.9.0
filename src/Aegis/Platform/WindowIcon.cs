using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Aegis.Platform;

/// <summary>
/// Define o ícone da janela em runtime quando o backend DesktopGL/SDL ou Win32 permite.
///
/// Importante:
/// - Falhas são silenciosas para não quebrar o jogo em Linux/macOS/Windows.
/// - O ícone do executável/taskbar é configurado no .csproj via ApplicationIcon.
/// - Este helper altera o ícone em runtime da janela e barra de tarefas do SO.
/// </summary>
internal static class WindowIcon
{
    public static void TrySet(IntPtr windowHandle, string? iconPath, GraphicsDevice graphicsDevice)
    {
        try
        {
            using Stream? stream = ResolveIconStream(iconPath);
            if (stream == null)
                return;

            using var texture = Texture2D.FromStream(graphicsDevice, stream);
            if (texture.Width <= 0 || texture.Height <= 0)
                return;

            var colors = new Color[texture.Width * texture.Height];
            texture.GetData(colors);

            // 1. Windows Win32 WM_SETICON (Taskbar e barra de título no Windows)
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && windowHandle != IntPtr.Zero)
            {
                TrySetWin32Icon(windowHandle, colors, texture.Width, texture.Height);
            }

            // 2. SDL2 Window Icon (para janelas SDL2 DesktopGL)
            TrySetSdlIcon(windowHandle, colors, texture.Width, texture.Height);
        }
        catch
        {
            // Ícone nunca deve impedir o jogo de abrir.
        }
    }

    private static Stream? ResolveIconStream(string? customPath)
    {
        if (!string.IsNullOrWhiteSpace(customPath) && File.Exists(customPath))
        {
            return File.OpenRead(customPath);
        }

        var candidates = new[]
        {
            Path.Combine(Directory.GetCurrentDirectory(), "res", "aegis-logo.png"),
            Path.Combine(Directory.GetCurrentDirectory(), "aegis-logo.png"),
            Path.Combine(AppContext.BaseDirectory, "res", "aegis-logo.png"),
            Path.Combine(AppContext.BaseDirectory, "aegis-logo.png")
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                return File.OpenRead(path);
            }
        }

        // Embedded resource fallback
        var assembly = typeof(WindowIcon).Assembly;
        var resourceNames = assembly.GetManifestResourceNames();
        foreach (var name in resourceNames)
        {
            if (name.EndsWith("aegis-logo.png", StringComparison.OrdinalIgnoreCase))
            {
                return assembly.GetManifestResourceStream(name);
            }
        }

        return null;
    }

    private static void TrySetSdlIcon(IntPtr windowHandle, Color[] colors, int width, int height)
    {
        try
        {
            var pixels = new byte[colors.Length * 4];
            for (var i = 0; i < colors.Length; i++)
            {
                var p = i * 4;
                pixels[p + 0] = colors[i].R;
                pixels[p + 1] = colors[i].G;
                pixels[p + 2] = colors[i].B;
                pixels[p + 3] = colors[i].A;
            }

            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                var surface = SDL_CreateRGBSurfaceFrom(
                    handle.AddrOfPinnedObject(),
                    width,
                    height,
                    32,
                    width * 4,
                    0x000000ff,
                    0x0000ff00,
                    0x00ff0000,
                    unchecked((int)0xff000000));

                if (surface == IntPtr.Zero)
                    return;

                try
                {
                    IntPtr sdlWindow = SDL_GL_GetCurrentWindow();
                    if (sdlWindow != IntPtr.Zero)
                    {
                        SDL_SetWindowIcon(sdlWindow, surface);
                    }

                    if (windowHandle != IntPtr.Zero && windowHandle != sdlWindow)
                    {
                        SDL_SetWindowIcon(windowHandle, surface);
                    }
                }
                finally
                {
                    SDL_FreeSurface(surface);
                }
            }
            finally
            {
                handle.Free();
            }
        }
        catch
        {
        }
    }

    private static void TrySetWin32Icon(IntPtr hWnd, Color[] colors, int width, int height)
    {
        try
        {
            var bgraPixels = new byte[width * height * 4];
            var maskBits = new byte[(width * height + 7) / 8];

            for (var i = 0; i < colors.Length; i++)
            {
                var p = i * 4;
                bgraPixels[p + 0] = colors[i].B;
                bgraPixels[p + 1] = colors[i].G;
                bgraPixels[p + 2] = colors[i].R;
                bgraPixels[p + 3] = colors[i].A;

                if (colors[i].A < 128)
                {
                    int maskIndex = i / 8;
                    int bitIndex = 7 - (i % 8);
                    maskBits[maskIndex] |= (byte)(1 << bitIndex);
                }
            }

            IntPtr hbmColor = CreateBitmap(width, height, 1, 32, bgraPixels);
            IntPtr hbmMask = CreateBitmap(width, height, 1, 1, maskBits);

            if (hbmColor == IntPtr.Zero || hbmMask == IntPtr.Zero)
            {
                if (hbmColor != IntPtr.Zero) DeleteObject(hbmColor);
                if (hbmMask != IntPtr.Zero) DeleteObject(hbmMask);
                return;
            }

            var iconInfo = new ICONINFO
            {
                fIcon = true,
                xHotspot = 0,
                yHotspot = 0,
                hbmMask = hbmMask,
                hbmColor = hbmColor
            };

            IntPtr hIcon = CreateIconIndirect(ref iconInfo);

            DeleteObject(hbmColor);
            DeleteObject(hbmMask);

            if (hIcon != IntPtr.Zero)
            {
                const uint WM_SETICON = 0x0080;
                IntPtr ICON_SMALL = (IntPtr)0;
                IntPtr ICON_BIG = (IntPtr)1;

                SendMessage(hWnd, WM_SETICON, ICON_SMALL, hIcon);
                SendMessage(hWnd, WM_SETICON, ICON_BIG, hIcon);
            }
        }
        catch
        {
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateIconIndirect(ref ICONINFO piconinfo);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateBitmap(int nWidth, int nHeight, uint cPlanes, uint cBitCount, byte[] lpBits);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr SDL_GL_GetCurrentWindow();

    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr SDL_CreateRGBSurfaceFrom(
        IntPtr pixels,
        int width,
        int height,
        int depth,
        int pitch,
        int rmask,
        int gmask,
        int bmask,
        int amask);

    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_SetWindowIcon(IntPtr window, IntPtr icon);

    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_FreeSurface(IntPtr surface);
}
