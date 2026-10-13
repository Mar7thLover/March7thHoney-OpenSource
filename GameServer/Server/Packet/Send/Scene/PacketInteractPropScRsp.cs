using March7thHoney.GameServer.Game.Scene.Entity;
using March7thHoney.Kcp;
using March7thHoney.Proto;

namespace March7thHoney.GameServer.Server.Packet.Send.Scene;

public class PacketInteractPropScRsp : BasePacket
{
    public PacketInteractPropScRsp(EntityProp? prop) : base(CmdIds.InteractPropScRsp)
    {
        var proto = new InteractPropScRsp { Retcode = prop == null ? (uint)Retcode.RetFail : 0 };

        if (prop != null)
        {
            proto.PropState = (uint)prop.State;
            proto.PropEntityId = (uint)prop.EntityId;
        }

        SetData(proto);
    }
}
