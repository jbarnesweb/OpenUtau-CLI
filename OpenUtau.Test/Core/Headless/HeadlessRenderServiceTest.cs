using System;
using System.Threading.Tasks;
using Xunit;

namespace OpenUtau.Core.Headless;

public class HeadlessRenderServiceTest {
    [Theory]
    [InlineData("input.mid", "output.wav", "inputPath")]
    [InlineData("input.ustx", "output.mp3", "outputPath")]
    public async Task RejectsUnsupportedFileExtensions(
        string inputPath,
        string outputPath,
        string parameterName) {
        var service = new HeadlessRenderService();

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => service.RenderMixdownAsync(inputPath, outputPath));

        Assert.Equal(parameterName, exception.ParamName);
    }
}
