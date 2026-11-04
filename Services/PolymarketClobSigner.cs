using System.Numerics;
using System.Text.Json;
using Nethereum.Signer;
using Nethereum.Signer.EIP712;

namespace WalletCopy.Services;

public static class PolymarketClobSigner
{
    public const string CtfExchangeAddress = "0x4bFb41d5B3570DeFd03C39a9A4D8dE6Bd8B8982E";
    public const string NegRiskCtfExchangeAddress = "0xC5d563A36AE78145C45a50134d48A1215220f80a";

    public static string GetAddressFromPrivateKey(string privateKeyHex)
    {
        var cleanKey = CleanPrivateKey(privateKeyHex);
        var key = new EthECKey(cleanKey);
        return key.GetPublicAddress();
    }

    public static string SignClobAuth(string privateKeyHex, string address, string timestamp, long nonce = 0)
    {
        var cleanKey = CleanPrivateKey(privateKeyHex);
        var key = new EthECKey(cleanKey);

        var typedData = new
        {
            types = new
            {
                EIP712Domain = new[]
                {
                    new { name = "name", type = "string" },
                    new { name = "version", type = "string" },
                    new { name = "chainId", type = "uint256" }
                },
                ClobAuth = new[]
                {
                    new { name = "address", type = "address" },
                    new { name = "timestamp", type = "string" },
                    new { name = "nonce", type = "uint256" },
                    new { name = "message", type = "string" }
                }
            },
            primaryType = "ClobAuth",
            domain = new
            {
                name = "ClobAuthDomain",
                version = "1",
                chainId = 137
            },
            message = new
            {
                address,
                timestamp,
                nonce = nonce.ToString(),
                message = "This message attests that I control the given wallet"
            }
        };

        var json = JsonSerializer.Serialize(typedData);
        return Eip712TypedDataSigner.Current.SignTypedDataV4(json, key);
    }

    public static string SignOrder(
        string privateKeyHex,
        string makerAddress,
        string signerAddress,
        string tokenId,
        BigInteger makerAmount,
        BigInteger takerAmount,
        int side,
        int signatureType,
        bool isNegRisk = false,
        long expiration = 0,
        long nonce = 0,
        long feeRateBps = 0,
        BigInteger? salt = null)
    {
        var cleanKey = CleanPrivateKey(privateKeyHex);
        var key = new EthECKey(cleanKey);

        var verifyingContract = isNegRisk ? NegRiskCtfExchangeAddress : CtfExchangeAddress;
        var domainName = isNegRisk ? "Polymarket Neg Risk CTF Exchange" : "Polymarket CTF Exchange";
        var randomSalt = salt ?? GenerateRandomSalt();

        var typedData = new
        {
            types = new
            {
                EIP712Domain = new[]
                {
                    new { name = "name", type = "string" },
                    new { name = "version", type = "string" },
                    new { name = "chainId", type = "uint256" },
                    new { name = "verifyingContract", type = "address" }
                },
                Order = new[]
                {
                    new { name = "salt", type = "uint256" },
                    new { name = "maker", type = "address" },
                    new { name = "signer", type = "address" },
                    new { name = "taker", type = "address" },
                    new { name = "tokenId", type = "uint256" },
                    new { name = "makerAmount", type = "uint256" },
                    new { name = "takerAmount", type = "uint256" },
                    new { name = "expiration", type = "uint256" },
                    new { name = "nonce", type = "uint256" },
                    new { name = "feeRateBps", type = "uint256" },
                    new { name = "side", type = "uint8" },
                    new { name = "signatureType", type = "uint8" }
                }
            },
            primaryType = "Order",
            domain = new
            {
                name = domainName,
                version = "1",
                chainId = 137,
                verifyingContract
            },
            message = new
            {
                salt = randomSalt.ToString(),
                maker = makerAddress,
                signer = signerAddress,
                taker = "0x0000000000000000000000000000000000000000",
                tokenId,
                makerAmount = makerAmount.ToString(),
                takerAmount = takerAmount.ToString(),
                expiration = expiration.ToString(),
                nonce = nonce.ToString(),
                feeRateBps = feeRateBps.ToString(),
                side,
                signatureType
            }
        };

        var json = JsonSerializer.Serialize(typedData);
        return Eip712TypedDataSigner.Current.SignTypedDataV4(json, key);
    }

    public static BigInteger GenerateRandomSalt()
    {
        var bytes = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        bytes[31] &= 0x7F; // ensure positive
        return new BigInteger(bytes);
    }

    private static string CleanPrivateKey(string privateKeyHex)
    {
        var clean = privateKeyHex.Trim();
        return clean.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? clean[2..]
            : clean;
    }
}
