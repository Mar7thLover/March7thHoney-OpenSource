using March7thHoney.GameServer.Game.Player;
using March7thHoney.Kcp;
using March7thHoney.Proto;

namespace March7thHoney.GameServer.Server.Packet.Recv.GridFight;

[Opcode(CmdIds.GridFightGetFormationCsReq)]
public sealed class HandlerGridFightGetFormationCsReq : PlayerHandler
{
    protected override Task OnHandle(Connection connection, PlayerInstance player)
    {
        return connection.SendPacket(GridFightPacketFactory.CreatePacket(
            CmdIds.GetGameFormationScRsp,
            new GetGameFormationScRsp()));
    }
}

[Opcode(CmdIds.GridFightFinishTutorialCsReq)]
public sealed class HandlerGridFightFinishTutorialCsReq : PlayerHandler
{
    protected override Task OnHandle(Connection connection, PlayerInstance player)
    {
        return connection.SendPacket(GridFightPacketFactory.CreatePacket(
            CmdIds.GridFightFinishTutorialScRsp,
            new GridFightFinishTutorialScRsp()));
    }
}
