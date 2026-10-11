using OpenUtau.Core.Headless;

namespace OpenUtau.Cli;

internal static class Program {
    private const int ExitSuccess = 0;
    private const int ExitInvalidArguments = 1;
    private const int ExitRenderFailed = 2;

    private static async Task<int> Main(string[] args) {
        if (args.Length == 1 && args[0] == "--help") {
            PrintUsage(Console.Out);
            return ExitSuccess;
        }

        if (args.Length == 0) {
            return Invalid("Missing command.");
        }

        if (args[0] != "render") {
            return Invalid($"Unknown command '{args[0]}'.");
        }

        return await RunRender(args);
    }

    private static async Task<int> RunRender(string[] args) {
        if (!RenderArguments.TryParse(args, out var renderArguments, out var error)) {
            return Invalid(error);
        }

        var headlessRenderService = new HeadlessRenderService();
        try {
            await headlessRenderService.RenderMixdownAsync(
                renderArguments.InputPath,
                renderArguments.OutputPath);
        } catch (Exception ex) {
            Console.Error.WriteLine($"Error: {ex.Message}");
            if (ex.InnerException != null) {
                Console.Error.WriteLine(ex.InnerException.Message);
            }
            return ExitRenderFailed;
        }

        return ExitSuccess;
    }

    private static int Invalid(string message) {
        Console.Error.WriteLine($"Error: {message}");
        PrintUsage(Console.Error);
        return ExitInvalidArguments;
    }

    private static void PrintUsage(TextWriter writer) {
        writer.WriteLine("Usage:");
        writer.WriteLine("  openutau-cli --help");
        writer.WriteLine("  openutau-cli render <input.ustx> --out <output.wav>");
    }
}

internal sealed record RenderArguments(string InputPath, string OutputPath) {
    public static bool TryParse(
        string[] args,
        out RenderArguments? renderArguments,
        out string error) {
        renderArguments = null;
        if (args.Length < 2) {
            error = "Missing input .ustx path.";
            return false;
        }
        if (!args[1].EndsWith(".ustx", StringComparison.OrdinalIgnoreCase)) {
            error = "Input path must end with .ustx.";
            return false;
        }
        if (args.Length < 3) {
            error = "Missing --out option.";
            return false;
        }
        if (args[2] != "--out") {
            error = "Expected --out option after input path.";
            return false;
        }
        if (args.Length < 4) {
            error = "Missing output .wav path.";
            return false;
        }
        if (!args[3].EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) {
            error = "Output path must end with .wav.";
            return false;
        }
        if (args.Length > 4) {
            error = "Unexpected extra arguments.";
            return false;
        }

        renderArguments = new RenderArguments(args[1], args[3]);
        error = string.Empty;
        return true;
    }
}
