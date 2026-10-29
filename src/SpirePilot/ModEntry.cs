using MegaCrit.Sts2.Core.Modding;
using SpirePilot.Infra;

namespace SpirePilot;

/// <summary>Entry point called by the game's mod loader on the main thread.</summary>
[ModInitializer(nameof(Initialize))]
public static class ModEntry
{
    public static void Initialize()
    {
        try
        {
            PilotRuntime.Initialize();
        }
        catch (Exception ex)
        {
            // Never take the game down with us.
            ModLog.Error("SpirePilot failed to initialize: " + ex);
        }
    }
}
