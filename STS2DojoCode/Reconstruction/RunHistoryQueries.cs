using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;

namespace STS2Dojo.STS2DojoCode.Reconstruction;

public static class RunHistoryQueries
{
    public static bool IsSinglePlayer(RunHistory run, ulong expectedPlayerId = 1) =>
        run.Players.Count == 1 && run.Players[0].Id == expectedPlayerId;

    public static IReadOnlyList<MapPointHistoryEntry> FlattenFloors(RunHistory run) =>
        run.MapPointHistory.SelectMany(act => act).ToList();

    public static bool IsCombatRoom(MapPointRoomHistoryEntry room) =>
        room.RoomType is RoomType.Monster or RoomType.Elite or RoomType.Boss;

    public static (MapPointHistoryEntry Floor, MapPointRoomHistoryEntry CombatRoom) FindCombatFloor(
        RunHistory run, int globalFloor)
    {
        IReadOnlyList<MapPointHistoryEntry> floors = FlattenFloors(run);
        if (globalFloor < 1 || globalFloor > floors.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(globalFloor),
                $"Run has {floors.Count} floors; floor {globalFloor} is out of range.");
        }

        MapPointHistoryEntry targetFloor = floors[globalFloor - 1];
        MapPointRoomHistoryEntry combatRoom = targetFloor.Rooms.FirstOrDefault(IsCombatRoom)
            ?? throw new InvalidOperationException(
                $"Floor {globalFloor} has no combat room (room types present: " +
                $"{string.Join(", ", targetFloor.Rooms.Select(r => r.RoomType))}).");
        return (targetFloor, combatRoom);
    }

    public static bool HasAnyCombatFloor(RunHistory run) =>
        FlattenFloors(run).Any(floor => floor.Rooms.Any(IsCombatRoom));
}
