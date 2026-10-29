using System.Runtime.InteropServices;

namespace SpirePilot.Infra;

/// <summary>
/// Linux / Steam Deck: Harmony 2.4's native MonoMod runtime needs the unwind symbols of libgcc_s, which the game
/// process does not export globally. Preloading the library with RTLD_GLOBAL before the first patch avoids a native
/// crash. BaseLib and RitsuLib do the same; we must not rely on them being installed.
/// Adapted from RitsuLib's LinuxHarmonyNativePreloader (MIT, BAKAOLC/STS2-RitsuLib).
/// </summary>
internal static class HarmonyPreload
{
    private const int RtldNow = 2;
    private const int RtldGlobal = 0x100;
    private static readonly string[] Candidates = { "libgcc_s.so.1", "libunwind.so.8", "libunwind.so" };
    private static readonly List<IntPtr> Handles = new();
    private static bool _attempted;

    [DllImport("libdl.so.2", EntryPoint = "dlopen", CharSet = CharSet.Ansi)]
    private static extern IntPtr DlOpen(string fileName, int flags);

    /// <summary>Idempotent; does nothing on other operating systems.</summary>
    public static void EnsureLoaded()
    {
        if (_attempted || !OperatingSystem.IsLinux()) return;
        _attempted = true;
        try
        {
            var loaded = new List<string>();
            foreach (var library in Candidates)
            {
                var handle = DlOpen(library, RtldNow | RtldGlobal);
                if (handle == IntPtr.Zero) continue;
                Handles.Add(handle); // keep the library loaded for the life of the process
                loaded.Add(library);
            }
            if (loaded.Count > 0) ModLog.Info("Harmony native preload (RTLD_GLOBAL): " + string.Join(", ", loaded));
            else ModLog.Warn("Harmony native preload: no libgcc_s/libunwind found; patching may still work");
        }
        catch (Exception ex)
        {
            ModLog.Warn("Harmony native preload skipped: " + ex.Message);
        }
    }
}
