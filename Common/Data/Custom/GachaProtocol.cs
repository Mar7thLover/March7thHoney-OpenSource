using March7thHoney.Enums;
using March7thHoney.Proto;

namespace March7thHoney.Data.Custom;

public static class GachaProtocol
{
    public static NEIMLKNMDBM BuildSelection(uint bannerId, uint decideItemType, IEnumerable<uint> order) => new()
    {
        MMELNCHIDNC = bannerId, GFANBHAEKOK = decideItemType, OJNEFBJHCCK = { order }
    };

    public static GachaInfo BuildInfo(BannerConfig banner, List<int> decideOrder, List<int> goldAvatar,
        int gachaPullCount = 0, int gachaPullLimit = 0, IEnumerable<int>? fourStarItems = null)
    {
        var fourStarPreview = (fourStarItems ?? banner.RateUpItems4).Select(id => (uint)id).ToArray();
        var featured = banner.RateUpItems5.Select(id => (uint)id).ToArray();
        var eventPreview = banner.GachaType is GachaTypeEnum.AvatarUp or GachaTypeEnum.WeaponUp or
            GachaTypeEnum.CollaborationAvatarUp or GachaTypeEnum.CollaborationWeaponUp;
        var info = new GachaInfo
        {
            GachaId = (uint)banner.GachaId, BeginTime = banner.BeginTime, EndTime = banner.EndTime,
            DetailUrl = "", HistoryUrl = "",
            // Candidate CN live projection: detail pictures are four-star; metadata is featured-first.
            PrizeItemList = { eventPreview ? fourStarPreview.Distinct() : featured },
            ONIHOMCELJK = { eventPreview ? featured.Concat(fourStarPreview).Distinct() : fourStarPreview }
        };
        if (banner.GachaType == GachaTypeEnum.Normal)
            info.GachaCeiling = new GachaCeiling
            {
                // Ceiling exchange is outside this rewrite; do not grant an unimplemented free reward.
                IsClaimed = true,
                AvatarList = { goldAvatar.Select(id => new GachaCeilingAvatar { AvatarId = (uint)id }) }
            };
        // Retain the working regular-event marker; collaboration must not inherit that projection.
        if (banner.GachaType == GachaTypeEnum.AvatarUp) info.ItemDetailList.Add(11);
        // shortcut: unknown progress fields stay unset until target-build semantics are verified.
        _ = decideOrder; _ = gachaPullCount; _ = gachaPullLimit;
        return info;
    }

    public static GetGachaInfoScRsp BuildPage(IEnumerable<GachaInfo> banners, IEnumerable<NEIMLKNMDBM> selections, uint random)
    {
        var page = new GetGachaInfoScRsp { Retcode = 0, GachaRandom = random };
        page.GachaInfoList.AddRange(banners);
        page.AKMDBPNEHDE.AddRange(selections);
        return page;
    }

    public static DoGachaScRsp BuildResult(int bannerId, int times, IEnumerable<GachaItem> items)
    {
        var response = new DoGachaScRsp { Retcode = 0, GachaId = (uint)bannerId, GachaNum = (uint)times };
        response.GachaItemList.AddRange(items);
        if (times is not (1 or 10) || response.GachaItemList.Count != times)
            throw new ArgumentException("Draw response does not match the planned draw count");
        return response;
    }

    public static DoGachaScRsp BuildError(int bannerId, Retcode retcode) => new()
    {
        GachaId = (uint)Math.Max(0, bannerId), Retcode = (uint)retcode
    };
}
