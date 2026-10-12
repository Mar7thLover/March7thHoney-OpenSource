using March7thHoney.Database.Gacha;
using March7thHoney.Enums;

namespace March7thHoney.Data.Custom;

public sealed class GachaRules(BannerConfig banner, Random? randomSource = null)
{
    private readonly Random random = randomSource ?? Random.Shared;
    private bool Weapon => banner.GachaType is GachaTypeEnum.WeaponUp or GachaTypeEnum.CollaborationWeaponUp;
    private bool Event => banner.GachaType is GachaTypeEnum.AvatarUp or GachaTypeEnum.CollaborationAvatarUp || Weapon;

    public static GachaPityFamilyEnum FamilyFor(GachaTypeEnum type) => type switch
    {
        GachaTypeEnum.Newbie => GachaPityFamilyEnum.Newbie,
        GachaTypeEnum.AvatarUp => GachaPityFamilyEnum.AvatarUp,
        GachaTypeEnum.WeaponUp => GachaPityFamilyEnum.WeaponUp,
        GachaTypeEnum.CollaborationAvatarUp => GachaPityFamilyEnum.AvatarCollaboration,
        GachaTypeEnum.CollaborationWeaponUp => GachaPityFamilyEnum.WeaponCollaboration,
        _ => GachaPityFamilyEnum.Normal
    };

    public (int ItemId, GachaData NextState) Evaluate(List<int> selected, List<int> goldAvatars,
        List<int> purpleAvatars, List<int> purpleWeapons, List<int> goldWeapons, List<int> blueWeapons, GachaData current)
    {
        var result = EvaluateBatch(1, selected, goldAvatars, purpleAvatars, purpleWeapons, goldWeapons, blueWeapons, current);
        return (result.Items[0], result.NextState);
    }

    public (List<int> Items, GachaData NextState) EvaluateBatch(int times, List<int> selected,
        List<int> goldAvatars, List<int> purpleAvatars, List<int> purpleWeapons, List<int> goldWeapons,
        List<int> blueWeapons, GachaData current)
    {
        if (times is not (1 or 10)) throw new ArgumentOutOfRangeException(nameof(times));
        var next = new GachaData
        {
            LastAvatarGachaFailed = current.LastAvatarGachaFailed,
            LastWeaponGachaFailed = current.LastWeaponGachaFailed,
            LastGachaFailedCount = current.LastGachaFailedCount,
            LastGachaPurpleFailedCount = current.LastGachaPurpleFailedCount,
            PityStates = current.PityStates.ToDictionary(pair => pair.Key, pair => new GachaPityState
            {
                PullsSinceFiveStar = pair.Value.PullsSinceFiveStar,
                PullsSinceFourStarOrHigher = pair.Value.PullsSinceFourStarOrHigher,
                NextFiveStarGuaranteedFeatured = pair.Value.NextFiveStarGuaranteedFeatured,
                NextFourStarGuaranteedFeatured = pair.Value.NextFourStarGuaranteedFeatured,
                TotalPulls = pair.Value.TotalPulls,
                NewbieFiveStarClaimed = pair.Value.NewbieFiveStarClaimed
            })
        };
        var items = new List<int>(times);
        for (var index = 0; index < times; index++)
            items.Add(Draw(selected, goldAvatars, purpleAvatars, purpleWeapons, goldWeapons, blueWeapons, next));
        return (items, next);
    }

    internal int Draw(List<int> selected, List<int> goldAvatars, List<int> purpleAvatars,
        List<int> purpleWeapons, List<int> goldWeapons, List<int> blueWeapons, GachaData data)
    {
        var family = FamilyFor(banner.GachaType);
        var state = data.GetPityState(family);
        var pull = state.PullsSinceFiveStar + 1;
        var fiveChance = banner.GachaType == GachaTypeEnum.Newbie ? (pull >= 50 ? 1 : .006)
            : Weapon ? (pull >= 80 ? 1 : pull >= 66 ? .219633 : .008)
            : pull >= 90 ? 1 : pull >= 76 ? .324 : .006;
        int item;
        if (Chance(fiveChance))
        {
            if (banner.GachaType == GachaTypeEnum.Newbie) item = Pick(goldAvatars);
            else if (!Event) item = PickCategory(goldAvatars, goldWeapons);
            else
            {
                var featured = banner.RateUpItems5.Distinct().ToList();
                var standard = Weapon ? goldWeapons : Exclude(selected, featured);
                if (!Weapon && standard.Count == 0) standard = Exclude(goldAvatars, featured);
                item = Featured(featured, standard, state.NextFiveStarGuaranteedFeatured, out var guaranteed);
                if (featured.Count > 0) state.NextFiveStarGuaranteedFeatured = guaranteed;
            }
            state.RecordFiveStar();
            if (banner.GachaType == GachaTypeEnum.Newbie) state.NewbieFiveStarClaimed = true;
        }
        else if (state.PullsSinceFourStarOrHigher + 1 >= 10 || Chance(Weapon ? .066 : .051))
        {
            var featured = banner.RateUpItems4.Where(id => purpleWeapons.Contains(id) || !Weapon && purpleAvatars.Contains(id))
                .Distinct().ToList();
            if (!Event || featured.Count == 0)
                item = Weapon ? Pick(Exclude(purpleAvatars.Concat(purpleWeapons), featured)) : PickCategory(purpleAvatars, purpleWeapons);
            else
            {
                item = Featured(featured, Exclude(purpleAvatars.Concat(purpleWeapons), featured),
                    state.NextFourStarGuaranteedFeatured, out var guaranteed);
                state.NextFourStarGuaranteedFeatured = guaranteed;
            }
            state.RecordFourStar();
        }
        else { item = Pick(blueWeapons); state.RecordThreeStar(); }

        if (family is GachaPityFamilyEnum.AvatarUp or GachaPityFamilyEnum.WeaponUp)
        {
            data.LastGachaFailedCount = state.PullsSinceFiveStar;
            data.LastGachaPurpleFailedCount = state.PullsSinceFourStarOrHigher;
            if (family == GachaPityFamilyEnum.AvatarUp) data.LastAvatarGachaFailed = state.NextFiveStarGuaranteedFeatured;
            else data.LastWeaponGachaFailed = state.NextFiveStarGuaranteedFeatured;
        }
        return item;
    }

    private int Featured(List<int> featured, List<int> standard, bool guaranteed, out bool nextGuaranteed)
    {
        nextGuaranteed = guaranteed;
        if (featured.Count == 0) return Pick(standard);
        var up = guaranteed || Chance(Weapon ? .75 : .5) || standard.Count == 0;
        nextGuaranteed = !up;
        return Pick(up ? featured : standard);
    }
    private static List<int> Exclude(IEnumerable<int> items, List<int> featured) => items.Except(featured).ToList();
    private int PickCategory(List<int> avatars, List<int> weapons) => avatars.Count == 0 ? Pick(weapons)
        : weapons.Count == 0 ? Pick(avatars) : Chance(.5) ? Pick(avatars) : Pick(weapons);
    private int Pick(List<int> items) => items.Count == 0 ? 0 : items[random.Next(items.Count)];
    private bool Chance(double chance) => random.NextDouble() < chance;

    public static void ApplyState(GachaData target, GachaData next)
    {
        target.LastAvatarGachaFailed = next.LastAvatarGachaFailed;
        target.LastWeaponGachaFailed = next.LastWeaponGachaFailed;
        target.LastGachaFailedCount = next.LastGachaFailedCount;
        target.LastGachaPurpleFailedCount = next.LastGachaPurpleFailedCount;
        target.PityStates = next.PityStates;
    }
}
