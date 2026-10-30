using AniMeido.Plugin.Base.Services;

namespace AniMeido.Tests;

public sealed class ArchiveRatingTests
{
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(0)]
    [InlineData(0.4)]
    [InlineData(10.5)]
    [InlineData(8.25)]
    public void RatingMustBeAFiniteHalfPointInTheExistingRange(double rating)
        => Assert.Throws<ArgumentOutOfRangeException>(() => ArchiveService.ValidateRating(rating));

    [Fact]
    public void MissingRatingAndEverySupportedHalfPointRemainValid()
    {
        ArchiveService.ValidateRating(null);
        for (var halfPoints = 1; halfPoints <= 20; halfPoints++)
            ArchiveService.ValidateRating(halfPoints / 2d);
    }
}
