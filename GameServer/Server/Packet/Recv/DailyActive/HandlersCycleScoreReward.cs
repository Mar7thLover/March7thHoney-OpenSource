using March7thHoney.GameServer.Server.Packet.Send.DailyActive;
using March7thHoney.Kcp;
using March7thHoney.Proto;

namespace March7thHoney.GameServer.Server.Packet.Recv.DailyActive;

[Opcode(CmdIds.CycleScoreRewardGetDataCsReq)]
public sealed class HandlerCycleScoreRewardGetDataCsReq : PlayerHandler
{
    protected override async Task OnHandle(Connection connection, PlayerInstance player)
        => await connection.SendPacket(new PacketCycleScoreRewardGetDataScRsp(player));
}

[Opcode(CmdIds.CycleScoreRewardTakeRewardCsReq)]
public sealed class HandlerCycleScoreRewardTakeRewardCsReq : PlayerHandler
{
    protected override async Task OnHandle(Connection connection, PlayerInstance player)
    {
        var packet = new BasePacket(CmdIds.CycleScoreRewardTakeRewardScRsp);
        packet.SetData(new CycleScoreRewardTakeRewardScRsp { Retcode = (uint)Retcode.RetFail });
        await connection.SendPacket(packet);
    }
}

[Opcode(CmdIds.CycleScoreRewardDestroyExceedRewardCsReq)]
public sealed class HandlerCycleScoreRewardDestroyExceedRewardCsReq : PlayerHandler
{
    protected override async Task OnHandle(Connection connection, PlayerInstance player)
    {
        var packet = new BasePacket(CmdIds.CycleScoreRewardDestroyExceedRewardScRsp);
        packet.SetData(new CycleScoreRewardDestroyExceedRewardScRsp { Retcode = (uint)Retcode.RetFail });
        await connection.SendPacket(packet);
    }
}
