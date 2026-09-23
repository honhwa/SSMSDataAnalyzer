using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using Microsoft.SqlServer.Management.UI.VSIntegration.Editors;
using SsmsDataAnalyzer.Core.History;

namespace SsmsDataAnalyzer.Vsix.History
{
    /// <summary>
    /// docs/query-history-api.md S-1 / docs/query-history-plan.md §6.1: a real completion
    /// signal (SQLEditors.dll's internal <c>IQueryExecutionHandler.ScriptExecutionCompleted</c>,
    /// carrying an <c>ExecutionResult</c> enum) exists, but only two reflection hops deep on
    /// private/internal implementation details -- MEDIUM confidence, never tried against a
    /// live SSMS process. This class is the "separate, fully guarded" component the plan calls
    /// for: it feature-detects ONCE per session, and on ANY failure at ANY step -- missing
    /// field, missing event, an unexpected signature, an exception raising/removing the handler
    /// -- it disables itself for the rest of the session, logs exactly one value-free line, and
    /// every caller falls back to leaving Duration/Outcome empty. Nothing else in this feature
    /// depends on it: QueryHistoryCapture always records an entry whether or not this listener
    /// ever fires.
    ///
    /// Row count is deliberately NOT attempted here (docs/query-history-api.md §1.1: it lives
    /// one hop further in, on a per-batch event/field, which is extra reflection risk this
    /// build does not take for what the plan itself calls a "nice to have" -- RowCount stays
    /// null in every Phase 1 entry). Duration is computed from our own stopwatch, started when
    /// the listener is attached (right after BeforeExecute), not from anything SSMS exposes --
    /// docs/query-history-api.md confirms no Duration/Elapsed field exists anywhere.
    /// </summary>
    internal static class QueryHistoryCompletionListener
    {
        private static volatile bool _disabledForSession;
        private static int _loggedDisableReason;

        private static readonly ConcurrentDictionary<int, Action<object>> PendingDispatch = new ConcurrentDictionary<int, Action<object>>();
        private static int _nextToken;

        /// <summary>
        /// Attempts to hook the completion signal for one query window's editor control.
        /// Returns a detach action on success, or null if the listener is unavailable this
        /// session (already disabled, or this attempt itself failed and disabled it) -- either
        /// way the caller (QueryHistoryCapture) treats null exactly like "never fires": the
        /// entry is still recorded, just without Duration/Outcome.
        /// </summary>
        public static Action TryAttach(SqlScriptEditorControl editor, Action<HistoryOutcome?, int?, long?> onCompleted)
        {
            if (_disabledForSession || editor == null || onCompleted == null) return null;

            try
            {
                object resultsControl = ReadPrivateField(editor, "m_sqlResultsControl");
                if (resultsControl == null)
                {
                    // Not a hard failure -- e.g. the window isn't connected/hasn't shown a
                    // results control yet. Nothing to hook this time; try again next execute.
                    return null;
                }

                Type resultsType = resultsControl.GetType();
                EventInfo eventInfo = FindEvent(resultsType, "ScriptExecutionCompleted");
                if (eventInfo == null)
                {
                    Disable("ScriptExecutionCompleted event not found on the internal results-control type");
                    return null;
                }

                Type handlerType = eventInfo.EventHandlerType;
                MethodInfo invokeMethod = handlerType.GetMethod("Invoke");
                ParameterInfo[] parameters = invokeMethod?.GetParameters();
                if (parameters == null || parameters.Length != 2 || parameters[0].ParameterType.IsValueType || parameters[1].ParameterType.IsValueType)
                {
                    Disable("ScriptExecutionCompleted has an unexpected delegate signature");
                    return null;
                }

                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                int token = Interlocked.Increment(ref _nextToken);

                Delegate handler = BuildForwardingDelegate(handlerType, parameters, token);
                if (handler == null)
                {
                    Disable("could not build a forwarding delegate for ScriptExecutionCompleted");
                    return null;
                }

                // At-most-once dispatch: DispatchOnce atomically removes this token from
                // PendingDispatch before acting on it, so a detach racing with an in-flight
                // event can never double-fire or fire after detach.
                PendingDispatch[token] = argsObj => DispatchOnce(token, argsObj, stopwatch, onCompleted);

                eventInfo.AddEventHandler(resultsControl, handler);

                return () =>
                {
                    try
                    {
                        eventInfo.RemoveEventHandler(resultsControl, handler);
                    }
                    catch
                    {
                        // Detach is best-effort cleanup only; a failure here must never surface.
                    }
                    PendingDispatch.TryRemove(token, out _);
                };
            }
            catch (Exception ex)
            {
                Disable(ex.GetType().Name);
                return null;
            }
        }

        private static void DispatchOnce(int token, object argsObj, System.Diagnostics.Stopwatch stopwatch, Action<HistoryOutcome?, int?, long?> onCompleted)
        {
            if (!PendingDispatch.TryRemove(token, out _)) return; // already dispatched or detached

            try
            {
                stopwatch.Stop();
                HistoryOutcome? outcome = TryReadOutcome(argsObj);
                int? durationMs = (int)Math.Min(stopwatch.ElapsedMilliseconds, int.MaxValue);
                onCompleted(outcome, durationMs, null);
            }
            catch (Exception ex)
            {
                Disable(ex.GetType().Name);
                try { onCompleted(null, null, null); } catch { /* never throw into SSMS */ }
            }
        }

        /// <summary>docs/query-history-api.md §1.1: <c>ExecutionResult</c> is a public property
        /// on the internal EventArgs type. Success=1, Failure=2, Cancel=4, Timeout=8, Halted=16
        /// (read from the enum's own .cctor in the spike, not guessed) -- mapped per the spike's
        /// own recommendation: Success-&gt;Success, Failure-&gt;Error, everything else-&gt;Cancelled.</summary>
        private static HistoryOutcome? TryReadOutcome(object argsObj)
        {
            if (argsObj == null) return null;
            PropertyInfo property = argsObj.GetType().GetProperty("ExecutionResult", BindingFlags.Public | BindingFlags.Instance);
            if (property == null) return null;

            object raw = property.GetValue(argsObj);
            if (raw == null) return null;

            int value = Convert.ToInt32(raw);
            switch (value)
            {
                case 1: return HistoryOutcome.Success;
                case 2: return HistoryOutcome.Error;
                default: return HistoryOutcome.Cancelled;
            }
        }

        /// <summary>
        /// Builds a small dynamic method whose signature exactly matches the internal delegate
        /// type's Invoke (object sender, TEventArgs args -- both reference types per the spike),
        /// so it can be bound with <see cref="MethodInfo.CreateDelegate(Type)"/> without ever
        /// naming the internal EventArgs type in source. The method body only boxes the token
        /// and forwards to <see cref="Dispatch"/>, which looks the real callback up by token --
        /// DynamicMethod cannot close over locals/captures, so a token indirection through
        /// <see cref="PendingDispatch"/> is the mechanism instead.
        /// </summary>
        private static Delegate BuildForwardingDelegate(Type handlerType, ParameterInfo[] parameters, int token)
        {
            var method = new DynamicMethod(
                "QueryHistoryCompletionThunk_" + token.ToString(System.Globalization.CultureInfo.InvariantCulture),
                typeof(void),
                new[] { parameters[0].ParameterType, parameters[1].ParameterType },
                typeof(QueryHistoryCompletionListener),
                skipVisibility: true);

            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldc_I4, token);
            il.Emit(OpCodes.Ldarg_1); // args (sender is arg 0, unused)
            MethodInfo dispatchMethod = typeof(QueryHistoryCompletionListener).GetMethod(nameof(RawDispatch), BindingFlags.NonPublic | BindingFlags.Static);
            il.Emit(OpCodes.Call, dispatchMethod);
            il.Emit(OpCodes.Ret);

            return method.CreateDelegate(handlerType);
        }

        /// <summary>Entry point called by the emitted IL -- looks the real callback up by token
        /// and hands it the raw EventArgs object. Never throws past this point.</summary>
        private static void RawDispatch(int token, object argsObj)
        {
            try
            {
                if (PendingDispatch.TryGetValue(token, out var action)) action(argsObj);
            }
            catch
            {
                // A handler exception here would otherwise propagate into SSMS's own event
                // dispatch for a completely unrelated internal control -- never let that happen.
            }
        }

        private static object ReadPrivateField(object instance, string fieldName)
        {
            for (Type t = instance.GetType(); t != null; t = t.BaseType)
            {
                FieldInfo field = t.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (field != null) return field.GetValue(instance);
            }
            throw new MissingFieldException("field not found: " + fieldName);
        }

        private static EventInfo FindEvent(Type type, string eventName)
        {
            for (Type t = type; t != null; t = t.BaseType)
            {
                EventInfo evt = t.GetEvent(eventName, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (evt != null) return evt;
            }
            return null;
        }

        private static void Disable(string reason)
        {
            _disabledForSession = true;
            // Logged exactly once per session, and value-free: a field/type/exception NAME is
            // not query text, a server name, or any literal (project rule), so this is safe to
            // send to OeDiagnostics even though it touches an internal SSMS implementation
            // detail.
            if (Interlocked.CompareExchange(ref _loggedDisableReason, 1, 0) == 0)
            {
                ObjectExplorer.OeDiagnostics.Info("Query history: completion-duration/outcome enrichment is not available in this SSMS build (" + reason + ") -- entries will still be recorded, without duration/outcome.");
            }
        }
    }
}
