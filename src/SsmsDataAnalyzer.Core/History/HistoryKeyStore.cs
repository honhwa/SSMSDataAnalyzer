namespace SsmsDataAnalyzer.Core.History
{
    /// <summary>
    /// Reads or creates the 64-byte <see cref="HistoryCipher"/> key, wrapped at rest via
    /// <see cref="IKeyProtector"/> (DPAPI in the Vsix). Per §3 "Privacy and safety": a missing
    /// or undecryptable key file means history is simply empty and a fresh key is created --
    /// this class never throws for that reason, only for I/O failures on write.
    /// </summary>
    public sealed class HistoryKeyStore
    {
        private const int KeyLength = 64;

        private readonly IHistoryFileSystem _fileSystem;
        private readonly IKeyProtector _protector;
        private readonly string _keyFilePath;

        public HistoryKeyStore(IHistoryFileSystem fileSystem, IKeyProtector protector, string keyFilePath)
        {
            _fileSystem = fileSystem;
            _protector = protector;
            _keyFilePath = keyFilePath;
        }

        /// <summary>
        /// Returns the existing key if the file exists and unwraps cleanly to 64 bytes;
        /// otherwise generates a new key, wraps and persists it, and returns that.
        /// </summary>
        public byte[] LoadOrCreate()
        {
            if (_fileSystem.FileExists(_keyFilePath))
            {
                byte[] existing = TryReadExistingKey();
                if (existing != null) return existing;
            }

            byte[] key = HistoryCipher.NewKey();
            byte[] protectedKey = _protector.Protect(key);

            // The key file is the FIRST thing written on a clean machine -- before HistoryStore
            // has appended anything, so before anything else has created the folder. Without
            // this the very first write throws DirectoryNotFoundException, initialization fails
            // and history stays silently empty forever (v0.19.0 field report: "I tried a few
            // executes and history is empty" -- the folder had never been created).
            string directory = System.IO.Path.GetDirectoryName(_keyFilePath);
            if (!string.IsNullOrEmpty(directory)) _fileSystem.EnsureDirectory(directory);

            _fileSystem.WriteAllBytes(_keyFilePath, protectedKey);
            return key;
        }

        private byte[] TryReadExistingKey()
        {
            try
            {
                byte[] protectedBytes = _fileSystem.ReadAllBytes(_keyFilePath);
                byte[] key = _protector.Unprotect(protectedBytes);
                if (key == null || key.Length != KeyLength) return null;
                return key;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Crypto-shred: deletes the key file so every existing history file becomes
        /// unreadable instantly, even if the subsequent file deletion (by the caller) is
        /// interrupted. Called first by "Clear all history".
        /// </summary>
        public void Destroy()
        {
            if (_fileSystem.FileExists(_keyFilePath))
            {
                _fileSystem.DeleteFile(_keyFilePath);
            }
        }
    }
}
