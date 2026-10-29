using System.Runtime.InteropServices;

namespace SpirePilot.Infra;

/// <summary>
/// Linux / Steam Deck: Harmony 2.4's native runtime needs the unwind symbols of libgcc/libunwind loaded with
/// RTLD_GLOBAL before the first patch, otherwise patching can crash. Same fix as RitsuLib's
/// LinuxHarmonyNativePreloader and BaseLib (both MIT). Does nothing on other platforms.
/// </summary>
internal static class HarmonyPreload
{
    private const int RtldNow = 2;
    private const int RtldGlobal = 0x100;
    private static readonly string[] Candidates = { "libgcc_s.so.1", "libunwind.so.8", "libunwind.so" };
    private static readonly List<IntPtr> Handles = new();
    private static bool _attempted;

    [DllImport("libdl.so.2", EntryPoint = "dlopen", CharSet = CharSet.Ansi)]
    private static extern IntPtr Dlopen(string filename, int flags);

    public static void EnsureLoaded()
    {
        if (_attempted || !OperatingSystem.IsLinux()) return;
        _attempted = true;
        foreach (var lib in Candidates)
        {
            try
            {
                var handle = Dlopen(lib, RtldNow | RtldGlobal);
                if (handle != IntPtr.Zero)
                {
                    Handles.Add(handle);
                    ModLog.Info("preloaded " + lib + " for Harmony");
                }
            }
            catch (Exception ex)
            {
                ModLog.Warn($"could not preload {lib}: {ex.Message}");
            }
        }
    }
}
