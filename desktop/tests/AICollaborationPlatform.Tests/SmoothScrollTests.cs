using AICollaborationPlatform.App.Views;

namespace AICollaborationPlatform.Tests;

public class SmoothScrollTests
{
    [Theory]
    [InlineData(-120, 120, 120)]   // one notch down scrolls one notch's worth
    [InlineData(120, 120, -120)]   // up goes back
    [InlineData(-15, 120, 15)]     // a touchpad's small nudge moves only a little, not a whole notch
    [InlineData(-360, 120, 360)]   // a fast flick sent as one big delta moves further
    public void Distance_follows_how_far_the_wheel_or_finger_moved(int delta, double perNotch, double expected) =>
        Assert.Equal(expected, SmoothScroll.Distance(delta, perNotch), 3);

    [Fact]
    public void Each_frame_closes_part_of_the_gap_and_eases_out()
    {
        var frame = TimeSpan.FromMilliseconds(16.7);
        var first = SmoothScroll.Step(0, 120, frame);
        var second = SmoothScroll.Step(first, 120, frame);

        Assert.InRange(first, 20, 40);                 // starts moving straight away
        Assert.True(second - first < first);           // then slows down as it arrives
    }

    [Fact]
    public void It_arrives_within_about_a_quarter_second_at_any_frame_rate()
    {
        foreach (var fps in new[] { 30.0, 60.0, 144.0 })
        {
            double position = 0;
            var frames = 0;
            while (position != 120 && frames < 1000)
            {
                position = SmoothScroll.Step(position, 120, TimeSpan.FromSeconds(1 / fps));
                frames++;
            }

            Assert.Equal(120, position);                       // lands exactly on the target
            Assert.InRange(frames / fps, 0.2, 0.6);            // same feel whatever the frame rate
        }
    }

    [Fact]
    public void Snaps_when_nearly_there_instead_of_crawling_forever() =>
        Assert.Equal(100, SmoothScroll.Step(99.8, 100, TimeSpan.FromMilliseconds(1)));
}
