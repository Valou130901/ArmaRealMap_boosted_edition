using GameRealisticMap.Arma3.GameEngine;

namespace GameRealisticMap.Arma3.Test.GameEngine
{
    public class GameConfigNamesTest
    {
        private const string ConfigWithInclude = @"class CfgWorlds
{
	class kelleysisland
	{
		worldName = ""kelleysisland.wrp"";
		class Names
		{
			#include ""kelleysisland.h""
		};
	};
};";

        private const string NamesHeader = @"class ECCorrections
{
	name=""Erie County Corrections"";
	position[]={2835.97,4577.44};
	type=""StrongpointArea"";
	radiusA=206.23;
};
class CedarPoint
{
	name=""Cedar Point"";
	position[]={1200.5,3400.25};
	type=""NameVillage"";
};";

        [Fact]
        public void ReadFromContent_FollowsInclude()
        {
            var names = GameConfigNames.ReadFromContent(ConfigWithInclude, _ => NamesHeader);

            Assert.Equal(2, names.Count);
            Assert.Equal("Erie County Corrections", names[0].Name);
            Assert.Equal("Cedar Point", names[1].Name);
            Assert.Equal(1200.5f, names[1].X);
            Assert.Equal(3400.25f, names[1].Y);
            Assert.True(names[1].IsSettlement);
            Assert.False(names[0].IsSettlement);
        }

        /// <summary>Every config shipped by the game uses CRLF, which the include regex has to accept.</summary>
        [Fact]
        public void ReadFromContent_FollowsIncludeWithWindowsLineEndings()
        {
            var config = ConfigWithInclude.Replace("\r\n", "\n").Replace("\n", "\r\n");
            var header = NamesHeader.Replace("\r\n", "\n").Replace("\n", "\r\n");

            var names = GameConfigNames.ReadFromContent(config, _ => header);

            Assert.Equal(2, names.Count);
            Assert.Equal("Cedar Point", names[1].Name);
        }

        [Fact]
        public void ReadFromContent_UnresolvedIncludeIsDropped()
        {
            var names = GameConfigNames.ReadFromContent(ConfigWithInclude, _ => null);

            Assert.Empty(names);
        }

        [Fact]
        public void ReadFromContent_WithoutResolverKeepsInlineNames()
        {
            var config = @"class CfgWorlds
{
	class map
	{
		class Names
		{
			class Town1
			{
				name=""Town One"";
				position[]={10,20};
				type=""NameCityCapital"";
			};
		};
	};
};";
            var names = GameConfigNames.ReadFromContent(config);

            var name = Assert.Single(names);
            Assert.Equal("Town One", name.Name);
            Assert.True(name.IsSettlement);
        }

        [Fact]
        public void ReadFromContent_IncludeCycleTerminates()
        {
            var names = GameConfigNames.ReadFromContent(ConfigWithInclude, _ => "#include \"loop.h\"");

            Assert.Empty(names);
        }
    }
}
