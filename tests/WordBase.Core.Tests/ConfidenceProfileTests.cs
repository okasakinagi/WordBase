using WordBase.Core.Models;
using WordBase.Core.Scheduling;
using Xunit;

namespace WordBase.Core.Tests;

public class ConfidenceProfileTests
{
    [Fact]
    public void KnownAndCorrect_MapsToGood()
    {
        var profile = ConfidenceProfile.Default;
        var confidence = profile.For(Interaction.KnownAndCorrect);
        Assert.Equal(1.0, confidence);
        Assert.Equal(Rating.Good, profile.ToRating(confidence));
    }

    [Fact]
    public void KnownNotQuizzed_MapsToHard()
    {
        var profile = ConfidenceProfile.Default;
        var confidence = profile.For(Interaction.KnownNotQuizzed);
        Assert.Equal(0.7, confidence);
        Assert.Equal(Rating.Hard, profile.ToRating(confidence));
    }

    [Fact]
    public void KnownButWrong_MapsToAgain()
    {
        var profile = ConfidenceProfile.Default;
        var confidence = profile.For(Interaction.KnownButWrong);
        Assert.Equal(0.2, confidence);
        Assert.Equal(Rating.Again, profile.ToRating(confidence));
    }

    [Fact]
    public void NotKnown_MapsToAgain()
    {
        var profile = ConfidenceProfile.Default;
        Assert.Equal(Rating.Again, profile.ToRating(profile.For(Interaction.NotKnown)));
    }

    [Fact]
    public void ExplicitRating_Wins()
    {
        Assert.Equal(Rating.Easy, ConfidenceProfile.Default.ToRating(0.2, Rating.Easy));
    }
}
