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
    }
}
