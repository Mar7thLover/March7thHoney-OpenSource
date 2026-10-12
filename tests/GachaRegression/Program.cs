using GachaRegression;
using Google.Protobuf;
using March7thHoney.Data.Custom;
using March7thHoney.Database.Gacha;
using March7thHoney.Enums;
using MemoryPack;
using Newtonsoft.Json;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    checks++;
}

var types = new[] { GachaTypeEnum.Newbie, GachaTypeEnum.Normal, GachaTypeEnum.AvatarUp,
    GachaTypeEnum.WeaponUp, GachaTypeEnum.CollaborationAvatarUp, GachaTypeEnum.CollaborationWeaponUp };
foreach (var type in types)
{
    var banner = new BannerConfig { GachaId = 1001, GachaType = type,
        RateUpItems5 = [999], RateUpItems4 = [301, 401] };
    var family = type switch {
        GachaTypeEnum.Newbie => GachaPityFamilyEnum.Newbie,
        GachaTypeEnum.AvatarUp => GachaPityFamilyEnum.AvatarUp,
        GachaTypeEnum.WeaponUp => GachaPityFamilyEnum.WeaponUp,
        GachaTypeEnum.CollaborationAvatarUp => GachaPityFamilyEnum.AvatarCollaboration,
        GachaTypeEnum.CollaborationWeaponUp => GachaPityFamilyEnum.WeaponCollaboration,
        _ => GachaPityFamilyEnum.Normal };
    var data = new GachaData();
    var state = data.GetPityState(family);
    state.PullsSinceFiveStar = type == GachaTypeEnum.Newbie ? 49 :
        type is GachaTypeEnum.WeaponUp or GachaTypeEnum.CollaborationWeaponUp ? 79 : 89;
    state.PullsSinceFourStarOrHigher = 9;
    state.NextFiveStarGuaranteedFeatured = true;
    var result = banner.DoGacha([101], [101], [301], [401], [201], [501], data);
    Check(result is 101 or 201 or 999, $"{type}: hard pity returns five-star");
    Check(state.TotalPulls == 1 && state.PullsSinceFiveStar == 0 && state.PullsSinceFourStarOrHigher == 0,
        $"{type}: hard pity counters");
    Check(data.PityStates.Count == 1, $"{type}: separate family");
    var bytes = MemoryPackSerializer.Serialize(state);
    Check(MemoryPackSerializer.Serialize(MemoryPackSerializer.Deserialize<GachaPityState>(bytes)!).SequenceEqual(bytes),
        $"{type}: persistence layout roundtrip");
}
var existing = new GachaData { PityStates = { ["11"] = new GachaPityState { TotalPulls = 123 } },
    LastGachaFailedCount = 80 };
var before = JsonConvert.SerializeObject(existing);
existing.EnsurePityStateMigrated();
Check(JsonConvert.SerializeObject(existing) == before, "existing pity is not remigrated");
var legacy = new GachaData { LastGachaFailedCount = 74, LastGachaPurpleFailedCount = 8,
    LastAvatarGachaFailed = true };
legacy.EnsurePityStateMigrated();
Check(legacy.GetPityState(GachaPityFamilyEnum.AvatarUp).PullsSinceFiveStar == 74, "legacy migration");
Check(legacy.GetPityState(GachaPityFamilyEnum.AvatarUp).NextFiveStarGuaranteedFeatured, "legacy guarantee");
foreach (var type in types)
foreach (var seed in Enumerable.Range(1, 20))
{
    var frozen = new LegacyBannerConfig { GachaType = type, RateUpItems5 = [999], RateUpItems4 = [301, 401] };
    var banner = new BannerConfig { GachaType = type, RateUpItems5 = [999], RateUpItems4 = [301, 401] };
    foreach (var id in new[] { 1001, 2001 })
    {
        frozen.GachaId = banner.GachaId = id;
        frozen.BeginTime = banner.BeginTime = 123;
        frozen.EndTime = banner.EndTime = 321;
        var projected = banner.ToInfo([101], [101], 17, 50);
        Check(projected.GachaId == id && projected.BeginTime == 123 && projected.EndTime == 321,
            "all banner types expose the configured active window");
        Check((projected.GachaCeiling != null) == (type == GachaTypeEnum.Normal),
            "ceiling follows banner kind rather than magic id");
        Check(projected.ItemDetailList.SequenceEqual(type == GachaTypeEnum.AvatarUp ? new uint[] { 11 } : []),
            "collaboration banner does not inherit regular event marker");
        var eventPreview = type is GachaTypeEnum.AvatarUp or GachaTypeEnum.WeaponUp or
            GachaTypeEnum.CollaborationAvatarUp or GachaTypeEnum.CollaborationWeaponUp;
        Check(projected.PrizeItemList.SequenceEqual(eventPreview ? new uint[] { 301, 401 } : new uint[] { 999 }) &&
            projected.ONIHOMCELJK.SequenceEqual(eventPreview ? new uint[] { 999, 301, 401 } : new uint[] { 301, 401 }),
            "event metadata is featured-first and detail previews are four-star; other pools retain projection");
    }
    LegacyBannerConfig.RandomSource = new Random(seed);
    var rules = new GachaRules(banner, new Random(seed));
    var oldState = new GachaData();
    var newState = new GachaData();
    var batchRules = new GachaRules(banner, new Random(seed));
    var batchState = new GachaData();
    var batchItems = new Queue<int>();
    for (var batch = 0; batch < 20; batch++)
    {
        var unchangedBatch = JsonConvert.SerializeObject(batchState);
        var planned = batchRules.EvaluateBatch(10, [101], [101], [301], [401], [201], [501], batchState);
        foreach (var item in planned.Items) batchItems.Enqueue(item);
        Check(unchangedBatch == JsonConvert.SerializeObject(batchState), "batch planning leaves input unchanged");
        GachaRules.ApplyState(batchState, planned.NextState);
    }
    for (var draw = 0; draw < 200; draw++)
    {
        var unchanged = JsonConvert.SerializeObject(newState);
        var expected = frozen.DoGacha([101], [101], [301], [401], [201], [501], oldState);
        var actual = rules.Evaluate([101], [101], [301], [401], [201], [501], newState);
        Check(unchanged == JsonConvert.SerializeObject(newState), "planning cannot mutate live state");
        Check(expected == actual.ItemId, $"{type}: seeded item parity");
        Check(expected == batchItems.Dequeue(), $"{type}: seeded batch item parity");
        GachaRules.ApplyState(newState, actual.NextState);
        Check(JsonConvert.SerializeObject(oldState) == JsonConvert.SerializeObject(newState), $"{type}: seeded pity parity");
    }
    Check(JsonConvert.SerializeObject(batchState) == JsonConvert.SerializeObject(newState), "batch and sequential pity parity");
}
foreach (var featured in new[] { 1505, 1403 })
{
    var banner = new BannerConfig { GachaId = 2120, GachaType = GachaTypeEnum.AvatarUp,
        RateUpItems5 = [featured], RateUpItems4 = [1110, 1206, 1210, featured] };
    var original = JsonConvert.SerializeObject(banner);
    var info = GachaProtocol.BuildInfo(banner, [], [], fourStarItems: [1110, 1206, 1210, 1110]);
    Check(info.ONIHOMCELJK.SequenceEqual(new uint[] { (uint)featured, 1110, 1206, 1210 }),
        "switching featured avatar changes first name/damage/star metadata instead of keeping Lynx");
    Check(info.PrizeItemList.SequenceEqual(new uint[] { 1110, 1206, 1210 }), "three four-star image inputs, no duplicate nodes");
    var wire = March7thHoney.Proto.GachaInfo.Parser.ParseFrom(info.ToByteArray());
    Check(wire.ONIHOMCELJK.SequenceEqual(info.ONIHOMCELJK) && wire.PrizeItemList.SequenceEqual(info.PrizeItemList),
        "preview order survives protocol serialization");
    Check(JsonConvert.SerializeObject(banner) == original, "display projection does not change rate-up draw configuration");
}
Check(GachaRewards.GetConversion(3, false, false, 0, 0) == (20, 0, false), "three-star dust");
Check(GachaRewards.GetConversion(5, false, false, 0, 0) == (0, 20, false), "five-star weapon");
Check(GachaRewards.GetConversion(4, false, false, 0, 0) == (0, 8, false), "four-star weapon");
foreach (var rarity in new[] { 4, 5 })
{
    Check(GachaRewards.GetConversion(rarity, true, false, 0, 0) == (0, 0, false), "new avatar");
    Check(GachaRewards.GetConversion(rarity, true, true, 0, 0) == (0, rarity == 5 ? 40 : 8, true), "avatar duplicate");
    Check(GachaRewards.GetConversion(rarity, true, true, 6, 0) == (0, rarity == 5 ? 100 : 20, false), "max rank without rank-up item");
    Check(!GachaRewards.GetConversion(rarity, true, true, 3, 3).RankUp, "held rank-up items count");
}
await IntegrationChecks.Run(Check, args.Length == 1 ? args[0] : null);
Console.WriteLine($"PASS {checks} rule, conversion, request and persistence checks");
