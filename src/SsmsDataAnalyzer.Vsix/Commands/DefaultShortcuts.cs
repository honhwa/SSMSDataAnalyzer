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
    /// 1. Run at most once per shortcut set (a version stamp in the settings store). Someone who
    ///    DELETES one of these shortcuts does not get it forced back on the next start.
    /// 2. Never touch a command that already has any binding — theirs, or ours from a previous
    ///    run.
    /// 3. Never take a key another command already uses. Assigning over it would silently break
    ///    whatever that was, which is exactly the complaint this class exists to prevent.
    ///
    /// Everything here is best-effort: a failure leaves the user with no default shortcut, which
    /// is a minor inconvenience, never a broken SSMS.
    /// </summary>
    internal static class DefaultShortcuts
    {
        /// <summary>Bump only when the SET below changes, so an existing user's untouched
        /// commands pick up newly added shortcuts without re-applying the old ones.</summary>
        private const string ShortcutSetVersion = "1";

        private const string CollectionPath = "SsmsDataAnalyzer";
        private const string AppliedProperty = "DefaultShortcutsApplied";

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

                if (store.CollectionExists(CollectionPath)
                    && store.PropertyExists(CollectionPath, AppliedProperty)
                    && store.GetString(CollectionPath, AppliedProperty, "") == ShortcutSetVersion)
                {
                    return; // Rule 1: already done for this set.
                }

                if (!(serviceProvider.GetService(typeof(DTE)) is DTE dte))
                {
                    OeDiagnostics.Warn("Default shortcuts: no DTE service; none were assigned.");
                    return;
                }

                Apply(dte);

                if (!store.CollectionExists(CollectionPath)) store.CreateCollection(CollectionPath);
                store.SetString(CollectionPath, AppliedProperty, ShortcutSetVersion);
            }
            catch (Exception ex)
            {
                // Never a failure mode of loading the package. No key names, no user data.
                OeDiagnostics.Error("Default shortcuts: could not be applied (" + ex.GetType().Name + ").");
            }
        }

        private static void Apply(DTE dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var byName = new Dictionary<string, Command>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> keysInUse = CollectKeysInUse(dte, byName);
            if (keysInUse == null)
            {
                OeDiagnostics.Warn("Default shortcuts: the shell's existing shortcuts could not be read in time, so none were assigned (assign your own under Tools > Options > Environment > Keyboard).");
                return;
            }

            int assigned = 0, keptExisting = 0, skippedConflict = 0;

            foreach (var (commandName, binding) in Defaults)
            {
                Command command = Find(byName, commandName);
                if (command == null) continue; // Not present in this build.

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
