using March7thHoney.Data;
using March7thHoney.GameServer.Game.Player;
using March7thHoney.Kcp;
using March7thHoney.Proto;

namespace March7thHoney.GameServer.Server.Packet.Send.Quest;

public class PacketGetQuestDataScRsp : BasePacket
{
    public PacketGetQuestDataScRsp(PlayerInstance player) : base(CmdIds.GetQuestDataScRsp)
    {
        var proto = new GetQuestDataScRsp();
        foreach (var quest in GameData.QuestDataData.Values)
        {
            var data = player.QuestManager?.Data.Quests.GetValueOrDefault(quest.QuestID)?.ToProto()
                ?? new Proto.Quest { Id = (uint)quest.QuestID };
            data.Status = player.QuestManager?.GetQuestStatus(quest.QuestID) ?? QuestStatus.QuestNone;
            proto.QuestList.Add(data);
        }
        SetData(proto);
    }
}
