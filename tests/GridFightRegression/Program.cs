using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading.Channels;
using Google.Protobuf;
using MemoryPack;
using March7thHoney.Data;
using March7thHoney.Database;
using March7thHoney.Database.Generated;
using March7thHoney.Database.Player;
using March7thHoney.Database.Quests;
using March7thHoney.Enums.Scene;
using March7thHoney.GameServer.Game.GridFight;
using March7thHoney.GameServer.Game.Inventory;
using March7thHoney.GameServer.Game.Mission;
using March7thHoney.GameServer.Game.Player;
using March7thHoney.GameServer.Game.Quest;
using March7thHoney.GameServer.Game.Scene;
using March7thHoney.GameServer.Game.Scene.Entity;
using March7thHoney.GameServer.Server;
using March7thHoney.GameServer.Server.Packet;
using March7thHoney.GameServer.Server.Packet.Recv.GridFight;
using March7thHoney.GameServer.Server.Packet.Recv.Scene;
using March7thHoney.GameServer.Server.Packet.Recv.Quest;
using March7thHoney.GameServer.Server.Packet.Recv.DailyActive;
using March7thHoney.GameServer.Server.Packet.Send.DailyActive;
using March7thHoney.Kcp;
using March7thHoney.Kcp.KcpSharp;
using March7thHoney.Proto;
using March7thHoney.Util;
using Microsoft.Data.Sqlite;

if (args.Length is < 1 or > 2) throw new ArgumentException("Usage: GridFightRegression <read-only Resource.bin> [SQLite backup copy]");
var scratch = Path.Combine(Path.GetTempPath(), $"m7h-gridfight-{Guid.NewGuid():N}");
Directory.CreateDirectory(scratch);
ConfigManager.Config.Path.ConfigPath = scratch;
ConfigManager.Config.GameServer.UsePacketEncryption = false;
ConfigManager.Config.ServerOption.LogOption.EnableGamePacketLog = false;
SQLitePCL.Batteries_V2.Init();
typeof(DatabaseHelper).GetField("_connString", BindingFlags.NonPublic | BindingFlags.Static)!
    .SetValue(null, $"Data Source={Path.Combine(scratch, "test.db")};Pooling=False");
var database = new DatabaseHelper();
using (var db = new SqliteConnection($"Data Source={Path.Combine(scratch, "test.db")};Pooling=False"))
{
    db.Open();
    foreach (var store in EntityStores.Map.Values) store.EnsureTable(db);
}
MemoryPackFormatterProvider.Register(new JsonObjectMemoryPackFormatter());
MemoryPackFormatterProvider.Register(new BigIntegerMemoryPackFormatter());
using (var file = File.OpenRead(args[0]))
using (var gzip = new GZipStream(file, CompressionMode.Decompress))
using (var data = new MemoryStream())
{
    gzip.CopyTo(data);
    GameData.ApplySnapshot(MemoryPackSerializer.Deserialize<GameDataSnapshot>(data.ToArray())!);
}
var checks = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException(label);
    checks++;
}
SceneConditionChecks.Run(Check);
HandlerManager.Init();
Check(HandlerManager.GetHandler(CmdIds.CycleScoreRewardGetDataCsReq) != null, "weekly score query must have a handler instead of an empty success response");
var player = new PlayerInstance(new PlayerData { Uid = 1 }) { Uid = 1 };
var quests = new QuestManager(player);
var missions = new MissionManager(player);
var grid = new GridFightManager(player, new Random(42));
typeof(PlayerInstance).GetProperty(nameof(PlayerInstance.QuestManager))!.SetValue(player, quests);
typeof(PlayerInstance).GetProperty(nameof(PlayerInstance.GridFightManager))!.SetValue(player, grid);
typeof(PlayerInstance).GetProperty(nameof(PlayerInstance.MissionManager))!.SetValue(player, missions);
typeof(PlayerInstance).GetProperty(nameof(PlayerInstance.InventoryManager))!.SetValue(player, new InventoryManager(player));
var entranceScene = new SceneInstance(player, GameData.MazePlaneData[20322], 20322001, 0);
var entranceGroup = entranceScene.FloorInfo!.Groups[2];
var entrance = new EntityProp(entranceScene, GameData.MazePropData[103154], entranceGroup,
    entranceGroup.PropList.Single(p => p.ID == 300003)) { EntityId = 1234, State = PropStateEnum.Closed };
entranceScene.Entities[entrance.EntityId] = entrance;
var endpoint = new IPEndPoint(IPAddress.Loopback, 1);
using var outbound = new LoopTransport();
using var inbound = new LoopTransport();
using var server = new KcpConversation(endpoint, outbound, null);
using var client = new KcpConversation(endpoint, inbound, null);
outbound.Peer = client;
inbound.Peer = server;
var connection = new QuietConnection(server, endpoint) { Player = player };
async Task<T> Request<T>(Handler handler, IMessage request, ushort opcode) where T : IMessage<T>, new()
{
    await handler.OnHandle(connection, [], request.ToByteArray());
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var buffer = new byte[March7thHoneyConnection.MaxPacketBytes];
    var result = await client.ReceiveAsync(buffer, timeout.Token);
    Check(BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(4, 2)) == opcode, "response opcode");
    var length = (int)BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(8, 4));
    Check(result.BytesReceived == length + 16, "response frame length");
    var response = new T();
    response.MergeFrom(buffer.AsSpan(12, length).ToArray());
    using var extraTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
    try
    {
        await client.ReceiveAsync(buffer, extraTimeout.Token);
        throw new InvalidOperationException("unexpected second response");
    }
    catch (OperationCanceledException) { checks++; }
    return response;
}
foreach (var status in new[] { QuestStatus.QuestDoing, QuestStatus.QuestFinish, QuestStatus.QuestClose })
{
    foreach (var id in GameData.QuestDataData.Keys)
        quests.Data.Quests[id] = new QuestInfo { QuestId = id, QuestStatus = status,
            Progress = status == QuestStatus.QuestDoing ? 0 : 1, FinishTime = 123 };
    var before = MemoryPackSerializer.Serialize(quests.Data.Quests);
    var weekly = await Request<CycleScoreRewardGetDataScRsp>(new HandlerCycleScoreRewardGetDataCsReq(),
        new CycleScoreRewardGetDataCsReq(), CmdIds.CycleScoreRewardGetDataScRsp);
    var now = ServerTimeProvider.GetServerUnixSec(player.Data);
    Check(weekly.Retcode == 0 && weekly.NFEDANFBIBE != null && weekly.NFEDANFBIBE.StartTime <= now &&
        weekly.NFEDANFBIBE.EndTime > now, "weekly panel has initialized data and cannot immediately expire");
    var weeklyAgain = await Request<CycleScoreRewardGetDataScRsp>(new HandlerCycleScoreRewardGetDataCsReq(),
        new CycleScoreRewardGetDataCsReq(), CmdIds.CycleScoreRewardGetDataScRsp);
    Check(weekly.Equals(weeklyAgain), "weekly read is idempotent within the same period");
    var reward = await Request<CycleScoreRewardTakeRewardScRsp>(new HandlerCycleScoreRewardTakeRewardCsReq(),
        new CycleScoreRewardTakeRewardCsReq(), CmdIds.CycleScoreRewardTakeRewardScRsp);
    Check(reward.Retcode == (uint)Retcode.RetFail, "unsupported weekly reward cannot report a fake successful claim");
    var destroy = await Request<CycleScoreRewardDestroyExceedRewardScRsp>(new HandlerCycleScoreRewardDestroyExceedRewardCsReq(),
        new CycleScoreRewardDestroyExceedRewardCsReq(), CmdIds.CycleScoreRewardDestroyExceedRewardScRsp);
    Check(destroy.Retcode == (uint)Retcode.RetFail, "unsupported weekly reward destruction fails without mutation");
    var questReply = await Request<GetQuestDataScRsp>(new HandlerGetQuestDataCsReq(), new GetQuestDataCsReq(), CmdIds.GetQuestDataScRsp);
    var serializedQuest = questReply.QuestList.Single(q => q.Id == 7300022);
    var storedQuest = quests.Data.Quests[7300022];
    Check(serializedQuest.Status == storedQuest.QuestStatus && serializedQuest.Progress == storedQuest.Progress &&
        serializedQuest.FinishTime == storedQuest.FinishTime, "login quest response must preserve status, progress and completion time");
    typeof(PlayerInstance).GetProperty(nameof(PlayerInstance.SceneInstance))!.SetValue(player, entranceScene);
    entrance.State = PropStateEnum.Hidden;
    for (var repeat = 0; repeat < 2; repeat++)
    {
        var interact = await Request<InteractPropScRsp>(new HandlerInteractPropCsReq(),
            new InteractPropCsReq { PropEntityId = 1234, InteractId2 = 2001 }, CmdIds.InteractPropScRsp);
        Check(interact.Retcode == 0 && interact.PropState == (uint)PropStateEnum.Open, "resource-defined transition stays idempotent for every quest state");
    }
    grid.ClearSession();
    for (var repeat = 0; repeat < 2; repeat++)
    {
        var tutorial = await Request<GridFightFinishTutorialScRsp>(new HandlerGridFightFinishTutorialCsReq(),
            new GridFightFinishTutorialCsReq(), CmdIds.GridFightFinishTutorialScRsp);
        Check(tutorial.Retcode == 0 && grid.Session == null, "idempotent tutorial acknowledgement");
    }
    var info = await Request<GridFightGetDataScRsp>(new HandlerGridFightGetDataCsReq(),
        new GridFightGetDataCsReq(), CmdIds.GridFightGetDataScRsp);
    Check(info.Retcode == 0 && info.SystemInfo.StaticInfo.Season == grid.Catalog.CurrentSeason,
        "activity data for every quest state");
    var start = await Request<GridFightStartGamePlayScRsp>(new HandlerGridFightStartGamePlayCsReq(),
        new GridFightStartGamePlayCsReq { Season = grid.Catalog.CurrentSeason, DivisionId = grid.Catalog.HighestDivision.ID },
        CmdIds.GridFightStartGamePlayScRsp);
    Check(start.Retcode == 0 && start.CurrentInfo != null && grid.Session != null, "legal game starts");
    foreach (var bad in new[] { new GridFightStartGamePlayCsReq { Season = uint.MaxValue, DivisionId = grid.Catalog.HighestDivision.ID },
        new GridFightStartGamePlayCsReq { Season = grid.Catalog.CurrentSeason, DivisionId = uint.MaxValue } })
    {
        var session = grid.Session;
        var reject = await Request<GridFightStartGamePlayScRsp>(new HandlerGridFightStartGamePlayCsReq(), bad,
            CmdIds.GridFightStartGamePlayScRsp);
        Check(reject.Retcode == (uint)Retcode.RetFail && ReferenceEquals(session, grid.Session), "invalid game leaves session intact");
    }
    Check(before.SequenceEqual(MemoryPackSerializer.Serialize(quests.Data.Quests)), "completion and claimed rewards unchanged");
}
foreach (var fakeDate in new[] { 20261011, 20261012, 20261018, 20261019 })
{
    player.Data.FakeTimeDate = fakeDate;
    var weekly = CycleScoreRewardGetDataScRsp.Parser.ParseFrom(new PacketCycleScoreRewardGetDataScRsp(player).Data);
    var now = ServerTimeProvider.GetServerUnixSec(player.Data);
    Check(weekly.NFEDANFBIBE.StartTime <= now && now < weekly.NFEDANFBIBE.EndTime &&
        weekly.NFEDANFBIBE.EndTime - weekly.NFEDANFBIBE.StartTime == 7 * 86400, "weekly data remains active across Sunday/Monday boundaries and fake time");
}
player.Data.FakeTimeDate = 0;
ConfigManager.Config.ServerOption.EnableQuest = false;
var disabledReply = await Request<GetQuestDataScRsp>(new HandlerGetQuestDataCsReq(), new GetQuestDataCsReq(), CmdIds.GetQuestDataScRsp);
Check(disabledReply.QuestList.Single(q => q.Id == 7300022).Equals(quests.Data.Quests[7300022].ToProto()), "disabled quests preserve claimed reward fields");
quests.Data.Quests.Remove(7300022);
disabledReply = await Request<GetQuestDataScRsp>(new HandlerGetQuestDataCsReq(), new GetQuestDataCsReq(), CmdIds.GetQuestDataScRsp);
Check(disabledReply.QuestList.Single(q => q.Id == 7300022).Status == QuestStatus.QuestFinish, "disabled missing quest stays unlocked");
ConfigManager.Config.ServerOption.EnableQuest = true;
var missingReply = await Request<GetQuestDataScRsp>(new HandlerGetQuestDataCsReq(), new GetQuestDataCsReq(), CmdIds.GetQuestDataScRsp);
Check(missingReply.QuestList.Single(q => q.Id == 7300022).Status == QuestStatus.QuestNone && !quests.Data.Quests.ContainsKey(7300022), "missing quest response does not create saved records");
foreach (var location in new[] { (10000, 10000003), (20322, 20322001) })
{
    var scene = new SceneInstance(player, GameData.MazePlaneData[location.Item1], location.Item2, 0);
    typeof(PlayerInstance).GetProperty(nameof(PlayerInstance.SceneInstance))!.SetValue(player, scene);
    var info = new SceneInfo { SceneMissionInfo = new MissionStatusBySceneInfo() };
    missions.OnLoadScene(info);
    Check(info.SceneMissionInfo.SubMissionStatusList.All(s => GameData.SubMissionData.ContainsKey((int)s.Id)), "scene projects only client-configured sub missions");
    Check(info.SceneMissionInfo.SubMissionStatusList.All(s => {
        var parent = (uint)GameData.SubMissionInfoData[(int)s.Id].MainMissionId;
        return info.SceneMissionInfo.FinishedMainMissionIdList.Contains(parent) || info.SceneMissionInfo.UnfinishedMainMissionIdList.Contains(parent);
    }), "scene sub mission has its canonical parent");
    Check(!info.SceneMissionInfo.SubMissionStatusList.Any(s => s.Id == 1802711101), "observed missing client mission is omitted");
}
var invalid = await Request<InteractPropScRsp>(new HandlerInteractPropCsReq(),
    new InteractPropCsReq { PropEntityId = uint.MaxValue }, CmdIds.InteractPropScRsp);
Check(invalid.Retcode == (uint)Retcode.RetFail && invalid.PropEntityId == 0, "invalid interaction fails explicitly");
if (args.Length == 2) await ReplayChecks.Run(args[1], database, Check);
Console.WriteLine($"PASS: {checks} GridFight checks; no production database connection.");

sealed class QuietConnection(KcpConversation conversation, IPEndPoint remote) : Connection(conversation, remote)
{
    public override void Start() { }
    public override void Stop() => IsOnline = false;
}
sealed class LoopTransport : IKcpTransport, IDisposable
{
    private readonly Channel<UdpReceiveResult> _packets = Channel.CreateUnbounded<UdpReceiveResult>();
    public KcpConversation Peer { get; set; } = null!;
    public LoopTransport() => _ = Deliver();
    private async Task Deliver()
    {
        await foreach (var packet in _packets.Reader.ReadAllAsync())
            await Peer.InputPakcetAsync(packet);
    }
    public ValueTask SendPacketAsync(Memory<byte> packet, IPEndPoint remoteEndpoint, CancellationToken cancellationToken)
        => _packets.Writer.WriteAsync(new UdpReceiveResult(packet.ToArray(), remoteEndpoint), cancellationToken);
    public void Dispose() => _packets.Writer.TryComplete();
}
