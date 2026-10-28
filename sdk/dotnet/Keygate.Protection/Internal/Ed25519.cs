using System;
using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Keygate.Protection.Internal
{
    /// <summary>
    /// Thin Ed25519 wrapper over BouncyCastle's pure-managed implementation.
    /// The private key is a 32-byte seed, which is what Keygate stores in the
    /// local secret store; the 64-byte expanded form is derived only in memory.
    /// </summary>
    internal static class Ed25519
    {
        public const int SeedSize = 32;
        public const int PublicKeySize = 32;
        public const int SignatureSize = 64;

        public static byte[] GenerateSeed()
        {
            var seed = new byte[SeedSize];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(seed);
            }
            return seed;
        }

        public static byte[] PublicKeyFromSeed(byte[] seed)
        {
            if (seed == null || seed.Length != SeedSize)
                throw new ArgumentException("Ed25519 seed must be 32 bytes.", nameof(seed));
            var privateKey = new Ed25519PrivateKeyParameters(seed, 0);
            return privateKey.GeneratePublicKey().GetEncoded();
        }

        public static byte[] Sign(byte[] seed, byte[] message)
        {
            if (seed == null || seed.Length != SeedSize)
                throw new ArgumentException("Ed25519 seed must be 32 bytes.", nameof(seed));
            if (message == null) throw new ArgumentNullException(nameof(message));

            var privateKey = new Ed25519PrivateKeyParameters(seed, 0);
            var signer = new Ed25519Signer();
            signer.Init(true, privateKey);
            signer.BlockUpdate(message, 0, message.Length);
            return signer.GenerateSignature();
        }

        public static bool Verify(byte[] publicKey, byte[] message, byte[] signature)
        {
            if (publicKey == null || publicKey.Length != PublicKeySize) return false;
            if (signature == null || signature.Length != SignatureSize) return false;
            if (message == null) return false;

            var key = new Ed25519PublicKeyParameters(publicKey, 0);
            var verifier = new Ed25519Signer();
            verifier.Init(false, key);
            verifier.BlockUpdate(message, 0, message.Length);
            return verifier.VerifySignature(signature);
        }

        /// <summary>
        /// Parses a public key written as 64 hex characters, base64url or
        /// standard base64, matching the server's accepted encodings.
        /// </summary>
        public static bool TryParsePublicKey(string value, out byte[] publicKey)
        {
            publicKey = Array.Empty<byte>();
            if (string.IsNullOrWhiteSpace(value)) return false;

            string trimmed = value.Trim();
            if (trimmed.Length == PublicKeySize * 2 && Hex.TryDecode(trimmed, out byte[] fromHex))
            {
                publicKey = fromHex;
                return true;
            }
            if (Base64Url.TryDecodeFlexible(trimmed, out byte[] fromB64) && fromB64.Length == PublicKeySize)
            {
                publicKey = fromB64;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Constant-time comparison for equal-length byte arrays. The BCL's
        /// CryptographicOperations.FixedTimeEquals is not available on
        /// netstandard2.0, which this library still targets.
        /// </summary>
        public static bool FixedTimeEquals(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            int diff = 0;
            for (int i = 0; i < left.Length; i++) diff |= left[i] ^ right[i];
            return diff == 0;
        }
    }
}
