using March7thHoney.Kcp;
using March7thHoney.Proto;
using March7thHoney.Data.Custom;

namespace March7thHoney.GameServer.Server.Packet.Send.Gacha;

public class PacketSetGachaDecideItemScRsp : BasePacket
{
    public PacketSetGachaDecideItemScRsp(uint gachaId, uint decideItemType, List<uint> order,
        Retcode retcode = Retcode.RetSucc) : base(CmdIds.SetGachaDecideItemScRsp)
    {
        // 4.3: SetGachaDecideItemScRsp 子结构混淆名变更
        //   LECPJJAMNPF -> NBLOJLDLBEB (type OEIEJHBCOOM -> NEIMLKNMDBM)
        //   PAPOKACIPPJ(gachaId) -> MMELNCHIDNC, DGOMHDMJHEK(order) -> OJNEFBJHCCK,
        //   MBOEFLAHLEM(decideItemType) -> GFANBHAEKOK (与 SetGachaDecideItemCsReq 命名一致)
        var proto = new SetGachaDecideItemScRsp
        {
            Retcode = (uint)retcode,
            NBLOJLDLBEB = GachaProtocol.BuildSelection(gachaId, decideItemType, order)
        };

        SetData(proto);
    }
}

