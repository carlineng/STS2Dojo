using HarmonyLib;
using MegaCrit.Sts2.Core.Saves;

namespace STS2Dojo.STS2DojoCode;

/// <summary>
/// Unconditional hard safety net around every profile-scoped save — the last remaining guard from the
/// §5h real-profile save-corruption incident (CLAUDE.md §5h/§6).
///
/// <para><b>History.</b> The Dojo used to reach the real profile's runs by temporarily flipping the single
/// global static <c>UserDataPathProvider.IsRunningModded = false</c> while a stock <c>NRunHistory</c> screen
/// was open (first as the landing screen, later only as a per-row "View All Combats" drill-in). That flip is
/// GONE: the custom Dojo screen reads the real profile's <c>.run</c> files directly (<c>DojoRunIndex</c>,
/// never touching the flag), and the in-row floor map (<c>DojoRunRow</c>) replaced the drill-in, so nothing
/// in the mod ever writes <c>IsRunningModded = false</c> anymore. The per-open restore hooks that used to
/// live here (submenu-close and scene-swap) went away with the flip.</para>
///
/// <para><b>Why this patch stays anyway.</b> It is the load-bearing guarantee, not a convenience. Each of
/// the three profile-scoped save methods resolves its file PATH live from <c>IsRunningModded</c> but always
/// serializes the CURRENT PROCESS's in-memory (modded-profile) data. If the flag were ever <c>false</c> when
/// any of them fired — a future regression, a new game code path, anything — the modded session's near-empty
/// progress would silently overwrite the REAL profile's <c>progress.save</c>, exactly as happened in §5h
/// (the Compendium vanishing was the symptom). Forcing the flag back to <c>true</c> at the actual save
/// chokepoint is always correct in a modded session and can only ever prevent a modded→real overwrite, never
/// cause the reverse — so it is kept as an unconditional last line of defense even though the code that made
/// it necessary is gone. See CLAUDE.md §6: prefer a hard net at the read/write chokepoint over trusting
/// today's call sites.</para>
/// </summary>
[HarmonyPatch(typeof(SaveManager))]
public static class DojoRunHistorySaveSafetyPatch
{
    [HarmonyPatch(nameof(SaveManager.SaveProgressFile))]
    [HarmonyPrefix]
    // ReSharper disable once UnusedMember.Global
    public static void SaveProgressFilePrefix() => RestoreFlag();

    [HarmonyPatch(nameof(SaveManager.SaveProfile))]
    [HarmonyPrefix]
    // ReSharper disable once UnusedMember.Global
    public static void SaveProfilePrefix() => RestoreFlag();

    [HarmonyPatch(nameof(SaveManager.SavePrefsFile))]
    [HarmonyPrefix]
    // ReSharper disable once UnusedMember.Global
    public static void SavePrefsFilePrefix() => RestoreFlag();

    private static void RestoreFlag() => UserDataPathProvider.IsRunningModded = true;
}
