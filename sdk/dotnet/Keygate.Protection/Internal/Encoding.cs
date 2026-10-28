using System;
using System.Text;

namespace Keygate.Protection.Internal
{
    /// <summary>
    /// base64url helpers. Keygate emits unpadded base64url (Go's
    /// base64.RawURLEncoding) but accepts padded standard base64 on input in
    /// a few places, so decoding is deliberately lenient.
    /// </summary>
    internal static class Base64Url
    {
        public static string Encode(byte[] bytes)
        {
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        public static byte[] Decode(string value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            string s = value.Trim().Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 0:
                    break;
                case 2:
                    s += "==";
                    break;
                case 3:
                    s += "=";
                    break;
                default:
                    throw new FormatException("Invalid base64url length.");
            }
            return Convert.FromBase64String(s);
        }

        /// <summary>
        /// Decodes the encodings accepted by the Go server: raw-url, url,
        /// standard and raw-standard base64.
        /// </summary>
        public static bool TryDecodeFlexible(string value, out byte[] bytes)
        {
            bytes = Array.Empty<byte>();
            if (string.IsNullOrWhiteSpace(value)) return false;
            try
            {
                bytes = Decode(value);
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }
    }

    internal static class Hex
    {
        public static string Encode(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        public static bool TryDecode(string value, out byte[] bytes)
        {
            bytes = Array.Empty<byte>();
            if (value == null || value.Length % 2 != 0) return false;
            var result = new byte[value.Length / 2];
            for (int i = 0; i < result.Length; i++)
            {
                int hi = HexValue(value[i * 2]);
                int lo = HexValue(value[i * 2 + 1]);
                if (hi < 0 || lo < 0) return false;
                result[i] = (byte)((hi << 4) | lo);
            }
            bytes = result;
            return true;
        }

        private static int HexValue(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }
    }
}
