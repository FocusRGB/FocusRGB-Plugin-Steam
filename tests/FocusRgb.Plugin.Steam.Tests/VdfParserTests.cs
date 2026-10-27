namespace FocusRgb.Plugin.Steam.Tests
{
    public sealed class VdfParserTests
    {
        [Fact]
        public void Parse_ReadsNestedObjectsEscapesAndComments()
        {
            var document = VdfParser.Parse(
                "// fixture\n\"root\" { \"path\" \"D:\\\\Games\" \"quoted\" \"a\\\"b\" }");

            var root = Assert.IsType<VdfObject>(document.GetObject("root"));
            Assert.Equal(@"D:\Games", root.GetString("path"));
            Assert.Equal("a\"b", root.GetString("quoted"));
        }

        [Fact]
        public void Parse_Throws_WhenObjectIsIncomplete()
        {
            Assert.Throws<FormatException>(() => VdfParser.Parse("\"root\" { \"key\" \"value\""));
        }

        [Fact]
        public void Parse_TreatsQuotedBraceAsAValue()
        {
            var document = VdfParser.Parse("\"root\" { \"value\" \"{\" }");

            Assert.Equal("{", document.GetObject("root")!.GetString("value"));
        }

        [Fact]
        public void Parse_RejectsExcessiveDepthAndSize()
        {
            var deep = string.Concat(Enumerable.Repeat("\"node\" { ", 66))
                + string.Concat(Enumerable.Repeat("}", 66));

            Assert.Throws<FormatException>(() => VdfParser.Parse(deep));
            Assert.Throws<FormatException>(() => VdfParser.Parse(new string('x', 4 * 1024 * 1024 + 1)));
        }
    }
}
