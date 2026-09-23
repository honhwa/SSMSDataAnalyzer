using System;
using System.Collections.Generic;
using System.Linq;
using SsmsDataAnalyzer.Core.ResultShape;
using Xunit;

namespace SsmsDataAnalyzer.Tests.ResultShape
{
    /// <summary>
    /// "SELECT FA.*, ..." against a query SQL Server cannot describe (a #temp table in scope).
    /// The text alone cannot say how many columns FA.* is, so the catalog is asked — but the
    /// expanded list still has to match the grid's headers one-for-one before any source is
    /// reported. A wrong answer here opens the WRONG TABLE, so most of these pin a refusal.
    /// </summary>
    public class StaticStarExpansionTests
    {
        private static StaticSelectItem Col(string qualifier, string column, string alias = null) =>
            new StaticSelectItem(alias ?? column, qualifier?.Split('.'), column);

        private static StaticSelectItem Star(string qualifier = null) =>
            StaticSelectItem.Star(qualifier?.Split('.'));

        private static StaticFromItem Table(string schema, string table, string alias = null, string db = null) =>
            new StaticFromItem(alias, db, schema, table, StaticFromKind.NamedTable);

        private static StaticQueryShape Shape(IEnumerable<StaticSelectItem> select, IEnumerable<StaticFromItem> from, params string[] ctes) =>
            new StaticQueryShape(select.ToList(), from.ToList(), ctes);

        /// <summary>A catalog that knows one table, by table name.</summary>
        private static Func<StaticFromItem, IReadOnlyList<string>> Catalog(string table, params string[] columns) =>
            from => string.Equals(from.Table, table, StringComparison.OrdinalIgnoreCase) ? columns : null;

        // ---- the field report -------------------------------------------------------------

        [Fact]
        public void FieldReport_SelectStarPlusColumns_OverATempTableQuery_Resolves()
        {
            // SELECT FA.*, MLPGI.SourceChangeTypeID, MLPGI.PaymentGroupID
            // FROM MiddleLayer.[PaymentGroup.Item] MLPGI
            // JOIN Finances.Accounting FA ON ...
            // WHERE FA.ClientID IN (SELECT ClientID FROM #tbl12A)
            var shape = Shape(
                new[] { Star("FA"), Col("MLPGI", "SourceChangeTypeID"), Col("MLPGI", "PaymentGroupID") },
                new[] { Table("MiddleLayer", "PaymentGroup.Item", "MLPGI"), Table("Finances", "Accounting", "FA") });

            var result = StaticShapeResolver.Resolve(
                shape,
                new[] { "ID", "ClientID", "StatusID", "SourceChangeTypeID", "PaymentGroupID" },
                Catalog("Accounting", "ID", "ClientID", "StatusID"));

            Assert.True(result.IsMatch);
            Assert.Equal(5, result.Columns.Count);

            // The three expanded columns come from the starred table...
            for (int i = 0; i < 3; i++)
            {
                Assert.True(result.Columns[i].HasSource);
                Assert.Equal("Finances", result.Columns[i].Schema);
                Assert.Equal("Accounting", result.Columns[i].Table);
            }
            Assert.Equal("ID", result.Columns[0].Column);
            Assert.Equal("ClientID", result.Columns[1].Column);
            Assert.Equal("StatusID", result.Columns[2].Column);

            // ...and the explicit ones still come from theirs.
            Assert.Equal("PaymentGroup.Item", result.Columns[3].Table);
            Assert.Equal("SourceChangeTypeID", result.Columns[3].Column);
            Assert.Equal("PaymentGroup.Item", result.Columns[4].Table);
        }

        [Fact]
        public void BareStar_WithASingleTable_Resolves()
        {
            var shape = Shape(new[] { Star() }, new[] { Table("dbo", "Orders") });

            var result = StaticShapeResolver.Resolve(shape, new[] { "Id", "Total" }, Catalog("Orders", "Id", "Total"));

            Assert.True(result.IsMatch);
            AssertFrom(result.Columns[0], "dbo", "Orders", "Id");
            AssertFrom(result.Columns[1], "dbo", "Orders", "Total");
        }

        // ---- refusals ----------------------------------------------------------------------

        [Fact]
        public void ExpansionThatDoesNotMatchTheGrid_Declines()
        {
            // The table gained a column since the query ran: the grid has 2, the catalog says 3.
            // Reporting sources from a list that does not line up would mislabel every column
            // after the change, so this must refuse outright.
            var shape = Shape(new[] { Star("o") }, new[] { Table("dbo", "Orders", "o") });

            var result = StaticShapeResolver.Resolve(shape, new[] { "Id", "Total" }, Catalog("Orders", "Id", "Total", "AddedLater"));

            Assert.False(result.IsMatch);
        }

        [Fact]
        public void ExpansionWithDifferentNames_Declines()
        {
            var shape = Shape(new[] { Star("o") }, new[] { Table("dbo", "Orders", "o") });

            var result = StaticShapeResolver.Resolve(shape, new[] { "Id", "Total" }, Catalog("Orders", "Id", "Amount"));

            Assert.False(result.IsMatch);
        }

        [Fact]
        public void NoExpander_DeclinesExactlyAsBefore()
        {
            var shape = Shape(new[] { Star("o") }, new[] { Table("dbo", "Orders", "o") });

            var result = StaticShapeResolver.Resolve(shape, new[] { "Id", "Total" });

            Assert.False(result.IsMatch);
            Assert.Equal(StaticShapeResolver.StarReason, result.DeclineReason);
        }

        [Fact]
        public void BareStar_AcrossAJoin_Declines()
        {
            // Which table's columns come first is the server's business, not ours.
            var shape = Shape(
                new[] { Star() },
                new[] { Table("dbo", "Orders", "o"), Table("dbo", "Customers", "c") });

            Assert.False(StaticShapeResolver.Resolve(shape, new[] { "Id" }, Catalog("Orders", "Id")).IsMatch);
            Assert.Null(StaticShapeResolver.ResolveStarTargets(shape));
        }

        [Fact]
        public void StarOnATempTable_Declines()
        {
            var shape = Shape(new[] { Star("t") }, new[] { Table(null, "#tbl12A", "t") });

            Assert.False(StaticShapeResolver.Resolve(shape, new[] { "ClientID" }, Catalog("#tbl12A", "ClientID")).IsMatch);
            Assert.Null(StaticShapeResolver.ResolveStarTargets(shape));
        }

        [Fact]
        public void StarOnACte_Declines()
        {
            var shape = Shape(new[] { Star("c") }, new[] { Table(null, "c", null) }, "c");

            Assert.Null(StaticShapeResolver.ResolveStarTargets(shape));
        }

        [Fact]
        public void StarOnAnUnknownAlias_Declines()
        {
            var shape = Shape(new[] { Star("zz") }, new[] { Table("dbo", "Orders", "o") });

            Assert.Null(StaticShapeResolver.ResolveStarTargets(shape));
        }

        [Fact]
        public void StarOnADuplicatedAlias_Declines()
        {
            // The same alias twice is ambiguous, so it names no table.
            var shape = Shape(
                new[] { Star("o") },
                new[] { Table("dbo", "Orders", "o"), Table("dbo", "OldOrders", "o") });

            Assert.Null(StaticShapeResolver.ResolveStarTargets(shape));
        }

        [Fact]
        public void StarWhoseTableCannotBeRead_Declines()
        {
            var shape = Shape(new[] { Star("o") }, new[] { Table("dbo", "Orders", "o") });

            var result = StaticShapeResolver.Resolve(shape, new[] { "Id" }, _ => null);

            Assert.False(result.IsMatch);
            Assert.Equal(StaticShapeResolver.StarReason, result.DeclineReason);
        }

        [Fact]
        public void QualifiedStar_MustAgreeWithTheSchema()
        {
            // "Other.Accounting.*" does not name Finances.Accounting.
            var shape = Shape(new[] { Star("Other.Accounting") }, new[] { Table("Finances", "Accounting") });

            Assert.Null(StaticShapeResolver.ResolveStarTargets(shape));
        }

        [Fact]
        public void SetOperationWithAStar_Declines()
        {
            var shape = new StaticQueryShape(
                new[] { Star("o") }.ToList(),
                new[] { Table("dbo", "Orders", "o") }.ToList(),
                new string[0],
                isSetOperation: true);

            Assert.Null(StaticShapeResolver.ResolveStarTargets(shape));
            Assert.False(StaticShapeResolver.Resolve(shape, new[] { "Id" }, Catalog("Orders", "Id")).IsMatch);
        }

        [Fact]
        public void NoStar_NeedsNoExpansion()
        {
            var shape = Shape(new[] { Col("o", "Id") }, new[] { Table("dbo", "Orders", "o") });

            Assert.Empty(StaticShapeResolver.ResolveStarTargets(shape));
            Assert.True(StaticShapeResolver.Resolve(shape, new[] { "Id" }).IsMatch);
        }

        private static void AssertFrom(StaticColumnSource c, string schema, string table, string column)
        {
            Assert.True(c.HasSource);
            Assert.Equal(schema, c.Schema);
            Assert.Equal(table, c.Table);
            Assert.Equal(column, c.Column);
        }
    }
}
