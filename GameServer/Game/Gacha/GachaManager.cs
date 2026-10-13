using System.Runtime.CompilerServices;
using March7thHoney.Data;
using March7thHoney.Data.Custom;
using March7thHoney.Database.Gacha;
using March7thHoney.Database.Inventory;
using March7thHoney.Enums;
using March7thHoney.GameServer.Game.Player;
using March7thHoney.GameServer.Server.Packet.Send.PlayerSync;
using March7thHoney.Proto;
using March7thHoney.Util;
using HistoryEntry = March7thHoney.Database.Gacha.GachaInfo;

namespace March7thHoney.GameServer.Game.Gacha;

public class GachaManager : BasePlayerManager<GachaData>
{
    private sealed class Session
    {
        internal readonly SemaphoreSlim Gate = new(1, 1);
        // shortcut: partial settlement quarantine is process-local; reconcile data before restarting.
        internal bool Interrupted;
    }
    private static readonly ConditionalWeakTable<GachaData, Session> Sessions = new();
    private static readonly Logger Log = new("Gacha");
    private sealed record Reward(int ItemId, bool NewAvatar, bool RankUp, int Dust, int Star);
    private Session Current => Sessions.GetValue(Data, _ => new Session());
    private GachaCatalog Catalog() => new(Data.CharacterEventNonFeaturedPool);

    public GachaManager(PlayerInstance player) : base(player)
    {
        Data.EnsurePityStateMigrated();
        if (Data.GachaHistory.Count > 50) Data.GachaHistory.RemoveRange(0, Data.GachaHistory.Count - 50);
        var catalog = Catalog();
        var defaults = GameData.DecideAvatarOrderData.Values.OrderBy(item => -item.Order).Select(item => item.ItemID);
        var selected = Data.CharacterEventNonFeaturedPool.Concat(Data.GachaDecideOrder).Concat(defaults)
            .Concat(GachaCatalog.StandardAvatars).Where(id => catalog.IsAvatar(id) && catalog.Rarity(id) == 5 && catalog.ValidReward(id))
            .Distinct().Take(7).ToList();
        Data.CharacterEventNonFeaturedPool = selected;
        Data.GachaDecideOrder = selected.ToList();
    }

    public List<int> GetGoldAvatars() => GachaCatalog.StandardAvatars.ToList();
    public List<int> GetGoldWeapons() => GachaCatalog.StandardWeapons.ToList();
    public List<int> GetAllGoldAvatars() => Catalog().GoldAvatars;
    public List<int> GetAllGoldWeapons() => Catalog().GoldWeapons;
    public List<int> GetPurpleAvatars() => Catalog().PurpleAvatars;
    public List<int> GetPurpleWeapons() => Catalog().PurpleWeapons;
    public List<int> GetBlueWeapons() => Catalog().BlueWeapons;
    public int GetRarity(int id) => Catalog().Rarity(id);
    public int GetType(int id) { var catalog = Catalog(); return catalog.Rarity(id) == 0 ? 0 : catalog.IsAvatar(id) ? 1 : 2; }

    public async ValueTask<DoGachaScRsp?> DoGacha(int bannerId, int times)
    {
        var session = Current;
        await session.Gate.WaitAsync();
        try { return session.Interrupted ? GachaProtocol.BuildError(bannerId, Retcode.RetServerInternalError)
            : await Execute(bannerId, times, session); }
        finally { session.Gate.Release(); }
    }

    private async ValueTask<DoGachaScRsp> Execute(int bannerId, int times, Session session)
    {
        var catalog = Catalog();
        DoGachaScRsp Reject(Retcode code) => GachaProtocol.BuildError(bannerId, code);
        if (!catalog.Banners.TryGetValue(bannerId, out var banner) || Player.InventoryManager == null || Player.AvatarManager == null)
            return Reject(Retcode.RetGachaIdNotExist);
        if (times is not (1 or 10)) return Reject(Retcode.RetGachaNumInvalid);
        var family = GachaRules.FamilyFor(banner.GachaType);
        var total = Data.PityStates.GetValueOrDefault(((int)family).ToString())?.TotalPulls ?? 0;
        if (banner.GachaType == GachaTypeEnum.Newbie && total + times > 50) return Reject(Retcode.RetGachaNewbieClose);
        var costId = banner.GachaType.GetCostItemId();
        var cost = banner.GachaType == GachaTypeEnum.Newbie && times == 10 ? 8 : times;
        if ((Player.InventoryManager.GetItem(costId)?.Count ?? 0) < cost) return Reject(Retcode.RetItemNotEnough);

        var draw = new GachaRules(banner).EvaluateBatch(times, catalog.Selected, GetGoldAvatars(), catalog.PurpleAvatars,
            catalog.PurpleWeapons, GetGoldWeapons(), catalog.BlueWeapons, Data);
        if (draw.Items.Any(id => !catalog.ValidReward(id))) return Reject(Retcode.RetGachaIdNotExist);
        if (Player.InventoryManager.Data.EquipmentItems.Count + draw.Items.Count(id => !catalog.IsAvatar(id)) > GameConstants.INVENTORY_MAX_EQUIPMENT)
            return Reject(Retcode.RetEquipmentExceedLimit);
        var rewards = PlanRewards(draw.Items, catalog);
        if (rewards == null) return Reject(Retcode.RetServerInternalError);
        var materialChanges = new Dictionary<int, long> { [costId] = -cost };
        foreach (var reward in rewards)
        {
            void Add(int id, int count) => materialChanges[id] = materialChanges.GetValueOrDefault(id) + count;
            if (reward.RankUp) Add(reward.ItemId + 10000, 1);
            Add(251, reward.Dust); Add(252, reward.Star);
        }
        if (materialChanges.Any(change => (long)(Player.InventoryManager.GetItem(change.Key)?.Count ?? 0) + change.Value is < 0 or > int.MaxValue))
            return Reject(Retcode.RetServerInternalError);

        session.Interrupted = true;
        try
        {
            var removed = await Player.InventoryManager.RemoveItem(costId, cost, sync: false);
            if (removed == null) { session.Interrupted = false; return Reject(Retcode.RetItemNotEnough); }
            var (responseItems, syncItems) = await ApplyRewards(rewards, catalog);
            syncItems.Add(removed);
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Data.GachaHistory.AddRange(draw.Items.Select(id => new HistoryEntry { GachaId = bannerId, ItemId = id, Time = timestamp }));
            GachaRules.ApplyState(Data, draw.NextState);
            MarkDirty();
            var response = GachaProtocol.BuildResult(bannerId, times, responseItems);
            session.Interrupted = false;
            try { await Player.SendPacket(new PacketPlayerSyncScNotify(syncItems.DistinctBy(item => (item.ItemId, item.UniqueId)).ToList())); }
            catch (Exception error) { Log.Warn($"Inventory synchronization failed ({error.GetType().Name}); reconnect to refresh, do not replay settlement."); }
            return response;
        }
        catch (Exception error)
        {
            MarkDirty();
            Log.Error($"Settlement interrupted ({error.GetType().Name}); further draws blocked until operator recovery.");
            return Reject(Retcode.RetServerInternalError);
        }
    }

    private List<Reward>? PlanRewards(List<int> items, GachaCatalog catalog)
    {
        var owned = new Dictionary<int, (bool Owned, int Rank, int Copies)>();
        var rewards = new List<Reward>();
        foreach (var id in items)
        {
            if (!owned.TryGetValue(id, out var state))
            {
                var avatar = catalog.IsAvatar(id) ? Player.AvatarManager!.GetFormalAvatar(id) : null;
                if (avatar != null && !avatar.PathInfos.ContainsKey(id)) return null;
                state = (avatar != null, avatar?.PathInfos[id].Rank ?? 0, Player.InventoryManager!.GetItem(id + 10000)?.Count ?? 0);
            }
            var conversion = GachaRewards.GetConversion(catalog.Rarity(id), catalog.IsAvatar(id), state.Owned, state.Rank, state.Copies);
            var isNew = catalog.IsAvatar(id) && !state.Owned;
            rewards.Add(new Reward(id, isNew, conversion.RankUp, conversion.Dust, conversion.Star));
            owned[id] = (state.Owned || isNew, state.Rank, state.Copies + (conversion.RankUp ? 1 : 0));
        }
        return rewards;
    }

    private async ValueTask<(List<GachaItem> Items, List<ItemData> Sync)> ApplyRewards(List<Reward> rewards, GachaCatalog catalog)
    {
        var result = new List<GachaItem>();
        var sync = new List<ItemData>();
        foreach (var reward in rewards)
        {
            if (reward.NewAvatar)
            {
                if (await Player.AvatarManager!.AddAvatar(reward.ItemId, isGacha: true) == null)
                    throw new InvalidOperationException("Avatar reward was not applied");
            }
            else
            {
                var applied = await Player.InventoryManager!.AddItem(reward.ItemId, 1, false, sync: false, returnRaw: true);
                if (applied != null) sync.Add(applied);
                else if (!catalog.IsAvatar(reward.ItemId) || reward.RankUp) throw new InvalidOperationException("Reward was not applied");
            }
            var item = new GachaItem { GachaItem_ = new Item { ItemId = (uint)reward.ItemId, Num = 1, Rank = 1, Level = 1 },
                IsNew = reward.NewAvatar, TransferItemList = new ItemList(), TokenItem = new ItemList() };
            if (reward.RankUp) item.TransferItemList.ItemList_.Add(new Item { ItemId = (uint)(reward.ItemId + 10000), Num = 1 });
            foreach (var (id, count) in new[] { (251, reward.Dust), (252, reward.Star) })
            {
                if (count == 0) continue;
                var applied = await Player.InventoryManager!.AddItem(id, count, false, sync: false, returnRaw: true)
                    ?? throw new InvalidOperationException("Conversion reward was not applied");
                sync.Add(applied);
                item.TokenItem.ItemList_.Add(new Item { ItemId = (uint)id, Num = (uint)count });
            }
            result.Add(item);
        }
        return (result, sync);
    }

    public GetGachaInfoScRsp ToProto()
    {
        var session = Current; session.Gate.Wait();
        try
        {
            var catalog = Catalog();
            var banners = new List<March7thHoney.Proto.GachaInfo>();
            var selections = new List<NEIMLKNMDBM>();
            foreach (var banner in catalog.Banners.Values)
            {
                var total = Data.PityStates.GetValueOrDefault(((int)GachaRules.FamilyFor(banner.GachaType)).ToString())?.TotalPulls ?? 0;
                if (banner.GachaType == GachaTypeEnum.Newbie && total >= 50) continue;
                banners.Add(GachaProtocol.BuildInfo(banner, catalog.Selected, GetGoldAvatars(), total,
                    banner.GachaType == GachaTypeEnum.Newbie ? 50 : 0,
                    banner.RateUpItems4.Where(id => catalog.Rarity(id) == 4).Distinct()));
                if (banner.GachaType is GachaTypeEnum.AvatarUp or GachaTypeEnum.CollaborationAvatarUp)
                    selections.Add(GachaProtocol.BuildSelection((uint)banner.GachaId, (uint)Data.CharacterEventDecideItemType,
                        catalog.Selected.Select(id => (uint)id)));
            }
            return GachaProtocol.BuildPage(banners, selections, (uint)Random.Shared.Next(1000, 1999));
        }
        finally { session.Gate.Release(); }
    }

    private Retcode Select(int bannerId, int type, IEnumerable<uint> ids)
    {
        if (Current.Interrupted) return Retcode.RetServerInternalError;
        var catalog = Catalog();
        if (!catalog.Banners.TryGetValue(bannerId, out var banner)) return Retcode.RetGachaIdNotExist;
        if (banner.GachaType is not (GachaTypeEnum.AvatarUp or GachaTypeEnum.CollaborationAvatarUp)) return Retcode.RetGachaDecideItemTypeInvalid;
        var selected = ids.Select(id => (int)id).Distinct().ToList();
        if (selected.Count != 7 || selected.Any(id => !catalog.IsAvatar(id) || catalog.Rarity(id) != 5 || !catalog.ValidReward(id)))
            return Retcode.RetGachaDecideItemIdInvalid;
        Data.CharacterEventNonFeaturedPool = selected;
        Data.GachaDecideOrder = selected.ToList();
        Data.CharacterEventDecideItemType = type;
        MarkDirty(); return Retcode.RetSucc;
    }
    public Retcode SetCharacterEventNonFeaturedPool(int id, int type, IEnumerable<uint> selected)
    {
        var session = Current; session.Gate.Wait();
        try { return Select(id, type, selected); } finally { session.Gate.Release(); }
    }
    public async ValueTask<Retcode> SetCharacterEventNonFeaturedPoolAsync(int id, int type, IEnumerable<uint> selected)
    {
        var session = Current; await session.Gate.WaitAsync();
        try { return Select(id, type, selected); } finally { session.Gate.Release(); }
    }
    public List<int> GetCharacterEventNonFeaturedPool()
    {
        var session = Current; session.Gate.Wait();
        try { return Data.CharacterEventNonFeaturedPool.ToList(); } finally { session.Gate.Release(); }
    }
}
