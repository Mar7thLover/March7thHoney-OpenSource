using March7thHoney.Data;
using March7thHoney.Data.Config;
using March7thHoney.Data.Excel;
using March7thHoney.Database;
using March7thHoney.Database.Inventory;
using March7thHoney.Enums.Item;
using March7thHoney.Enums.Mission;
using March7thHoney.GameServer.Game.Battle;
using March7thHoney.GameServer.Game.Mission.FinishAction;
using March7thHoney.GameServer.Game.Mission.FinishType;
using March7thHoney.GameServer.Game.Player;
using March7thHoney.GameServer.Game.Scene.Entity;
using March7thHoney.GameServer.Server.Packet.Send.HeartDial;
using March7thHoney.GameServer.Server.Packet.Send.Mission;
using March7thHoney.GameServer.Server.Packet.Send.PlayerSync;
using March7thHoney.GameServer.Server.Packet.Send.Scene;
using March7thHoney.Kcp;
using March7thHoney.Proto;
using March7thHoney.Util;
using MissionData = March7thHoney.Database.Quests.MissionData;

namespace March7thHoney.GameServer.Game.Mission;

public class MissionManager(PlayerInstance player) : BasePlayerManager<MissionData>(player)
{
    #region Initializer & Properties

    public static readonly Dictionary<FinishActionTypeEnum, MissionFinishActionHandler> ActionHandlers = [];
    public static readonly Dictionary<MissionFinishTypeEnum, MissionFinishTypeHandler> FinishTypeHandlers = [];

    public readonly List<int> SkipSubMissionList = []; // bug

    /// <summary>
    ///     Whether the mission system runs for THIS player. Normally the server config, but /mission start
    ///     flips it on per player so the story is playable on a server that ships with missions off.
    /// </summary>
    public bool Enabled => Data.MissionEnabled;

    #endregion

    #region Mission Actions

    public List<int> GetAllFinishedMissionIds()
    {
        return Data.FinishedSubMissionIds
            .Concat(Data.FinishedMainMissionIds)
            .Where(x => x > 0)
            .Distinct()
            .OrderBy(x => x)
            .ToList();
    }

    public async ValueTask<List<MissionSync?>> AcceptMainMission(int missionId, bool sendPacket = true)
    {
        if (!Enabled) return [];
        if (Data.GetMainMissionStatus(missionId) != MissionPhaseEnum.None) return []; // already accepted
        // Get entry sub mission
        GameData.MainMissionData.TryGetValue(missionId, out var mission);
        if (mission == null) return [];

        Data.SetMainMissionStatus(missionId, MissionPhaseEnum.Accept);

        // A "Gap" row with no MissionInfo wiring is a display-only transition entry (e.g. the
        // Trailblaze chain 1054400/1054402/1054403 and the beat missions 1054407/1054408/1054409):
        // it has no start subs and no finish gate, so FinishSubMission can never close it and every
        // successor whose TakeParam is MultiSequence on it stalls forever. Auto-finish it on accept
        // so the chain cascades the way the official server advances it — FinishMainMission re-runs
        // AcceptMainMissionByCondition, which picks up the next stage in the same pass.
        // Deliberately restricted to Gap: a Main-type mission with a missing MissionInfo is playable
        // content (or a resource-dump gap) and must never be silently skipped.
        if (mission.IsDisplayOnlyGap)
        {
            await FinishMainMission(missionId);
            return [];
        }

        var list = new List<MissionSync?>();
        foreach (var i in mission.MissionInfo?.StartSubMissionList ?? [])
            list.Add(await AcceptSubMission(i, sendPacket));
        if (missionId == 4030001 || missionId == 4030002)
        {
            // skip  not implemented
            foreach (var x in mission.MissionInfo?.SubMissionList ?? [])
                await AcceptSubMission(x.ID);
            foreach (var x in mission.MissionInfo?.SubMissionList ?? [])
                await FinishSubMission(x.ID);
        }

        if (missionId == 1000400)
        {
            await Player.AddAvatar(1003);
            await Player.LineupManager!.AddAvatarToCurTeam(1003);
        }

        // message
        foreach (var sectionConfigExcel in GameData.MessageSectionConfigData.Values.Where(x =>
                     x.MainMissionLink == missionId))
            await Player.MessageManager!.AddMessageSection(sectionConfigExcel.ID);

        foreach (var info in mission.MissionInfo!.SubMissionList.Where(x =>
                     x.FinishType is MissionFinishTypeEnum.MessagePerformSectionFinish
                         or MissionFinishTypeEnum.MessageSectionFinish))
            await Player.MessageManager!.AddMessageSection(info.ParamInt1);

        return list;
    }

    public async ValueTask<MissionSync> AcceptMainMissionByCondition(bool sendPacket = true)
    {
        var sync = new MissionSync();
        foreach (var nextMission in GameData.MainMissionData.Values)
        {
            if (!nextMission.IsEqual(Data)) continue;
            if (Data.GetMainMissionStatus(nextMission.MainMissionID) != MissionPhaseEnum.None)
                continue; // already accepted
            var res = await AcceptMainMission(nextMission.MainMissionID, sendPacket);
            foreach (var subMission in res)
                if (subMission != null)
                    sync.MissionList.AddRange(subMission.MissionList);
        }

        return sync;
    }

    public async ValueTask<List<MissionSync?>> ReAcceptMainMission(int missionId, bool sendPacket = true)
    {
        if (!Enabled) return [];

        GameData.MainMissionData.TryGetValue(missionId, out var mission);
        if (mission == null) return [];
        MissionSync sync = new();

        foreach (var subMission in mission.SubMissionIds)
            if (Data.GetSubMissionStatus(subMission) == MissionPhaseEnum.Finish ||
                Data.GetSubMissionStatus(subMission) == MissionPhaseEnum.Accept)
                sync.MissionList.Add(new Proto.Mission
                {
                    Id = (uint)subMission,
                    Status = MissionStatus.MissionNone
                });

        foreach (var subMission in
                 mission.SubMissionIds) Data.SetSubMissionStatus(subMission, MissionPhaseEnum.None); // reset

        Data.SetMainMissionStatus(missionId, MissionPhaseEnum.None); // reset
        await Player.SendPacket(new PacketPlayerSyncScNotify(sync));

        return await AcceptMainMission(missionId, sendPacket);
    }

    public async ValueTask RemoveMainMission(int missionId)
    {
        if (!Enabled) return;
        Data.SetMainMissionStatus(missionId, MissionPhaseEnum.None);

        GameData.MainMissionData.TryGetValue(missionId, out var mission);
        if (mission == null) return;

        MissionSync sync = new();

        foreach (var subMission in mission.SubMissionIds)
        {
            Data.SetSubMissionStatus(subMission, MissionPhaseEnum.None);
            await SetMissionProgress(subMission, 0, false);
            sync.MissionList.Add(new Proto.Mission
            {
                Id = (uint)subMission,
                Status = MissionStatus.MissionNone
            });
        }

        await Player.SendPacket(new PacketPlayerSyncScNotify(sync));
    }

    public async ValueTask AcceptSubMission(int missionId)
    {
        if (!Enabled) return;
        await AcceptSubMission(missionId, true);
    }

    public async ValueTask<MissionSync?> AcceptSubMission(int missionId, bool sendPacket,
        bool doFinishTypeAction = true)
    {
        if (!Enabled) return null;
        GameData.SubMissionInfoData.TryGetValue(missionId, out var mission);
        if (mission == null) return null;
        if (Data.GetSubMissionStatus(missionId) != MissionPhaseEnum.None) return null; // already accepted

        Data.SetSubMissionStatus(missionId, MissionPhaseEnum.Accept);

        var sync = new MissionSync();
        sync.MissionList.Add(new Proto.Mission
        {
            Id = (uint)missionId,
            Status = MissionStatus.MissionDoing
        });

        if (sendPacket)
        {
            await Player.SendPacket(new PacketPlayerSyncScNotify(sync));
            if (Player.SceneInstance != null)
            {
                await Player.SceneInstance.SyncGroupInfo();
                await EnsureRunningKillMonsterTargetsVisible();
                await SyncStartedKillMonsterSceneSnapshot(sync);
            }
        }

        if (mission.SubMissionInfo != null)
            try
            {
                FinishTypeHandlers.TryGetValue(mission.SubMissionInfo.FinishType, out var handler);
                if (doFinishTypeAction)
                    if (handler != null)
                        await handler.HandleMissionFinishType(Player, mission.SubMissionInfo, null);
            }
            catch
            {
            }

        if (SkipSubMissionList.Contains(missionId)) await FinishSubMission(missionId);

        if (sendPacket && mission.SubMissionInfo?.LevelFloorID == Player.SceneInstance?.FloorId)
            if (mission.SubMissionInfo?.GroupIDList != null)
                foreach (var group in mission.SubMissionInfo.GroupIDList)
                    await Player.SceneInstance!.EntityLoader!.LoadGroup(group);

        // TODO: Mission Task
        Player.TaskManager?.MissionTaskTrigger.TriggerMissionTask(missionId);

        return sync;
    }

    public async ValueTask FinishMainMission(int missionId)
    {
        if (!Enabled) return;
        if (!GameData.MainMissionData.TryGetValue(missionId, out var mainMission)) return;
        if (Data.GetMainMissionStatus(missionId) != MissionPhaseEnum.Accept) return;
        Data.SetMainMissionStatus(missionId, MissionPhaseEnum.Finish);
        var sync = new MissionSync();
        sync.FinishedMainMissionIdList.Add((uint)missionId);
        // get next main mission
        foreach (var mission in mainMission.SubMissionIds)
            if (GetSubMissionStatus(mission) != MissionPhaseEnum.Finish)
                if (Data.GetSubMissionStatus(mission) != MissionPhaseEnum.Finish)
                {
                    Data.SetSubMissionStatus(mission, MissionPhaseEnum.Finish);
                    sync.MissionList.Add(new Proto.Mission
                    {
                        Id = (uint)mission,
                        Status = MissionStatus.MissionFinish
                    });
                }

        var mainSync = await AcceptMainMissionByCondition(false);
        sync.MissionList.AddRange(mainSync.MissionList);

        await Player.SendPacket(new PacketPlayerSyncScNotify(sync));
        if (Player.SceneInstance != null)
        {
            await Player.SceneInstance.SyncGroupInfo();
            await LoadRunningMissionGroupsForCurrentFloor();
            await EnsureRunningKillMonsterTargetsVisible();
            await SyncStartedKillMonsterSceneSnapshot(sync);
        }

        await Player.SendPacket(new PacketStartFinishMainMissionScNotify(missionId));
        await Player.SendPacket(new PacketFinishedMissionScNotify(missionId));
        await HandleMissionReward(missionId);
        await HandleFinishType(MissionFinishTypeEnum.FinishMission);

        await Player.RaidManager!.CheckIfLeaveRaid();

    }

    public async ValueTask FinishSubMission(int missionId)
    {
        if (!Enabled) return;
        GameData.SubMissionInfoData.TryGetValue(missionId, out var subMission);
        if (subMission == null) return;
        var mainMissionId = subMission.MainMissionId;
        if (Data.GetSubMissionStatus(missionId) != MissionPhaseEnum.Accept) return; // not accepted
        GameData.MainMissionData.TryGetValue(mainMissionId, out var mainMission); // get main mission
        if (mainMission == null) return;
        Data.SetSubMissionStatus(missionId, MissionPhaseEnum.Finish); // set finish

        await SetMissionProgress(missionId, subMission.SubMissionInfo?.Progress ?? 1);

        var sync = new MissionSync();
        sync.MissionList.Add(new Proto.Mission
        {
            Id = (uint)missionId,
            Status = MissionStatus.MissionFinish,
            Progress = (uint)(subMission.SubMissionInfo?.Progress ?? 1)
        });

        await TriggerNextSubMissions(mainMission, sync);

        await Player.SendPacket(new PacketPlayerSyncScNotify(sync));
        if (Player.SceneInstance != null)
        {
            await Player.SceneInstance.SyncGroupInfo();
            await LoadRunningMissionGroupsForCurrentFloor();
            await EnsureRunningKillMonsterTargetsVisible();
            await SyncStartedKillMonsterSceneSnapshot(sync);
        }

        await Player.SendPacket(new PacketStartFinishSubMissionScNotify(missionId));

        if (mainMission.MissionInfo != null)
            await HandleFinishAction(mainMission.MissionInfo, missionId);

        // Get if it should finish main mission
        // get current main mission
        var shouldFinish = true;
        mainMission.MissionInfo?.FinishSubMissionList.ForEach(id =>
        {
            if (GetSubMissionStatus(id) != MissionPhaseEnum.Finish) shouldFinish = false;
        });

        foreach (var nextMission in GetRunningSubMissionList())
        {
            FinishTypeHandlers.TryGetValue(nextMission.FinishType, out var handler);
            if (handler != null) await handler.HandleMissionFinishType(Player, nextMission, null);
        }

        if (shouldFinish) await FinishMainMission(mainMissionId);

        await HandleSubMissionNarrativeHooks(missionId);

        // handle reward
        await HandleSubMissionReward(missionId);
        //Player.StoryLineManager!.CheckIfEnterStoryLine();
        //Player.StoryLineManager!.CheckIfFinishStoryLine();

    }

    // Accept the sub-missions unlocked by finishing one, honoring AnySequence (OR) / MultiSequence (AND) gates.
    private async ValueTask TriggerNextSubMissions(MainMissionExcel mainMission, MissionSync sync)
    {
        foreach (var nextMission in mainMission.MissionInfo?.SubMissionList ?? [])
        {
            if (nextMission.TakeType != SubMissionTakeTypeEnum.AnySequence &&
                nextMission.TakeType != SubMissionTakeTypeEnum.MultiSequence) continue;
            var canAccept = nextMission.TakeType == SubMissionTakeTypeEnum.MultiSequence; // mean and operation
            foreach (var id in nextMission.TakeParamIntList ?? [])
                if (GetSubMissionStatus(id) != MissionPhaseEnum.Finish &&
                    nextMission.TakeType == SubMissionTakeTypeEnum.MultiSequence)
                {
                    canAccept = false;
                    break;
                }
                else if (GetSubMissionStatus(id) == MissionPhaseEnum.Finish &&
                         nextMission.TakeType == SubMissionTakeTypeEnum.AnySequence) // or operation
                {
                    canAccept = true;
                    break;
                }

            if (canAccept)
            {
                var s = await AcceptSubMission(nextMission.ID, false, false);
                if (s != null)
                    sync.MissionList.Add(new Proto.Mission
                    {
                        Id = (uint)nextMission.ID,
                        Status = MissionStatus.MissionDoing
                    });
            }
        }
    }

    // Hardcoded story-mission side effects (Heliobus lineup swap / HeartDial script unlocks).
    private async ValueTask HandleSubMissionNarrativeHooks(int missionId)
    {
        if (missionId == 101140201)
        {
            // Player.ChangeAvatarPathType(8001, Enums.Avatar.MultiPathAvatarTypeEnum.Knight);
            var list = Player.LineupManager!.GetCurLineup()!.BaseAvatars!
                .Select(x => x.SpecialAvatarId > 0 ? x.SpecialAvatarId : x.BaseAvatarId).ToList();
            list[list.IndexOf(8001)] = Player.Data.CurrentGender == Gender.Man ? 1008003 : 1008004;
            Player.LineupManager!.SetExtraLineup(ExtraLineupType.LineupHeliobus, list);
        }

        if (missionId == 103040103)
            await Player.SendPacket(new PacketHeartDialScriptChangeScNotify(HeartDialUnlockStatus.UnlockSingle));

        if (missionId == 103040104)
            await Player.SendPacket(new PacketHeartDialScriptChangeScNotify(HeartDialUnlockStatus.UnlockAll));
    }

    public async ValueTask HandleFinishAction(MissionInfo info, int subMissionId)
    {
        var subMission = info.SubMissionList.Find(x => x.ID == subMissionId);
        if (subMission == null) return;

        foreach (var action in subMission.FinishActionList ?? []) await HandleFinishAction(action);
    }

    public async ValueTask HandleFinishAction(FinishActionInfo actionInfo)
    {
        ActionHandlers.TryGetValue(actionInfo.FinishActionType, out var handler);
        if (handler != null)
            await handler.OnHandle(actionInfo.FinishActionPara, actionInfo.FinishActionParaString, Player);
    }

    public async ValueTask HandleMissionReward(int mainMissionId)
    {
        GameData.MainMissionData.TryGetValue(mainMissionId, out var mainMission);
        if (mainMission == null) return;
        GameData.RewardDataData.TryGetValue(mainMission.RewardID, out var reward);
        var itemList = new List<ItemData>();

        foreach (var item in reward?.GetItems() ?? [])
        {
            GameData.ItemConfigData.TryGetValue(item.Item1, out var itemExcel);
            var res = await Player.InventoryManager!.AddItem(item.Item1, item.Item2,
                itemExcel?.ItemMainType == ItemMainTypeEnum.AvatarCard); // notify if avatar card
            if (res != null) itemList.Add(res);
        }

        var hCoin = await Player.InventoryManager!.AddItem(1, reward?.Hcoin ?? 0, false);
        if (hCoin != null) itemList.Add(hCoin);

        foreach (var i in mainMission.SubRewardList)
        {
            GameData.RewardDataData.TryGetValue(i, out var rewardDataExcel);
            var hCoin2 = await Player.InventoryManager!.AddItem(1, rewardDataExcel?.Hcoin ?? 0, false); // hcoin
            if (hCoin2 != null) itemList.Add(hCoin2);
            foreach (var item in rewardDataExcel?.GetItems() ?? []) // items
            {
                GameData.ItemConfigData.TryGetValue(item.Item1, out var itemExcel);
                var res = await Player.InventoryManager!.AddItem(item.Item1, item.Item2,
                    itemExcel?.ItemMainType == ItemMainTypeEnum.AvatarCard); // notify if avatar card
                if (res != null) itemList.Add(res);
            }
        }


        if (itemList.Count > 0)
            await Player.SendPacket(new PacketMissionRewardScNotify(mainMissionId, 0, itemList));
    }

    public async ValueTask HandleSubMissionReward(int subMissionId)
    {
        GameData.SubMissionInfoData.TryGetValue(subMissionId, out var subMission);
        if (subMission == null) return;
        GameData.RewardDataData.TryGetValue(subMission.SubMissionInfo?.SubRewardID ?? 0, out var reward);
        var itemList = new List<ItemData>();

        foreach (var item in reward?.GetItems() ?? [])
        {
            GameData.ItemConfigData.TryGetValue(item.Item1, out var itemExcel);
            var res = await Player.InventoryManager!.AddItem(item.Item1, item.Item2,
                itemExcel?.ItemMainType == ItemMainTypeEnum.AvatarCard); // notify if avatar card
            if (res != null) itemList.Add(res);
        }

        await Player.SendPacket(new PacketSubMissionRewardScNotify(subMissionId, itemList));
    }

    public async ValueTask HandleFinishType(MissionFinishTypeEnum finishType, object? arg = null, bool pushQuest = true)
    {
        FinishTypeHandlers.TryGetValue(finishType, out var handler);
        foreach (var mission in GetRunningSubMissionList())
            if (mission.FinishType == finishType)
                if (handler != null)
                    await handler.HandleMissionFinishType(Player, mission, arg);

        foreach (var quest in Player.QuestManager?.GetRunningQuest() ?? [])
        {
            var excel = GameData.QuestDataData.GetValueOrDefault(quest.QuestId);
            if (excel == null) continue;
            var finishWay = GameData.FinishWayData.GetValueOrDefault(excel.FinishWayID);
            if (finishWay == null) continue;
            if (finishWay.FinishType == finishType)
                if (handler != null)
                    await handler.HandleQuestFinishType(Player, excel, finishWay, arg);
        }

        if (pushQuest)
            await Player.QuestManager!.SyncQuest();
    }

    public async ValueTask HandleAllFinishType(object? arg = null)
    {
        foreach (var handler in FinishTypeHandlers) await HandleFinishType(handler.Key, arg, false);
        await Player.QuestManager!.SyncQuest();
    }

    /// <summary>Returns whether the talk string closed any mission or quest gate.</summary>
    public async ValueTask<bool> HandleTalkStr(string talkString)
    {
        if (!Enabled) return false;

        var handled = false;

        foreach (var mission in GetRunningSubMissionList())
            if (mission.FinishType == MissionFinishTypeEnum.Talk)
                if (mission.ParamStr1 == talkString)
                {
                    await FinishSubMission(mission.ID);
                    handled = true;
                }

        foreach (var quest in Player.QuestManager?.GetRunningQuest() ?? [])
        {
            var excel = GameData.QuestDataData.GetValueOrDefault(quest.QuestId);
            if (excel == null) continue;
            var finishWay = GameData.FinishWayData.GetValueOrDefault(excel.FinishWayID);
            if (finishWay == null) continue;
            if (finishWay.FinishType == MissionFinishTypeEnum.Talk)
                if (finishWay.ParamStr1 == talkString)
                {
                    await Player.QuestManager!.FinishQuest(quest.QuestId);
                    handled = true;
                }
        }

        return handled;
    }

    /// <summary>
    ///     Fallback for a degenerate FinishTalkMissionCsReq — a talk report whose string matches no
    ///     configured ParamStr1 gate and that carries sub_mission_id 0. The client emits that shape when
    ///     its local client-side ("type C") performance for the step never armed, so it reports a plain
    ///     NPC dialogue (talk_str "0") instead of the mission string, and the story chain stalls.
    /// </summary>
    /// <remarks>
    ///     The candidate set is deliberately narrow: only a FINISH GATE of the currently tracked main,
    ///     with FinishType Talk, on the floor the player is standing on, and currently running. That is
    ///     the single step whose completion unlocks the next main, so a generic dialogue can never
    ///     advance a mid-chain objective. Zero or several candidates are a no-op, so an ordinary floor
    ///     with multiple talk NPCs can never be mis-advanced.
    /// </remarks>
    public async ValueTask<bool> FinishDegenerateTalk()
    {
        if (!Enabled) return false;

        var floorId = Player.SceneInstance?.FloorId ?? 0;
        if (floorId == 0) return false;

        var trackedMainId = Data.TrackingMainMissionId;
        if (trackedMainId <= 0) return false;
        if (!GameData.MainMissionData.TryGetValue(trackedMainId, out var mainMission)) return false;

        var candidates = new List<int>();
        foreach (var subId in mainMission.MissionInfo.FinishSubMissionList)
        {
            if (GetSubMissionStatus(subId) != MissionPhaseEnum.Accept) continue;
            var info = GetSubMissionInfo(subId);
            if (info == null) continue;
            if (info.FinishType != MissionFinishTypeEnum.Talk) continue;
            if (info.LevelFloorID != floorId) continue;
            candidates.Add(subId);
        }

        if (candidates.Count != 1) return false;

        await FinishSubMission(candidates[0]);
        return true;
    }

    public async ValueTask HandleCustomValue(List<MissionCustomValue> values, int missionId)
    {
        if (!Enabled) return;

        GameData.SubMissionInfoData.TryGetValue(missionId, out var subMission);
        if (subMission == null) return;
        var mainMissionId = subMission.MainMissionId;
        GameData.MainMissionData.TryGetValue(mainMissionId, out var mainMission);
        if (mainMission == null) return;

        foreach (var mission in mainMission.MissionInfo?.SubMissionList ?? [])
            if (mission.TakeType == SubMissionTakeTypeEnum.CustomValue)
            {
                var accept = false;
                List<List<int>> list = [mission.TakeParamIntList ?? []];
                if (mission.TakeParamIntList?.Count > 5)
                {
                    // every 3 as group
                    var group = mission.TakeParamIntList.Count / 3;
                    list = [];
                    for (var i = 0; i < group; i++)
                    {
                        var customValue = mission.TakeParamIntList.GetRange(i * 3, 3);
                        list.Add(customValue);
                    }
                }

                foreach (var customValues in list)
                {
                    var thisAccept = true;
                    var matched = 0;

                    // The groups are alternatives, so each one restarts at request index 0, and the
                    // index advances per config slot — not only on a match. Sharing one cursor across
                    // groups made the second group of a >5-entry list read request indices 3-5.
                    for (var index = 0; index < customValues.Count; index++)
                    {
                        var customValue = customValues[index];
                        if (customValue == 0 && index == 0) continue; // skip 0
                        var valueInst = values.Find(x => x.Index == index);
                        if (valueInst == null) continue; // a missing request entry does not fail the group
                        if (valueInst.CustomValue != customValue)
                        {
                            thisAccept = false;
                            break;
                        }

                        matched++;
                    }

                    // A group that matched nothing must not accept: `values` is raw client input, so a
                    // request carrying a single unrelated index would otherwise leave thisAccept true
                    // and open every CustomValue-gated sub of the client-chosen main mission.
                    if (thisAccept && matched > 0) accept = true; // accept if any group is satisfied
                }

                if (accept) await AcceptSubMission(mission.ID);
            }
    }

    public async ValueTask AddMissionProgress(int missionId, int progress)
    {
        if (!Enabled) return;

        Data.SubMissionProgressDict.TryGetValue(missionId, out var currentProgress);
        Data.SubMissionProgressDict[missionId] = currentProgress + progress;
        GameData.SubMissionInfoData.TryGetValue(missionId, out var subMission);
        if (subMission == null) return;

        if (currentProgress + progress >= (subMission.SubMissionInfo?.Progress ?? 1)) return;

        var sync = new MissionSync();
        sync.MissionList.Add(new Proto.Mission
        {
            Id = (uint)missionId,
            Progress = (uint)(currentProgress + progress)
        });

        await Player.SendPacket(new PacketPlayerSyncScNotify(sync));
    }

    public async ValueTask SetMissionProgress(int missionId, int progress, bool sendPacket = true)
    {
        if (!Enabled) return;

        Data.SubMissionProgressDict[missionId] = progress;
        GameData.SubMissionInfoData.TryGetValue(missionId, out var subMission);
        if (subMission == null) return;

        if (progress >= (subMission.SubMissionInfo?.Progress ?? 1)) return;

        var sync = new MissionSync();
        sync.MissionList.Add(new Proto.Mission
        {
            Id = (uint)missionId,
            Progress = (uint)progress
        });

        if (sendPacket)
            await Player.SendPacket(new PacketPlayerSyncScNotify(sync));
    }

    #endregion

    #region Mission Status

    public MissionPhaseEnum GetMainMissionStatus(int missionId)
    {
        if (!Enabled) return MissionPhaseEnum.Finish;

        return Data.GetMainMissionStatus(missionId);
    }

    public MissionPhaseEnum GetSubMissionStatus(int missionId)
    {
        if (!Enabled) return MissionPhaseEnum.Finish;

        return Data.GetSubMissionStatus(missionId);
    }

    public SubMissionInfo? GetSubMissionInfo(int missionId)
    {
        GameData.SubMissionInfoData.TryGetValue(missionId, out var subMission);
        if (subMission == null) return null;
        return subMission.SubMissionInfo;
    }

    public List<int> GetRunningSubMissionIdList()
    {
        if (!Enabled) return [];

        var list = new List<int>();
        list.AddRange(Data.RunningSubMissionIds);
        return list;
    }

    public List<SubMissionInfo> GetRunningSubMissionList()
    {
        if (!Enabled) return [];

        var list = new List<SubMissionInfo>();
        var ids = new List<int>();
        ids.AddRange(Data.RunningSubMissionIds);
        foreach (var id in ids)
        {
            GameData.SubMissionInfoData.TryGetValue(id, out var mission);
            if (mission != null && mission.SubMissionInfo != null) list.Add(mission.SubMissionInfo);
        }

        return list;
    }

    public int GetMissionProgress(int missionId)
    {
        GameData.SubMissionInfoData.TryGetValue(missionId, out var subMission);
        if (!Enabled) return subMission?.SubMissionInfo?.Progress ?? 0;

        Data.SubMissionProgressDict.TryGetValue(missionId, out var progress);
        return progress;
    }

    /// <summary>
    ///     Resolves which main mission the navigator should track.
    ///     <paramref name="requested" /> is the client's explicit target; anything &lt;= 1 is the
    ///     "unset / auto-select" sentinel the client sends on login and right after a main completes.
    /// </summary>
    /// <remarks>
    ///     Order: explicit request → keep the current track while it is still running → follow the
    ///     just-finished track's NextTrackMainMission → lowest running Main-type mission.
    ///     The last two steps matter after a story beat closes: without them the auto-track lands on
    ///     a hidden Branch/Gap transition (e.g. 1000111, whose only sub has no finishable graph), the
    ///     client renders an empty objective list and the player has nothing to follow.
    /// </remarks>
    public int ResolveTrackingMainMissionId(int requested)
    {
        if (requested > 1) return requested;

        var data = Data;
        var prev = data.TrackingMainMissionId;
        if (prev > 0 && data.RunningMainMissionIds.Contains(prev)) return prev;

        if (prev > 0 &&
            GameData.MainMissionData.TryGetValue(prev, out var prevExcel) &&
            prevExcel.NextTrackMainMission > 0 &&
            data.RunningMainMissionIds.Contains(prevExcel.NextTrackMainMission))
            return prevExcel.NextTrackMainMission;

        var running = data.RunningMainMissionIds.Where(x => x > 0).OrderBy(x => x).ToList();
        foreach (var id in running)
        {
            // Type is Unknown when the resource cache predates the column — treat that as "no
            // information" and keep the old first-running-mission behaviour instead of skipping.
            if (!GameData.MainMissionData.TryGetValue(id, out var excel)) continue;
            if (excel.Type is not (MainMissionTypeEnum.Unknown or MainMissionTypeEnum.Main)) continue;
            return id;
        }

        return running.FirstOrDefault();
    }

    public int GetTrackMissionAnchorSubMissionId(int mainMissionId)
    {
        if (!GameData.MainMissionData.TryGetValue(mainMissionId, out var mainMission)) return 0;

        var runningSubMissions = GetRunningSubMissionIdList().ToHashSet();
        foreach (var subMission in mainMission.MissionInfo.SubMissionList)
        {
            if (!runningSubMissions.Contains(subMission.ID)) continue;
            for (var i = (subMission.TakeParamIntList?.Count ?? 0) - 1; i >= 0; i--)
            {
                var prerequisite = subMission.TakeParamIntList![i];
                if (GetSubMissionStatus(prerequisite) == MissionPhaseEnum.Finish)
                    return prerequisite;
            }
        }

        return mainMission.MissionInfo.SubMissionList
            .Where(x => GetSubMissionStatus(x.ID) == MissionPhaseEnum.Finish)
            .Select(x => x.ID)
            .LastOrDefault();
    }

    /// <summary>
    ///     Floor a main mission wants the player on: the first running sub that names one, otherwise the
    ///     tracked anchor sub. When the mission's own subs carry no floor (a transition row), the search
    ///     walks back along NextTrackMainMission to the mission that leads into it. 0 = unknown.
    /// </summary>
    public int GetMissionFloorId(int mainMissionId)
    {
        var visited = new HashSet<int>();
        while (mainMissionId > 0 && visited.Add(mainMissionId))
        {
            var floor = GetOwnFloorId(mainMissionId);
            if (floor != 0) return floor;

            mainMissionId = GameData.MainMissionData.Values
                .FirstOrDefault(x => x.NextTrackMainMission == mainMissionId)?.MainMissionID ?? 0;
        }

        return 0;
    }

    private int GetOwnFloorId(int mainMissionId)
    {
        if (!GameData.MainMissionData.TryGetValue(mainMissionId, out var mainMission)) return 0;

        foreach (var subId in GetRunningSubMissionIdList())
        {
            if (!GameData.SubMissionInfoData.TryGetValue(subId, out var sub)) continue;
            if (sub.MainMissionId != mainMissionId) continue;
            if (sub.SubMissionInfo is { LevelFloorID: > 0 } running) return running.LevelFloorID;
        }

        var anchorId = GetTrackMissionAnchorSubMissionId(mainMissionId);
        if (anchorId > 0 && GetSubMissionInfo(anchorId) is { LevelFloorID: > 0 } anchor) return anchor.LevelFloorID;

        // Nothing running yet (the mission was only just accepted): fall back to the first sub that
        // declares a floor at all, so /mission goto still lands somewhere sensible.
        return mainMission.MissionInfo.SubMissionList
            .Select(x => x.LevelFloorID)
            .FirstOrDefault(x => x > 0);
    }

    /// <summary>Map entrance to use when teleporting to a floor, preferring one with a start anchor.</summary>
    public static MapEntranceExcel? FindEntranceForFloor(int floorId)
    {
        if (floorId == 0) return null;

        var candidates = GameData.MapEntranceData.Values
            .Where(x => x.FloorID == floorId)
            .OrderBy(x => x.ID)
            .ToList();

        return candidates.FirstOrDefault(x => x.StartAnchorID > 0) ?? candidates.FirstOrDefault();
    }

    public async ValueTask EnsureRunningKillMonsterTargetsVisible()
    {
        var scene = Player.SceneInstance;
        if (scene?.EntityLoader == null) return;

        foreach (var mission in GetRunningSubMissionList())
        {
            await EnsureKillMonsterTargetVisible(mission, true);
        }
    }

    private async ValueTask SyncStartedKillMonsterSceneSnapshot(MissionSync sync)
    {
        var scene = Player.SceneInstance;
        if (scene?.EntityLoader == null) return;

        var shouldSyncScene = false;
        foreach (var missionSync in sync.MissionList)
        {
            if (missionSync.Status != MissionStatus.MissionDoing) continue;
            if (!GameData.SubMissionInfoData.TryGetValue((int)missionSync.Id, out var subMission)) continue;
            if (subMission.SubMissionInfo is not { } mission) continue;
            if (scene.HasClientSceneSnapshotGroup(mission.ParamInt1)) continue;

            shouldSyncScene = await EnsureKillMonsterTargetVisible(mission, false) || shouldSyncScene;
        }

        if (!shouldSyncScene) return;

        await Player.SendPacket(new PacketEnterSceneByServerScNotify(scene));
    }

    private async ValueTask<bool> EnsureKillMonsterTargetVisible(SubMissionInfo mission, bool sendRefresh)
    {
        var scene = Player.SceneInstance;
        if (scene?.EntityLoader == null) return false;
        if (mission.FinishType != MissionFinishTypeEnum.KillMonster) return false;
        if (mission.LevelFloorID != scene.FloorId) return false;

        var targetGroupId = mission.ParamInt1;
        var targetInstId = mission.ParamInt2;
        if (targetGroupId <= 0 || targetInstId <= 0) return false;

        var groupEntities = scene.Entities.Values
            .Where(x => x.GroupId == targetGroupId)
            .ToList();

        if (groupEntities.Count == 0)
        {
            groupEntities = await scene.EntityLoader.LoadGroup(targetGroupId, false) ?? [];
        }

        if (groupEntities.Count == 0) return false;

        var target = groupEntities
            .OfType<EntityMonster>()
            .FirstOrDefault(x => x.InstId == targetInstId);
        if (target is not { IsAlive: true }) return false;

        if (sendRefresh)
            await Player.SendPacket(new PacketSceneGroupRefreshScNotify(Player, groupEntities));

        return true;
    }

    private async ValueTask LoadRunningMissionGroupsForCurrentFloor()
    {
        var scene = Player.SceneInstance;
        if (scene?.EntityLoader == null) return;

        foreach (var mission in GetRunningSubMissionList())
        {
            if (mission.LevelFloorID != scene.FloorId) continue;
            foreach (var group in mission.GroupIDList ?? [])
                await scene.EntityLoader.LoadGroup(group);
        }
    }

    #endregion

    #region Handlers

    public async ValueTask OnBattleFinish(BattleInstance instance, PVEBattleResultCsReq req)
    {
        foreach (var mission in GetRunningSubMissionIdList())
        {
            var subMission = GetSubMissionInfo(mission);
            if (subMission != null && subMission.FinishType == MissionFinishTypeEnum.StageWin &&
                req.EndStatus == BattleEndStatus.BattleEndWin) // TODO: Move to handler
                if (req.StageId.ToString().StartsWith(subMission.ParamInt1.ToString()))
                {
                    await FinishSubMission(mission);
                    instance.EventId = 0;
                }
        }

        await HandleAllFinishType(instance);
    }

    public async ValueTask OnPlayerInteractWithProp()
    {
        foreach (var id in GetRunningSubMissionIdList())
            if (GetSubMissionInfo(id)?.FinishType == MissionFinishTypeEnum.PropState)
            {
                FinishTypeHandlers.TryGetValue(MissionFinishTypeEnum.PropState, out var handler);
                if (handler != null) await handler.HandleMissionFinishType(Player, GetSubMissionInfo(id)!, null);
            }
    }

    public async ValueTask OnPlayerChangeScene()
    {
        foreach (var id in GetRunningSubMissionIdList())
        {
            var info = GetSubMissionInfo(id);
            if (info == null) continue;

            if (info.LevelFloorID == Player.SceneInstance?.FloorId)
            {
                if (info.GroupIDList == null) continue;
                foreach (var group in info.GroupIDList)
                    await Player.SceneInstance.EntityLoader!.LoadGroup(group, false);
            }
        }
    }

    public void OnLoadScene(SceneInfo info)
    {
        var targetSubIds =
            Player.SceneInstance?.FloorInfo?.Groups.Values.SelectMany(x => x.RelatedMissionId).ToList() ?? [];

        HashSet<int> mainIds = [];
        HashSet<int> subIds = [];
        HashSet<int> invalidSubIds = [];
        var currentPlane = Player.SceneInstance?.PlaneId ?? 0;
        var currentFloor = Player.SceneInstance?.FloorId ?? 0;

        void AddSubMissionStatus(SubMissionInfo subMission, int mainMissionId)
        {
            if (!GameData.SubMissionData.ContainsKey(subMission.ID) ||
                !GameData.SubMissionInfoData.TryGetValue(subMission.ID, out var canonical) ||
                canonical.MainMissionId != mainMissionId ||
                (subMission.MainMissionID != 0 && subMission.MainMissionID != mainMissionId))
            {
                invalidSubIds.Add(subMission.ID);
                return;
            }
            if (!subIds.Add(subMission.ID)) return;

            info.SceneMissionInfo.SubMissionStatusList.Add(new Proto.Mission
            {
                Id = (uint)subMission.ID,
                Status = GetSubMissionStatus(subMission.ID).ToProto(),
                Progress = (uint)GetMissionProgress(subMission.ID)
            });

            mainIds.Add(mainMissionId);
        }

        foreach (var mainMission in GameData.MainMissionData.Values)
        foreach (var subMission in mainMission.MissionInfo.SubMissionList)
            if (targetSubIds.Contains(subMission.ID))
                AddSubMissionStatus(subMission, mainMission.MainMissionID);

        if (currentFloor != 0)
            foreach (var mainMission in GameData.MainMissionData.Values)
            foreach (var subMission in mainMission.MissionInfo.SubMissionList)
            {
                if (subMission.LevelFloorID != currentFloor) continue;
                if (subMission.LevelPlaneID != 0 && currentPlane != 0 && subMission.LevelPlaneID != currentPlane)
                    continue;

                AddSubMissionStatus(subMission, mainMission.MainMissionID);
            }

        foreach (var mainId in mainIds)
            if (GetMainMissionStatus(mainId) == MissionPhaseEnum.Finish)
                info.SceneMissionInfo.FinishedMainMissionIdList.Add((uint)mainId);
            else
                info.SceneMissionInfo.UnfinishedMainMissionIdList.Add((uint)mainId);

        // Activity-bound main missions: for each ContentPackage whose entrance lives on the
        // floor the player is loading, surface its main missions as "unfinished" so the client
        // treats the activity entry (map icons, in-scene activity NPCs) as live. We keep the
        // regular Mission system untouched - this only affects scene-mission serialization.
        if (currentFloor != 0)
            foreach (var pkg in GameData.ContentPackageConfigData.Values)
            {
                if (pkg.MainMissionIDList.Count == 0) continue;
                if (!pkg.TouchesFloor(currentFloor)) continue;

                foreach (var missionId in pkg.MainMissionIDList)
                {
                    if (!GameData.MainMissionData.ContainsKey(missionId)) continue;
                    var mid = (uint)missionId;
                    if (info.SceneMissionInfo.FinishedMainMissionIdList.Contains(mid)) continue;
                    if (info.SceneMissionInfo.UnfinishedMainMissionIdList.Contains(mid)) continue;
                    info.SceneMissionInfo.UnfinishedMainMissionIdList.Add(mid);
                }
            }
        if (invalidSubIds.Count > 0)
            new March7thHoney.Util.Logger("Mission").Warn(
                $"Skipped {invalidSubIds.Count} invalid scene mission references on floor={currentFloor}.");
    }

    #endregion
}
