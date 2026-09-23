using System;
using SsmsDataAnalyzer.Core.History;

namespace SsmsDataAnalyzer.Tests.History
{
    /// <summary>
    /// Stands in for DPAPI. XORs with a fixed per-instance pad so "protect then unprotect with
    /// a different instance" fails the way a different Windows user's DPAPI would, without
    /// pulling in any real crypto here (the thing under test is <see cref="HistoryKeyStore"/>'s
    /// handling of the protector, not the protector itself).
    /// </summary>
    public sealed class FakeKeyProtector : IKeyProtector
    {
        private readonly byte _pad;
        public bool ThrowOnUnprotect { get; set; }

        public FakeKeyProtector(byte pad = 0x5A)
        {
            _pad = pad;
        }

        public byte[] Protect(byte[] plaintextKey)
        {
            byte[] result = new byte[plaintextKey.Length];
            for (int i = 0; i < plaintextKey.Length; i++) result[i] = (byte)(plaintextKey[i] ^ _pad);
            return result;
        }

        public byte[] Unprotect(byte[] protectedKey)
        {
            if (ThrowOnUnprotect) throw new InvalidOperationException("Simulated DPAPI failure.");
            byte[] result = new byte[protectedKey.Length];
            for (int i = 0; i < protectedKey.Length; i++) result[i] = (byte)(protectedKey[i] ^ _pad);
            return result;
        }
    }
}
