using System;
using System.Text;
using SsmsDataAnalyzer.Core.History;
using Xunit;

namespace SsmsDataAnalyzer.Tests.History
{
    public class HistoryCipherTests
    {
        [Fact]
        public void NewKey_Is64Bytes()
        {
            byte[] key = HistoryCipher.NewKey();
            Assert.Equal(64, key.Length);
        }

        [Fact]
        public void NewKey_IsRandom_AcrossCalls()
        {
            byte[] a = HistoryCipher.NewKey();
            byte[] b = HistoryCipher.NewKey();
            Assert.NotEqual(Convert.ToBase64String(a), Convert.ToBase64String(b));
        }

        [Fact]
        public void Constructor_RejectsWrongKeyLength()
        {
            Assert.Throws<ArgumentException>(() => new HistoryCipher(new byte[32]));
            Assert.Throws<ArgumentException>(() => new HistoryCipher(new byte[65]));
        }

        [Fact]
        public void EncryptLine_HasVersionPrefix()
        {
            var cipher = new HistoryCipher(HistoryCipher.NewKey());
            string line = cipher.EncryptLine("hello");
            Assert.StartsWith(HistoryCipher.LinePrefix, line, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("")]
        [InlineData("hello world")]
        [InlineData("CREATE LOGIN bob WITH PASSWORD = 'S3cr3t!'")]
        [InlineData("line1\nline2\ttab")]
        [InlineData("unicode éü \U0001F600")]
        public void RoundTrips(string plaintext)
        {
            var cipher = new HistoryCipher(HistoryCipher.NewKey());

            string line = cipher.EncryptLine(plaintext);
            bool ok = cipher.TryDecryptLine(line, out string decrypted);

            Assert.True(ok);
            Assert.Equal(plaintext, decrypted);
        }

        [Fact]
        public void EncryptLine_UsesFreshIv_EachCall()
        {
            var cipher = new HistoryCipher(HistoryCipher.NewKey());
            string a = cipher.EncryptLine("same plaintext");
            string b = cipher.EncryptLine("same plaintext");
            Assert.NotEqual(a, b);
        }

        [Fact]
        public void TryDecryptLine_RejectsTamperedCiphertext()
        {
            var cipher = new HistoryCipher(HistoryCipher.NewKey());
            string line = cipher.EncryptLine("do not tamper with me");

            string payload = line.Substring(HistoryCipher.LinePrefix.Length);
            byte[] bytes = Convert.FromBase64String(payload);
            bytes[bytes.Length - 1] ^= 0xFF; // flip a bit in the MAC region
            string tampered = HistoryCipher.LinePrefix + Convert.ToBase64String(bytes);

            bool ok = cipher.TryDecryptLine(tampered, out string decrypted);

            Assert.False(ok);
            Assert.Null(decrypted);
        }

        [Fact]
        public void TryDecryptLine_RejectsTamperedMiddleByte()
        {
            var cipher = new HistoryCipher(HistoryCipher.NewKey());
            string line = cipher.EncryptLine("some reasonably long plaintext to have a body");

            string payload = line.Substring(HistoryCipher.LinePrefix.Length);
            byte[] bytes = Convert.FromBase64String(payload);
            bytes[20] ^= 0x01;
            string tampered = HistoryCipher.LinePrefix + Convert.ToBase64String(bytes);

            bool ok = cipher.TryDecryptLine(tampered, out string decrypted);

            Assert.False(ok);
        }

        [Fact]
        public void TryDecryptLine_RejectsWrongKey()
        {
            var cipher1 = new HistoryCipher(HistoryCipher.NewKey());
            var cipher2 = new HistoryCipher(HistoryCipher.NewKey());

            string line = cipher1.EncryptLine("secret text");
            bool ok = cipher2.TryDecryptLine(line, out string decrypted);

            Assert.False(ok);
            Assert.Null(decrypted);
        }

        [Theory]
        [InlineData("")]
        [InlineData("no prefix here")]
        [InlineData("v1:not-valid-base64!!!")]
        [InlineData("v1:")]
        [InlineData(null)]
        public void TryDecryptLine_NeverThrows_OnGarbage(string garbage)
        {
            var cipher = new HistoryCipher(HistoryCipher.NewKey());
            bool ok = cipher.TryDecryptLine(garbage, out string decrypted);

            Assert.False(ok);
            Assert.Null(decrypted);
        }

        [Fact]
        public void TryDecryptLine_RejectsTooShortPayload()
        {
            var cipher = new HistoryCipher(HistoryCipher.NewKey());
            string tooShort = HistoryCipher.LinePrefix + Convert.ToBase64String(new byte[10]);

            bool ok = cipher.TryDecryptLine(tooShort, out string decrypted);

            Assert.False(ok);
        }
    }
}
