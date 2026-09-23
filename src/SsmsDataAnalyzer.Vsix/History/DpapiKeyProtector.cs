using System.Security.Cryptography;
using SsmsDataAnalyzer.Core.History;

namespace SsmsDataAnalyzer.Vsix.History
{
    /// <summary>
    /// <see cref="IKeyProtector"/> via Windows DPAPI (docs/query-history-plan.md §3 "Layer 1").
    /// Scope <see cref="DataProtectionScope.CurrentUser"/> ties the wrapped key to the Windows
    /// user's own login secret; a fixed extra-entropy value is mixed in so a blob produced here
    /// can't be unprotected by some other DPAPI caller on the same account that happens to also
    /// use no entropy. <see cref="System.Security.Cryptography.ProtectedData"/> lives in
    /// System.Security.dll, part of .NET Framework 4.7.2 -- nothing extra is shipped in the
    /// .vsix for this.
    /// </summary>
    internal sealed class DpapiKeyProtector : IKeyProtector
    {
        // Fixed, non-secret entropy -- DPAPI's own per-user secret is what actually protects
        // the key; this only scopes the blob to this specific use so it can't be swapped for
        // some other DPAPI-protected blob on the same account.
        private static readonly byte[] Entropy =
        {
            0x53, 0x73, 0x6d, 0x73, 0x44, 0x61, 0x74, 0x61, 0x41, 0x6e, 0x61, 0x6c, 0x79, 0x7a, 0x65, 0x72,
            0x51, 0x75, 0x65, 0x72, 0x79, 0x48, 0x69, 0x73, 0x74, 0x6f, 0x72, 0x79, 0x2e, 0x76, 0x31, 0x00
        };

        public byte[] Protect(byte[] plaintextKey)
        {
            return ProtectedData.Protect(plaintextKey, Entropy, DataProtectionScope.CurrentUser);
        }

        /// <summary>Documented (IKeyProtector's contract) to throw on failure -- wrong user,
        /// corrupted blob, key rolled by a Windows profile reset, etc. -- so HistoryKeyStore can
        /// treat that as "no usable key" and mint a fresh one, per the plan's "a missing or
        /// undecryptable key means history is simply empty" rule. Never caught here.</summary>
        public byte[] Unprotect(byte[] protectedKey)
        {
            return ProtectedData.Unprotect(protectedKey, Entropy, DataProtectionScope.CurrentUser);
        }
    }
}
