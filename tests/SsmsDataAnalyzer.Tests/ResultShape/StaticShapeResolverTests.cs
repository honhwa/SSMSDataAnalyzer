using System;
using System.Collections.Generic;
using System.Linq;
using SsmsDataAnalyzer.Core.ResultShape;
using Xunit;

namespace SsmsDataAnalyzer.Tests.ResultShape
{
    /// <summary>
    /// The static (query-text) source fallback's rules. A wrong answer here opens the WRONG
    /// TABLE, so most of these pin a refusal: whole-statement declines (*, count, names) and
    /// per-column "no source" (unqualified, temp, variable, CTE, derived, duplicate alias,
    /// expression, literal).
    /// </summary>
    public class StaticShapeResolverTests
    {
        private static StaticSelectItem Col(string qualifier, string column, string alias = null) =>
            new StaticSelectItem(alias ?? column, qualifier == null ? null : qualifier.Split('.'), column);

        private static StaticSelectItem Expr(string alias) => new StaticSelectItem(alias, null, null);

        private static StaticFromItem Table(string schema, string table, string alias = null, string db = null) =>
            new StaticFromItem(alias, db, schema, table, StaticFromKind.NamedTable);

        private static StaticQueryShape Shape(IEnumerable<StaticSelectItem> select, IEnumerable<StaticFromItem> from, params string[] ctes) =>
            new StaticQueryShape(select.ToList(), from.ToList(), ctes);

        private static void AssertSource(StaticColumnSource c, string db, string schema, string table, string column)
        {
            Assert.True(c.HasSource);
            Assert.Equal(db, c.Database);
            Assert.Equal(schema, c.Schema);
            Assert.Equal(table, c.Table);
            Assert.Equal(column, c.Column);
        }

        // ---- the field report -------------------------------------------------------------

        [Fact]
        public void FieldReport_TempTableJoin_RealTablesResolve_TempDoesNot()
        {
            // SELECT 'Stanje prije' AS 'Stanje', FA.ID AS FA_ID, FA.StatusID AS FA_StatusID,
            //        FADCS.Code AS FA_StatusCode, N.AccountingID AS N_Acc, MPGIdcT.NameHR AS 'NAMEHR_type'
            // FROM #Nalozi AS N JOIN Finances.Accounting AS FA ... JOIN Finances.[Accounting.DCStatus] AS FADCS ...
            //   LEFT JOIN MiddleLayer.[PaymentGroup.Item.DCType] AS MPGIdcT ...
            var shape = Shape(
                new[]
                {
                    Expr("Stanje"),
                    Col("FA", "ID", "FA_ID"),
                    Col("FA", "StatusID", "FA_StatusID"),
                    Col("FADCS", "Code", "FA_StatusCode"),
                    Col("N", "AccountingID", "N_Acc"),
                    Col("MPGIdcT", "NameHR", "NAMEHR_type")
                },
                new[]
                {
                    Table(null, "#Nalozi", "N"),
                    Table("Finances", "Accounting", "FA"),
                    Table("Finances", "Accounting.DCType", "FADCT"),
                    Table("Finances", "Accounting.DCStatus", "FADCS"),
                    Table("MiddleLayer", "PaymentGroup.Item", "MPGI"),
                    Table("MiddleLayer", "PaymentGroup.Item.DCType", "MPGIdcT")
                });

            var r = StaticShapeResolver.Resolve(shape, new[] { "Stanje", "FA_ID", "FA_StatusID", "FA_StatusCode", "N_Acc", "NAMEHR_type" });

            Assert.True(r.IsMatch);
            Assert.Equal(6, r.Columns.Count);
            Assert.False(r.Columns[0].HasSource);                        // literal
            AssertSource(r.Columns[1], null, "Finances", "Accounting", "ID");
            AssertSource(r.Columns[2], null, "Finances", "Accounting", "StatusID");
            AssertSource(r.Columns[3], null, "Finances", "Accounting.DCStatus", "Code");
            Assert.False(r.Columns[4].HasSource);                        // #temp alias
            AssertSource(r.Columns[5], null, "MiddleLayer", "PaymentGroup.Item.DCType", "NameHR");
            Assert.Equal("FA_ID", r.Columns[1].OutputName);
        }

        // ---- whole-statement declines ------------------------------------------------------

        [Fact]
        public void SelectStar_DeclinesWholeStatement()
        {
            var shape = Shape(new[] { StaticSelectItem.Star() }, new[] { Table("dbo", "T", "t") });
            var r = StaticShapeResolver.Resolve(shape, new[] { "A" });
            Assert.False(r.IsMatch);
            Assert.Equal(StaticShapeResolver.StarReason, r.DeclineReason);
        }

        [Fact]
        public void AliasStar_AnywhereInList_DeclinesWholeStatement()
        {
            var shape = Shape(new[] { Col("t", "A"), StaticSelectItem.Star() }, new[] { Table("dbo", "T", "t") });
            var r = StaticShapeResolver.Resolve(shape, new[] { "A", "B" });
            Assert.False(r.IsMatch);
            Assert.Equal(StaticShapeResolver.StarReason, r.DeclineReason);
        }

        [Fact]
        public void CountMismatch_Declines()
        {
            var shape = Shape(new[] { Col("t", "A") }, new[] { Table("dbo", "T", "t") });
            var r = StaticShapeResolver.Resolve(shape, new[] { "A", "B" });
            Assert.False(r.IsMatch);
            Assert.Equal(StaticShapeResolver.CountReason, r.DeclineReason);
        }

        [Fact]
        public void NameMismatch_Declines_AndIsCaseSensitiveLikeTheDescribeMatcher()
        {
            var shape = Shape(new[] { Col("t", "A") }, new[] { Table("dbo", "T", "t") });
            Assert.Equal(StaticShapeResolver.NameReason, StaticShapeResolver.Resolve(shape, new[] { "B" }).DeclineReason);
            Assert.Equal(StaticShapeResolver.NameReason, StaticShapeResolver.Resolve(shape, new[] { "a" }).DeclineReason);
        }

        [Fact]
        public void Unparsed_Declines()
        {
            var r = StaticShapeResolver.Resolve(StaticQueryShape.Unusable(), new[] { "A" });
            Assert.False(r.IsMatch);
            Assert.Equal(StaticShapeResolver.NotParsedReason, r.DeclineReason);
            Assert.Empty(r.Columns);
        }

        [Fact]
        public void NullShape_Declines_NullGridNamesThrows()
        {
            Assert.False(StaticShapeResolver.Resolve(null, new[] { "A" }).IsMatch);
            Assert.Throws<ArgumentNullException>(() => StaticShapeResolver.Resolve(StaticQueryShape.Unusable(), null));
        }

        // ---- names --------------------------------------------------------------------------

        [Fact]
        public void MissingOutputName_MatchesNoColumnNameHeader_AndHasNoSource()
        {
            var shape = Shape(new[] { Col("t", "A"), Expr(null) }, new[] { Table("dbo", "T", "t") });
            var r = StaticShapeResolver.Resolve(shape, new[] { "A", "(No column name)" });
            Assert.True(r.IsMatch);
            Assert.False(r.Columns[1].HasSource);
        }

        [Fact]
        public void MissingOutputName_AgainstANamedHeader_Declines()
        {
            var shape = Shape(new[] { Expr(null) }, new StaticFromItem[0]);
            Assert.Equal(StaticShapeResolver.NameReason, StaticShapeResolver.Resolve(shape, new[] { "X" }).DeclineReason);
        }

        [Fact]
        public void ColumnWithoutAlias_UsesItsOwnName()
        {
            var shape = Shape(new[] { Col("t", "Code") }, new[] { Table("dbo", "T", "t") });
            var r = StaticShapeResolver.Resolve(shape, new[] { "Code" });
            Assert.True(r.IsMatch);
            AssertSource(r.Columns[0], null, "dbo", "T", "Code");
        }

        // ---- per-column "no source" ---------------------------------------------------------

        [Fact]
        public void UnqualifiedColumn_HasNoSource_EvenWithASingleTable()
        {
            var shape = Shape(new[] { Col(null, "A") }, new[] { Table("dbo", "T") });
            var r = StaticShapeResolver.Resolve(shape, new[] { "A" });
            Assert.True(r.IsMatch);
            Assert.False(r.Columns[0].HasSource);
        }

        [Fact]
        public void ExpressionAndLiteralColumns_HaveNoSource_ButCount()
        {
            var shape = Shape(new[] { Expr("Total"), Expr("Label"), Col("t", "A") }, new[] { Table("dbo", "T", "t") });
            var r = StaticShapeResolver.Resolve(shape, new[] { "Total", "Label", "A" });
            Assert.True(r.IsMatch);
            Assert.False(r.Columns[0].HasSource);
            Assert.False(r.Columns[1].HasSource);
            AssertSource(r.Columns[2], null, "dbo", "T", "A");
        }

        [Theory]
        [InlineData("#Temp")]
        [InlineData("##GlobalTemp")]
        [InlineData("@tv")]
        public void TempTablesAndTableVariables_HaveNoSource(string name)
        {
            var shape = Shape(new[] { Col("x", "A"), Col(name, "B") }, new[] { Table(null, name, "x"), Table(null, name) });
            // Second from item uses the same name with no alias → key is the name itself.
            var r = StaticShapeResolver.Resolve(shape, new[] { "A", "B" });
            Assert.True(r.IsMatch);
            Assert.False(r.Columns[0].HasSource);
            Assert.False(r.Columns[1].HasSource);
        }

        [Fact]
        public void TempTableInADatabaseQualifiedName_StillHasNoSource()
        {
            var shape = Shape(new[] { Col("x", "A") }, new[] { Table("dbo", "#T", "x", db: "tempdb") });
            Assert.False(StaticShapeResolver.Resolve(shape, new[] { "A" }).Columns[0].HasSource);
        }

        [Fact]
        public void CteAlias_HasNoSource_ButASchemaQualifiedTableOfTheSameNameDoes()
        {
            var shape = Shape(
                new[] { Col("c", "A"), Col("r", "B") },
                new[] { Table(null, "Recent", "c"), Table("dbo", "Recent", "r") },
                "Recent");
            var r = StaticShapeResolver.Resolve(shape, new[] { "A", "B" });
            Assert.True(r.IsMatch);
            Assert.False(r.Columns[0].HasSource);
            AssertSource(r.Columns[1], null, "dbo", "Recent", "B");
        }

        [Fact]
        public void CteNameMatchIsCaseInsensitive()
        {
            var shape = Shape(new[] { Col("c", "A") }, new[] { Table(null, "RECENT", "c") }, "recent");
            Assert.False(StaticShapeResolver.Resolve(shape, new[] { "A" }).Columns[0].HasSource);
        }

        [Fact]
        public void DerivedTableAndTvfAliases_HaveNoSource()
        {
            var shape = Shape(
                new[] { Col("d", "A"), Col("f", "B"), Col("t", "C") },
                new[] { StaticFromItem.Unresolvable("d"), StaticFromItem.Unresolvable("f"), Table("dbo", "T", "t") });
            var r = StaticShapeResolver.Resolve(shape, new[] { "A", "B", "C" });
            Assert.True(r.IsMatch);
            Assert.False(r.Columns[0].HasSource);
            Assert.False(r.Columns[1].HasSource);
            AssertSource(r.Columns[2], null, "dbo", "T", "C");
        }

        [Fact]
        public void DuplicateAlias_BothTablesLoseTheirColumns()
        {
            var shape = Shape(
                new[] { Col("x", "A"), Col("y", "B") },
                new[] { Table("dbo", "T1", "x"), Table("dbo", "T2", "X"), Table("dbo", "T3", "y") });
            var r = StaticShapeResolver.Resolve(shape, new[] { "A", "B" });
            Assert.True(r.IsMatch);
            Assert.False(r.Columns[0].HasSource);
            AssertSource(r.Columns[1], null, "dbo", "T3", "B");
        }

        [Fact]
        public void SameTableTwiceWithoutAlias_IsAmbiguous()
        {
            var shape = Shape(new[] { Col("T", "A") }, new[] { Table("dbo", "T"), Table("other", "T") });
            Assert.False(StaticShapeResolver.Resolve(shape, new[] { "A" }).Columns[0].HasSource);
        }

        [Fact]
        public void UnknownQualifier_HasNoSource()
        {
            var shape = Shape(new[] { Col("z", "A") }, new[] { Table("dbo", "T", "t") });
            Assert.False(StaticShapeResolver.Resolve(shape, new[] { "A" }).Columns[0].HasSource);
        }

        [Fact]
        public void AliasedTable_CannotBeReferencedByItsTableName()
        {
            var shape = Shape(new[] { Col("T", "A") }, new[] { Table("dbo", "T", "t2") });
            Assert.False(StaticShapeResolver.Resolve(shape, new[] { "A" }).Columns[0].HasSource);
        }

        [Fact]
        public void SameColumnNameFromTwoTables_EachResolvesToItsOwnTable()
        {
            var shape = Shape(
                new[] { Col("a", "ID"), Col("b", "ID") },
                new[] { Table("dbo", "Parent", "a"), Table("dbo", "Child", "b") });
            var r = StaticShapeResolver.Resolve(shape, new[] { "ID", "ID" });
            Assert.True(r.IsMatch);
            AssertSource(r.Columns[0], null, "dbo", "Parent", "ID");
            AssertSource(r.Columns[1], null, "dbo", "Child", "ID");
        }

        [Fact]
        public void NoFromClause_EverythingHasNoSource()
        {
            var shape = Shape(new[] { Col("t", "A") }, new StaticFromItem[0]);
            var r = StaticShapeResolver.Resolve(shape, new[] { "A" });
            Assert.True(r.IsMatch);
            Assert.False(r.Columns[0].HasSource);
        }

        // ---- multi-part names ---------------------------------------------------------------

        [Fact]
        public void ThreePartTableName_KeepsTheDatabase()
        {
            var shape = Shape(new[] { Col("o", "A") }, new[] { Table("Sales", "Orders", "o", db: "OtherDb") });
            AssertSource(StaticShapeResolver.Resolve(shape, new[] { "A" }).Columns[0], "OtherDb", "Sales", "Orders", "A");
        }

        [Fact]
        public void UnqualifiedTable_LeavesSchemaNullForTheCatalogToResolve()
        {
            var shape = Shape(new[] { Col("Orders", "A") }, new[] { Table(null, "Orders") });
            AssertSource(StaticShapeResolver.Resolve(shape, new[] { "A" }).Columns[0], null, null, "Orders", "A");
        }

        [Fact]
        public void TwoPartColumnQualifier_MustAgreeWithTheTablesSchema()
        {
            var from = new[] { Table("Sales", "Orders") };
            AssertSource(StaticShapeResolver.Resolve(Shape(new[] { Col("Sales.Orders", "A") }, from), new[] { "A" }).Columns[0],
                null, "Sales", "Orders", "A");
            Assert.False(StaticShapeResolver.Resolve(Shape(new[] { Col("dbo.Orders", "A") }, from), new[] { "A" }).Columns[0].HasSource);
        }

        [Fact]
        public void TwoPartColumnQualifier_OnAnUnqualifiedTable_IsNotTrusted()
        {
            var shape = Shape(new[] { Col("dbo.Orders", "A") }, new[] { Table(null, "Orders") });
            Assert.False(StaticShapeResolver.Resolve(shape, new[] { "A" }).Columns[0].HasSource);
        }

        [Fact]
        public void ThreePartColumnQualifier_MustAgreeWithTheDatabase()
        {
            var from = new[] { Table("Sales", "Orders", db: "Db1") };
            Assert.True(StaticShapeResolver.Resolve(Shape(new[] { Col("Db1.Sales.Orders", "A") }, from), new[] { "A" }).Columns[0].HasSource);
            Assert.False(StaticShapeResolver.Resolve(Shape(new[] { Col("Db2.Sales.Orders", "A") }, from), new[] { "A" }).Columns[0].HasSource);
        }

        [Fact]
        public void MultiPartQualifier_OnAnAliasedTable_HasNoSource()
        {
            var shape = Shape(new[] { Col("Sales.o", "A") }, new[] { Table("Sales", "Orders", "o") });
            Assert.False(StaticShapeResolver.Resolve(shape, new[] { "A" }).Columns[0].HasSource);
        }

        // ---- UNION: the adapter hands over the FIRST query specification only ------------------

        [Fact]
        public void Union_FirstSpecificationDecidesNames_ButNoColumnHasASource()
        {
            // SELECT t.A, t.B FROM dbo.T t UNION ALL SELECT u.X, u.Y FROM dbo.U u → names A, B;
            // a row may come from either branch, so (like the DM) no source at all.
            var first = new StaticQueryShape(new[] { Col("t", "A"), Col("t", "B") }, new[] { Table("dbo", "T", "t") }, null, isSetOperation: true);
            var r = StaticShapeResolver.Resolve(first, new[] { "A", "B" });
            Assert.True(r.IsMatch);
            Assert.False(r.Columns[0].HasSource);
            Assert.False(r.Columns[1].HasSource);
            Assert.Equal(StaticShapeResolver.NameReason, StaticShapeResolver.Resolve(first, new[] { "X", "Y" }).DeclineReason);
        }

        // ---- ShapeMatch integration: agreement + wording ----------------------------------------

        private static DescribedColumn StaticRow(int ordinal, string name, string table, string column = null) =>
            new DescribedColumn
            {
                Ordinal = ordinal,
                Name = name,
                IsHidden = false,
                IsStatic = true,
                SourceDatabase = table == null ? null : "Db",
                SourceSchema = table == null ? null : "dbo",
                SourceTable = table,
                SourceColumn = table == null ? null : (column ?? name),
                SystemTypeName = table == null ? null : "int",
                MaxLength = table == null ? 0 : 4
            };

        private const string Note = " (resolved from the query text — SQL Server couldn't describe it: SQL Server error 208: x)";

        [Fact]
        public void MatchedStatic_CarriesTheNote_AndResolvesThroughTheUsualAgreementRule()
        {
            var m = ResultShapeMatcher.MatchedStatic(new[] { new MatchedBatch(0, new[] { StaticRow(1, "A", "T"), StaticRow(2, "B", null) }) }, Note);

            Assert.True(m.IsMatch);
            Assert.Equal(Note, m.StaticNote);
            var a = ResultShapeMatcher.ResolveColumn(m, 1, "A");
            Assert.True(a.Succeeded);
            Assert.Equal("T", a.Described.SourceTable);
            Assert.Equal(1, a.MatchCount);
        }

        [Fact]
        public void MatchedStatic_NoSourceColumn_SaysItDoesNotComeFromASingleTable()
        {
            var m = ResultShapeMatcher.MatchedStatic(new[] { new MatchedBatch(0, new[] { StaticRow(1, "B", null) }) }, Note);
            var r = ResultShapeMatcher.ResolveColumn(m, 1, "B");
            Assert.False(r.Succeeded);
            Assert.Equal("Go to source: 'B' doesn't come from a single table in the query — declined rather than risk the wrong table.", r.DeclineMessage);
        }

        [Fact]
        public void DescribedNoSourceColumn_KeepsTheComputedExpressionWording()
        {
            var row = StaticRow(1, "B", null);
            row.IsStatic = false;
            var m = ResultShapeMatcher.Match(new IReadOnlyList<DescribedColumn>[] { new[] { row } }, 1, new[] { "B" }, 0, null);
            Assert.Equal("Go to source: 'B' is a computed expression — it has no base table.", ResultShapeMatcher.ResolveColumn(m, 1, "B").DeclineMessage);
            Assert.Null(m.StaticNote);
        }

        [Fact]
        public void MatchedStatic_DisagreeingCandidates_Decline()
        {
            var m = ResultShapeMatcher.MatchedStatic(new[]
            {
                new MatchedBatch(0, new[] { StaticRow(1, "A", "T1") }),
                new MatchedBatch(1, new[] { StaticRow(1, "A", "T2") })
            }, Note);
            var r = ResultShapeMatcher.ResolveColumn(m, 1, "A");
            Assert.False(r.Succeeded);
            Assert.Contains("does not resolve the same way", r.DeclineMessage);
        }

        [Fact]
        public void MatchedStatic_OneCandidateWithoutSource_DisagreesWithOneWithSource()
        {
            var m = ResultShapeMatcher.MatchedStatic(new[]
            {
                new MatchedBatch(0, new[] { StaticRow(1, "A", "T1") }),
                new MatchedBatch(1, new[] { StaticRow(1, "A", null) })
            }, Note);
            Assert.False(ResultShapeMatcher.ResolveColumn(m, 1, "A").Succeeded);
        }

        [Fact]
        public void MatchedStatic_SameBatchIdenticalRows_CountOnce()
        {
            var m = ResultShapeMatcher.MatchedStatic(new[]
            {
                new MatchedBatch(0, new[] { StaticRow(1, "A", "T") }),
                new MatchedBatch(0, new[] { StaticRow(1, "A", "T") }, 2)
            }, Note);
            Assert.Single(m.Matches);
            Assert.Equal(1, ResultShapeMatcher.ResolveColumn(m, 1, "A").MatchCount);
        }

        [Fact]
        public void MatchedStatic_RequiresMatchesAndANote()
        {
            var rows = new[] { new MatchedBatch(0, new[] { StaticRow(1, "A", "T") }) };
            Assert.Throws<ArgumentException>(() => ResultShapeMatcher.MatchedStatic(new MatchedBatch[0], Note));
            Assert.Throws<ArgumentException>(() => ResultShapeMatcher.MatchedStatic(rows, ""));
            Assert.Throws<ArgumentNullException>(() => ResultShapeMatcher.MatchedStatic(null, Note));
        }

        [Fact]
        public void DescribeError_QuotesNumberAndMessage()
        {
            var text = ResultShapeMatcher.DescribeError(new DescribedColumn { ErrorNumber = 208, ErrorMessage = "Invalid object name '#Nalozi'." });
            Assert.Equal("SQL Server error 208: Invalid object name '#Nalozi'.", text);
            Assert.Equal("an unknown error", ResultShapeMatcher.DescribeError(null));
            Assert.Equal("SQL Server error 208: Inval…", ResultShapeMatcher.DescribeError(new DescribedColumn { ErrorNumber = 208, ErrorMessage = "Invalid object name '#Nalozi'." }, 5));
        }
    }
}
