using System.Globalization;
using System.Net.Http;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WalletCopy.Models;

namespace WalletCopy.Services;

public sealed class PolymarketClobClient
{
    private const string ClobBaseUrl = "https://clob.polymarket.com";
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    public async Task<ClobApiCredentials> CreateOrDeriveApiCredentialsAsync(
        string privateKeyHex,
        CancellationToken cancellationToken = default)
    {
        var address = PolymarketClobSigner.GetAddressFromPrivateKey(privateKeyHex);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var signature = PolymarketClobSigner.SignClobAuth(privateKeyHex, address, timestamp, 0);

        // 1. Tenta POST /auth/api-key
        try
        {
            using var createRequest = new HttpRequestMessage(HttpMethod.Post, $"{ClobBaseUrl}/auth/api-key");
            createRequest.Headers.Add("POLY_ADDRESS", address);
            createRequest.Headers.Add("POLY_SIGNATURE", signature);
            createRequest.Headers.Add("POLY_TIMESTAMP", timestamp);
            createRequest.Headers.Add("POLY_NONCE", "0");

            using var createResponse = await Http.SendAsync(createRequest, cancellationToken);
            if (createResponse.IsSuccessStatusCode)
            {
                var json = await createResponse.Content.ReadAsStringAsync(cancellationToken);
                return ParseCredentials(json);
            }
        }
        catch (HttpRequestException)
        {
            // Falha ao criar, tenta derivar
        }

        // 2. Tenta GET /auth/derive-api-key
        var deriveTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var deriveSig = PolymarketClobSigner.SignClobAuth(privateKeyHex, address, deriveTimestamp, 0);

        using var deriveRequest = new HttpRequestMessage(HttpMethod.Get, $"{ClobBaseUrl}/auth/derive-api-key");
        deriveRequest.Headers.Add("POLY_ADDRESS", address);
        deriveRequest.Headers.Add("POLY_SIGNATURE", deriveSig);
        deriveRequest.Headers.Add("POLY_TIMESTAMP", deriveTimestamp);
        deriveRequest.Headers.Add("POLY_NONCE", "0");

        using var deriveResponse = await Http.SendAsync(deriveRequest, cancellationToken);
        var deriveJson = await deriveResponse.Content.ReadAsStringAsync(cancellationToken);

        if (!deriveResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Não foi possível autenticar na Polymarket CLOB: {deriveResponse.StatusCode} · {deriveJson}");
        }

        return ParseCredentials(deriveJson);
    }

    public async Task<ClobOrderResponse> PostOrderAsync(
        TradingCredentials credentials,
        ClobOrderRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!credentials.IsConfigured)
        {
            return new ClobOrderResponse
            {
                Success = false,
                ErrorMessage = "Credenciais de trading da Polymarket não configuradas."
            };
        }

        var isBuy = request.Side.Equals("BUY", StringComparison.OrdinalIgnoreCase);
        var limitPrice = request.Price;

        if (request.MaxSlippagePercent > 0m)
        {
            if (isBuy)
            {
                limitPrice = Math.Min(0.99m, request.Price * (1m + (request.MaxSlippagePercent / 100m)));
            }
            else
            {
                limitPrice = Math.Max(0.01m, request.Price * (1m - (request.MaxSlippagePercent / 100m)));
            }
        }

        limitPrice = Math.Round(limitPrice, 4);

        BigInteger makerAmount;
        BigInteger takerAmount;
        string sideStr;
        int sideInt;

        if (isBuy)
        {
            // Maker paga USDC (preço limite * tamanho), Taker entrega Shares
            var usdcAtomic = Math.Round(limitPrice * request.Size * 1_000_000m, MidpointRounding.AwayFromZero);
            var sharesAtomic = Math.Round(request.Size * 1_000_000m, MidpointRounding.AwayFromZero);
            makerAmount = new BigInteger(usdcAtomic);
            takerAmount = new BigInteger(sharesAtomic);
            sideStr = "BUY";
            sideInt = 0;
        }
        else
        {
            // Maker entrega Shares, Taker paga USDC
            var sharesAtomic = Math.Round(request.Size * 1_000_000m, MidpointRounding.AwayFromZero);
            var usdcAtomic = Math.Round(limitPrice * request.Size * 1_000_000m, MidpointRounding.AwayFromZero);
            makerAmount = new BigInteger(sharesAtomic);
            takerAmount = new BigInteger(usdcAtomic);
            sideStr = "SELL";
            sideInt = 1;
        }

        var salt = PolymarketClobSigner.GenerateRandomSalt();
        var signatureType = (int)credentials.WalletType;

        var signature = PolymarketClobSigner.SignOrder(
            credentials.SignerPrivateKey,
            credentials.EffectiveFunderAddress,
            credentials.SignerAddress,
            request.TokenId,
            makerAmount,
            takerAmount,
            sideInt,
            signatureType,
            request.IsNegRisk,
            request.Expiration,
            0,
            0,
            salt);

        var orderPayload = new
        {
            order = new
            {
                salt = salt.ToString(),
                maker = credentials.EffectiveFunderAddress,
                signer = credentials.SignerAddress,
                taker = "0x0000000000000000000000000000000000000000",
                tokenId = request.TokenId,
                makerAmount = makerAmount.ToString(),
                takerAmount = takerAmount.ToString(),
                side = sideStr,
                expiration = request.Expiration.ToString(),
                nonce = "0",
                feeRateBps = "0",
                signatureType,
                signature
            },
            owner = credentials.ApiKey,
            orderType = request.OrderType
        };

        var bodyJson = JsonSerializer.Serialize(orderPayload);
        const string path = "/order";

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{ClobBaseUrl}{path}")
        {
            Content = new StringContent(bodyJson, Encoding.UTF8, "application/json")
        };

        ApplyL2Headers(httpRequest, "POST", path, bodyJson, credentials);

        using var httpResponse = await Http.SendAsync(httpRequest, cancellationToken);
        var responseJson = await httpResponse.Content.ReadAsStringAsync(cancellationToken);

        if (!httpResponse.IsSuccessStatusCode)
        {
            return new ClobOrderResponse
            {
                Success = false,
                ErrorMessage = $"Polymarket rejeitou a ordem ({httpResponse.StatusCode}): {ExtractErrorMessage(responseJson)}",
                RawJson = responseJson
            };
        }

        return ParseOrderResponse(responseJson, request.Size, limitPrice);
    }

    public async Task<bool> CancelOrderAsync(
        TradingCredentials credentials,
        string orderId,
        CancellationToken cancellationToken = default)
    {
        if (!credentials.IsConfigured || string.IsNullOrWhiteSpace(orderId))
        {
            return false;
        }

        var payload = JsonSerializer.Serialize(new { orderID = orderId });
        const string path = "/order";

        using var httpRequest = new HttpRequestMessage(HttpMethod.Delete, $"{ClobBaseUrl}{path}")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        ApplyL2Headers(httpRequest, "DELETE", path, payload, credentials);

        using var httpResponse = await Http.SendAsync(httpRequest, cancellationToken);
        return httpResponse.IsSuccessStatusCode;
    }

    public async Task<IReadOnlyList<ClobOpenOrder>> GetOpenOrdersAsync(
        TradingCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        if (!credentials.IsConfigured)
        {
            return [];
        }

        const string path = "/orders";
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, $"{ClobBaseUrl}{path}");
        ApplyL2Headers(httpRequest, "GET", path, null, credentials);

        using var httpResponse = await Http.SendAsync(httpRequest, cancellationToken);
        if (!httpResponse.IsSuccessStatusCode)
        {
            return [];
        }

        var json = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<ClobOpenOrder>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
            var assetId = item.TryGetProperty("asset_id", out var assetProp) ? assetProp.GetString() ?? "" : "";
            var side = item.TryGetProperty("side", out var sideProp) ? sideProp.GetString() ?? "" : "";
            var price = item.TryGetProperty("price", out var priceProp) && decimal.TryParse(priceProp.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var p) ? p : 0m;
            var size = item.TryGetProperty("original_size", out var origProp) && decimal.TryParse(origProp.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var s) ? s : 0m;
            var matched = item.TryGetProperty("size_matched", out var matchProp) && decimal.TryParse(matchProp.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var m) ? m : 0m;

            list.Add(new ClobOpenOrder
            {
                Id = id,
                AssetId = assetId,
                Side = side,
                Price = price,
                OriginalSize = size,
                SizeMatched = matched,
                CreatedAt = DateTime.UtcNow
            });
        }

        return list;
    }

    private static void ApplyL2Headers(
        HttpRequestMessage request,
        string method,
        string path,
        string? body,
        TradingCredentials credentials)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var message = timestamp + method.ToUpperInvariant() + path + (body ?? "");

        var normalizedSecret = credentials.ApiSecret.Replace('-', '+').Replace('_', '/');
        switch (normalizedSecret.Length % 4)
        {
            case 2: normalizedSecret += "=="; break;
            case 3: normalizedSecret += "="; break;
        }

        var secretBytes = Convert.FromBase64String(normalizedSecret);
        using var hmac = new HMACSHA256(secretBytes);
        var signatureBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
        var signature = Convert.ToBase64String(signatureBytes).Replace('+', '-').Replace('/', '_');

        request.Headers.Add("POLY_API_KEY", credentials.ApiKey);
        request.Headers.Add("POLY_PASSPHRASE", credentials.ApiPassphrase);
        request.Headers.Add("POLY_TIMESTAMP", timestamp);
        request.Headers.Add("POLY_ADDRESS", credentials.SignerAddress);
        request.Headers.Add("POLY_SIGNATURE", signature);
    }

    private static ClobApiCredentials ParseCredentials(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var apiKey = root.TryGetProperty("apiKey", out var k)
            ? k.GetString()
            : root.TryGetProperty("api_key", out var k2) ? k2.GetString() : "";

        var secret = root.TryGetProperty("secret", out var s) ? s.GetString() : "";
        var passphrase = root.TryGetProperty("passphrase", out var p) ? p.GetString() : "";

        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(passphrase))
        {
            throw new InvalidOperationException("Credenciais retornadas pela Polymarket estão incompletas.");
        }

        return new ClobApiCredentials
        {
            ApiKey = apiKey,
            Secret = secret,
            Passphrase = passphrase
        };
    }

    private static ClobOrderResponse ParseOrderResponse(string json, decimal requestedSize, decimal limitPrice)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var orderId = root.TryGetProperty("orderID", out var idProp)
            ? idProp.GetString() ?? ""
            : root.TryGetProperty("order_id", out var id2) ? id2.GetString() ?? "" : "";

        var status = root.TryGetProperty("status", out var sProp) ? sProp.GetString() ?? "Submitted" : "Submitted";
        var takingAmount = root.TryGetProperty("takingAmount", out var tProp) && decimal.TryParse(tProp.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var t) ? t : requestedSize;
        var makingAmount = root.TryGetProperty("makingAmount", out var mProp) && decimal.TryParse(mProp.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var m) ? m : requestedSize * limitPrice;

        return new ClobOrderResponse
        {
            Success = true,
            OrderId = orderId,
            Status = status,
            ExecutedSize = takingAmount,
            ExecutedPrice = limitPrice,
            TotalCost = makingAmount,
            RawJson = json
        };
    }

    private static string ExtractErrorMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("errorMsg", out var e1)) return e1.GetString() ?? json;
            if (doc.RootElement.TryGetProperty("message", out var e2)) return e2.GetString() ?? json;
            if (doc.RootElement.TryGetProperty("error", out var e3)) return e3.GetString() ?? json;
            return json;
        }
        catch
        {
            return json;
        }
    }
}

