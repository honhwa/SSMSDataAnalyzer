using SsmsDataAnalyzer.Core.History;
using Xunit;

namespace SsmsDataAnalyzer.Tests.History
{
    public class HistoryKeyStoreTests
    {
        [Fact]
        public void LoadOrCreate_CreatesKey_WhenNoFileExists()
        {
            var fs = new FakeHistoryFileSystem();
            var protector = new FakeKeyProtector();
            var store = new HistoryKeyStore(fs, protector, @"C:\hist\key.bin");

            byte[] key = store.LoadOrCreate();

            Assert.Equal(64, key.Length);
            Assert.True(fs.FileExists(@"C:\hist\key.bin"));
        }

        [Fact]
        public void LoadOrCreate_IsStable_AcrossReloadsWithSameProtector()
        {
            var fs = new FakeHistoryFileSystem();
            var protector = new FakeKeyProtector();
            var store1 = new HistoryKeyStore(fs, protector, @"C:\hist\key.bin");
            byte[] key1 = store1.LoadOrCreate();

            var store2 = new HistoryKeyStore(fs, protector, @"C:\hist\key.bin");
            byte[] key2 = store2.LoadOrCreate();

            Assert.Equal(key1, key2);
        }

        [Fact]
        public void LoadOrCreate_CreatesNewKey_WhenProtectorCannotUnwrap()
        {
            var fs = new FakeHistoryFileSystem();
            var writer = new FakeKeyProtector();
            var writerStore = new HistoryKeyStore(fs, writer, @"C:\hist\key.bin");
            byte[] original = writerStore.LoadOrCreate();

            // Simulate "a different Windows user's DPAPI" -- unprotect fails.
            var failingProtector = new FakeKeyProtector { ThrowOnUnprotect = true };
            var readerStore = new HistoryKeyStore(fs, failingProtector, @"C:\hist\key.bin");

            byte[] fresh = readerStore.LoadOrCreate();

            Assert.Equal(64, fresh.Length);
            Assert.NotEqual(original, fresh);
        }

        [Fact]
        public void Destroy_RemovesKeyFile()
        {
            var fs = new FakeHistoryFileSystem();
            var protector = new FakeKeyProtector();
            var store = new HistoryKeyStore(fs, protector, @"C:\hist\key.bin");
            store.LoadOrCreate();

            store.Destroy();

            Assert.False(fs.FileExists(@"C:\hist\key.bin"));
        }

        [Fact]
        public void Destroy_WhenNoFileExists_DoesNotThrow()
        {
            var fs = new FakeHistoryFileSystem();
            var protector = new FakeKeyProtector();
            var store = new HistoryKeyStore(fs, protector, @"C:\hist\key.bin");

            store.Destroy();
        }

        [Fact]
        public void AfterDestroy_LoadOrCreate_MakesFreshKey()
        {
            var fs = new FakeHistoryFileSystem();
            var protector = new FakeKeyProtector();
            var store = new HistoryKeyStore(fs, protector, @"C:\hist\key.bin");
            byte[] original = store.LoadOrCreate();

            store.Destroy();
            byte[] afterDestroy = store.LoadOrCreate();

            Assert.NotEqual(original, afterDestroy);
        }
    }
}
