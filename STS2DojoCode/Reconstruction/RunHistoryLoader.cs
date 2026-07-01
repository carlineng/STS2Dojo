using System;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Migrations;

namespace STS2Dojo.STS2DojoCode.Reconstruction;

/// <summary>
/// Loads a <c>.run</c> history file from an arbitrary absolute path, reusing the game's own
/// <see cref="RunHistory"/> schema and <see cref="MigrationManager"/> so schema_version 8 vs 9 drift
/// (see runfiles/SCHEMA.md Q9) is handled by the same migrations the game applies to its own saves,
/// instead of us hand-branching on schema_version.
/// </summary>
public static class RunHistoryLoader
{
    public static RunHistory Load(string absolutePath)
    {
        MigrationManager migrationManager = new MigrationManager(new LocalFileSaveStore());
        ReadSaveResult<RunHistory> result = migrationManager.LoadSave<RunHistory>(absolutePath);
        if (!result.Success || result.SaveData == null)
        {
            throw new InvalidOperationException(
                $"Failed to load run history '{absolutePath}': {result.Status} {result.ErrorMessage}");
        }
        return result.SaveData;
    }
}
