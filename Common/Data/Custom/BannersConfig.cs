using MemoryPack;
using March7thHoney.Database.Gacha;
using March7thHoney.Enums;
using March7thHoney.Proto;
using Newtonsoft.Json;
using GachaInfo = March7thHoney.Proto.GachaInfo;

namespace March7thHoney.Data.Custom;

[MemoryPackable]
public partial class BannersConfig
{
    public List<BannerConfig> Banners { get; set; } = [];
}

[MemoryPackable]
public partial class BannerConfig
{
    [JsonProperty("id")] public int GachaId { get; set; }
    public long BeginTime { get; set; }
    public long EndTime { get; set; }
    public GachaTypeEnum GachaType { get; set; }
    public List<int> RateUpItems5 { get; set; } = [];
    public List<int> RateUpItems4 { get; set; } = [];
    public int GetRateUpItem5Chance { get; set; } = 6; // 0.6% chance (0.6%*1000)
    public int MaxCount { get; set; } = 90;
    public int EventChance { get; set; } = 50;

    public int DoGacha(
        List<int> characterEventNonFeaturedPool,
        List<int> standardGoldAvatars,
        List<int> purpleAvatars,
        List<int> purpleWeapons,
        List<int> standardGoldWeapons,
        List<int> blueWeapons,
        GachaData data)
    {
        return new GachaRules(this).Draw(characterEventNonFeaturedPool, standardGoldAvatars,
            purpleAvatars, purpleWeapons, standardGoldWeapons, blueWeapons, data);
    }

    public GachaInfo ToInfo(List<int> decideOrder, List<int> goldAvatar, int gachaPullCount = 0,
        int gachaPullLimit = 0)
    {
        return GachaProtocol.BuildInfo(this, decideOrder, goldAvatar, gachaPullCount, gachaPullLimit);
    }

    /// <summary>
    ///     Whether the banner's configured time window covers <paramref name="unixSeconds" />. A window
    ///     bound of 0 means "unbounded". The client hides a banner whose window has closed, which is the
    ///     usual reason a freshly configured event pool never shows up.
    /// </summary>
    public bool IsActiveAt(long unixSeconds)
    {
        if (BeginTime > 0 && unixSeconds < BeginTime) return false;
        if (EndTime > 0 && unixSeconds > EndTime) return false;
        return true;
    }

}
