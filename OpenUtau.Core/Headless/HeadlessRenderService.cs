using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using OpenUtau.Classic;
using OpenUtau.Core.Format;
using OpenUtau.Core.Render;
using OpenUtau.Core.SignalChain;
using OpenUtau.Core.Ustx;

namespace OpenUtau.Core.Headless;

public class HeadlessRenderService {
    private static readonly Lazy<HeadlessRuntime> Runtime = new(() => new HeadlessRuntime());
    private static readonly SemaphoreSlim RenderLock = new(1, 1);

    public async Task RenderMixdownAsync(
        string inputPath,
        string outputPath,
        CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrEmpty(inputPath);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        if (!inputPath.EndsWith(".ustx", StringComparison.OrdinalIgnoreCase)) {
            throw new ArgumentException("Input path must end with .ustx.", nameof(inputPath));
        }

        if (!outputPath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) {
            throw new ArgumentException("Output path must end with .wav.", nameof(outputPath));
        }

        cancellationToken.ThrowIfCancellationRequested();

        inputPath = Path.GetFullPath(inputPath);
        outputPath = Path.GetFullPath(outputPath);
        if (!File.Exists(inputPath)) {
            throw new FileNotFoundException($"USTX project not found: {inputPath}", inputPath);
        }
        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrEmpty(outputDirectory) || !Directory.Exists(outputDirectory)) {
            throw new DirectoryNotFoundException($"Output directory not found: {outputDirectory}");
        }

        await RenderLock.WaitAsync(cancellationToken);
        try {
            var runtime = Runtime.Value;
            var project = await runtime.RunAsync(() => {
                cancellationToken.ThrowIfCancellationRequested();
                var loaded = Formats.ReadProject(new[] { inputPath }, validate: false)
                    ?? throw new InvalidDataException($"Failed to load USTX project: {inputPath}");
                DocManager.Inst.SetProject(loaded);
                return loaded;
            }, cancellationToken);

            await ValidateAsync(runtime, project, cancellationToken);
            ValidateRenderConfiguration(project);

            await Task.Run(() => {
                cancellationToken.ThrowIfCancellationRequested();
                CancellationTokenSource renderCancellation = null!;
                try {
                    var engine = new RenderEngine(project);
                    var projectMix = engine.RenderMixdown(
                        runtime.Scheduler,
                        ref renderCancellation,
                        wait: true,
                        cancellationToken).Item1;
                    cancellationToken.ThrowIfCancellationRequested();
                    CheckFileWritable(outputPath);
                    WaveFileWriter.CreateWaveFile16(outputPath, new ExportAdapter(projectMix));
                } finally {
                    renderCancellation?.Dispose();
                }
            }, cancellationToken);
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) when (ex is not HeadlessRenderException) {
            throw new HeadlessRenderException(
                $"Failed to render '{inputPath}' to '{outputPath}'.", Unwrap(ex));
        } finally {
            RenderLock.Release();
        }
    }

    private static async Task ValidateAsync(
        HeadlessRuntime runtime,
        UProject project,
        CancellationToken cancellationToken) {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var subscriber = new PhonemizationSubscriber(project, completion);
        await runtime.RunAsync(() => {
            DocManager.Inst.AddSubscriber(subscriber);
            try {
                project.ValidateFull();
                subscriber.CompleteIfReady();
            } catch {
                DocManager.Inst.RemoveSubscriber(subscriber);
                throw;
            }
        }, cancellationToken);

        using var registration = cancellationToken.Register(
            () => completion.TrySetCanceled(cancellationToken));
        try {
            await completion.Task;
        } finally {
            await runtime.RunAsync(
                () => DocManager.Inst.RemoveSubscriber(subscriber),
                CancellationToken.None);
        }
    }

    private static void ValidateRenderConfiguration(UProject project) {
        var usedTrackNumbers = project.parts
            .OfType<UVoicePart>()
            .Select(part => part.trackNo)
            .Distinct();
        foreach (var trackNumber in usedTrackNumbers) {
            var track = project.tracks[trackNumber];
            if (track.Singer == null || !track.Singer.Found) {
                throw new InvalidOperationException(
                    $"Singer '{track.singer ?? "(not configured)"}' was not found for track {trackNumber + 1}. " +
                    $"Searched: {string.Join(", ", PathManager.Inst.SingersPaths)}.");
            }
            if (!track.Singer.Loaded) {
                throw new InvalidOperationException(
                    $"Singer '{track.Singer.Id}' failed to load for track {trackNumber + 1}.");
            }
            if (track.Phonemizer == null) {
                throw new InvalidOperationException(
                    $"No phonemizer is available for track {trackNumber + 1}.");
            }
            if (track.RendererSettings?.Renderer == null) {
                throw new InvalidOperationException(
                    $"Renderer '{track.RendererSettings?.renderer ?? "(not configured)"}' is unavailable for track {trackNumber + 1}.");
            }
        }

        var phonemizerError = project.parts
            .OfType<UVoicePart>()
            .SelectMany(part => part.phonemes)
            .Select(phoneme => phoneme.ErrorException)
            .FirstOrDefault(error => error != null);
        if (phonemizerError != null) {
            throw new InvalidOperationException("Project phonemization failed.", phonemizerError);
        }
    }

    private static void CheckFileWritable(string outputPath) {
        if (File.Exists(outputPath)) {
            using var stream = File.Open(
                outputPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        }
    }

    private static Exception Unwrap(Exception exception) {
        if (exception is AggregateException aggregate) {
            return aggregate.Flatten().InnerExceptions.Count == 1
                ? aggregate.Flatten().InnerException!
                : aggregate.Flatten();
        }
        return exception;
    }

    private sealed class PhonemizationSubscriber : ICmdSubscriber {
        private readonly UProject project;
        private readonly TaskCompletionSource completion;

        public PhonemizationSubscriber(UProject project, TaskCompletionSource completion) {
            this.project = project;
            this.completion = completion;
        }

        public void OnNext(UCommand cmd, bool isUndo) {
            if (cmd is PhonemizedNotification) {
                CompleteIfReady();
            }
        }

        public void CompleteIfReady() {
            if (project.parts.OfType<UVoicePart>().All(part => part.PhonemesUpToDate)) {
                completion.TrySetResult();
            }
        }
    }

    private sealed class HeadlessRuntime {
        private readonly SingleThreadTaskScheduler scheduler = new("OpenUtau Headless");

        public TaskScheduler Scheduler => scheduler;

        public HeadlessRuntime() {
            RunAsync(() => {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                ToolsManager.Inst.Initialize();
                SingerManager.Inst.Initialize();
                DocManager.Inst.Initialize(Thread.CurrentThread, scheduler);
                DocManager.Inst.PostOnUIThread = action =>
                    Task.Factory.StartNew(
                        action,
                        CancellationToken.None,
                        TaskCreationOptions.None,
                        scheduler);
            }, CancellationToken.None).GetAwaiter().GetResult();
        }

        public Task RunAsync(Action action, CancellationToken cancellationToken) {
            return Task.Factory.StartNew(
                action,
                cancellationToken,
                TaskCreationOptions.None,
                scheduler);
        }

        public Task<T> RunAsync<T>(Func<T> action, CancellationToken cancellationToken) {
            return Task.Factory.StartNew(
                action,
                cancellationToken,
                TaskCreationOptions.None,
                scheduler);
        }
    }

    private sealed class SingleThreadTaskScheduler : TaskScheduler {
        private readonly BlockingCollection<Task> tasks = new();
        private readonly Thread thread;

        public SingleThreadTaskScheduler(string name) {
            thread = new Thread(Run) {
                IsBackground = true,
                Name = name,
            };
            thread.Start();
        }

        protected override void QueueTask(Task task) {
            tasks.Add(task);
        }

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) {
            return Thread.CurrentThread == thread && TryExecuteTask(task);
        }

        protected override System.Collections.Generic.IEnumerable<Task>? GetScheduledTasks() {
            return tasks.ToArray();
        }

        private void Run() {
            foreach (var task in tasks.GetConsumingEnumerable()) {
                TryExecuteTask(task);
            }
        }
    }
}

public sealed class HeadlessRenderException : Exception {
    public HeadlessRenderException(string message, Exception innerException)
        : base(message, innerException) {
    }
}
