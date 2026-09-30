using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell.Settings;
using SsmsDataAnalyzer.Vsix.ObjectExplorer;

namespace SsmsDataAnalyzer.Vsix.Commands
{
    /// <summary>
    /// Gives every command a default keyboard shortcut on first run, without ever touching a
    /// shortcut the user has already set.
    ///
    /// Why this is done at runtime rather than as &lt;KeyBinding&gt; defaults in VSCommandTable.vsct:
    /// a command-table binding is baked into the extension, and how the shell merges a NEWLY
    /// ADDED default with a user's own customisation on upgrade cannot be verified from outside
    /// a running SSMS. Doing it here means the rule is explicit and observable instead of
    /// assumed: look first, and if anything is already bound, leave it alone.
    ///
    /// The rules, in order:
    /// 1. Each command gets exactly ONE chance at its default, ever -- tracked per command (see
    ///    <see cref="OfferedProperty"/>), not by a single shortcut-set version stamp. A version
    ///    bump would re-run the WHOLE list, and a command whose default the user had DELETED has
    ///    no binding at that point, so it would be handed that default again -- breaking the
    ///    promise that a deleted default stays deleted. (docs/source-control-sync-plan.md §13.9
    ///    lead correction, 2026-09-29: this replaced the old <c>ShortcutSetVersion</c> stamp,
    ///    which had exactly that bug. It ships now because this is the first release that adds
    ///    a command to <see cref="Defaults"/> after the original ten.)
    /// 2. Never touch a command that already has any binding — theirs, or ours from a previous
    ///    run.
    /// 3. Never take a key another command already uses. Assigning over it would silently break
    ///    whatever that was, which is exactly the complaint this class exists to prevent.
    ///
    /// Migration: the old v0.25 stamp (<see cref="AppliedProperty"/> == "1") means the original
    /// ten commands have already had their one chance -- they are seeded into the offered list
    /// rather than being reconsidered, so nobody who deleted one of those ten sees it return.
    ///
    /// Everything here is best-effort: a failure leaves the user with no default shortcut, which
    /// is a minor inconvenience, never a broken SSMS.
    /// </summary>
    internal static class DefaultShortcuts
    {
        private const string CollectionPath = "SsmsDataAnalyzer";

        /// <summary>Legacy v0.25 stamp: "1" means the original ten commands (the ones present
        /// at the time) were already offered. Read only for migration; never written again.</summary>
        private const string AppliedProperty = "DefaultShortcutsApplied";

        /// <summary>Delimited list of CanonicalNames that have already been offered their
        /// default, one way or another (assigned, kept-existing, or skipped-conflict all count
        /// as "offered" -- see <see cref="Apply"/>). '|' cannot appear in a command's
        /// CanonicalName, so no escaping is needed.</summary>
        private const string OfferedProperty = "DefaultShortcutsOffered";
        private const char OfferedSeparator = '|';

        /// <summary>The original ten commands (v0.25), seeded into the offered list on
        /// migration so a deleted one never comes back.</summary>
        private static readonly string[] OriginalTenCommands =
        {
            "SsmsDataAnalyzer.QueryHistory", "SsmsDataAnalyzer.FindInResults",
            "SsmsDataAnalyzer.GoToSourceForValue", "SsmsDataAnalyzer.PeekSourceForValue",
            "SsmsDataAnalyzer.PivotRows", "SsmsDataAnalyzer.AggregateSelection",
            "SsmsDataAnalyzer.AnalyzeData", "SsmsDataAnalyzer.PasteAsSqlIn",
            "SsmsDataAnalyzer.PasteAsNumericSqlIn", "SsmsDataAnalyzer.ScriptObjectAsAlter",
        };

        /// <summary>
        /// One chord prefix (Ctrl+Alt+Q, "Query tools") plus a mnemonic letter. A chord claims
        /// ONE key combination instead of ten, which keeps the chance of colliding with
        /// something the user relies on as small as it can be while still shipping defaults.
        /// </summary>
        private static readonly (string Command, string Binding)[] Defaults =
        {
            ("SsmsDataAnalyzer.QueryHistory",         "Global::Ctrl+Alt+Q, H"),
            ("SsmsDataAnalyzer.FindInResults",        "Global::Ctrl+Alt+Q, F"),
            ("SsmsDataAnalyzer.GoToSourceForValue",   "Global::Ctrl+Alt+Q, G"),
            ("SsmsDataAnalyzer.PeekSourceForValue",   "Global::Ctrl+Alt+Q, P"),
            ("SsmsDataAnalyzer.PivotRows",            "Global::Ctrl+Alt+Q, V"),
            ("SsmsDataAnalyzer.AggregateSelection",   "Global::Ctrl+Alt+Q, A"),
            ("SsmsDataAnalyzer.AnalyzeData",          "Global::Ctrl+Alt+Q, D"),
            ("SsmsDataAnalyzer.PasteAsSqlIn",         "Global::Ctrl+Alt+Q, I"),
            ("SsmsDataAnalyzer.PasteAsNumericSqlIn",  "Global::Ctrl+Alt+Q, N"),
            ("SsmsDataAnalyzer.ScriptObjectAsAlter",  "Global::Ctrl+Alt+Q, S"),

            // docs/source-control-sync-plan.md §13.9 -- first command added since the
            // original ten; exercises the per-command "offered" tracking above for real.
            ("SsmsDataAnalyzer.CheckSourceControl",   "Global::Ctrl+Alt+Q, C"),
        };

        /// <summary>Scanning every command in the shell to find which keys are taken is the
        /// expensive part, and it runs on the UI thread. Time-boxed: if the shell is slow, we
        /// give up on the conflict check and assign nothing rather than either hanging SSMS or
        /// assigning blind.</summary>
        private static readonly TimeSpan ScanBudget = TimeSpan.FromSeconds(3);

        public static void ApplyOnce(IServiceProvider serviceProvider)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var store = GetSettingsStore(serviceProvider);
                if (store == null) return; // Cannot remember having run; see GetSettingsStore.

                HashSet<string> offered = LoadOfferedSet(store);

                // Rule 1: only a command not yet in the offered set gets a chance this run.
                var pending = new List<(string Command, string Binding)>();
                foreach (var entry in Defaults)
                {
                    if (!offered.Contains(entry.Command)) pending.Add(entry);
                }
                if (pending.Count == 0) return; // Every known command has already had its chance.

                if (!(serviceProvider.GetService(typeof(DTE)) is DTE dte))
                {
                    OeDiagnostics.Warn("Default shortcuts: no DTE service; none were assigned.");
                    return;
                }

                IEnumerable<string> nowOffered = Apply(dte, pending);

                foreach (string name in nowOffered) offered.Add(name);
                SaveOfferedSet(store, offered);
            }
            catch (Exception ex)
            {
                // Never a failure mode of loading the package. No key names, no user data.
                OeDiagnostics.Error("Default shortcuts: could not be applied (" + ex.GetType().Name + ").");
            }
        }

        /// <summary>The offered set as of the last run: the persisted list, plus (migration)
        /// the original ten commands when only the legacy v0.25 stamp is present and no list
        /// has been written yet.</summary>
        private static HashSet<string> LoadOfferedSet(WritableSettingsStore store)
        {
            var offered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            bool hasOfferedList = store.CollectionExists(CollectionPath)
                && store.PropertyExists(CollectionPath, OfferedProperty);

            if (hasOfferedList)
            {
                string raw = store.GetString(CollectionPath, OfferedProperty, "");
                foreach (string name in raw.Split(OfferedSeparator))
                {
                    if (!string.IsNullOrEmpty(name)) offered.Add(name);
                }
                return offered;
            }

            // No offered list yet. Migration: the legacy stamp means the original ten already
            // had their one chance -- seed them in so none of them can come back from the dead
            // for someone who deleted one. A machine with neither marker (fresh install) seeds
            // nothing, so every command -- the original ten included -- gets its normal chance.
            bool legacyApplied = store.CollectionExists(CollectionPath)
                && store.PropertyExists(CollectionPath, AppliedProperty)
                && store.GetString(CollectionPath, AppliedProperty, "") == "1";

            if (legacyApplied)
            {
                foreach (string name in OriginalTenCommands) offered.Add(name);
            }

            return offered;
        }

        private static void SaveOfferedSet(WritableSettingsStore store, HashSet<string> offered)
        {
            if (!store.CollectionExists(CollectionPath)) store.CreateCollection(CollectionPath);
            store.SetString(CollectionPath, OfferedProperty, string.Join(OfferedSeparator.ToString(), offered));
        }

        /// <summary>Applies only the commands in <paramref name="pending"/> (those not yet
        /// offered). Returns the CanonicalNames that were actually looked at this run --
        /// assigned, left because they already had a binding, or skipped for a key conflict
        /// all count as "offered": the command had its one chance, whatever it decided. A
        /// command not present in this build at all is NOT returned, so it still gets its
        /// chance once it appears in a later session.</summary>
        private static IEnumerable<string> Apply(DTE dte, List<(string Command, string Binding)> pending)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var byName = new Dictionary<string, Command>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> keysInUse = CollectKeysInUse(dte, byName);
            if (keysInUse == null)
            {
                OeDiagnostics.Warn("Default shortcuts: the shell's existing shortcuts could not be read in time, so none were assigned (assign your own under Tools > Options > Environment > Keyboard).");
                return Array.Empty<string>();
            }

            int assigned = 0, keptExisting = 0, skippedConflict = 0;
            var nowOffered = new List<string>();

            foreach (var (commandName, binding) in pending)
            {
                Command command = Find(byName, commandName);
                if (command == null) continue; // Not present in this build -- try again next time.
                nowOffered.Add(commandName);

                // Rule 2: anything already bound is the user's business, not ours.
                if (HasAnyBinding(command)) { keptExisting++; continue; }

                // Rule 3: never take a key something else already uses.
                if (keysInUse.Contains(NormalizeKey(binding))) { skippedConflict++; continue; }

                try
                {
                    command.Bindings = new object[] { binding };
                    keysInUse.Add(NormalizeKey(binding));
                    assigned++;
                }
                catch (Exception ex)
                {
                    OeDiagnostics.Warn("Default shortcuts: one shortcut could not be assigned (" + ex.GetType().Name + ").");
                }
            }

            OeDiagnostics.Info("Default shortcuts: " + assigned + " assigned, " + keptExisting
                + " left as already configured, " + skippedConflict + " skipped because the key was already in use.");
            return nowOffered;
        }

        /// <summary>
        /// Some of these commands declare their name with a leading dot in VSCommandTable.vsct
        /// (".SsmsDataAnalyzer.AnalyzeData"), so an exact lookup alone would silently miss them
        /// and quietly skip their shortcut. Match the dotted form too.
        /// </summary>
        private static Command Find(Dictionary<string, Command> byName, string commandName)
        {
            if (byName.TryGetValue(commandName, out var exact)) return exact;
            if (byName.TryGetValue("." + commandName, out var dotted)) return dotted;
            return null;
        }

        private static bool HasAnyBinding(Command command)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                return command.Bindings is object[] bindings && bindings.Length > 0;
            }
            catch
            {
                // Unreadable means unknown, and unknown means hands off.
                return true;
            }
        }

        /// <summary>Every key combination currently bound to anything, or null if the shell
        /// took too long to enumerate.</summary>
        private static HashSet<string> CollectKeysInUse(DTE dte, Dictionary<string, Command> byName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var clock = Stopwatch.StartNew();

            try
            {
                foreach (Command command in dte.Commands)
                {
                    if (clock.Elapsed > ScanBudget) return null;
                    if (command == null) continue;

                    try
                    {
                        string name = command.Name;
                        if (!string.IsNullOrEmpty(name)) byName[name] = command;
                    }
                    catch { /* an unreadable name is just one we cannot target */ }

                    object[] bindings;
                    try { bindings = command.Bindings as object[]; }
                    catch { continue; }
                    if (bindings == null) continue;

                    foreach (object binding in bindings)
                    {
                        if (binding is string text && text.Length > 0) keys.Add(NormalizeKey(text));
                    }
                }
            }
            catch (Exception ex)
            {
                OeDiagnostics.Warn("Default shortcuts: reading existing shortcuts failed (" + ex.GetType().Name + ").");
                return null;
            }

            return keys;
        }

        /// <summary>A binding reads "Scope::Keys". The scope differs between commands ("Global",
        /// "Text Editor", …) while the KEYS are what actually collide, so compare on those.</summary>
        private static string NormalizeKey(string binding)
        {
            int separator = binding.IndexOf("::", StringComparison.Ordinal);
            string keys = separator >= 0 ? binding.Substring(separator + 2) : binding;
            return keys.Replace(" ", string.Empty);
        }

        private static WritableSettingsStore GetSettingsStore(IServiceProvider serviceProvider)
        {
            try
            {
                var manager = new ShellSettingsManager(serviceProvider);
                return manager.GetWritableSettingsStore(SettingsScope.UserSettings);
            }
            catch (Exception ex)
            {
                // Without a store we cannot remember that we have run, and re-applying on every
                // start would fight the user. Better to do nothing at all.
                OeDiagnostics.Warn("Default shortcuts: the settings store is unavailable (" + ex.GetType().Name + "); no defaults were assigned.");
                return null;
            }
        }
    }
}
