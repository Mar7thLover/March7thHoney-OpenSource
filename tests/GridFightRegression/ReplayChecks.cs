using System.Net;
using System.Reflection;
using March7thHoney.Command;
using March7thHoney.Command.Command;
using March7thHoney.Command.Command.Cmd;
using March7thHoney.Data;
using March7thHoney.Database;
using March7thHoney.Database.Generated;
using March7thHoney.Database.Player;
using March7thHoney.GameServer.Game.Player;
using March7thHoney.GameServer.Game.Scene;
using March7thHoney.GameServer.Server.Packet.Send.GridFight;
using March7thHoney.GameServer.Server.Packet.Send.Quest;
using March7thHoney.Kcp.KcpSharp;
using March7thHoney.Proto;
using Microsoft.Data.Sqlite;

internal static class ReplayChecks
{
    internal static async Task Run(string snapshot, DatabaseHelper database, Action<bool, string> check)
    {
        // Only an operator-created backup under Ops/Backups is accepted; never open the live database.
        var copy = Path.GetFullPath(snapshot);
        if (!copy.Contains($"{Path.DirectorySeparatorChar}Ops{Path.DirectorySeparatorChar}Backups{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Replay requires an Ops/Backups SQLite copy");
        DatabaseHelper.UidInstanceMap.Clear();
        using (var source = new SqliteConnection($"Data Source={copy};Mode=ReadOnly;Pooling=False"))
        {
            source.Open();
            foreach (var (type, store) in EntityStores.Map)
            {
                if (type.Namespace?.Contains(".Account") == true) continue;
                foreach (var entity in store.LoadAll(source))
                    DatabaseHelper.UidInstanceMap.GetOrAdd(entity.Uid, _ => []).Add(entity);
            }
        }
        var players = DatabaseHelper.GetAllInstance<PlayerData>()!;
        check(players.Count == 1, "single-player backup fixture");
        var player = new PlayerInstance(players.Single());
        typeof(PlayerInstance).GetMethod("WireManagers", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(player, null);
        var scene = new SceneInstance(player, GameData.MazePlaneData[player.Data.PlaneId], player.Data.FloorId, player.Data.EntryId);
        typeof(PlayerInstance).GetProperty(nameof(PlayerInstance.SceneInstance))!.SetValue(player, scene);
        var claimed = player.QuestManager!.Data.Quests.Values.Where(q => q.QuestStatus == QuestStatus.QuestClose)
            .ToDictionary(q => q.QuestId, q => (q.Progress, q.FinishTime));
        var endpoint = new IPEndPoint(IPAddress.Loopback, 1);
        using var conversation = new KcpConversation(endpoint, new DiscardTransport(), null);
        var target = new QuietConnection(conversation, endpoint) { Player = player };
        var command = new CommandUnlockAll();
        var arg = new CommandArg("unlockall", new SilentSender(), target);
        var baselineActivity = new PacketGridFightGetDataScRsp(player).Data.ToArray();
        Console.WriteLine($"Replay clock: fakeDate={player.Data.FakeTimeDate} effectiveUnix={March7thHoney.Util.ServerTimeProvider.GetServerUnixSec(player.Data)}");
        foreach (var row in GameData.GridFightSeasonModuleData.Values)
            Console.WriteLine($"GridFight schedule resource: season={row.SeasonID} subSeason={row.SubSeasonID} activityModule={row.ActivityModuleID}");
        var entranceScene = new SceneInstance(player, GameData.MazePlaneData[20322], 20322001, 2032202);
        void Inspect(string step)
        {
            check(baselineActivity.SequenceEqual(new PacketGridFightGetDataScRsp(player).Data.ToArray()),
                "activity response unchanged after " + step);
            var quests = GetQuestDataScRsp.Parser.ParseFrom(new PacketGetQuestDataScRsp(player).Data);
            check(quests.QuestList.All(q => !player.QuestManager.Data.Quests.TryGetValue((int)q.Id, out var saved) ||
                (q.Progress == saved.Progress && q.FinishTime == saved.FinishTime &&
                 q.Status == player.QuestManager.GetQuestStatus((int)q.Id))), "persisted quest fields after " + step);
            check(claimed.All(pair => player.QuestManager.Data.Quests.TryGetValue(pair.Key, out var quest) &&
                quest.QuestStatus == QuestStatus.QuestClose && (quest.Progress, quest.FinishTime) == pair.Value),
                "claimed rewards preserved after " + step);
            var persisted = player.SceneData!.GetFloorSavedValues(20322001);
            var projected = entranceScene.BuildClientFloorSavedData();
            Console.WriteLine($"Replay {step}: completed={player.QuestManager.Data.Quests.Values.Count(q => q.QuestStatus is QuestStatus.QuestFinish or QuestStatus.QuestClose)} tutorials={player.TutorialData!.Tutorials.Count} guides={player.TutorialGuideData!.Tutorials.Count} gridFlagSaved={persisted.GetValueOrDefault("FSV_370GridFight")} gridFlagSent={projected.GetValueOrDefault("FSV_370GridFight")}");
        }
        Inspect("before");
        var entranceSaved = player.SceneData!.GetFloorSavedValues(20322001);
        foreach (var group in entranceScene.FloorInfo!.Groups.Values.Where(g => g.PropList.Any(p => p.PropID == 103154)))
            Console.WriteLine($"Entrance resource group={group.Id} conditions={group.SavedValueCondition.Conditions.Count} knownComparisons={group.SavedValueCondition.Conditions.Count(c => c.Operation != March7thHoney.Enums.Task.CompareTypeEnum.Unknow)} allowed={group.SavedValueCondition.IsTrue(entranceSaved)}");
        for (var repeat = 0; repeat < 2; repeat++)
        {
            await command.UnlockAllQuests(arg);
            Inspect($"{repeat + 1}/quest");
            await command.UnlockAllChallenge(arg);
            Inspect($"{repeat + 1}/challenge");
            await command.UnlockAllGrid(arg);
            Inspect($"{repeat + 1}/grid");
            await command.UnlockAllMissions(arg);
            Inspect($"{repeat + 1}/mission");
            await command.UnlockAllTutorial(arg);
            Inspect($"{repeat + 1}/tutorial");
            await command.UnlockAllRogue(arg);
            Inspect($"{repeat + 1}/rogue");
            var info = GridFightGetDataScRsp.Parser.ParseFrom(new PacketGridFightGetDataScRsp(player).Data);
            check(info.Retcode == 0 && info.SystemInfo.StaticInfo.Season > 0, "backup activity data after GM sequence");
            var grid = player.GridFightManager!;
            check(grid.Start(grid.Catalog.CurrentSeason, grid.Catalog.HighestDivision.ID, false), "backup legal game after repeated GM sequence");
            grid.ClearSession();
            check(claimed.All(pair => player.QuestManager.Data.Quests.TryGetValue(pair.Key, out var quest) &&
                quest.QuestStatus == QuestStatus.QuestClose && (quest.Progress, quest.FinishTime) == pair.Value),
                "GM replay preserves all previously claimed rewards");
        }
        DatabaseHelper.MarkDirty(player.Uid);
        database.SaveDatabase();
        Console.WriteLine("Backup replay: GM sequence repeated twice in isolated cache and temporary SQLite; no account table loaded.");
    }
    private sealed class SilentSender : ICommandSender
    {
        public ValueTask SendMsg(string msg) => ValueTask.CompletedTask;
        public bool HasPermission(string permission) => true;
        public int GetSender() => 0;
    }
    private sealed class DiscardTransport : IKcpTransport
    {
        public ValueTask SendPacketAsync(Memory<byte> packet, IPEndPoint remoteEndpoint, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }
}
