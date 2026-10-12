namespace March7thHoney.Data.Custom;

public static class GachaRewards
{
    public static (int Dust, int Star, bool RankUp) GetConversion(int rarity, bool isAvatar,
        bool owned, int rank, int rankUpCount)
    {
        if (rarity < 4) return (20, 0, false);
        if (!isAvatar) return (0, rarity == 5 ? 20 : 8, false);
        if (!owned) return (0, 0, false);
        var maxed = rank + rankUpCount >= 6;
        return (0, rarity == 5 ? (maxed ? 100 : 40) : (maxed ? 20 : 8), !maxed);
    }
}
