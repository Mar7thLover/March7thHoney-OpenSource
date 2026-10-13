using March7thHoney.Data;
using March7thHoney.Data.Custom;
using March7thHoney.Enums;
using March7thHoney.Enums.Item;

namespace March7thHoney.GameServer.Game.Gacha;

internal sealed class GachaCatalog
{
    // CN 4.6 group 31 renders two subgroups, each with two avatar and two weapon tabs.
    private static readonly (int Id, GachaTypeEnum Type)[] CollaborationGroup =
    [
        (5003, GachaTypeEnum.CollaborationAvatarUp), (5004, GachaTypeEnum.CollaborationAvatarUp),
        (6003, GachaTypeEnum.CollaborationWeaponUp), (6004, GachaTypeEnum.CollaborationWeaponUp),
        (5001, GachaTypeEnum.CollaborationAvatarUp), (5002, GachaTypeEnum.CollaborationAvatarUp),
        (6001, GachaTypeEnum.CollaborationWeaponUp), (6002, GachaTypeEnum.CollaborationWeaponUp)
    ];
    internal static readonly int[] StandardAvatars = [1003, 1004, 1101, 1107, 1104, 1209, 1211];
    internal static readonly int[] StandardWeapons = [23000, 23002, 23003, 23004, 23005, 23012, 23013];
    private static readonly HashSet<int> BattlePassWeapons = [21028, 21029, 21030, 21031, 21032, 21033, 21034,
        21052, 21053, 21055, 21056, 21057, 21058, 21060, 21061, 21062, 21065];
    private readonly Dictionary<int, (int Rarity, bool Avatar)> items = [];
    internal List<int> GoldAvatars => items.Where(item => item.Value is (5, true)).Select(item => item.Key).ToList();
    internal List<int> GoldWeapons => items.Where(item => item.Value is (5, false)).Select(item => item.Key).ToList();
    internal List<int> PurpleAvatars => items.Where(item => item.Value is (4, true)).Select(item => item.Key).ToList();
    internal List<int> PurpleWeapons => items.Where(item => item.Value is (4, false)).Select(item => item.Key).ToList();
    internal List<int> BlueWeapons => items.Where(item => item.Value is (3, false)).Select(item => item.Key).ToList();
    internal Dictionary<int, BannerConfig> Banners { get; } = [];
    internal List<int> Selected { get; }

    internal GachaCatalog(IEnumerable<int> selection)
    {
        foreach (var avatar in GameData.AvatarConfigData.Values)
        {
            var rarity = avatar.Rarity == RarityEnum.CombatPowerAvatarRarityType5 ? 5
                : avatar.Rarity == RarityEnum.CombatPowerAvatarRarityType4 ? 4 : 0;
            if (rarity == 0 || rarity == 4 && (avatar.MaxRank <= 0 ||
                GameData.MultiplePathAvatarConfigData.TryGetValue(avatar.AvatarID, out var path) && path.BaseAvatarID != avatar.AvatarID)) continue;
            items[avatar.AvatarID] = (rarity, true);
        }
        foreach (var weapon in GameData.EquipmentConfigData.Values)
        {
            if (!weapon.Release) continue;
            var rarity = weapon.Rarity switch
            {
                RarityEnum.CombatPowerLightconeRarity5 => 5,
                RarityEnum.CombatPowerLightconeRarity4 => 4,
                RarityEnum.CombatPowerLightconeRarity3 => 3,
                _ => 0
            };
            if (rarity == 0 || rarity == 4 && (BattlePassWeapons.Contains(weapon.EquipmentID) ||
                GameData.BattlePassRewardItemIds.Contains(weapon.EquipmentID) || weapon.EquipmentID is >= 22000 and < 23000)) continue;
            items[weapon.EquipmentID] = (rarity, false);
        }
        Selected = selection.ToList();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var group in GameData.BannersConfig.Banners.GroupBy(banner => banner.GachaId))
        {
            if (group.Count() != 1) continue;
            var banner = group.Single();
            if (banner.GachaId > 0 && banner.IsActiveAt(now) && IsComplete(banner)) Banners.Add(banner.GachaId, banner);
        }
        // A partial merge group aborts client page initialization, including later standard pools.
        if (!CollaborationGroup.All(member => Banners.TryGetValue(member.Id, out var banner) && banner.GachaType == member.Type))
            foreach (var member in CollaborationGroup) Banners.Remove(member.Id);
    }

    internal int Rarity(int id) => items.GetValueOrDefault(id).Rarity;
    internal bool IsAvatar(int id) => items.GetValueOrDefault(id).Avatar;
    internal bool ValidReward(int id)
    {
        if (!items.ContainsKey(id) || !GameData.ItemConfigData.TryGetValue(id, out var item)) return false;
        if (!IsAvatar(id)) return item.ItemMainType == ItemMainTypeEnum.Equipment;
        return item.ItemMainType == ItemMainTypeEnum.AvatarCard && GameData.AvatarConfigData[id].MaxRank >= 6 &&
            (!GameData.MultiplePathAvatarConfigData.TryGetValue(id, out var path) || path.BaseAvatarID == id) &&
            GameData.ItemConfigData.ContainsKey(id + 10000);
    }

    private bool IsComplete(BannerConfig banner)
    {
        var cost = banner.GachaType.GetCostItemId();
        if (cost == 0 || new[] { cost, 251, 252 }.Any(id => !GameData.ItemConfigData.TryGetValue(id, out var material) ||
            material.ItemMainType != ItemMainTypeEnum.Material || material.PileLimit < 100)) return false;
        if (banner.RateUpItems5.Any(id => Rarity(id) != 5) || banner.RateUpItems4.Any(id => Rarity(id) is not (4 or 5))) return false;
        var gold = banner.GachaType switch
        {
            GachaTypeEnum.Newbie => StandardAvatars.ToList(),
            GachaTypeEnum.WeaponUp or GachaTypeEnum.CollaborationWeaponUp => StandardWeapons.ToList(),
            GachaTypeEnum.AvatarUp or GachaTypeEnum.CollaborationAvatarUp => Selected.Concat(StandardAvatars).Distinct().ToList(),
            _ => StandardAvatars.Concat(StandardWeapons).ToList()
        };
        var purple = PurpleAvatars.Concat(PurpleWeapons).ToList();
        var blue = BlueWeapons;
        return gold.Count > 0 && purple.Count > 0 && blue.Count > 0 &&
            gold.Concat(purple).Concat(blue).Concat(banner.RateUpItems5).Concat(banner.RateUpItems4).All(ValidReward);
    }
}
