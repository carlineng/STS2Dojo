using System;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;

namespace STS2Dojo.STS2DojoCode;

/// <summary>
/// Shared §8.0 launch sequence: stand up a throwaway NON-SAVING run and drop into a chosen encounter,
/// bypassing the map. Used by both <c>dojo</c> (junk loadout) and <c>dojoreplay</c> (reconstructed loadout).
/// </summary>
public static class DojoLaunch
{
    public static async Task<RunState> StartThrowawayRun(NGame game, CharacterModel character, int ascensionLevel)
    {
        if (RunManager.Instance.IsInProgress)
        {
            RunManager.Instance.CleanUp();
        }

        string seed = SeedHelper.GetRandomSeed();
        return await game.StartNewSingleplayerRun(
            character,
            shouldSave: false,
            ActModel.GetDefaultList(),
            Array.Empty<ModifierModel>(),
            seed,
            GameMode.Standard,
            ascensionLevel);
    }

    public static async Task EnterEncounter(EncounterModel encounter)
    {
        await RunManager.Instance.EnterRoomDebug(encounter.RoomType, MapPointType.Unassigned, encounter);
    }
}
