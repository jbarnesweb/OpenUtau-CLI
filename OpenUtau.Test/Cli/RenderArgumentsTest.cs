using OpenUtau.Cli;
using Xunit;

namespace OpenUtau.Test.Cli;

public class RenderArgumentsTest {
    [Fact]
    public void ParsesRenderArguments() {
        var success = RenderArguments.TryParse(
            new[] { "render", "song.USTX", "--out", "song.WAV" },
            out var arguments,
            out var error);

        Assert.True(success);
        Assert.Equal(string.Empty, error);
        Assert.Equal("song.USTX", arguments!.InputPath);
        Assert.Equal("song.WAV", arguments.OutputPath);
    }

    [Theory]
    [InlineData(new[] { "render" }, "Missing input .ustx path.")]
    [InlineData(new[] { "render", "song.mid" }, "Input path must end with .ustx.")]
    [InlineData(new[] { "render", "song.ustx" }, "Missing --out option.")]
    [InlineData(new[] { "render", "song.ustx", "-o" }, "Expected --out option after input path.")]
    [InlineData(new[] { "render", "song.ustx", "--out" }, "Missing output .wav path.")]
    [InlineData(new[] { "render", "song.ustx", "--out", "song.mp3" }, "Output path must end with .wav.")]
    [InlineData(new[] { "render", "song.ustx", "--out", "song.wav", "extra" }, "Unexpected extra arguments.")]
    public void RejectsInvalidRenderArguments(string[] args, string expectedError) {
        var success = RenderArguments.TryParse(args, out var arguments, out var error);

        Assert.False(success);
        Assert.Null(arguments);
        Assert.Equal(expectedError, error);
    }
}
