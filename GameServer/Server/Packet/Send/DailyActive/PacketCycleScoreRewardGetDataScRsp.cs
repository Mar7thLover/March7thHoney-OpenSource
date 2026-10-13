using March7thHoney.GameServer.Game.Player;
using March7thHoney.Kcp;
using March7thHoney.Proto;
using March7thHoney.Util;

namespace March7thHoney.GameServer.Server.Packet.Send.DailyActive;

public sealed class PacketCycleScoreRewardGetDataScRsp : BasePacket
{
    public PacketCycleScoreRewardGetDataScRsp(PlayerInstance player) : base(CmdIds.CycleScoreRewardGetDataScRsp)
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(ServerTimeProvider.GetServerUnixSec(player.Data)).ToLocalTime();
        var monday = now.Date.AddDays(-(((int)now.DayOfWeek + 6) % 7));
        var start = new DateTimeOffset(monday, now.Offset);
        // shortcut: empty calendar-week snapshot only; implement persisted scoring before enabling reward writes.
        SetData(new CycleScoreRewardGetDataScRsp
        {
            NFEDANFBIBE = new GAAMBBFPCHB
            {
                StartTime = start.ToUnixTimeSeconds(),
                EndTime = start.AddDays(7).ToUnixTimeSeconds()
            }
        });
    }
}
