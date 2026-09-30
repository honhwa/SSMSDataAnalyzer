using System.Collections.Generic;
using SsmsDataAnalyzer.Core.SourceControl;
using Xunit;

namespace SsmsDataAnalyzer.Tests.SourceControl
{
    public class TableColumnComparerTests
    {
        private static TableColumn Col(string name, string type = "int", int? length = null, int? precision = null,
            int? scale = null, bool nullable = false, bool identity = false, bool computed = false) =>
            new TableColumn(name, type, length, precision, scale, nullable, identity, computed);

        [Fact]
        public void Identical_Is_Empty()
        {
            var a = new List<TableColumn> { Col("Id", identity: true), Col("Name", "nvarchar", 50, nullable: true) };
            var b = new List<TableColumn> { Col("Id", identity: true), Col("Name", "nvarchar", 50, nullable: true) };
            Assert.Empty(TableColumnComparer.Compare(a, b));
        }

        [Fact]
        public void Names_Compare_Case_Insensitively()
        {
            Assert.Empty(TableColumnComparer.Compare(new[] { Col("ID") }, new[] { Col("id") }));
        }

        [Fact]
        public void Type_Changed()
        {
            IReadOnlyList<string> d = TableColumnComparer.Compare(new[] { Col("A", "int") }, new[] { Col("A", "bigint") });
            Assert.Equal("[A]: int NOT NULL in repo, bigint NOT NULL on server", Assert.Single(d));
        }

        [Fact]
        public void Length_Changed()
        {
            IReadOnlyList<string> d = TableColumnComparer.Compare(
                new[] { Col("A", "nvarchar", 50) }, new[] { Col("A", "nvarchar", 100) });
            Assert.Equal("[A]: nvarchar(50) NOT NULL in repo, nvarchar(100) NOT NULL on server", Assert.Single(d));
        }

        [Fact]
        public void Precision_And_Scale_Changed()
        {
            IReadOnlyList<string> d = TableColumnComparer.Compare(
                new[] { Col("Amount", "decimal", precision: 18, scale: 2) }, new[] { Col("Amount", "decimal", precision: 19, scale: 4) });
            Assert.Equal("[Amount]: decimal(18,2) NOT NULL in repo, decimal(19,4) NOT NULL on server", Assert.Single(d));
        }

        [Fact]
        public void Nullability_Changed()
        {
            IReadOnlyList<string> d = TableColumnComparer.Compare(new[] { Col("A", nullable: false) }, new[] { Col("A", nullable: true) });
            Assert.Equal("[A]: int NOT NULL in repo, int NULL on server", Assert.Single(d));
        }

        [Fact]
        public void Identity_Changed()
        {
            IReadOnlyList<string> d = TableColumnComparer.Compare(new[] { Col("A", identity: false) }, new[] { Col("A", identity: true) });
            Assert.Equal("[A]: int NOT NULL in repo, int IDENTITY NOT NULL on server", Assert.Single(d));
        }

        [Fact]
        public void Computed_Vs_Plain_Differs_But_Two_Computed_Match()
        {
            Assert.Single(TableColumnComparer.Compare(new[] { Col("A", computed: true) }, new[] { Col("A") }));
            Assert.Empty(TableColumnComparer.Compare(new[] { Col("A", computed: true) }, new[] { Col("A", "bigint", computed: true) }));
        }

        [Fact]
        public void Column_Added_On_Server()
        {
            IReadOnlyList<string> d = TableColumnComparer.Compare(new[] { Col("A") }, new[] { Col("A"), Col("Note", "nvarchar", 20) });
            Assert.Equal("[Note] only on server", Assert.Single(d));
        }

        [Fact]
        public void Column_Removed_On_Server()
        {
            IReadOnlyList<string> d = TableColumnComparer.Compare(new[] { Col("A"), Col("Old") }, new[] { Col("A") });
            Assert.Equal("[Old] only in repo", Assert.Single(d));
        }

        [Fact]
        public void Order_Differs()
        {
            IReadOnlyList<string> d = TableColumnComparer.Compare(new[] { Col("A"), Col("B") }, new[] { Col("B"), Col("A") });
            Assert.Equal("column order differs", Assert.Single(d));
        }

        [Fact]
        public void Nulls_Are_Treated_As_Empty()
        {
            Assert.Empty(TableColumnComparer.Compare(null, null));
        }
    }
}
