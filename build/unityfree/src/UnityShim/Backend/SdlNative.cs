using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace UnityShim.Backend
{
    public static class SdlNative
    {
        private static bool _initialized;
        public static string LoadedLibraryPath { get; private set; } = string.Empty;

        public static void EnsureInitialized()
        {
            if (_initialized) return;
            _initialized = true;
            NativeLibrary.SetDllImportResolver(typeof(SDL.SDL3).Assembly, ResolveLibrary);
        }

        private static nint ResolveLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (libraryName == "SDL3" || libraryName == "libSDL3" || libraryName == "SDL3.dll" || libraryName == "libSDL3.0.dylib")
            {
                string baseDir = AppContext.BaseDirectory;
                string[] candidates =
                {
                    Path.Combine(baseDir, "libSDL3.0.dylib"),
                    Path.Combine(baseDir, "libSDL3.dylib"),
                    Path.GetFullPath(Path.Combine(baseDir, "..", "Frameworks", "libSDL3.0.dylib")),
                    "/opt/homebrew/opt/sdl3/lib/libSDL3.0.dylib",
                    "/usr/local/opt/sdl3/lib/libSDL3.0.dylib"
                };

                foreach (string path in candidates)
                {
                    if (File.Exists(path) && NativeLibrary.TryLoad(path, out nint handle))
                    {
                        LoadedLibraryPath = path;
                        return handle;
                    }
                }
            }

            return nint.Zero;
        }

        public static unsafe string GetErrorString()
        {
            byte* err = SDL.SDL3.Unsafe_SDL_GetError();
            return err == null ? string.Empty : (Marshal.PtrToStringUTF8((nint)err) ?? string.Empty);
        }
    }
}
