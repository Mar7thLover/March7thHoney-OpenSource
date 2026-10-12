using System.Reflection;
using System.IO.Compression;
using MemoryPack;
using March7thHoney.Data;
using March7thHoney.Data.Custom;
using March7thHoney.Data.Excel;
using March7thHoney.Database;
using March7thHoney.Database.Avatar;
using March7thHoney.Database.Gacha;
using March7thHoney.Database.Friend;
using March7thHoney.Database.Generated;
using March7thHoney.Database.Inventory;
using March7thHoney.Database.Player;
using March7thHoney.Enums;
using March7thHoney.Enums.Item;
using March7thHoney.GameServer.Game.Avatar;
using March7thHoney.GameServer.Game.Gacha;
using March7thHoney.GameServer.Game.Inventory;
using March7thHoney.GameServer.Game.Player;
using March7thHoney.Proto;
using March7thHoney.Util;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;

namespace GachaRegression;

internal static class IntegrationChecks
{
    internal static async Task Run(Action<bool, string> check, string? resourceCache = null)
    {
        // No real configuration, account or runtime files are read by this process.
        var file = Path.Combine(Path.GetTempPath(), $"m7h-gacha-test-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={file};Pooling=False";
        SQLitePCL.Batteries_V2.Init();
        typeof(DatabaseHelper).GetField("_connString", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, connectionString);
        var database = new DatabaseHelper();
        try
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                foreach (var store in EntityStores.Map.Values) store.EnsureTable(connection);
            }
            foreach (var id in new[] { 101, 102, 251, 252 })
                GameData.ItemConfigData[id] = new ItemConfigExcel { ID = id, ItemMainType = ItemMainTypeEnum.Material,
                    PileLimit = int.MaxValue };
            GameData.ItemConfigData[20001] = new ItemConfigExcel { ID = 20001, ItemMainType = ItemMainTypeEnum.Equipment };
            GameData.EquipmentConfigData[20001] = new EquipmentConfigExcel { EquipmentID = 20001, Release = true,
                Rarity = RarityEnum.CombatPowerLightconeRarity3 };
            var banner = new BannerConfig { GachaId = 1001, GachaType = GachaTypeEnum.Normal };
            GameData.BannersConfig.Banners.Clear();
            GameData.BannersConfig.Banners.Add(banner);
            var player = new PlayerInstance(new PlayerData { Uid = 1 }) { Uid = 1 };
            var inventory = new InventoryManager(player);
            var avatars = new AvatarManager(player);
            typeof(PlayerInstance).GetProperty(nameof(PlayerInstance.InventoryManager))!.SetValue(player, inventory);
            typeof(PlayerInstance).GetProperty(nameof(PlayerInstance.AvatarManager))!.SetValue(player, avatars);
            var manager = new GachaManager(player);
            foreach (var id in manager.GetGoldAvatars())
            {
                GameData.AvatarConfigData[id] = new AvatarConfigExcel { AvatarID = id, MaxRank = 6,
                    Rarity = RarityEnum.CombatPowerAvatarRarityType5 };
                GameData.ItemConfigData[id] = new ItemConfigExcel { ID = id, ItemMainType = ItemMainTypeEnum.AvatarCard };
                GameData.ItemConfigData[id + 10000] = new ItemConfigExcel { ID = id + 10000,
                    ItemMainType = ItemMainTypeEnum.Material, PileLimit = int.MaxValue };
                avatars.Data.FormalAvatars.Add(new FormalAvatarInfo { BaseAvatarId = id, AvatarId = id,
                    PathInfos = { [id] = new PathInfo(id) { Rank = 6 } } });
            }
            foreach (var id in manager.GetGoldWeapons().Append(21001))
            {
                GameData.ItemConfigData[id] = new ItemConfigExcel { ID = id, ItemMainType = ItemMainTypeEnum.Equipment };
                GameData.EquipmentConfigData[id] = new EquipmentConfigExcel { EquipmentID = id, Release = true,
                    Rarity = id == 21001 ? RarityEnum.CombatPowerLightconeRarity4 : RarityEnum.CombatPowerLightconeRarity5 };
            }
            // Recreate the manager after fixture resources exist, like normal resource-first startup.
            manager = new GachaManager(player);
            inventory.Data.MaterialItems.Add(new ItemData { ItemId = 101, Count = 100 });
            string State() => JsonConvert.SerializeObject(new { Gacha = manager.Data, Inventory = inventory.Data });
            async Task Reject(int id, int count, Retcode expected)
            {
                var before = State();
                var response = (await manager.DoGacha(id, count))!;
                check(response.Retcode == (uint)expected, "request error code");
                check(State() == before, "rejected request leaves inventory and pity unchanged");
            }
            await Reject(9999, 1, Retcode.RetGachaIdNotExist);
            await Reject(1001, 0, Retcode.RetGachaNumInvalid);
            await Reject(1001, 11, Retcode.RetGachaNumInvalid);
            banner.EndTime = 1;
            await Reject(1001, 1, Retcode.RetGachaIdNotExist);
            banner.EndTime = 0;
            banner.GachaType = GachaTypeEnum.Unknown;
            await Reject(1001, 1, Retcode.RetGachaIdNotExist);
            banner.GachaType = GachaTypeEnum.Normal;
            banner.RateUpItems5 = [999999];
            await Reject(1001, 1, Retcode.RetGachaIdNotExist);
            banner.RateUpItems5.Clear();
            inventory.Data.MaterialItems[0].Count = 0;
            await Reject(1001, 1, Retcode.RetItemNotEnough);
            inventory.Data.MaterialItems[0].Count = 100;
            inventory.Data.EquipmentItems.AddRange(Enumerable.Range(0, GameConstants.INVENTORY_MAX_EQUIPMENT)
                .Select(_ => new ItemData { ItemId = 20001, Count = 1 }));
            await Reject(1001, 10, Retcode.RetEquipmentExceedLimit);
            inventory.Data.EquipmentItems.Clear();
            var single = (await manager.DoGacha(1001, 1))!;
            check(single.Retcode == 0 && single.GachaNum == 1 && single.GachaItemList.Count == 1, "single response");
            check(inventory.GetItem(101)!.Count == 99 && manager.Data.GachaHistory.Count == 1, "single cost and history");
            var ten = (await manager.DoGacha(1001, 10))!;
            check(ten.Retcode == 0 && ten.GachaItemList.Count == 10 && inventory.GetItem(101)!.Count == 89,
                "ten response and cost");
            var threeStars = single.GachaItemList.Concat(ten.GachaItemList)
                .Count(item => item.GachaItem_.ItemId == 20001);
            check((inventory.GetItem(251)?.Count ?? 0) == threeStars * 20 && manager.Data.GachaHistory.Count == 11,
                "dust matches actual three-star rewards and history");
            banner.GachaType = GachaTypeEnum.Newbie;
            var newbie = (await manager.DoGacha(1001, 10))!;
            check(newbie.Retcode == 0 && inventory.GetItem(101)!.Count == 81, "newbie discount");
            manager.Data.GetPityState(GachaPityFamilyEnum.Newbie).TotalPulls = 50;
            await Reject(1001, 1, Retcode.RetGachaNewbieClose);
            banner.GachaType = GachaTypeEnum.Normal;
            inventory.GetItem(101)!.Count = 10;
            var outcomes = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ => (await manager.DoGacha(1001, 10))!));
            check(outcomes.Count(r => r.Retcode == 0) == 1 && outcomes.Count(r => r.Retcode == (uint)Retcode.RetItemNotEnough) == 1,
                "concurrent ten draws cannot spend the same tickets");
            foreach (var id in manager.GetGoldAvatars())
                GameData.AvatarConfigData[id] = new AvatarConfigExcel { AvatarID = id, MaxRank = 6,
                    Rarity = RarityEnum.CombatPowerAvatarRarityType5 };
            GameData.BannersConfig.Banners.Add(new BannerConfig { GachaId = 2001, GachaType = GachaTypeEnum.AvatarUp });
            var selectionBefore = State();
            check(manager.SetCharacterEventNonFeaturedPool(2001, 1, [1003]) == Retcode.RetGachaDecideItemIdInvalid,
                "incomplete selection rejected");
            check(State() == selectionBefore, "invalid selection preserves state");
            check(manager.SetCharacterEventNonFeaturedPool(2001, 1, manager.GetGoldAvatars().Select(x => (uint)x)) == Retcode.RetSucc,
                "valid selection accepted");
            GameData.AvatarConfigData[4001] = new AvatarConfigExcel { AvatarID = 4001, MaxRank = 0,
                Rarity = RarityEnum.CombatPowerAvatarRarityType5 };
            var savedSelection = State();
            check(await manager.SetCharacterEventNonFeaturedPoolAsync(2001, 1,
                    manager.GetGoldAvatars().Skip(1).Select(id => (uint)id).Append(4001u)) == Retcode.RetGachaDecideItemIdInvalid,
                "selection with unavailable reward rejected");
            check(State() == savedSelection, "unavailable selection preserves state");
            GameData.AvatarConfigData.Remove(4001);
            GameData.BannersConfig.Banners.Add(new BannerConfig { GachaId = 3001,
                GachaType = GachaTypeEnum.CollaborationAvatarUp });
            var page = manager.ToProto();
            check(page.AKMDBPNEHDE.Count == 2, "page contains event and collaboration selections");
            foreach (var selection in page.AKMDBPNEHDE)
                check(selection.Equals(GachaProtocol.BuildSelection(selection.MMELNCHIDNC,
                    (uint)manager.Data.CharacterEventDecideItemType,
                    manager.GetCharacterEventNonFeaturedPool().Select(id => (uint)id))), "initial selection matches setter contract");
            banner.RateUpItems5 = [999999];
            check(!manager.ToProto().GachaInfoList.Any(info => info.GachaId == 1001), "invalid pool is not advertised");
            banner.RateUpItems5.Clear();
            GameData.ItemConfigData.Remove(21001);
            await Reject(1001, 10, Retcode.RetGachaIdNotExist);
            check(manager.ToProto().GachaInfoList.Count == 0, "missing reachable candidate hides affected pools");
            GameData.ItemConfigData[21001] = new ItemConfigExcel { ID = 21001, ItemMainType = ItemMainTypeEnum.Equipment };
            GameData.ItemConfigData[1101] = new ItemConfigExcel { ID = 1101, ItemMainType = ItemMainTypeEnum.AvatarCard };
            GameData.ItemConfigData[11101] = new ItemConfigExcel { ID = 11101, ItemMainType = ItemMainTypeEnum.Material };
            await inventory.AddItem(1101, 1, false, sync: false);
            check(inventory.GetItem(11101) == null, "rank-six duplicate cannot grant an extra rank-up item");
            var catalogMethod = typeof(GachaManager).GetMethod("Catalog", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var planner = typeof(GachaManager).GetMethod("PlanRewards", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var existingAvatar = avatars.GetFormalAvatar(1101)!;
            existingAvatar.PathInfos[1101].Rank = 5;
            var planBefore = State();
            var plannedRewards = ((System.Collections.IEnumerable)planner.Invoke(manager,
                [new List<int> { 1101, 1101 }, catalogMethod.Invoke(manager, null)!])!).Cast<object>().ToList();
            int Star(object reward) => (int)reward.GetType().GetProperty("Star")!.GetValue(reward)!;
            bool RankUp(object reward) => (bool)reward.GetType().GetProperty("RankUp")!.GetValue(reward)!;
            check(Star(plannedRewards[0]) == 40 && RankUp(plannedRewards[0]) &&
                Star(plannedRewards[1]) == 100 && !RankUp(plannedRewards[1]),
                "batch reward planning advances held copies across the max-rank boundary");
            check(State() == planBefore, "reward planning cannot modify real inventory or gacha state");
            existingAvatar.PathInfos[1101].Rank = 6;
            var beforeSave = State();
            database.SaveDatabase();
            using var loadedConnection = new SqliteConnection(connectionString);
            loadedConnection.Open();
            var loadedGacha = (GachaData)EntityStores.Map[typeof(GachaData)].LoadAll(loadedConnection).Single();
            var loadedInventory = (InventoryData)EntityStores.Map[typeof(InventoryData)].LoadAll(loadedConnection).Single();
            check(JsonConvert.SerializeObject(new { Data = loadedGacha, Inventory = loadedInventory }) ==
                JsonConvert.SerializeObject(new { Data = manager.Data, Inventory = inventory.Data }), "SQLite reload preserves full state");
            check(State() == beforeSave, "save cannot mutate gameplay state");
            var gachaStore = EntityStores.Map[typeof(GachaData)];
            var changed = manager.Data.GetPityState(GachaPityFamilyEnum.Normal);
            changed.TotalPulls += 7;
            DatabaseHelper.MarkDirty(player.Uid);
            EntityStores.Map[typeof(GachaData)] = gachaStore with
            {
                Update = (_, _) => throw new InvalidOperationException("synthetic save failure")
            };
            try { database.SaveDatabase(); }
            finally { EntityStores.Map[typeof(GachaData)] = gachaStore; }
            var afterFailure = (GachaData)gachaStore.LoadAll(loadedConnection).Single();
            check(afterFailure.GetPityState(GachaPityFamilyEnum.Normal).TotalPulls == changed.TotalPulls - 7,
                "failed save rolls back persisted state");
            database.SaveDatabase();
            var afterRetry = (GachaData)gachaStore.LoadAll(loadedConnection).Single();
            check(afterRetry.GetPityState(GachaPityFamilyEnum.Normal).TotalPulls == changed.TotalPulls,
                "failed batch stays dirty and retries");

            // Synthetic incomplete avatar resources fail after tickets are removed.
            var failingPlayer = new PlayerInstance(new PlayerData { Uid = 2 }) { Uid = 2 };
            var failingInventory = new InventoryManager(failingPlayer);
            var failingAvatars = new AvatarManager(failingPlayer);
            typeof(PlayerInstance).GetProperty(nameof(PlayerInstance.InventoryManager))!.SetValue(failingPlayer, failingInventory);
            typeof(PlayerInstance).GetProperty(nameof(PlayerInstance.AvatarManager))!.SetValue(failingPlayer, failingAvatars);
            var failingManager = new GachaManager(failingPlayer);
            failingInventory.Data.MaterialItems.Add(new ItemData { ItemId = 101, Count = 20 });
            banner.GachaType = GachaTypeEnum.Newbie;
            failingManager.Data.GetPityState(GachaPityFamilyEnum.Newbie).PullsSinceFiveStar = 49;
            var interrupted = (await failingManager.DoGacha(1001, 1))!;
            check(interrupted.Retcode == (uint)Retcode.RetServerInternalError && failingInventory.GetItem(101)!.Count == 19,
                "partial settlement error is reported");
            var interruptedState = JsonConvert.SerializeObject(new { failingInventory.Data, Gacha = failingManager.Data });
            failingManager = new GachaManager(failingPlayer);
            check((await failingManager.DoGacha(1001, 1))!.Retcode == (uint)Retcode.RetServerInternalError,
                "recreated manager retains settlement quarantine");
            check(await failingManager.SetCharacterEventNonFeaturedPoolAsync(2001, 1,
                manager.GetGoldAvatars().Select(id => (uint)id)) == Retcode.RetServerInternalError,
                "quarantined player cannot change selection");
            check(JsonConvert.SerializeObject(new { failingInventory.Data, Gacha = failingManager.Data }) == interruptedState,
                "quarantined request cannot mutate partial state again");

            if (resourceCache != null)
            {
                // Optional read-only resource compatibility check; never reads the runtime database or credentials.
                MemoryPackFormatterProvider.Register(new JsonObjectMemoryPackFormatter());
                MemoryPackFormatterProvider.Register(new BigIntegerMemoryPackFormatter());
                using var input = File.OpenRead(resourceCache);
                using var gzip = new GZipStream(input, CompressionMode.Decompress);
                using var buffer = new MemoryStream();
                gzip.CopyTo(buffer);
                GameData.ApplySnapshot(MemoryPackSerializer.Deserialize<GameDataSnapshot>(buffer.ToArray())!);
                var bannersFile = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Banners.json");
                GameData.BannersConfig = new BannersConfig
                {
                    Banners = JsonConvert.DeserializeObject<List<BannerConfig>>(File.ReadAllText(bannersFile))!
                };
                var catalogManager = new GachaManager(player);
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var active = GameData.BannersConfig.Banners.Where(b => b.IsActiveAt(now)).ToList();
                var catalogPage = catalogManager.ToProto();
                check(catalogPage.GachaInfoList.Count == active.Count, "loaded resource cache has complete supported catalog");
                foreach (var current in active)
                {
                    var info = catalogPage.GachaInfoList.Single(info => info.GachaId == current.GachaId);
                    var eventPreview = current.GachaType is GachaTypeEnum.AvatarUp or GachaTypeEnum.WeaponUp or
                        GachaTypeEnum.CollaborationAvatarUp or GachaTypeEnum.CollaborationWeaponUp;
                    var fourStarPreview = eventPreview ? info.PrizeItemList : info.ONIHOMCELJK;
                    check(fourStarPreview.All(id => catalogManager.GetRarity((int)id) == 4),
                        "four-star image inputs exclude legacy five-star preview entries");
                    if (eventPreview)
                        check(info.ONIHOMCELJK.Take(current.RateUpItems5.Count).SequenceEqual(current.RateUpItems5.Select(id => (uint)id)),
                            "every event and collaboration banner names its featured item first");
                }
                check(catalogPage.AKMDBPNEHDE.Count == active.Count(b => b.GachaType is
                    GachaTypeEnum.AvatarUp or GachaTypeEnum.CollaborationAvatarUp), "loaded catalog selection coverage");
                // Target client group 31 has two fixed subgroups, not an arbitrary list of active banners.
                int[] collaboration = [5003, 5004, 6003, 6004, 5001, 5002, 6001, 6002];
                check(collaboration.All(id => catalogPage.GachaInfoList.Any(info => info.GachaId == id)),
                    "CN 4.6 merge group supplies every fixed tab");
                var oldOnly = GameData.BannersConfig.Banners.Where(b => b.GachaId is 5003 or 5004 or 6003 or 6004).ToList();
                foreach (var omitted in oldOnly) GameData.BannersConfig.Banners.Remove(omitted);
                var legacyPage = catalogManager.ToProto();
                check(legacyPage.GachaInfoList.All(info => !collaboration.Contains((int)info.GachaId)),
                    "legacy four-banner config cannot publish the client zero-of-two subgroup");
                check(legacyPage.GachaInfoList.Any(info => info.GachaId == 1001), "legacy config retains standard initialization");
                GameData.BannersConfig.Banners.AddRange(oldOnly);
                foreach (var missing in collaboration)
                {
                    var omitted = GameData.BannersConfig.Banners.Single(b => b.GachaId == missing);
                    GameData.BannersConfig.Banners.Remove(omitted);
                    var beforePartial = State();
                    var partialPage = catalogManager.ToProto();
                    check(partialPage.GachaInfoList.All(info => !collaboration.Contains((int)info.GachaId)),
                        "one missing merge member hides the entire broken group");
                    check(partialPage.GachaInfoList.Any(info => info.GachaId == 1001), "partial merge keeps standard pool available");
                    check((await catalogManager.DoGacha(5001, 1))!.Retcode == (uint)Retcode.RetGachaIdNotExist,
                        "hidden incomplete group cannot consume tickets");
                    check(State() == beforePartial, "incomplete group rejection preserves inventory and pity");
                    GameData.BannersConfig.Banners.Add(omitted);
                }
                var member = GameData.BannersConfig.Banners.Single(b => b.GachaId == 5003);
                var originalType = member.GachaType;
                member.GachaType = GachaTypeEnum.AvatarUp;
                check(catalogManager.ToProto().GachaInfoList.All(info => !collaboration.Contains((int)info.GachaId)),
                    "wrong merge member type cannot initialize fixed avatar tabs");
                member.GachaType = originalType;
                var originalEnd = member.EndTime;
                member.EndTime = 1;
                check(catalogManager.ToProto().GachaInfoList.All(info => !collaboration.Contains((int)info.GachaId)),
                    "expired merge member excludes incomplete group");
                member.EndTime = originalEnd;
                var originalFeatured = member.RateUpItems5;
                member.RateUpItems5 = [999999];
                check(catalogManager.ToProto().GachaInfoList.All(info => !collaboration.Contains((int)info.GachaId)),
                    "missing reward resource excludes incomplete group");
                member.RateUpItems5 = originalFeatured;
                GameData.BannersConfig.Banners.Add(member);
                check(catalogManager.ToProto().GachaInfoList.All(info => !collaboration.Contains((int)info.GachaId)),
                    "ambiguous duplicate excludes incomplete group");
                GameData.BannersConfig.Banners.Remove(member);
                check(catalogManager.ToProto().GachaInfoList.Count == active.Count, "restored complete group restores page");
                typeof(PlayerInstance).GetProperty(nameof(PlayerInstance.FriendRecordData))!.SetValue(player, new FriendRecordData());
                inventory.Data.MaterialItems.RemoveAll(item => item.ItemId is 101 or 102);
                inventory.Data.MaterialItems.AddRange(new[] { new ItemData { ItemId = 101, Count = 500 },
                    new ItemData { ItemId = 102, Count = 500 } });
                foreach (var currentBanner in active)
                foreach (var count in new[] { 1, 10 })
                {
                    var balances = inventory.Data.MaterialItems.ToDictionary(item => item.ItemId, item => item.Count);
                    var equipmentBefore = inventory.Data.EquipmentItems.Count;
                    var avatarsBefore = avatars.Data.FormalAvatars.Count;
                    var response = (await catalogManager.DoGacha(currentBanner.GachaId, count))!;
                    check(response.Retcode == 0 && response.GachaItemList.Count == count,
                        "loaded catalog supports single and ten draws");
                    var expected = new Dictionary<int, int>();
                    var cost = currentBanner.GachaType == GachaTypeEnum.Newbie && count == 10 ? 8 : count;
                    expected[currentBanner.GachaType.GetCostItemId()] = -cost;
                    foreach (var item in response.GachaItemList.SelectMany(item =>
                        item.TokenItem.ItemList_.Concat(item.TransferItemList.ItemList_)))
                        expected[(int)item.ItemId] = expected.GetValueOrDefault((int)item.ItemId) + (int)item.Num;
                    foreach (var id in balances.Keys.Concat(inventory.Data.MaterialItems.Select(item => item.ItemId)).Distinct())
                        check((inventory.GetItem(id)?.Count ?? 0) - balances.GetValueOrDefault(id) == expected.GetValueOrDefault(id),
                            "loaded reward material delta matches response including duplicates within ten draws");
                    check(inventory.Data.EquipmentItems.Count - equipmentBefore == response.GachaItemList.Count(item =>
                            GameData.ItemConfigData[(int)item.GachaItem_.ItemId].ItemMainType == ItemMainTypeEnum.Equipment),
                        "loaded equipment delta matches response");
                    check(avatars.Data.FormalAvatars.Count - avatarsBefore == response.GachaItemList.Count(item => item.IsNew),
                        "loaded avatar delta matches response");
                }
                Console.WriteLine($"Resource compatibility: {catalogPage.GachaInfoList.Count} banners, {catalogPage.AKMDBPNEHDE.Count} selections");
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(file);
        }
    }
}
