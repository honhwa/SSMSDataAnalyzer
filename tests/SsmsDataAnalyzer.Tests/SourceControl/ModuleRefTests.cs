using SsmsDataAnalyzer.Core.SourceControl;
using Xunit;

namespace SsmsDataAnalyzer.Tests.SourceControl
{
    public class ModuleRefTests
    {
        [Fact]
        public void Equality_Is_Schema_And_Name_OrdinalIgnoreCase_Kind_Excluded()
        {
            var a = new ModuleRef("ABB", "ChangeStatus", DbObjectKind.Procedure);
            var b = new ModuleRef("abb", "changestatus", DbObjectKind.View);
            Assert.Equal(a, b);
            Assert.Equal(a.GetHashCode(), b.GetHashCode());
        }

        [Fact]
        public void Unqualified_Ref_Never_Equals_Qualified_Ref()
        {
            var unqualified = new ModuleRef(null, "ChangeStatus", DbObjectKind.Procedure);
            var qualified = new ModuleRef("dbo", "ChangeStatus", DbObjectKind.Procedure);
            Assert.NotEqual(unqualified, qualified);
        }

        [Fact]
        public void ToString_Brackets_Schema_And_Name()
        {
            var m = new ModuleRef("ABB", "ChangeStatus", DbObjectKind.Procedure);
            Assert.Equal("[ABB].[ChangeStatus]", m.ToString());
        }

        [Fact]
        public void ToString_Doubles_Closing_Bracket_Inside_Name()
        {
            var m = new ModuleRef(null, "Weird]Name", DbObjectKind.View);
            Assert.Equal("[Weird]]Name]", m.ToString());
        }

        [Fact]
        public void WithSchema_Replaces_Schema_Keeps_Name_And_Kind()
        {
            var m = new ModuleRef(null, "ChangeStatus", DbObjectKind.Procedure);
            ModuleRef withSchema = m.WithSchema("ABB");
            Assert.Equal("ABB", withSchema.Schema);
            Assert.Equal("ChangeStatus", withSchema.Name);
            Assert.Equal(DbObjectKind.Procedure, withSchema.Kind);
        }
    }
}
