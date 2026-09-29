using System;
using System.Linq;
using SsmsDataAnalyzer.Core.SourceControl;
using Xunit;

namespace SsmsDataAnalyzer.Tests.SourceControl
{
    /// <summary>
    /// Field report (v0.28.3 preview): SSDT keeps DML triggers in their TABLE's file —
    /// DebtLedger\Tables\Debt.sql holds the table, its indexes and two triggers, each after a GO.
    /// Indexing only the first object per file meant every changed trigger was reported
    /// "missing from repo". These pin the fix: every object in a file is indexed, each with its
    /// own batch text.
    /// </summary>
    public class MultiObjectFileTests
    {
        // The shape of the real file, abbreviated.
        private const string DebtSql =
            "﻿CREATE TABLE [DebtLedger].[Debt] (\r\n" +
            "    [ID] INT NOT NULL,\r\n" +
            "    [StatusID] INT NOT NULL\r\n" +
            ");\r\n" +
            "GO\r\n" +
            "CREATE NONCLUSTERED INDEX [IX_ClientID]\r\n" +
            "    ON [DebtLedger].[Debt]([ID] ASC);\r\n" +
            "GO\r\n" +
            "\r\n" +
            "CREATE TRIGGER [DebtLedger].[Debt.Status.Insert]\r\n" +
            "    ON [DebtLedger].[Debt] AFTER INSERT\r\n" +
            "AS\r\n" +
            "BEGIN\r\n" +
            "    SET NOCOUNT ON;\r\n" +
            "    SELECT 'status';\r\n" +
            "END\r\n" +
            "GO\r\n" +
            "\r\n" +
            "CREATE TRIGGER [DebtLedger].[Debt.History.Insert]\r\n" +
            "    ON [DebtLedger].[Debt] AFTER INSERT\r\n" +
            "AS\r\n" +
            "BEGIN\r\n" +
            "    SELECT 'history';\r\n" +
            "END\r\n";

        private static ModuleRef Ref(string schema, string name, DbObjectKind kind) => new ModuleRef(schema, name, kind);

        [Fact]
        public void TableFile_DefinesTheTableAndBothTriggers()
        {
            var all = ModuleFileParser.IdentifyAll(DebtSql);

            Assert.Equal(3, all.Count);
            Assert.Equal(Ref("DebtLedger", "Debt", DbObjectKind.Table), all[0].Module);
            Assert.Equal(Ref("DebtLedger", "Debt.Status.Insert", DbObjectKind.Trigger), all[1].Module);
            Assert.Equal(Ref("DebtLedger", "Debt.History.Insert", DbObjectKind.Trigger), all[2].Module);
        }

        [Fact]
        public void EachObjectGetsOnlyItsOwnBatch()
        {
            var all = ModuleFileParser.IdentifyAll(DebtSql);
            string statusTrigger = all[1].Text;

            Assert.Contains("SELECT 'status'", statusTrigger);
            Assert.DoesNotContain("CREATE TABLE", statusTrigger);
            Assert.DoesNotContain("'history'", statusTrigger);
        }

        [Fact]
        public void FieldReport_TriggerInsideTableFile_IsFound_AndMatches_NotMissing()
        {
            var project = SqlProjectReader.Read(
                "<Project><ItemGroup><Build Include=\"DebtLedger\\Tables\\Debt.sql\" /></ItemGroup></Project>");
            var repo = RepoIndex.Build(new[] { new RepoFile(@"DebtLedger\Tables\Debt.sql", DebtSql) }, project);

            // What sys.sql_modules holds for the trigger: its own text only.
            string serverDefinition =
                "CREATE TRIGGER [DebtLedger].[Debt.Status.Insert]\n    ON [DebtLedger].[Debt] AFTER INSERT\nAS\nBEGIN\n" +
                "    SET NOCOUNT ON;\n    SELECT 'status';\nEND";

            var trigger = Ref("DebtLedger", "Debt.Status.Insert", DbObjectKind.Trigger);
            var candidate = new ChangeCandidate("SQLTEST8", "AgricultureFinances", trigger,
                DateTime.UtcNow, ChangeSource.QueryHistory, DdlAction.Alter);

            var finding = SyncChecker.Check(candidate,
                new ServerObjectState(true, DbObjectKind.Trigger, serverDefinition), repo);

            Assert.Equal(SyncStatus.Matches, finding.Status);
            Assert.Equal(@"DebtLedger\Tables\Debt.sql", finding.RelativePath);
        }

        [Fact]
        public void AChangedTriggerInsideTableFile_IsReportedAsDiffering()
        {
            var project = SqlProjectReader.Read(
                "<Project><ItemGroup><Build Include=\"DebtLedger\\Tables\\Debt.sql\" /></ItemGroup></Project>");
            var repo = RepoIndex.Build(new[] { new RepoFile(@"DebtLedger\Tables\Debt.sql", DebtSql) }, project);

            string serverDefinition =
                "CREATE TRIGGER [DebtLedger].[Debt.Status.Insert] ON [DebtLedger].[Debt] AFTER INSERT AS " +
                "BEGIN SET NOCOUNT ON; SELECT 'status CHANGED'; END";

            var candidate = new ChangeCandidate("SQLTEST8", "AgricultureFinances",
                Ref("DebtLedger", "Debt.Status.Insert", DbObjectKind.Trigger),
                DateTime.UtcNow, ChangeSource.QueryHistory, DdlAction.Alter);

            var finding = SyncChecker.Check(candidate,
                new ServerObjectState(true, DbObjectKind.Trigger, serverDefinition), repo);

            Assert.Equal(SyncStatus.DiffersFromRepo, finding.Status);
        }

        [Fact]
        public void GoInsideAStringOrComment_DoesNotSplit()
        {
            string text =
                "CREATE PROCEDURE dbo.P AS\r\n" +
                "SELECT '\r\nGO\r\n';\r\n" +
                "/*\r\nGO\r\n*/\r\n" +
                "SELECT 2;\r\n";

            var all = ModuleFileParser.IdentifyAll(text);

            Assert.Single(all);
            Assert.Contains("SELECT 2", all[0].Text);
        }

        [Fact]
        public void GoWithCountOrComment_Splits_ButGotoAndGoMidLine_DoNot()
        {
            string text =
                "CREATE PROCEDURE dbo.A AS SELECT 1\r\n" +
                "GO 2 -- repeat\r\n" +
                "CREATE PROCEDURE dbo.B AS BEGIN GOTO done; done: SELECT 2 END\r\n" +
                "  go  \r\n" +
                "CREATE VIEW dbo.C AS SELECT 3 AS X\r\n";

            var names = ModuleFileParser.IdentifyAll(text).Select(x => x.Module.Name).ToList();

            Assert.Equal(new[] { "A", "B", "C" }, names);
        }

        [Fact]
        public void SingleObjectFile_BehavesAsBefore()
        {
            string text = "﻿-- header\r\nCREATE PROCEDURE [ABB].[ABB.ChangeStatus] AS SELECT 1\r\n";

            var all = ModuleFileParser.IdentifyAll(text);

            Assert.Single(all);
            Assert.Equal("ABB.ChangeStatus", all[0].Module.Name);
            Assert.Equal(ModuleFileParser.TryIdentify(text), all[0].Module);
        }
    }
}
