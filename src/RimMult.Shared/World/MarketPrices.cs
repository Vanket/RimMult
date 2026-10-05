using System;
using System.Collections.Generic;
using System.Linq;
using RimMult.Shared.Serialization;

namespace RimMult.Shared.World;

/// <summary>
/// News on the world market: for a while, everything in one item category (a RimWorld <c>ThingCategoryDef</c>, children
/// included) costs <see cref="Factor"/> times as much — "a war is coming: weapons +30 %".
/// </summary>
public sealed class MarketNews
{
    public string Category { get; set; } = "";
    public float Factor { get; set; } = 1f;
    public long StartTick { get; set; }
    public long EndTick { get; set; }

    public void Write(ByteWriter writer)
    {
        writer.WriteString(Category);
        writer.WriteFloat(Factor);
        writer.WriteVarInt(StartTick);
        writer.WriteVarInt(EndTick);
    }

    public static MarketNews Read(ByteReader reader) => new()
    {
        Category = reader.ReadRequiredString(),
        Factor = reader.ReadFloat(),
        StartTick = reader.ReadVarInt(),
        EndTick = reader.ReadVarInt(),
    };
}

/// <summary>
/// The world's prices, the same for everyone: what players buy from the NPC factions gets dearer, what they sell to
/// them gets cheaper, and both drift back to normal with time; now and then news moves a whole category for some days.
/// The server keeps the numbers (per item def name); each game turns them into prices for its own items.
/// </summary>
public sealed class MarketPrices
{
    public const float MinFactor = 0.5f;
    public const float MaxFactor = 2f;

    /// <summary>A deal worth this much silver moves its item's price by <see cref="MaxStepPerDeal"/>.</summary>
    public const float SilverPerFullStep = 3000f;
    public const float MaxStepPerDeal = 0.15f;

    /// <summary>Each game day a price moves this share of the way back to normal.</summary>
    public const float DriftPerDay = 0.08f;

    private const long TicksPerDay = 60_000;

    /// <summary>The categories news may concern (vanilla <c>ThingCategoryDef</c> names; mods' items sit in them too).</summary>
    public static readonly string[] NewsCategories =
    {
        "FoodMeals", "FoodRaw", "Medicine", "Drugs", "Textiles", "ResourcesRaw", "Manufactured", "StoneBlocks",
        "WeaponsRanged", "WeaponsMelee", "Apparel", "ApparelArmor", "BodyParts",
    };

    /// <summary>Price factor per item def name; items not listed cost their normal price.</summary>
    public Dictionary<string, float> Items { get; set; } = new();

    public List<MarketNews> News { get; set; } = new();

    /// <summary>World tick of the next news (0: not planned yet).</summary>
    public long NextNewsTick { get; set; }

    /// <summary>World tick prices last drifted at.</summary>
    public long LastDriftTick { get; set; }

    /// <summary>An item's own factor (news not counted: categories are the game's business).</summary>
    public float ItemFactor(string defName) => Items.TryGetValue(defName, out var factor) ? factor : 1f;

    /// <summary>The factor of news running now for one of the item's categories (multiplied).</summary>
    public float NewsFactor(IEnumerable<string> categories, long now)
    {
        var set = new HashSet<string>(categories);
        var factor = 1f;
        foreach (var news in News)
        {
            if (news.StartTick <= now && now < news.EndTick && set.Contains(news.Category))
                factor *= news.Factor;
        }
        return factor;
    }

    /// <summary>Players bought <paramref name="defName"/> from the NPCs for <paramref name="silver"/>: it gets dearer.</summary>
    public void Bought(string defName, float silver) => Move(defName, silver, +1);

    /// <summary>Players sold <paramref name="defName"/> to the NPCs for <paramref name="silver"/>: it gets cheaper.</summary>
    public void Sold(string defName, float silver) => Move(defName, silver, -1);

    private void Move(string defName, float silver, int direction)
    {
        if (defName.Length == 0 || silver <= 0f)
            return;
        var step = Math.Min(MaxStepPerDeal, MaxStepPerDeal * silver / SilverPerFullStep);
        var factor = ItemFactor(defName) * (1f + direction * step);
        Items[defName] = Math.Max(MinFactor, Math.Min(MaxFactor, factor));
    }

    /// <summary>Prices drift back towards normal, a day's worth at a time; ended news is dropped. True if anything changed.</summary>
    public bool Drift(long now)
    {
        if (LastDriftTick == 0 || now < LastDriftTick)
        {
            LastDriftTick = now;
            return false;
        }
        var days = (now - LastDriftTick) / TicksPerDay;
        if (days <= 0)
            return false;
        LastDriftTick += days * TicksPerDay;
        var keep = (float)Math.Pow(1f - DriftPerDay, days);
        foreach (var key in Items.Keys.ToList())
        {
            var factor = 1f + (Items[key] - 1f) * keep;
            if (Math.Abs(factor - 1f) < 0.01f)
                Items.Remove(key);
            else
                Items[key] = factor;
        }
        News.RemoveAll(n => n.EndTick <= now);
        return true;
    }

    /// <summary>
    /// Every 5–9 days some news: a category gets dearer (×1.2–1.5) or cheaper (×0.65–0.85) for 7–15 days. Returns it,
    /// or null when it isn't time yet.
    /// </summary>
    public MarketNews? MaybeNews(long now, Random random)
    {
        if (NextNewsTick == 0)
        {
            NextNewsTick = now + random.Next(5, 10) * TicksPerDay;
            return null;
        }
        if (now < NextNewsTick)
            return null;
        NextNewsTick = now + random.Next(5, 10) * TicksPerDay;
        var running = new HashSet<string>(News.Select(n => n.Category));
        var free = NewsCategories.Where(c => !running.Contains(c)).ToList();
        if (free.Count == 0)
            return null;
        var dearer = random.Next(2) == 0;
        var news = new MarketNews
        {
            Category = free[random.Next(free.Count)],
            Factor = dearer ? 1.2f + 0.3f * (float)random.NextDouble() : 0.65f + 0.2f * (float)random.NextDouble(),
            StartTick = now,
            EndTick = now + random.Next(7, 16) * TicksPerDay,
        };
        News.Add(news);
        return news;
    }

    public void Write(ByteWriter writer)
    {
        writer.WriteVarUInt((ulong)Items.Count);
        foreach (var pair in Items)
        {
            writer.WriteString(pair.Key);
            writer.WriteFloat(pair.Value);
        }
        writer.WriteVarUInt((ulong)News.Count);
        foreach (var news in News)
            news.Write(writer);
        writer.WriteVarInt(NextNewsTick);
        writer.WriteVarInt(LastDriftTick);
    }

    public static MarketPrices Read(ByteReader reader)
    {
        var prices = new MarketPrices();
        var items = reader.ReadVarUInt();
        if (items > 100_000)
            throw new ProtocolException($"Too many prices: {items}");
        for (var i = 0UL; i < items; i++)
            prices.Items[reader.ReadRequiredString()] = reader.ReadFloat();
        var news = reader.ReadVarUInt();
        if (news > 1000)
            throw new ProtocolException($"Too much news: {news}");
        for (var i = 0UL; i < news; i++)
            prices.News.Add(MarketNews.Read(reader));
        prices.NextNewsTick = reader.ReadVarInt();
        prices.LastDriftTick = reader.ReadVarInt();
        return prices;
    }
}
