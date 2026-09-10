using AniMeido.Contracts.Models;

namespace AniMeido.Tests
{
    public class SeasonHelperTests
    {
        [Theory]
        [InlineData(1, Season.Winter)]
        [InlineData(2, Season.Winter)]
        [InlineData(3, Season.Winter)]
        [InlineData(4, Season.Spring)]
        [InlineData(5, Season.Spring)]
        [InlineData(6, Season.Spring)]
        [InlineData(7, Season.Summer)]
        [InlineData(8, Season.Summer)]
        [InlineData(9, Season.Summer)]
        [InlineData(10, Season.Fall)]
        [InlineData(11, Season.Fall)]
        [InlineData(12, Season.Fall)]
        public void FromMonth_ReturnsCorrectSeason(int month, Season expected)
        {
            Assert.Equal(expected, SeasonHelper.FromMonth(month));
        }

        [Theory]
        [InlineData(Season.Winter, 1)]
        [InlineData(Season.Spring, 4)]
        [InlineData(Season.Summer, 7)]
        [InlineData(Season.Fall, 10)]
        public void ToMonth_ReturnsCorrectMonth(Season season, int expected)
        {
            Assert.Equal(expected, SeasonHelper.ToMonth(season));
        }

        [Fact]
        public void FromMonth_Invalid_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => SeasonHelper.FromMonth(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => SeasonHelper.FromMonth(13));
        }

        [Fact]
        public void GetCurrentSeason_ReturnsValidSeason()
        {
            var (year, season) = SeasonHelper.GetCurrentSeason();
            Assert.True(year >= 2020);
            Assert.True(Enum.IsDefined(season));
        }
    }
}
