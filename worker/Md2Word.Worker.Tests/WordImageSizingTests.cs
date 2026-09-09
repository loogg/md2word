using Md2Word.Worker.Services;

namespace Md2Word.Worker.Tests;

public sealed class WordImageSizingTests
{
    [Fact]
    public void AvailableWidthSubtractsMarginsAndPositiveParagraphIndents()
    {
        var width = WordAutomationService.CalculateAvailableImageWidth(
            pageWidth: 595.3f,
            leftMargin: 56.7f,
            rightMargin: 56.7f,
            leftIndent: 18f,
            rightIndent: 12f);

        Assert.Equal(451.9f, width, precision: 1);
    }

    [Fact]
    public void AvailableWidthIgnoresNegativeParagraphIndents()
    {
        var width = WordAutomationService.CalculateAvailableImageWidth(
            pageWidth: 595.3f,
            leftMargin: 56.7f,
            rightMargin: 56.7f,
            leftIndent: -18f,
            rightIndent: -12f);

        Assert.Equal(481.9f, width, precision: 1);
    }

    [Fact]
    public void OversizeImageShrinksProportionally()
    {
        var result = WordAutomationService.ConstrainImageSize(1200f, 800f, 480f);

        Assert.True(result.Resized);
        Assert.Equal(480f, result.Width);
        Assert.Equal(320f, result.Height);
    }

    [Fact]
    public void ImageWithinWordLayoutToleranceIsNotRescaled()
    {
        var result = WordAutomationService.ConstrainImageSize(
            width: 481.95f,
            height: 271.1f,
            availableWidth: 481.9f);

        Assert.False(result.Resized);
        Assert.Equal(481.95f, result.Width);
        Assert.Equal(271.1f, result.Height);
    }

    [Fact]
    public void ImageBeyondWordLayoutToleranceStillShrinks()
    {
        var result = WordAutomationService.ConstrainImageSize(
            width: 483f,
            height: 271.69f,
            availableWidth: 481.9f);

        Assert.True(result.Resized);
        Assert.Equal(481.9f, result.Width);
    }

    [Theory]
    [InlineData(400f, 200f, 480f)]
    [InlineData(0f, 200f, 480f)]
    [InlineData(400f, 0f, 480f)]
    [InlineData(400f, 200f, 0f)]
    public void ImageIsNotChangedWhenNoSafeShrinkIsRequired(float width, float height, float availableWidth)
    {
        var result = WordAutomationService.ConstrainImageSize(width, height, availableWidth);

        Assert.False(result.Resized);
        Assert.Equal(width, result.Width);
        Assert.Equal(height, result.Height);
    }
}
