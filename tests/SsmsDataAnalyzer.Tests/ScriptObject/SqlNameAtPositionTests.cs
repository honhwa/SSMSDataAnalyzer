using SsmsDataAnalyzer.Core.ScriptObject;
using Xunit;

namespace SsmsDataAnalyzer.Tests.ScriptObject
{
    public class SqlNameAtPositionTests
    {
        /// <summary>Finds the name at the position marked by '|' (the marker is removed).</summary>
        private static SqlNameLookupResult At(string textWithCaret)
        {
            int caret = textWithCaret.IndexOf('|');
            Assert.True(caret >= 0, "test text needs a | caret marker");
            string text = textWithCaret.Remove(caret, 1);
            return SqlNameAtPosition.Find(text, caret);
        }

        private static SqlMultipartName Found(string textWithCaret)
        {
            var result = At(textWithCaret);
            Assert.Equal(SqlNameLookupStatus.Found, result.Status);
            return result.Name;
        }

        [Fact]
        public void PlainName_InsideWord()
        {
            var name = Found("SELECT * FROM Ord|ers WHERE 1 = 1");
            Assert.Null(name.Schema);
            Assert.Equal("Orders", name.Name);
        }

        [Fact]
        public void CaretAtStartOrJustAfterName_CountsAsOnName()
        {
            Assert.Equal("Orders", Found("SELECT * FROM |Orders").Name);
            Assert.Equal("Orders", Found("SELECT * FROM Orders|").Name);
            Assert.Equal("Orders", Found("EXEC dbo.Orders|;").Name);
        }

        [Fact]
        public void TwoPartName_CaretOnSchema_ReturnsWholeName()
        {
            var name = Found("SELECT * FROM d|bo.Orders o");
            Assert.Equal("dbo", name.Schema);
            Assert.Equal("Orders", name.Name);
        }

        [Fact]
        public void CaretOnDot_ReturnsWholeName()
        {
            var name = Found("SELECT * FROM dbo|.Orders");
            Assert.Equal("dbo", name.Schema);
            Assert.Equal("Orders", name.Name);
        }

        [Fact]
        public void BracketedNameWithDots_IsOnePart()
        {
            var name = Found("SELECT * FROM [Finances].[Accounting.Compensation.Corr|ection.Item] AS i");
            Assert.Null(name.Database);
            Assert.Equal("Finances", name.Schema);
            Assert.Equal("Accounting.Compensation.Correction.Item", name.Name);
            Assert.Equal("[Finances].[Accounting.Compensation.Correction.Item]", name.ToObjectIdArgument());
        }

        [Fact]
        public void BracketedName_CaretOnOpeningBracket()
        {
            var name = Found("SELECT * FROM |[dbo].[Intervention.ABB.Request.Change.History]");
            Assert.Equal("dbo", name.Schema);
            Assert.Equal("Intervention.ABB.Request.Change.History", name.Name);
        }

        [Fact]
        public void BracketEscape_IsUnescapedAndRebracketed()
        {
            var name = Found("SELECT * FROM dbo.[Bracket]]Ta|ble]");
            Assert.Equal("Bracket]Table", name.Name);
            Assert.Equal("[dbo].[Bracket]]Table]", name.ToObjectIdArgument());
        }

        [Fact]
        public void QuotedIdentifier_IsUnescaped()
        {
            var name = Found("SELECT * FROM \"my \"\"odd\"\" sch|ema\".\"T\"");
            Assert.Equal("my \"odd\" schema", name.Schema);
            Assert.Equal("T", name.Name);
        }

        [Fact]
        public void ThreePartName_HasDatabase()
        {
            var name = Found("SELECT * FROM SalesDb.dbo.Cust|omers");
            Assert.Equal("SalesDb", name.Database);
            Assert.Equal("dbo", name.Schema);
            Assert.Equal("Customers", name.Name);
        }

        [Fact]
        public void ThreePartName_WithEmptySchema()
        {
            var name = Found("SELECT * FROM SalesDb..Cust|omers");
            Assert.Equal("SalesDb", name.Database);
            Assert.Null(name.Schema);
            Assert.Equal("Customers", name.Name);
            Assert.Equal("[SalesDb]..[Customers]", name.ToString());
        }

        [Fact]
        public void FourPartName_HasServer()
        {
            var name = Found("SELECT * FROM [LINKED\\SRV].SalesDb.dbo.Customers|");
            Assert.Equal("LINKED\\SRV", name.Server);
            Assert.Equal("SalesDb", name.Database);
        }

        [Fact]
        public void FiveParts_AreRejected()
        {
            Assert.Equal(SqlNameLookupStatus.TooManyParts, At("SELECT * FROM a.b.c.d.e|").Status);
        }

        [Fact]
        public void WhitespaceAroundDots_IsStillOneName()
        {
            var name = Found("SELECT * FROM dbo . Ord|ers");
            Assert.Equal("dbo", name.Schema);
            Assert.Equal("Orders", name.Name);
        }

        [Fact]
        public void AdjacentWords_AreNotJoined()
        {
            var name = Found("SELECT * FROM dbo.Orders o|rd WHERE 1 = 1");
            Assert.Null(name.Schema);
            Assert.Equal("ord", name.Name);
        }

        [Fact]
        public void TrailingDotWhileTyping_IsIgnored()
        {
            var name = Found("SELECT * FROM d|bo.");
            Assert.Null(name.Schema);
            Assert.Equal("dbo", name.Name);
        }

        [Fact]
        public void CommaSeparatedNames_PickOnlyTheOneUnderCaret()
        {
            Assert.Equal("B", Found("SELECT * FROM dbo.A, dbo.|B").Name);
        }

        [Fact]
        public void Variable_IsRejected()
        {
            Assert.Equal(SqlNameLookupStatus.Variable, At("DECLARE @Ord|ers int;").Status);
        }

        [Fact]
        public void TempTable_IsRejected()
        {
            Assert.Equal(SqlNameLookupStatus.TemporaryObject, At("SELECT * FROM #tm|p").Status);
            Assert.Equal(SqlNameLookupStatus.TemporaryObject, At("SELECT * FROM ##glo|bal").Status);
        }

        [Fact]
        public void ReservedKeyword_IsRejected_ButBracketedKeywordIsAName()
        {
            Assert.Equal(SqlNameLookupStatus.Keyword, At("SEL|ECT * FROM x").Status);
            Assert.Equal("Select", Found("SELECT * FROM dbo.[Sel|ect]").Name);
        }

        [Fact]
        public void NonReservedWord_IsStillAName()
        {
            Assert.Equal("Name", Found("SELECT * FROM dbo.Na|me").Name);
        }

        [Fact]
        public void InsideLineComment_IsRejected()
        {
            Assert.Equal(SqlNameLookupStatus.InsideComment, At("-- see dbo.Ord|ers\r\nSELECT 1").Status);
        }

        [Fact]
        public void InsideNestedBlockComment_IsRejected()
        {
            Assert.Equal(SqlNameLookupStatus.InsideComment, At("/* outer /* inner */ still dbo.Ord|ers */ SELECT 1").Status);
            Assert.Equal("Orders", Found("/* outer /* inner */ done */ SELECT * FROM Ord|ers").Name);
        }

        [Fact]
        public void InsideString_IsRejected()
        {
            Assert.Equal(SqlNameLookupStatus.InsideString, At("SELECT 'it''s dbo.Ord|ers'").Status);
            Assert.Equal(SqlNameLookupStatus.InsideString, At("SELECT N'dbo.Ord|ers'").Status);
        }

        [Fact]
        public void StringBeforeName_DoesNotConfuseLexer()
        {
            Assert.Equal("Orders", Found("SELECT 'a]b.[c' FROM dbo.Ord|ers").Name);
        }

        [Fact]
        public void NumbersAndPunctuation_AreNothing()
        {
            Assert.Equal(SqlNameLookupStatus.NothingAtPosition, At("SELECT 12|3").Status);
            Assert.Equal(SqlNameLookupStatus.NothingAtPosition, At("SELECT a +| b").Status);
            Assert.Equal(SqlNameLookupStatus.NothingAtPosition, At("|").Status);
        }

        [Fact]
        public void MultiLineStatement_UsesCorrectLine()
        {
            var name = Found("SELECT *\r\nFROM [Finances].[Accounting.Compensation.Correction.Item] AS i\r\nJOIN dbo.Or|ders o ON 1 = 1");
            Assert.Equal("dbo", name.Schema);
            Assert.Equal("Orders", name.Name);
        }

        [Fact]
        public void UnterminatedBracket_StillReturnsItsText()
        {
            var name = Found("SELECT * FROM dbo.[Unfinish|ed");
            Assert.Equal("Unfinished", name.Name);
        }

        [Fact]
        public void Span_CoversWholeName()
        {
            const string text = "SELECT * FROM [dbo].[A.B] x";
            var result = SqlNameAtPosition.Find(text, text.IndexOf("A.B"));
            Assert.Equal("[dbo].[A.B]", text.Substring(result.Start, result.Length));
        }

        [Fact]
        public void OutOfRange_IsNothing()
        {
            Assert.Equal(SqlNameLookupStatus.NothingAtPosition, SqlNameAtPosition.Find("abc", 10).Status);
            Assert.Equal(SqlNameLookupStatus.NothingAtPosition, SqlNameAtPosition.Find(null, 0).Status);
        }
    }
}
