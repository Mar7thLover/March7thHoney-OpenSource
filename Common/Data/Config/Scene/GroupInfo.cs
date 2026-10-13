using MemoryPack;
using March7thHoney.Data.Config.Task;
using March7thHoney.Database.Quests;
using March7thHoney.Enums;
using March7thHoney.Enums.Mission;
using March7thHoney.Enums.Scene;
using March7thHoney.Enums.Task;
using March7thHoney.Util;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace March7thHoney.Data.Config.Scene;

[MemoryPackable]
public partial class GroupInfo
{
    public int Id;

    [JsonConverter(typeof(StringEnumConverter))]
    public GroupLoadSideEnum LoadSide { get; set; }

    [JsonConverter(typeof(StringEnumConverter))]
    public GroupCategoryEnum Category { get; set; }

    public LevelGroupSystemUnlockCondition? SystemUnlockCondition { get; set; } = null;
    public string LevelGraph { get; set; } = "";
    public bool LoadOnInitial { get; set; }
    public string GroupName { get; set; } = "";
    public SavedValueLoadCondition SavedValueCondition { get; set; } = new();
    public AtmosphereCondition AtmosphereCondition { get; set; } = new();
    public LoadCondition LoadCondition { get; set; } = new();
    public LoadCondition UnloadCondition { get; set; } = new();
    public LoadCondition ForceUnloadCondition { get; set; } = new();

    [JsonConverter(typeof(StringEnumConverter))]
    public SaveTypeEnum SaveType { get; set; } = SaveTypeEnum.Save;

    public int OwnerMainMissionID { get; set; }
    public List<AnchorInfo> AnchorList { get; set; } = [];
    public List<MonsterInfo> MonsterList { get; set; } = [];
    public List<PropInfo> PropList { get; set; } = [];
    public List<NpcInfo> NPCList { get; set; } = [];
    public Dictionary<int, GroupPropertyConfigInfo> GroupPropertyMap { get; set; } = [];

    // Raw JSON consumed at load time by Load(); not cached (JObject isn't MemoryPack-serializable).
    [MemoryPackIgnore] public ValueSourceInfo? ValueSource { get; set; }

    [JsonIgnore] public LevelGraphConfigInfo? LevelGraphConfig { get; set; }

    [JsonIgnore] public Dictionary<string, List<int>> PropTriggerCustomString { get; set; } = [];
    [JsonIgnore] public List<string> ControlFloorSavedValue { get; set; } = [];
    [JsonIgnore] public List<int> RelatedBattleId { get; set; } = [];
    [JsonIgnore] public List<int> RelatedMissionId { get; set; } = [];

    public void Load()
    {
        foreach (var prop in PropList) prop.Load(this);

        foreach (var source in ValueSource?.Values ?? [])
            if (source["Key"]?.ToString() == "FSV")
            {
                var value = source["Value"];
                if (value != null)
                    ControlFloorSavedValue.Add(value.ToString());
            }

        foreach (var info in LevelGraphConfig?.OnInitSequece ?? [])
        foreach (var configInfo in info.TaskList)
            if (configInfo is TriggerBattle battle)
                if (battle.EventID.GetValue() > 0)
                    RelatedBattleId.Add(battle.EventID.GetValue());

        foreach (var info in LevelGraphConfig?.OnStartSequece ?? [])
        foreach (var configInfo in info.TaskList)
            if (configInfo is TriggerBattle battle)
                if (battle.EventID.GetValue() > 0)
                    RelatedBattleId.Add(battle.EventID.GetValue());

        if (LoadSide != GroupLoadSideEnum.Client) return;
        foreach (var info in AtmosphereCondition.Conditions)
            if (info.TryGetValue("SubMissionID", out var value) && value is long v)
            {
                // try cast to int
                var missionId = (int)v;
                RelatedMissionId.Add(missionId);
            }
    }
}

[MemoryPackable]
public partial class AtmosphereCondition
{
    public List<Dictionary<string, object>> Conditions { get; set; } = [];

    [JsonConverter(typeof(StringEnumConverter))]
    public OperationEnum Operation { get; set; } = OperationEnum.And;
}

[MemoryPackable]
public partial class LoadCondition
{
    public List<Condition> Conditions { get; set; } = [];

    [JsonConverter(typeof(StringEnumConverter))]
    public OperationEnum Operation { get; set; } = OperationEnum.And;

    /// <summary>
    ///     Official deferral marker on an unload condition: once it turns true the group must stay in the
    ///     scene the player is currently in and only disappear on the next level load. Ignored for load
    ///     conditions (a load is always evaluated at build time anyway).
    /// </summary>
    public bool DelayToLevelReload { get; set; }

    public bool IsTrue(MissionData mission, bool defaultResult = true)
    {
        if (Conditions.Count == 0) return defaultResult;

        // check load condition
        List<Func<bool>> conditionChecks = [];

        foreach (var condition in Conditions)
            if (condition.Type == LevelGroupMissionTypeEnum.MainMission)
            {
                var status = mission.GetMainMissionStatus(condition.ID);
                if (!mission.MissionEnabled) status = MissionPhaseEnum.Finish;

                condition.Phase = condition.Phase == MissionPhaseEnum.Cancel
                    ? MissionPhaseEnum.Finish
                    : condition.Phase;

                conditionChecks.Add(CheckFunc);
                continue;

                bool CheckFunc()
                {
                    return status == condition.Phase;
                }
            }
            else
            {
                // sub mission
                var status = mission.GetSubMissionStatus(condition.ID);
                if (!mission.MissionEnabled) status = MissionPhaseEnum.Finish;
                condition.Phase = condition.Phase == MissionPhaseEnum.Cancel
                    ? MissionPhaseEnum.Finish
                    : condition.Phase;

                conditionChecks.Add(CheckFunc);
                continue;

                bool CheckFunc()
                {
                    return status == condition.Phase;
                }
            }

        return Operation switch
        {
            OperationEnum.And => UtilTools.CheckAnd(conditionChecks, defaultResult),
            OperationEnum.Or => UtilTools.CheckOr(conditionChecks, defaultResult),
            OperationEnum.Not => !UtilTools.CheckOr(conditionChecks, defaultResult),
            _ => defaultResult
        };
    }
}

[MemoryPackable]
public partial class SavedValueLoadCondition
{
    public List<SavedValueCondition> Conditions { get; set; } = [];

    [JsonConverter(typeof(StringEnumConverter))]
    public OperationEnum Operation { get; set; } = OperationEnum.And;

    public bool IsTrue(Dictionary<string, int> savedValue, bool defaultResult = true)
    {
        if (Conditions.Count == 0) return defaultResult;

        // check load condition
        List<Func<bool>> conditionChecks = [];
        foreach (var condition in Conditions)
        {
            // saved value
            var status = savedValue.GetValueOrDefault(condition.SavedValueName, 0);
            if (condition.Operation == CompareTypeEnum.Unknow) continue;

            conditionChecks.Add(CheckFunc);
            continue;

            bool CheckFunc()
            {
                return UtilTools.CompareNumberByOperationEnum(status, condition.Value, condition.Operation);
            }
        }

        return Operation switch
        {
            OperationEnum.And => UtilTools.CheckAnd(conditionChecks, defaultResult),
            OperationEnum.Or => UtilTools.CheckOr(conditionChecks, defaultResult),
            OperationEnum.Not => !UtilTools.CheckOr(conditionChecks, defaultResult),
            _ => defaultResult
        };
    }
}

[MemoryPackable]
public partial class Condition
{
    [JsonConverter(typeof(StringEnumConverter))]
    public LevelGroupMissionTypeEnum Type { get; set; } = LevelGroupMissionTypeEnum.MainMission;

    public int ID { get; set; }

    [JsonConverter(typeof(StringEnumConverter))]
    public MissionPhaseEnum Phase { get; set; } = MissionPhaseEnum.Accept;
}

[MemoryPackable]
public partial class SavedValueCondition
{
    public string SavedValueName { get; set; } = "";

    public int Value { get; set; }

    [JsonConverter(typeof(StringEnumConverter))]
    public CompareTypeEnum Operation { get; set; } = CompareTypeEnum.Unknow;
}

[MemoryPackable]
public partial class LevelGroupSystemUnlockCondition
{
    public List<int> Conditions { get; set; } = [];

    [JsonConverter(typeof(StringEnumConverter))]
    public OperationEnum Operation { get; set; }
}

[MemoryPackable]
public partial class GroupPropertyConfigInfo
{
    public int ID { get; set; }
    public string Name { get; set; } = "";

    [JsonConverter(typeof(StringEnumConverter))]
    public GroupPropertySideEnum Side { get; set; }

    public short DefaultValue { get; set; }
    public short MaxValue { get; set; }
    public short MinValue { get; set; }
}
