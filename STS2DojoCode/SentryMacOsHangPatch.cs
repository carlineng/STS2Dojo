using HarmonyLib;
using MegaCrit.Sts2.Core.Debug;

namespace STS2Dojo.STS2DojoCode;

/// <summary>
/// Workaround for a modded-startup deadlock on macOS 26.x.
///
/// The native Sentry GDExtension registers a main-thread CFRunLoop observer. In a modded run the game tears
/// the extension down via <see cref="SentryService.Shutdown"/> (it calls the extension's native <c>shutdown</c>
/// because event reporting is disabled when modded). On macOS 26.x, after that native teardown the still-registered
/// CFRunLoop observer fires and calls back into the torn-down extension, wedging the main thread at the main menu
/// (beachball / black window). Confirmed by sampling the frozen process: the main thread sits forever in
/// libsentry's run-loop observer callback.
///
/// An unmodded run never shuts Sentry down, so the observer stays valid and the game boots fine. Skipping
/// <c>Shutdown()</c> on macOS reproduces that safe state. Modded event reporting stays suppressed independently
/// (the game's DisableGdExtensionIfModded sets the native sampler to reject, and the .NET before-send hook rejects
/// when modded), so telemetry behavior is unchanged — we only prevent the crash-prone teardown.
///
/// Scoped to macOS so Windows/Linux keep the game's normal modded Sentry teardown.
/// </summary>
[HarmonyPatch(typeof(SentryService), nameof(SentryService.Shutdown))]
public static class SentryMacOsHangPatch
{
    // Harmony prefix: return false to skip the original method.
    // ReSharper disable once UnusedMember.Global
    public static bool Prefix()
    {
        // TODO(telemetry): skipping Shutdown() leaves the .NET Sentry SDK alive, so modded runs report at
        // unmodded parity (gated by the user's data-upload consent) rather than fully silent. To make modded
        // runs send nothing regardless of consent, also close the .NET SDK here (e.g. Sentry.SentrySdk.Close()
        // via reflection to avoid a Sentry.dll build reference) while still skipping the native teardown below.
        if (!System.OperatingSystem.IsMacOS())
        {
            return true; // other platforms: run the original teardown unchanged
        }

        MainFile.Logger.Info(
            "[STS2Dojo] Skipping SentryService.Shutdown() on macOS to avoid the native Sentry run-loop-observer deadlock.");
        return false; // macOS: skip the teardown
    }
}
