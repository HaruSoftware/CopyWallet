using System.IO;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Numerics;
using System.Text;
using System.Net.WebSockets;
using WalletCopy.Models;

namespace WalletCopy.Services;

public sealed class PolymarketApiClient
{
    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly TimeSpan MarketCacheDuration = TimeSpan.FromMinutes(1);
    private const string PolygonRpcUrl = "https://polygon-bor-rpc.publicnode.com";
    private const string PusdTokenAddress = "0xC011a7E12a19f7B1f670d46F03B03f3342E82DFB";
    private static readonly ConcurrentDictionary<string, MarketFeeSchedule> FeeSchedules = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim FeeScheduleSemaphore = new(4);
    private static readonly Uri MarketWebSocketUri = new("wss://ws-subscriptions-clob.polymarket.com/ws/market");
    private readonly SemaphoreSlim _marketCacheSemaphore = new(1, 1);
    private IReadOnlyList<MarketCard>? _marketCache;
    private DateTime _marketCacheExpiresAt;
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
        var imageSlugs = tradeItems
            .Select(item => ReadString(item, "event_slug", "slug"))
            .Where(slug => !string.IsNullOrWhiteSpace(slug))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var imageTasks = imageSlugs.ToDictionary(
            slug => slug!,
            slug => GetMarketImageAsync(slug!, cancellationToken),
            StringComparer.OrdinalIgnoreCase);
        await Task.WhenAll(imageTasks.Values);

        return tradeItems.Select(item =>
        {
            var isTaker = takerTradeKeys.Contains(BuildTradeKey(item));
            var marketSlug = ReadString(item, "event_slug", "slug") ?? "";
            var hasFeeSchedule = !isTaker
                                 || (feeScheduleTasks.TryGetValue(marketSlug, out var feeScheduleTask)
                                     && feeScheduleTask.Result.IsAvailable);
            var feeSchedule = isTaker && feeScheduleTasks.TryGetValue(marketSlug, out feeScheduleTask)
                ? feeScheduleTask.Result
                : MarketFeeSchedule.NoFees;
            var imageUrl = ReadString(
                item,
                "icon",
                "image",
                "market_icon",
                "market_image",
                "event_icon",
                "event_image");
            imageUrl ??= ReadNestedImageUrl(item, "market", "event", "metadata");
            if (string.IsNullOrWhiteSpace(imageUrl)
                && imageTasks.TryGetValue(marketSlug, out var imageTask))
            {
                imageUrl = imageTask.Result;
            }

            return new WalletTrade
            {
                MarketTitle = ReadString(item, "title", "market_title") ?? "Mercado sem título",
                Outcome = ReadString(item, "outcome") ?? "—",
                Side = ReadString(item, "side") ?? ReadString(item, "type") ?? "TRADE",
                WalletAddress = ReadString(item, "proxy_wallet", "proxyWallet", "user") ?? walletAddress,
                MarketImageUrl = imageUrl ?? "",
                MarketSlug = marketSlug,
                TokenId = ReadString(item, "token_id", "asset_id", "assetId") ?? "",
                ConditionId = ReadString(item, "condition_id", "conditionId") ?? "",
                TransactionHash = ReadString(item, "transaction_hash", "transactionHash") ?? "",
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
                ImageUrl = ReadString(
                    item,
                    "icon",
                    "image",
                    "market_icon",
                    "market_image",
                    "event_icon",
                    "event_image") ?? "",
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
        if (_marketCache is not null && DateTime.UtcNow < _marketCacheExpiresAt)
        {
            return _marketCache;
        }

        await _marketCacheSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (_marketCache is not null && DateTime.UtcNow < _marketCacheExpiresAt)
            {
                return _marketCache;
            }

            const string featuredUri = "https://gamma-api.polymarket.com/events?active=true&closed=false&limit=100&order=volume24hr&ascending=false";
            var now = DateTime.UtcNow;
            const string cryptoUri = "https://gamma-api.polymarket.com/events?active=true&closed=false&tag_slug=crypto&limit=100&order=endDate&ascending=false";
            var featuredTask = GetJsonAsync(featuredUri, cancellationToken);
            var cryptoTask = GetJsonAsync(cryptoUri, cancellationToken);
            var fastCryptoTask = FetchDeterministicFastCryptoMarketsAsync(now, cancellationToken);
            await Task.WhenAll(featuredTask, cryptoTask, fastCryptoTask);
            using var featuredDocument = await featuredTask;
            using var quickDocument = await cryptoTask;
            var fastCryptoMarkets = await fastCryptoTask;
            if (featuredDocument.RootElement.ValueKind != JsonValueKind.Array
                || quickDocument.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("A resposta de mercados da Polymarket veio em um formato inesperado.");
            }

            var rawMarkets = new List<MarketCard>();
            if (featuredDocument.RootElement.ValueKind == JsonValueKind.Array)
            {
                rawMarkets.AddRange(featuredDocument.RootElement.EnumerateArray().SelectMany(ReadEventMarkets));
            }
            if (quickDocument.RootElement.ValueKind == JsonValueKind.Array)
            {
                rawMarkets.AddRange(quickDocument.RootElement.EnumerateArray().SelectMany(ReadEventMarkets));
            }
            rawMarkets.AddRange(fastCryptoMarkets);

            var validMarkets = rawMarkets
                .Where(m => m.IsActive
                            && !m.IsClosed
                            && !string.IsNullOrWhiteSpace(m.ConditionId)
                            && (!m.EndDate.HasValue || m.EndDate.Value > now.AddSeconds(-30)))
                .GroupBy(m => m.ConditionId, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            var currentFastMarkets = validMarkets
                .Where(m => !string.IsNullOrEmpty(m.TimeframeLabel))
                .GroupBy(m => $"{m.Asset}_{m.TimeframeLabel}", StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderBy(m => m.EndDate ?? DateTime.MaxValue).First())
                .OrderBy(m => m.TimeframeLabel switch { "5m" => 1, "15m" => 2, "1h" => 3, "4h" => 4, _ => 5 })
                .ThenBy(m => m.Asset)
                .ToList();

            var otherMarkets = validMarkets
                .Where(m => string.IsNullOrEmpty(m.TimeframeLabel))
                .OrderByDescending(m => m.Volume24h)
                .Take(30)
                .ToList();

            var markets = currentFastMarkets.Concat(otherMarkets)









                .ToArray();
            _marketCache = markets;
            _marketCacheExpiresAt = DateTime.UtcNow + TimeSpan.FromSeconds(25);
            return _marketCache;
        }
        finally
        {
            _marketCacheSemaphore.Release();
        }
    }

    public async Task<List<MarketOutcome>> GetMarketOutcomesByConditionAsync(
        string conditionId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var uri = $"https://gamma-api.polymarket.com/markets?condition_id={Uri.EscapeDataString(conditionId)}";
            using var document = await GetJsonAsync(uri, cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }
            var marketElement = document.RootElement.EnumerateArray().FirstOrDefault();
            if (marketElement.ValueKind != JsonValueKind.Object)
            {
                return [];
            }
            return ReadMarketOutcomes(marketElement);
        }
        catch
        {
            return [];
        }
    }

    public async Task<MarketOrderBook> GetOrderBookAsync(
        string tokenId,
        CancellationToken cancellationToken = default)
    {
        using var document = await GetJsonAsync(
            $"https://clob.polymarket.com/book?token_id={Uri.EscapeDataString(tokenId)}",
            cancellationToken);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("O book público da Polymarket veio em um formato inesperado.");
        }

        return new MarketOrderBook
        {
            Bids = ReadOrderBookLevels(root, "bids"),
            Asks = ReadOrderBookLevels(root, "asks")
        };
    }

    public Task<MarketFeeSchedule> GetMarketFeeScheduleAsync(
        string marketSlug,
        CancellationToken cancellationToken = default) =>
        GetFeeScheduleAsync(marketSlug, cancellationToken);

    public async Task StreamOrderBooksAsync(
        IEnumerable<string> assetIds,
        Func<string, MarketOrderBook, Task> onBookUpdated,
        Action<string?> onConnectionStatusChanged,
        CancellationToken cancellationToken = default)
    {
        var assets = assetIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (assets.Length == 0)
        {
            throw new ArgumentException("Ao menos um outcome é necessário para iniciar o stream.", nameof(assetIds));
        }

        var books = assets.ToDictionary(asset => asset, _ => new OrderBookState(), StringComparer.Ordinal);
        var reconnectDelay = TimeSpan.FromSeconds(1);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var socket = new ClientWebSocket();
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                await socket.ConnectAsync(MarketWebSocketUri, cancellationToken);
                await socket.SendAsync(
                    Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                    {
                        assets_ids = assets,
                        type = "market"
                    })),
                    WebSocketMessageType.Text,
                    true,
                    cancellationToken);
                onConnectionStatusChanged("Conectado · recebendo cotações ao vivo");
                reconnectDelay = TimeSpan.FromSeconds(1);

                using var connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var receiveTask = ReceiveMarketStreamAsync(
                    socket,
                    books,
                    onBookUpdated,
                    connectionCancellation.Token);
                var pingTask = SendMarketPingsAsync(socket, connectionCancellation.Token);
                await Task.WhenAny(receiveTask, pingTask);
                connectionCancellation.Cancel();
                try
                {
                    await Task.WhenAll(receiveTask, pingTask);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                }

                if (!cancellationToken.IsCancellationRequested)
                {
                    throw new WebSocketException("O feed de mercado foi encerrado.");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (
                exception is WebSocketException or HttpRequestException or IOException or JsonException)
            {
                onConnectionStatusChanged($"Feed desconectado · reconectando: {exception.Message}");
                try
                {
                    await Task.Delay(reconnectDelay, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                reconnectDelay = TimeSpan.FromSeconds(Math.Min(reconnectDelay.TotalSeconds * 2, 15));
            }
        }

        onConnectionStatusChanged(null);
    }

    public async Task<IReadOnlyList<MarketCard>> GetResolvedMarketsAsync(
        IEnumerable<string> conditionIds,
        CancellationToken cancellationToken = default)
    {
        var ids = conditionIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var resolvedMarkets = new List<MarketCard>();
        foreach (var batch in ids.Chunk(50))
        {
            var encodedIds = string.Join("&", batch.Select(id =>
                $"condition_ids={Uri.EscapeDataString(id)}"));
            using var document = await GetJsonAsync(
                $"https://gamma-api.polymarket.com/markets?{encodedIds}",
                cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("A resposta de resolução da Polymarket veio em um formato inesperado.");
            }

            resolvedMarkets.AddRange(document.RootElement.EnumerateArray()
                .Select(ReadResolvedMarket));
        }

        return resolvedMarkets;
    }

    private static IEnumerable<MarketCard> ReadEventMarkets(JsonElement eventItem)
    {
        if (!eventItem.TryGetProperty("markets", out var markets)
            || markets.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var eventTitle = ReadString(eventItem, "title", "question") ?? "Mercado sem título";
        var eventSlug = ReadString(eventItem, "slug", "event_slug") ?? "";
        var imageUrl = ReadString(eventItem, "image", "icon") ?? "";
        return markets.EnumerateArray()
            .Select(market => ReadMarketCard(
                market,
                eventTitle,
                eventSlug,
                imageUrl,
                ReadDecimal(eventItem, "volume24hr", "volume_24hr", "volume")));
    }

    private static MarketCard ReadResolvedMarket(JsonElement market) =>
        ReadMarketCard(
            market,
            ReadString(market, "question", "title") ?? "Mercado sem título",
            ReadString(market, "slug") ?? "",
            ReadString(market, "image", "icon") ?? "",
            ReadDecimal(market, "volume24hr", "volume_24hr", "volume"));

    private static MarketCard ReadMarketCard(
        JsonElement market,
        string eventTitle,
        string eventSlug,
        string eventImageUrl,
        decimal eventVolume)
    {
        var slug = ReadString(market, "slug") ?? eventSlug;
        var volume = ReadDecimal(market, "volume24hr", "volume_24hr", "volume");
        if (volume == 0m)
        {
            volume = eventVolume;
        }

        var conditionId = ReadString(market, "conditionId", "condition_id") ?? "";
        var title = ReadString(market, "question", "title") ?? eventTitle;
        var timeframe = GetTimeframeLabel(slug, title);
        var asset = GetAssetFromSlugOrTitle(slug, title);
        return new MarketCard
        {
            MarketId = conditionId,
            ConditionId = conditionId,
            EventTitle = eventTitle,
            Slug = slug,
            Title = title,
            Asset = asset,
            ImageUrl = ReadString(market, "image", "icon") ?? eventImageUrl,
            Volume24h = volume,
            VolumeLabel = FormatVolume(volume),
            Category = timeframe.Length > 0 ? $"Cripto · {timeframe}" : "Em alta",
            TimeframeLabel = timeframe,
            EndDate = ReadDateTime(market, "endDate", "endDateIso"),
            IsActive = ReadBoolean(market, "active"),
            IsClosed = ReadBoolean(market, "closed"),
            Outcomes = ReadMarketOutcomes(market)
        };
    }

    private static List<MarketOutcome> ReadMarketOutcomes(JsonElement market)
    {
        var names = ReadStringArray(market, "outcomes");
        var tokenIds = ReadStringArray(market, "clobTokenIds", "token_ids");
        var prices = ReadDecimalArray(market, "outcomePrices", "outcome_prices");
        var outcomes = new List<MarketOutcome>(names.Count);
        for (var index = 0; index < names.Count; index++)
        {
            outcomes.Add(new MarketOutcome
            {
                Name = names[index],
                TokenId = index < tokenIds.Count ? tokenIds[index] : "",
                Price = index < prices.Count ? prices[index] : 0m,
                BestBid = ReadDecimal(market, "bestBid", "best_bid"),
                BestAsk = ReadDecimal(market, "bestAsk", "best_ask")
            });
        }

        return outcomes;
    }

    private static List<string> ReadStringArray(JsonElement item, params string[] names)
    {
        var value = ReadArrayValue(item, names);
        if (value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Where(element => element.ValueKind != JsonValueKind.Null)
            .Select(element => element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : element.ToString())
            .ToList();
    }

    private static List<decimal> ReadDecimalArray(JsonElement item, params string[] names)
    {
        var value = ReadArrayValue(item, names);
        if (value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Select(element => decimal.TryParse(
                element.ToString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed) ? parsed : 0m)
            .ToList();
    }

    private static JsonElement ReadArrayValue(JsonElement item, IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            if (!item.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Array)
            {
                return value;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                using var document = JsonDocument.Parse(value.GetString() ?? "[]");
                if (document.RootElement.ValueKind == JsonValueKind.Array)
                {
                    return document.RootElement.Clone();
                }
            }
        }

        return default;
    }

    private static List<OrderBookLevel> ReadOrderBookLevels(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var levels)
            || levels.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"O book público não contém uma lista válida de {propertyName}.");
        }

        return levels.EnumerateArray()
            .Select(level => new OrderBookLevel
            {
                Price = ReadDecimal(level, "price"),
                Shares = ReadDecimal(level, "size")
            })
            .Where(level => level.Price > 0m && level.Shares > 0m)
            .ToList();
    }

    private static async Task ReceiveMarketStreamAsync(
        ClientWebSocket socket,
        IReadOnlyDictionary<string, OrderBookState> books,
        Func<string, MarketOrderBook, Task> onBookUpdated,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[32 * 1024];
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                if (result.MessageType != WebSocketMessageType.Text
                    || message.Length + result.Count > 1024 * 1024)
                {
                    throw new WebSocketException("A mensagem recebida do feed excede o formato esperado.");
                }

                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            if (text.Equals("PONG", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var document = JsonDocument.Parse(text, DocumentOptions);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                {
                    await ProcessMarketStreamEventAsync(item, books, onBookUpdated);
                }
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                await ProcessMarketStreamEventAsync(root, books, onBookUpdated);
            }
        }
    }

    private static async Task ProcessMarketStreamEventAsync(
        JsonElement message,
        IReadOnlyDictionary<string, OrderBookState> books,
        Func<string, MarketOrderBook, Task> onBookUpdated)
    {
        var payload = message.TryGetProperty("payload", out var eventPayload)
                      && eventPayload.ValueKind == JsonValueKind.Object
            ? eventPayload
            : message;
        var eventType = ReadString(message, "event_type", "type") ?? "";
        if (eventType.Equals("book", StringComparison.OrdinalIgnoreCase))
        {
            var assetId = ReadString(payload, "asset_id", "assetId") ?? "";
            if (books.TryGetValue(assetId, out var book))
            {
                book.Replace(payload);
                await onBookUpdated(assetId, book.ToMarketOrderBook());
            }

            return;
        }

        if (eventType.Equals("price_change", StringComparison.OrdinalIgnoreCase)
            && payload.TryGetProperty("price_changes", out var priceChanges)
            && priceChanges.ValueKind == JsonValueKind.Array)
        {
            foreach (var change in priceChanges.EnumerateArray())
            {
                var assetId = ReadString(change, "asset_id", "assetId") ?? "";
                if (!books.TryGetValue(assetId, out var book))
                {
                    continue;
                }

                book.Apply(change);
                await onBookUpdated(assetId, book.ToMarketOrderBook());
            }

            return;
        }

        if (eventType.Equals("best_bid_ask", StringComparison.OrdinalIgnoreCase))
        {
            var assetId = ReadString(payload, "asset_id", "assetId") ?? "";
            if (books.TryGetValue(assetId, out var book))
            {
                book.SetBestPrices(
                    ReadNullableDecimal(payload, "best_bid") ?? ReadNullableDecimal(payload, "bestBid"),
                    ReadNullableDecimal(payload, "best_ask") ?? ReadNullableDecimal(payload, "bestAsk"));
                await onBookUpdated(assetId, book.ToMarketOrderBook());
            }
        }
    }

    private static async Task SendMarketPingsAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        var ping = Encoding.UTF8.GetBytes("PING");
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            await socket.SendAsync(ping, WebSocketMessageType.Text, true, cancellationToken);
        }
    }

    private static string GetTimeframeLabel(string slug, string title)
    {
        var s = (slug + " " + title).ToLowerInvariant();
        if (s.Contains("-updown-5m-") || s.Contains("-5m-") || s.Contains(" 5m") || s.Contains(" 5 min")) return "5m";
        if (s.Contains("-updown-15m-") || s.Contains("-15m-") || s.Contains(" 15m") || s.Contains(" 15 min")) return "15m";
        if (s.Contains("-updown-1h-") || s.Contains("-updown-1hr-") || s.Contains("-1h-") || s.Contains("-1hr-") || s.Contains(" 1h") || s.Contains(" 1 hour") || s.Contains(" 1 hr")) return "1h";
        if (s.Contains("-updown-4h-") || s.Contains("-updown-4hr-") || s.Contains("-4h-") || s.Contains("-4hr-") || s.Contains(" 4h") || s.Contains(" 4 hour") || s.Contains(" 4 hr")) return "4h";
        return "";
    }

    private static string GetAssetFromSlugOrTitle(string slug, string title)
    {
        var s = (slug + " " + title).ToLowerInvariant();
        if (s.Contains("btc") || s.Contains("bitcoin")) return "BTC";
        if (s.Contains("eth") || s.Contains("ethereum")) return "ETH";
        if (s.Contains("sol") || s.Contains("solana")) return "SOL";
        if (s.Contains("xrp") || s.Contains("ripple")) return "XRP";
        if (s.Contains("doge") || s.Contains("dogecoin")) return "DOGE";
        if (s.Contains("bnb") || s.Contains("binance")) return "BNB";
        if (s.Contains("hype") || s.Contains("hyperliquid")) return "HYPE";
        return "CRYPTO";
    }

    private async Task<List<MarketCard>> FetchDeterministicFastCryptoMarketsAsync(
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        var assets = new[] { "btc", "eth", "sol", "xrp", "doge" };
        var intervals = new[] { ("5m", 5), ("15m", 15), ("1h", 60), ("4h", 240) };
        var tasks = new List<Task<MarketCard?>>();

        foreach (var asset in assets)
        {
            foreach (var (label, minutes) in intervals)
            {
                var totalMinutes = (int)(utcNow.TimeOfDay.TotalMinutes);
                var roundedMinutes = (totalMinutes / minutes) * minutes;
                var windowStart = utcNow.Date.AddMinutes(roundedMinutes);
                var ts = new DateTimeOffset(windowStart).ToUnixTimeSeconds();
                var slug = $"{asset}-updown-{label}-{ts}";

                tasks.Add(FetchMarketBySlugAsync(slug, cancellationToken));
            }
        }

        var results = await Task.WhenAll(tasks);
        return results.Where(m => m is not null).Select(m => m!).ToList();
    }

    private async Task<MarketCard?> FetchMarketBySlugAsync(string slug, CancellationToken cancellationToken)
    {
        try
        {
            using var doc = await GetJsonAsync(
                $"https://gamma-api.polymarket.com/events/slug/{Uri.EscapeDataString(slug)}",
                cancellationToken);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return ReadEventMarkets(doc.RootElement).FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string GetTimeframeLabelLegacy(string slug)
    {
        foreach (var timeframe in new[] { "5m", "15m", "1h", "4h" })
        {
            if (slug.Contains($"-updown-{timeframe}-", StringComparison.OrdinalIgnoreCase))
            {
                return timeframe;
            }
        }

        return "";
    }

    private static DateTime? ReadDateTime(JsonElement item, params string[] names)
    {
        var text = ReadString(item, names);
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? date.UtcDateTime
            : null;
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

    private static async Task<MarketFeeSchedule> GetFeeScheduleAsync(string marketSlug, CancellationToken cancellationToken)
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
                var hasFeeFlag = root.TryGetProperty("feesEnabled", out var feeFlagElement)
                                 && (feeFlagElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                                     || (feeFlagElement.ValueKind == JsonValueKind.String
                                         && bool.TryParse(feeFlagElement.GetString(), out _)));
                var feesEnabled = ReadBoolean(root, "feesEnabled");
                var rateText = root.TryGetProperty("feeSchedule", out var scheduleElement)
                               && scheduleElement.ValueKind == JsonValueKind.Object
                    ? ReadString(scheduleElement, "rate")
                    : null;
                var exponentText = root.TryGetProperty("feeSchedule", out scheduleElement)
                                   && scheduleElement.ValueKind == JsonValueKind.Object
                    ? ReadString(scheduleElement, "exponent")
                    : null;
                var rate = 0m;
                var exponent = 0m;
                var hasFeeParameters = root.TryGetProperty("feeSchedule", out var feeScheduleElement)
                    && feeScheduleElement.ValueKind == JsonValueKind.Object
                    && decimal.TryParse(rateText, NumberStyles.Float, CultureInfo.InvariantCulture, out rate)
                    && decimal.TryParse(exponentText, NumberStyles.Float, CultureInfo.InvariantCulture, out exponent)
                    && rate >= 0m
                    && exponent >= 0m;
                var schedule = hasFeeFlag && !feesEnabled
                    ? MarketFeeSchedule.NoFees
                    : hasFeeFlag && hasFeeParameters
                    ? new MarketFeeSchedule(
                        true,
                        feesEnabled,
                        rate,
                        exponent)
                    : MarketFeeSchedule.Unavailable(
                        hasFeeFlag
                            ? "Parâmetros de taxa ausentes ou inválidos na resposta da Gamma API."
                            : "A resposta da Gamma API não informou se o mercado cobra taxa.");

                if (schedule.IsAvailable)
                {
                    FeeSchedules.TryAdd(marketSlug, schedule);
                }

                return schedule;
            }
            catch (HttpRequestException exception)
            {
                return MarketFeeSchedule.Unavailable(exception.Message);
            }
            catch (JsonException exception)
            {
                return MarketFeeSchedule.Unavailable(exception.Message);
            }
        }
        finally
        {
            FeeScheduleSemaphore.Release();
        }
    }

    private static async Task<string> GetMarketImageAsync(
            string marketSlug,
            CancellationToken cancellationToken)
        {
            try
            {
                using var document = await GetJsonAsync(
                    $"https://gamma-api.polymarket.com/events/slug/{Uri.EscapeDataString(marketSlug)}",
                    cancellationToken);
                var root = document.RootElement;
                var image = ReadString(root, "image", "icon", "market_image", "event_image");
                if (!string.IsNullOrWhiteSpace(image))
                {
                    return image;
                }

                if (root.TryGetProperty("markets", out var markets)
                    && markets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var market in markets.EnumerateArray())
                    {
                        image = ReadString(market, "image", "icon");
                        if (!string.IsNullOrWhiteSpace(image))
                        {
                            return image;
                        }
                    }

                }
            }
            catch (Exception exception) when (
                exception is HttpRequestException
                or TaskCanceledException
                or JsonException
                or InvalidDataException)
            {
            }
            return "";
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

    private static string? ReadNestedImageUrl(JsonElement item, params string[] objectNames)
    {
        foreach (var objectName in objectNames)
        {
            if (item.TryGetProperty(objectName, out var nested)
                && nested.ValueKind == JsonValueKind.Object)
            {
                var image = ReadString(nested, "image", "icon", "url", "image_url", "imageUrl");
                if (!string.IsNullOrWhiteSpace(image))
                {
                    return image;
                }
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

    private sealed class OrderBookState
    {
        private readonly Dictionary<decimal, decimal> _bids = [];
        private readonly Dictionary<decimal, decimal> _asks = [];
        private decimal? _bestBidOverride;
        private decimal? _bestAskOverride;

        public void Replace(JsonElement book)
        {
            _bids.Clear();
            _asks.Clear();
            ReadLevels(book, "bids", _bids);
            ReadLevels(book, "asks", _asks);
            _bestBidOverride = null;
            _bestAskOverride = null;
        }

        public void Apply(JsonElement change)
        {
            var price = ReadDecimal(change, "price");
            var size = ReadDecimal(change, "size");
            var side = ReadString(change, "side") ?? "";
            var levels = side.Equals("BUY", StringComparison.OrdinalIgnoreCase)
                ? _bids
                : _asks;
            if (price > 0m)
            {
                if (size <= 0m)
                {
                    levels.Remove(price);
                }
                else
                {
                    levels[price] = size;
                }
            }

            _bestBidOverride = ReadNullableDecimal(change, "best_bid")
                               ?? ReadNullableDecimal(change, "bestBid");
            _bestAskOverride = ReadNullableDecimal(change, "best_ask")
                               ?? ReadNullableDecimal(change, "bestAsk");
        }

        public void SetBestPrices(decimal? bestBid, decimal? bestAsk)
        {
            _bestBidOverride = bestBid;
            _bestAskOverride = bestAsk;
        }

        public MarketOrderBook ToMarketOrderBook() => new()
        {
            Bids = _bids
                .OrderByDescending(level => level.Key)
                .Select(level => new OrderBookLevel { Price = level.Key, Shares = level.Value })
                .ToList(),
            Asks = _asks
                .OrderBy(level => level.Key)
                .Select(level => new OrderBookLevel { Price = level.Key, Shares = level.Value })
                .ToList(),
            BestBidOverride = _bestBidOverride,
            BestAskOverride = _bestAskOverride
        };

        private static void ReadLevels(
            JsonElement book,
            string propertyName,
            IDictionary<decimal, decimal> target)
        {
            if (!book.TryGetProperty(propertyName, out var levels)
                || levels.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var level in levels.EnumerateArray())
            {
                var price = ReadDecimal(level, "price");
                var size = ReadDecimal(level, "size");
                if (price > 0m && size > 0m)
                {
                    target[price] = size;
                }
            }
        }
    }
}
