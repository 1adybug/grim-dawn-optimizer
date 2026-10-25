using System.Globalization;
using System.Text;
using System.Text.Json;
using GrimDawnOptimizer.App.Services;
using GrimDawnOptimizer.App.ViewModels;
using GrimDawnOptimizer.Core.Services;
using GrimDawnOptimizer.Bridge.Trainer;
using GrimDawnOptimizer.Bridge.PathPicking;

namespace GrimDawnOptimizer.Bridge;

public static class Program
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static int Main(string[] args)
    {
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("zh-CN");
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
        string? dataDirectory = null;
        if (args.Length != 0)
        {
            if (args.Length != 2 || args[0] != "--data-dir" || !Path.IsPathFullyQualified(args[1]))
            {
                Console.Error.WriteLine("Usage: GrimDawnOptimizer.Bridge [--data-dir <absolute-directory>]");
                return 2;
            }
            dataDirectory = Path.GetFullPath(args[1]);
        }

        string? DataFile(string name) => dataDirectory is null ? null : Path.Combine(dataDirectory, name);
        using var context = new SingleThreadContext();
        SynchronizationContext.SetSynchronizationContext(context);
        var controller = new BridgeController(new MainViewModel(
            new GameDataSource(), new SaveReader(),
            new BeamSearchOptimizer(new CharacterStatEngine(), new CombatSimulator()),
            new JsonProfileStore(DataFile("profiles.json")),
            new JsonSourceHistoryStore(DataFile("source-history.json")),
            new UiPreferencesStore(DataFile("ui-settings.json"))));
        using var trainer = new TrainerController(
            new TrainerSettings(DataFile("combat-settings.json"), importLegacyDefault: dataDirectory is null),
            action => context.Post(_ => action(), null));
        using var picker = new PathPickerService();
        var closing = false;

        void Write(object value)
        {
            if (closing) return;
            try
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(value, JsonOptions));
                Console.Out.Flush();
            }
            catch (IOException)
            {
                closing = true;
                picker.Dispose();
                try { trainer.Dispose(); }
                finally { context.Complete(); }
            }
        }

        void Publish()
        {
            if (!controller.Dirty || closing) return;
            controller.Dirty = false;
            Write(new { @event = "state", state = controller.Snapshot() });
        }

        void PublishTrainer()
        {
            if (!trainer.Dirty || closing) return;
            trainer.Dirty = false;
            Write(new { @event = "trainer", state = trainer.Snapshot() });
        }

        context.UnhandledException += error =>
        {
            controller.ReportError(error);
            Write(new { @event = "error", error = error.Message, state = controller.Snapshot() });
        };

        // Progress reports can arrive hundreds of times per second. Publish only
        // coherent snapshots after callbacks finish, at most eight times a second.
        using var timer = new Timer(_ => context.Post(_ => Publish(), null), null, 125, 125);
        var trainerPollQueued = 0;
        using var trainerTimer = new Timer(_ =>
        {
            if (Interlocked.Exchange(ref trainerPollQueued, 1) != 0) return;
            context.Post(_ =>
            {
                try { if (!closing) { trainer.Poll(); PublishTrainer(); } }
                finally { Interlocked.Exchange(ref trainerPollQueued, 0); }
            }, null);
        }, null, 250, 250);
        context.Post(async _ => { Publish(); PublishTrainer(); await controller.InitializeAsync(); Publish(); }, null);

        _ = Task.Run(async () =>
        {
            try
            {
                while (await Console.In.ReadLineAsync() is { } line)
                {
                    var requestLine = line;
                    context.Post(async _ =>
                    {
                        JsonElement? id = null;
                        var trainerRequest = false;
                        var pickerRequest = false;
                        try
                        {
                            if (requestLine.Length > 1_048_576) throw new ArgumentException("请求过大。");
                            using var request = JsonDocument.Parse(requestLine);
                            var root = request.RootElement;
                            if (root.ValueKind != JsonValueKind.Object) throw new ArgumentException("请求必须是 JSON 对象。");
                            if (root.TryGetProperty("id", out var requestId)) id = requestId.Clone();
                            if (id is null || id.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
                                throw new ArgumentException("请求缺少有效的 id。");
                            if (!root.TryGetProperty("command", out var command) || command.ValueKind != JsonValueKind.String)
                                throw new ArgumentException("请求缺少 command。");
                            trainerRequest = TrainerController.IsTrainerCommand(command.GetString()!);
                            pickerRequest = command.GetString() == "pickPath";
                            var payload = root.TryGetProperty("payload", out var value) ? value : default;
                            if (pickerRequest)
                            {
                                var selectedPath = await picker.PickAsync(payload);
                                Write(new { id, ok = true, state = selectedPath });
                                return;
                            }
                            if (trainerRequest)
                            {
                                await trainer.ExecuteAsync(command.GetString()!, payload);
                                Write(new { id, ok = true, state = trainer.Snapshot() });
                                PublishTrainer();
                                return;
                            }
                            await controller.ExecuteAsync(command.GetString()!, payload);
                            Write(new { id, ok = true, state = controller.Snapshot() });
                            Publish();
                        }
                        catch (Exception error)
                        {
                            if (pickerRequest)
                            {
                                Write(new { id, ok = false, error = error.Message, state = new PathPickerResult(null) });
                                return;
                            }
                            if (trainerRequest)
                            {
                                trainer.ReportError(error);
                                Write(new { id, ok = false, error = error.Message, state = trainer.Snapshot() });
                                PublishTrainer();
                                return;
                            }
                            Write(new { id, ok = false, error = error.Message, state = controller.Snapshot() });
                        }
                    }, null);
                }
            }
            catch (IOException) { /* The parent closed its pipe. */ }
            finally
            {
                context.Post(async _ =>
                {
                    closing = true;
                    picker.Dispose();
                    try
                    {
                        try { trainer.Dispose(); }
                        finally { await controller.ShutdownAsync(); }
                    }
                    catch (Exception error) { Console.Error.WriteLine(error.Message); }
                    finally { context.Complete(); }
                }, null);
            }
        });

        context.Run();
        return 0;
    }
}
