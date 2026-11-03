using System.IO;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Numerics;
using System.Text;
using WalletCopy.Models;

namespace WalletCopy.Services;

public sealed class PolymarketApiClient
{
    private static readonly HttpClient Http = CreateHttpClient();
    private const string PolygonRpcUrl = "https://polygon-bor-rpc.publicnode.com";
    private const string PusdTokenAddress = "0xC011a7E12a19f7B1f670d46F03B03f3342E82DFB";
    private static readonly ConcurrentDictionary<string, FeeSchedule> FeeSchedules = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim FeeScheduleSemaphore = new(4);
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    public async Task<IReadOnlyList<WalletTrade>> GetRecentTradesAsync(
        string walletAddress,
        CancellationToken cancellationToken = default)
    {
        var encodedAddress = Uri.EscapeDataString(walletAddress);
        var regularTradesTask = GetJsonAsync(
            $"https://data-api.polymarket.com/v2/trades?user={encodedAddress}&taker_only=false&limit=30",
            cancellationToken);
        var takerTradesTask = GetJsonAsync(
            $"https://data-api.polymarket.com/v2/trades?user={encodedAddress}&taker_only=true&limit=30",
            cancellationToken);
        await Task.WhenAll(regularTradesTask, takerTradesTask);

        using var regularTradesDocument = await regularTradesTask;
        using var takerTradesDocument = await takerTradesTask;
        var takerTradeKeys = ReadData(takerTradesDocument.RootElement)
            .Select(BuildTradeKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tradeItems = ReadData(regularTradesDocument.RootElement)
            .Where(item =>
            {
                var side = ReadString(item, "side", "type");
                return side is not null
                       && (side.Equals("BUY", StringComparison.OrdinalIgnoreCase)
                           || side.Equals("SELL", StringComparison.OrdinalIgnoreCase));
            })
            .ToArray();

        var takerMarketSlugs = tradeItems
            .Where(item => takerTradeKeys.Contains(BuildTradeKey(item)))
            .Select(item => ReadString(item, "event_slug", "slug"))
            .Where(slug => !string.IsNullOrWhiteSpace(slug))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var feeScheduleTasks = takerMarketSlugs
            .ToDictionary(
                slug => slug!,
                slug => GetFeeScheduleAsync(slug!, cancellationToken),
                StringComparer.OrdinalIgnoreCase);
        await Task.WhenAll(feeScheduleTasks.Values);

        return tradeItems.Select(item =>
        {
            var isTaker = takerTradeKeys.Contains(BuildTradeKey(item));
            var marketSlug = ReadString(item, "event_slug", "slug") ?? "";
            var hasFeeSchedule = !isTaker
                                 || (feeScheduleTasks.TryGetValue(marketSlug, out var feeScheduleTask)
                                     && feeScheduleTask.Result.IsAvailable);
            var feeSchedule = isTaker && feeScheduleTasks.TryGetValue(marketSlug, out feeScheduleTask)
                ? feeScheduleTask.Result
                : FeeSchedule.NoFees;

            return new WalletTrade
            {
                MarketTitle = ReadString(item, "title", "market_title") ?? "Mercado sem título",
                Outcome = ReadString(item, "outcome") ?? "—",
                Side = ReadString(item, "side") ?? ReadString(item, "type") ?? "TRADE",
                WalletAddress = ReadString(item, "proxy_wallet", "proxyWallet", "user") ?? walletAddress,
                MarketImageUrl = ReadString(item, "icon", "image") ?? "",
                MarketSlug = marketSlug,
                Price = ReadDecimal(item, "price"),
                Size = ReadDecimal(item, "size"),
                UsdcSize = ReadTradeValue(item),
                IsTaker = isTaker,
                FeeScheduleAvailable = hasFeeSchedule,
                FeesEnabled = feeSchedule.FeesEnabled,
                TakerFeeRate = feeSchedule.Rate,
                FeeExponent = feeSchedule.Exponent,
                Timestamp = ReadTimestamp(item)
            };
        }).ToArray();
    }

    public async Task<IReadOnlyList<WalletPosition>> GetOpenPositionsAsync(
        string walletAddress,
        CancellationToken cancellationToken = default)
    {
        var uri = $"https://data-api.polymarket.com/v2/positions?user={Uri.EscapeDataString(walletAddress)}&status=OPEN&limit=30";
        using var document = await GetJsonAsync(uri, cancellationToken);
        return ReadData(document.RootElement)
            .Select(item => new WalletPosition
            {
                MarketTitle = ReadString(item, "title", "market_title") ?? "Mercado sem título",
                Outcome = ReadString(item, "outcome") ?? "—",
                ImageUrl = ReadString(item, "icon", "image") ?? "",
                CurrentSize = ReadDecimal(item, "current_size", "currentSize"),
                AveragePrice = ReadDecimal(item, "avg_price", "avgPrice"),
                CurrentValue = ReadDecimal(item, "current_value", "currentValue"),
                TotalPnl = ReadDecimal(item, "total_pnl", "totalPnl"),
                PercentPnl = ReadDecimal(item, "percent_pnl", "percentPnl")
            })
            .ToArray();
    }

    public async Task<WalletPerformance> GetWalletPerformanceAsync(
        string walletAddress,
        CancellationToken cancellationToken = default)
    {
        var encodedAddress = Uri.EscapeDataString(walletAddress);
        var dailyPnlTask = GetJsonAsync(
            $"https://data-api.polymarket.com/v2/user-pnl?user={encodedAddress}&interval=1d&fidelity=1h",
            cancellationToken);
        var statsTask = GetJsonAsync(
            $"https://data-api.polymarket.com/v2/user-stats?user={encodedAddress}",
            cancellationToken);
        var portfolioValueTask = GetJsonAsync(
            $"https://data-api.polymarket.com/v2/value?user={encodedAddress}",
            cancellationToken);

        await Task.WhenAll(dailyPnlTask, statsTask, portfolioValueTask);
        using var dailyPnlDocument = await dailyPnlTask;
        using var statsDocument = await statsTask;
        using var portfolioValueDocument = await portfolioValueTask;

        var dailyPnl = ReadEnvelopeData(dailyPnlDocument.RootElement);
        var dailyPoints = dailyPnl.ValueKind == JsonValueKind.Object
                          && dailyPnl.TryGetProperty("points", out var points)
                          && points.ValueKind == JsonValueKind.Array
            ? points.EnumerateArray().ToArray()
            : [];
        decimal? dailyPnlChange = null;
        if (dailyPoints.Length >= 2)
        {
            var firstValue = ReadNullableDecimal(dailyPoints[0], "economic_pnl");
            var latestValue = ReadNullableDecimal(dailyPoints[^1], "economic_pnl");
            if (firstValue.HasValue && latestValue.HasValue)
            {
                dailyPnlChange = latestValue.Value - firstValue.Value;
            }
        }

        var stats = ReadEnvelopeData(statsDocument.RootElement);
        var allTimePnl = stats.ValueKind == JsonValueKind.Object
                         && stats.TryGetProperty("all_time_pnl", out var allTime)
                         && allTime.ValueKind == JsonValueKind.Object
            ? ReadNullableDecimal(allTime, "economic_pnl")
            : null;

        var portfolioValue = ReadNullableDecimal(ReadEnvelopeData(portfolioValueDocument.RootElement), "value");
        return new WalletPerformance
        {
            DailyPnl = dailyPnlChange,
            AllTimePnl = allTimePnl,
            PortfolioValue = portfolioValue
        };
    }

    public async Task<decimal> GetWalletPusdBalanceAsync(
        string walletAddress,
        CancellationToken cancellationToken = default)
    {
        if (walletAddress.Length != 42
            || !walletAddress.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            || walletAddress.AsSpan(2).ContainsAnyExcept("0123456789abcdefABCDEF"))
        {
            throw new ArgumentException("O endereço da carteira é inválido.", nameof(walletAddress));
        }

        var call = new
        {
            jsonrpc = "2.0",
            method = "eth_call",
            @params = new object[]
            {
                new
                {
                    to = PusdTokenAddress,
                    data = $"0x70a08231{walletAddress[2..].ToLowerInvariant().PadLeft(64, '0')}"
                },
                "latest"
            },
            id = 1
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, PolygonRpcUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(call), Encoding.UTF8, "application/json")
        };
        using var response = await Http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, DocumentOptions, cancellationToken);
        var root = document.RootElement;
        if (root.TryGetProperty("error", out var error))
        {
            throw new InvalidDataException($"Falha ao consultar o saldo pUSD na Polygon: {error}");
        }

        if (!root.TryGetProperty("result", out var result)
            || result.ValueKind != JsonValueKind.String
            || result.GetString() is not { } rawBalance
            || !rawBalance.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            || rawBalance.Length <= 2)
        {
            throw new InvalidDataException("A resposta da Polygon não contém um saldo pUSD válido.");
        }

        try
        {
            var rawUnits = BigInteger.Parse(
                $"0{rawBalance[2..]}",
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture);
            return decimal.Parse(rawUnits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)
                   / 1_000_000m;
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            throw new InvalidDataException("O saldo pUSD retornado pela Polygon está fora do formato esperado.", exception);
        }
    }

    public async Task<IReadOnlyList<MarketCard>> GetTrendingMarketsAsync(
        CancellationToken cancellationToken = default)
    {
        const string uri = "https://gamma-api.polymarket.com/events?active=true&closed=false&limit=8&order=volume24hr&ascending=false";
        using var document = await GetJsonAsync(uri, cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("A resposta de mercados da Polymarket veio em um formato inesperado.");
        }

        return document.RootElement.EnumerateArray()
            .Select(item => new MarketCard
            {
                Title = ReadString(item, "title", "question") ?? "Mercado sem título",
                ImageUrl = ReadString(item, "image", "icon") ?? "",
                VolumeLabel = FormatVolume(ReadDecimal(item, "volume24hr", "volume_24hr", "volume")),
                Category = ReadString(item, "category") ?? "Em alta"
            })
            .ToArray();
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(18) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WalletCopy/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    private static async Task<JsonDocument> GetJsonAsync(string uri, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, DocumentOptions, cancellationToken);
    }

    private static async Task<FeeSchedule> GetFeeScheduleAsync(string marketSlug, CancellationToken cancellationToken)
    {
        if (FeeSchedules.TryGetValue(marketSlug, out var cachedSchedule))
        {
            return cachedSchedule;
        }

        await FeeScheduleSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (FeeSchedules.TryGetValue(marketSlug, out cachedSchedule))
            {
                return cachedSchedule;
            }

            try
            {
                var uri = $"https://gamma-api.polymarket.com/markets/slug/{Uri.EscapeDataString(marketSlug)}";
                using var document = await GetJsonAsync(uri, cancellationToken);
                var root = document.RootElement;
                var feesEnabled = ReadBoolean(root, "feesEnabled");
                var hasFeeParameters = root.TryGetProperty("feeSchedule", out var feeScheduleElement)
                    && feeScheduleElement.ValueKind == JsonValueKind.Object
                    && ReadString(feeScheduleElement, "rate") is not null
                    && ReadString(feeScheduleElement, "exponent") is not null;
                var schedule = hasFeeParameters
                    ? new FeeSchedule(
                        true,
                        feesEnabled,
                        ReadDecimal(feeScheduleElement, "rate"),
                        ReadDecimal(feeScheduleElement, "exponent"))
                    : feesEnabled
                        ? FeeSchedule.Unavailable
                        : FeeSchedule.NoFees;

                if (schedule.IsAvailable)
                {
                    FeeSchedules.TryAdd(marketSlug, schedule);
                }

                return schedule;
            }
            catch (HttpRequestException)
            {
                return FeeSchedule.Unavailable;
            }
            catch (JsonException)
            {
                return FeeSchedule.Unavailable;
            }
        }
        finally
        {
            FeeScheduleSemaphore.Release();
        }
    }

    private static IEnumerable<JsonElement> ReadData(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root.EnumerateArray().ToArray();
        }

        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Array)
        {
            return data.EnumerateArray().ToArray();
        }

        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("data", out data)
            && data.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        throw new InvalidDataException("A resposta de dados da Polymarket veio em um formato inesperado.");
    }

    private static string? ReadString(JsonElement item, params string[] names)
    {
        foreach (var name in names)
        {
            if (item.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null)
            {
                return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
            }
        }

        return null;
    }

    private static JsonElement ReadEnvelopeData(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("data", out var data)
            ? data
            : throw new InvalidDataException("A resposta da Polymarket veio sem o campo de dados esperado.");

    private static decimal? ReadNullableDecimal(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value)
            || value.ValueKind == JsonValueKind.Null
            || value.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        return decimal.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static bool ReadBoolean(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value)
        && (value.ValueKind == JsonValueKind.True
            || (value.ValueKind == JsonValueKind.String
                && bool.TryParse(value.GetString(), out var parsed)
                && parsed));

    private static decimal ReadDecimal(JsonElement item, params string[] names)
    {
        var text = ReadString(item, names);
        return decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0m;
    }

    private static decimal ReadTradeValue(JsonElement item)
    {
        var usdcSize = ReadDecimal(item, "usdc_size", "usdcSize", "amount");
        return usdcSize != 0m
            ? usdcSize
            : ReadDecimal(item, "size") * ReadDecimal(item, "price");
    }

    private static string BuildTradeKey(JsonElement item) => string.Join(
        "|",
        ReadString(item, "transaction_hash") ?? "",
        ReadString(item, "condition_id") ?? "",
        ReadString(item, "token_id") ?? "",
        ReadString(item, "side") ?? "",
        ReadString(item, "timestamp") ?? "",
        ReadDecimal(item, "size").ToString(CultureInfo.InvariantCulture),
        ReadDecimal(item, "price").ToString(CultureInfo.InvariantCulture));

    private static DateTime ReadTimestamp(JsonElement item)
    {
        var raw = ReadDecimal(item, "timestamp");
        if (raw > 0)
        {
            var milliseconds = raw > 10_000_000_000m ? (long)raw : (long)(raw * 1000m);
            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime;
        }

        var text = ReadString(item, "timestamp");
        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.ToUniversalTime()
            : DateTime.UtcNow;
    }

    private static string FormatVolume(decimal volume)
    {
        if (volume >= 1_000_000m)
        {
            return $"${volume / 1_000_000m:N1}M vol. 24h";
        }

        return volume >= 1_000m ? $"${volume / 1_000m:N0}k vol. 24h" : $"${volume:N0} vol. 24h";
    }

    private readonly record struct FeeSchedule(bool IsAvailable, bool FeesEnabled, decimal Rate, decimal Exponent)
    {
        public static FeeSchedule NoFees => new(true, false, 0m, 1m);
        public static FeeSchedule Unavailable => new(false, false, 0m, 1m);
    }
}
