using System;
using System.Security.Cryptography;
using System.Text;

namespace SsmsDataAnalyzer.Core.History
{
    /// <summary>
    /// DPAPI (or any other OS key wrap) lives outside Core -- this is the seam. The Vsix
    /// implementation wraps <c>System.Security.Cryptography.ProtectedData</c>; tests use a
    /// trivial fake. <see cref="Unprotect"/> is documented to throw on failure (wrong user,
    /// corrupted blob, etc.) so <see cref="HistoryKeyStore"/> can treat that as "no usable key".
    /// </summary>
    public interface IKeyProtector
    {
        byte[] Protect(byte[] plaintextKey);

        byte[] Unprotect(byte[] protectedKey);
    }

    /// <summary>
    /// AES-256-CBC + HMAC-SHA256, encrypt-then-MAC, one line at a time. Every line gets a fresh
    /// random IV. A line is <c>"v1:" + base64(IV | ciphertext | MAC)</c>; the MAC covers
    /// <c>IV | ciphertext</c> and is checked in constant time before any decryption is
    /// attempted, so a tampered or corrupted line is rejected without ever running through AES.
    /// <see cref="TryDecryptLine"/> never throws -- a bad line is simply skipped by callers.
    /// </summary>
    public sealed class HistoryCipher
    {
        public const string LinePrefix = "v1:";

        private const int KeyLength = 64;
        private const int AesKeyLength = 32;
        private const int HmacKeyLength = 32;
        private const int IvLength = 16;
        private const int MacLength = 32; // HMAC-SHA256 output

        private readonly byte[] _aesKey;
        private readonly byte[] _hmacKey;

        public HistoryCipher(byte[] key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (key.Length != KeyLength)
            {
                throw new ArgumentException($"Key must be exactly {KeyLength} bytes (32 AES + 32 HMAC).", nameof(key));
            }

            _aesKey = new byte[AesKeyLength];
            _hmacKey = new byte[HmacKeyLength];
            Buffer.BlockCopy(key, 0, _aesKey, 0, AesKeyLength);
            Buffer.BlockCopy(key, AesKeyLength, _hmacKey, 0, HmacKeyLength);
        }

        public static byte[] NewKey()
        {
            var key = new byte[KeyLength];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(key);
            }
            return key;
        }

        public string EncryptLine(string plaintext)
        {
            if (plaintext == null) throw new ArgumentNullException(nameof(plaintext));

            byte[] iv = new byte[IvLength];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(iv);
            }

            byte[] cipherBytes;
            using (var aes = CreateAes())
            {
                aes.IV = iv;
                using (var encryptor = aes.CreateEncryptor(_aesKey, iv))
                {
                    byte[] plainBytes = Encoding.UTF8.GetBytes(plaintext);
                    cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
                }
            }

            byte[] mac = ComputeMac(iv, cipherBytes);

            byte[] payload = new byte[iv.Length + cipherBytes.Length + mac.Length];
            Buffer.BlockCopy(iv, 0, payload, 0, iv.Length);
            Buffer.BlockCopy(cipherBytes, 0, payload, iv.Length, cipherBytes.Length);
            Buffer.BlockCopy(mac, 0, payload, iv.Length + cipherBytes.Length, mac.Length);

            return LinePrefix + Convert.ToBase64String(payload);
        }

        public bool TryDecryptLine(string line, out string plaintext)
        {
            plaintext = null;
            try
            {
                if (line == null || !line.StartsWith(LinePrefix, StringComparison.Ordinal)) return false;

                string base64 = line.Substring(LinePrefix.Length);
                byte[] payload;
                try
                {
                    payload = Convert.FromBase64String(base64);
                }
                catch (FormatException)
                {
                    return false;
                }

                if (payload.Length < IvLength + MacLength) return false;

                int cipherLength = payload.Length - IvLength - MacLength;
                byte[] iv = new byte[IvLength];
                byte[] cipherBytes = new byte[cipherLength];
                byte[] mac = new byte[MacLength];
                Buffer.BlockCopy(payload, 0, iv, 0, IvLength);
                Buffer.BlockCopy(payload, IvLength, cipherBytes, 0, cipherLength);
                Buffer.BlockCopy(payload, IvLength + cipherLength, mac, 0, MacLength);

                byte[] expectedMac = ComputeMac(iv, cipherBytes);
                if (!ConstantTimeEquals(expectedMac, mac)) return false;

                using (var aes = CreateAes())
                {
                    using (var decryptor = aes.CreateDecryptor(_aesKey, iv))
                    {
                        byte[] plainBytes;
                        try
                        {
                            plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
                        }
                        catch (CryptographicException)
                        {
                            return false;
                        }
                        plaintext = Encoding.UTF8.GetString(plainBytes);
                        return true;
                    }
                }
            }
            catch
            {
                plaintext = null;
                return false;
            }
        }

        private byte[] ComputeMac(byte[] iv, byte[] cipherBytes)
        {
            using (var hmac = new HMACSHA256(_hmacKey))
            {
                hmac.TransformBlock(iv, 0, iv.Length, null, 0);
                hmac.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
                return hmac.Hash;
            }
        }

        private static Aes CreateAes()
        {
            var aes = Aes.Create();
            aes.KeySize = 256;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            return aes;
        }

        private static bool ConstantTimeEquals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++)
            {
                diff |= a[i] ^ b[i];
            }
            return diff == 0;
        }
    }
}
