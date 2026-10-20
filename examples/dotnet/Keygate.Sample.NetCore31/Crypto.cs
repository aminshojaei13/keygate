using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using Org.BouncyCastle.Math.EC.Rfc8032;

namespace Keygate.Sample
{
public static class Base64Url
{
    // Keygate uses base64url WITHOUT padding (Go's base64.RawURLEncoding).
    public static byte[] Decode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 0: break;
            case 2: s += "=="; break;
            case 3: s += "="; break;
            default: throw new FormatException("Invalid base64url length.");
        }
        return Convert.FromBase64String(s);
    }

    public static string Encode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public static class Hex
{
    public static byte[] Decode(string hex)
    {
        if (hex.Length % 2 != 0) throw new FormatException("Hex string must have an even length.");
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }
}

/// <summary>
/// Ed25519 verification via BouncyCastle (pure managed). Keygate signs the
/// base64url payload text with Ed25519; verification uses the raw 32-byte
/// public key served by GET /api/v1/license/pubkey.
/// </summary>
public static class Ed25519Verifier
{
    public static bool Verify(byte[] publicKey, byte[] message, byte[] signature)
    {
        if (publicKey.Length != Ed25519.PublicKeySize) return false; // 32 bytes
        if (signature.Length != Ed25519.SignatureSize) return false; // 64 bytes
        // BouncyCastle's Ed25519 public key is always 32 bytes, so the public
        // key length is implicit in the API (no separate length argument).
        return Ed25519.Verify(signature, 0, publicKey, 0, message, 0, message.Length);
    }
}

}
