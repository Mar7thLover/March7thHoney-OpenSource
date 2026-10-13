using March7thHoney.GameServer.Server.Packet.Send.Gacha;
using March7thHoney.Kcp;
using March7thHoney.Proto;

namespace March7thHoney.GameServer.Server.Packet.Recv.Gacha;

[Opcode(CmdIds.SetGachaDecideItemCsReq)]
public class HandlerSetGachaDecideItemCsReq : Handler<SetGachaDecideItemCsReq>
{
    protected override async Task OnHandle(Connection connection, PlayerInstance player, SetGachaDecideItemCsReq req)
    {
        var manager = player.GachaManager!;
        // 4.3: MBOEFLAHLEM -> GFANBHAEKOK (decide item type), DGOMHDMJHEK -> OJNEFBJHCCK (selected items)
        var retcode = await manager.SetCharacterEventNonFeaturedPoolAsync((int)req.GachaId, (int)req.GFANBHAEKOK,
            req.OJNEFBJHCCK);
        var savedOrder = retcode == Retcode.RetSucc
            ? manager.GetCharacterEventNonFeaturedPool().Select(id => (uint)id).ToList()
            : req.OJNEFBJHCCK.ToList();

        await connection.SendPacket(new PacketSetGachaDecideItemScRsp(req.GachaId, req.GFANBHAEKOK, savedOrder,
            retcode));
    }
}

