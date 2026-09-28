using SsmsDataAnalyzer.Core.SourceControl;
using Xunit;

namespace SsmsDataAnalyzer.Tests.SourceControl
{
    /// <summary>One test per §7 rule: equivalence AND a near-miss that must stay different.</summary>
    public class ModuleDefinitionNormalizerTests
    {
        [Fact]
        public void Rule1_Text_Before_First_Create_Is_Ignored()
        {
            string withHeader = "﻿-- Author: mtusek\nSET ANSI_NULLS ON\nGO\nCREATE PROCEDURE dbo.Foo AS SELECT 1";
            string bare = "CREATE PROCEDURE dbo.Foo AS SELECT 1";
            Assert.True(ModuleDefinitionNormalizer.AreEquivalent(withHeader, bare));

            // Near-miss: a real difference AFTER the first CREATE must still be caught.
            string differsAfterCreate = "CREATE PROCEDURE dbo.Foo AS SELECT 2";
            Assert.False(ModuleDefinitionNormalizer.AreEquivalent(withHeader, differsAfterCreate));
        }

        [Fact]
        public void Rule2_Create_Alter_And_CreateOrAlter_Are_Equivalent()
        {
            string create = "CREATE PROCEDURE dbo.Foo AS SELECT 1";
            string alter = "ALTER PROCEDURE dbo.Foo AS SELECT 1";
            string createOrAlter = "CREATE OR ALTER PROCEDURE dbo.Foo AS SELECT 1";
            Assert.True(ModuleDefinitionNormalizer.AreEquivalent(create, alter));
            Assert.True(ModuleDefinitionNormalizer.AreEquivalent(create, createOrAlter));

            // Near-miss: same action-equivalence machinery must not blur the object TYPE keyword.
            string createView = "CREATE VIEW dbo.Foo AS SELECT 1";
            Assert.False(ModuleDefinitionNormalizer.AreEquivalent(create, createView));
        }

        [Fact]
        public void Rule3_Whitespace_And_Comments_Are_Ignored()
        {
            string a = "CREATE PROCEDURE dbo.Foo\nAS\nSELECT 1 -- returns one";
            string b = "CREATE   PROCEDURE   dbo.Foo AS /* block */ SELECT 1";
            Assert.True(ModuleDefinitionNormalizer.AreEquivalent(a, b));

            // Near-miss: a changed identifier (not a comment) must still be a real change.
            string differentIdentifier = "CREATE PROCEDURE dbo.Bar\nAS\nSELECT 1 -- returns one";
            Assert.False(ModuleDefinitionNormalizer.AreEquivalent(a, differentIdentifier));
        }

        [Fact]
        public void Rule4_Keywords_And_Identifiers_Compare_Case_Insensitively()
        {
            string bracketed = "CREATE PROCEDURE [ABB] AS select 1";
            string quoted = "create procedure \"ABB\" AS SELECT 1";
            string plain = "Create Procedure abb As Select 1";
            Assert.True(ModuleDefinitionNormalizer.AreEquivalent(bracketed, quoted));
            Assert.True(ModuleDefinitionNormalizer.AreEquivalent(bracketed, plain));

            // Near-miss: a genuinely different name must not collapse together.
            string differentName = "CREATE PROCEDURE [XYZ] AS select 1";
            Assert.False(ModuleDefinitionNormalizer.AreEquivalent(bracketed, differentName));
        }

        [Fact]
        public void Rule5_String_Literals_Compare_Exactly()
        {
            string same = "CREATE PROCEDURE dbo.Foo AS SELECT 'abc'";
            string sameAgain = "CREATE   PROCEDURE dbo.Foo AS SELECT 'abc'";
            Assert.True(ModuleDefinitionNormalizer.AreEquivalent(same, sameAgain));

            // Near-miss: 'abc' vs 'ABC' -- case in a string literal IS a real change.
            string differentCase = "CREATE PROCEDURE dbo.Foo AS SELECT 'ABC'";
            Assert.False(ModuleDefinitionNormalizer.AreEquivalent(same, differentCase));
        }

        [Fact]
        public void Rule6_Trailing_Go_Is_Ignored()
        {
            string withGo = "CREATE PROCEDURE dbo.Foo AS SELECT 1\nGO";
            string withoutGo = "CREATE PROCEDURE dbo.Foo AS SELECT 1";
            Assert.True(ModuleDefinitionNormalizer.AreEquivalent(withGo, withoutGo));

            // Near-miss: trailing GO must not swallow a real difference just before it.
            string differsBeforeGo = "CREATE PROCEDURE dbo.Foo AS SELECT 2\nGO";
            Assert.False(ModuleDefinitionNormalizer.AreEquivalent(withGo, differsBeforeGo));
        }

        [Fact]
        public void Rule0_Crlf_Vs_Lf_Line_Endings_Throughout_Are_Equivalent()
        {
            string crlf = "CREATE PROCEDURE dbo.Foo\r\nAS\r\nBEGIN\r\n    SELECT 1\r\nEND\r\n";
            string lf = "CREATE PROCEDURE dbo.Foo\nAS\nBEGIN\n    SELECT 1\nEND\n";
            Assert.True(ModuleDefinitionNormalizer.AreEquivalent(crlf, lf));
        }

        [Fact]
        public void Rule0_Multiline_String_Literal_Crlf_Vs_Lf_Is_Equivalent()
        {
            // sys.sql_modules.definition keeps line endings exactly as sent (verified live,
            // 2026-09-28), so a multi-line string literal -- compared exactly under rule 5 --
            // must still be equivalent when only its line endings differ.
            string crlf = "CREATE PROCEDURE dbo.Foo AS SELECT N'line one\r\nline two'";
            string lf = "CREATE PROCEDURE dbo.Foo AS SELECT N'line one\nline two'";
            Assert.True(ModuleDefinitionNormalizer.AreEquivalent(crlf, lf));

            // Near-miss: the string TEXT itself differs, not just the line ending -- must stay different.
            string differentText = "CREATE PROCEDURE dbo.Foo AS SELECT N'line one\nline TWO'";
            Assert.False(ModuleDefinitionNormalizer.AreEquivalent(crlf, differentText));
        }

        [Fact]
        public void Dotted_Bracketed_Name_Is_Not_Equivalent_To_Three_Plain_Identifiers()
        {
            // [ABB].[ABB.ChangeStatus] is (identifier, dot, identifier-with-embedded-dot);
            // ABB.ABB.ChangeStatus is three plain identifiers joined by two real dots.
            string bracketed = "CREATE PROCEDURE [ABB].[ABB.ChangeStatus] AS SELECT 1";
            string plainThreeParts = "CREATE PROCEDURE ABB.ABB.ChangeStatus AS SELECT 1";
            Assert.False(ModuleDefinitionNormalizer.AreEquivalent(bracketed, plainThreeParts));
        }

        [Fact]
        public void Empty_And_Null_Text_Never_Throws()
        {
            Assert.Equal(string.Empty, ModuleDefinitionNormalizer.Normalize(""));
            Assert.Equal(string.Empty, ModuleDefinitionNormalizer.Normalize(null));
            Assert.True(ModuleDefinitionNormalizer.AreEquivalent(null, ""));
        }
    }
}
