using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Text.Json.Nodes;
using FFmpegFreeUI.AbAv1;
using LakeUI;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "crf-search")
        {
            return RunFakeAbAv1Async(args).GetAwaiter().GetResult();
        }

        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "ffmpegfreeui-ab-av1-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);

        try
        {
            Console.WriteLine("[1/6] install fake ab-av1");
            InstallFakeAbAv1BesidePlugin();
            Console.WriteLine("[2/6] verify preset mapping");
            var fixture = CreatePresetFixture(temporaryDirectory);
            VerifyPresetMapping(fixture);
            Console.WriteLine("[3/6] scan VMAF models");
            Task.Run(VerifyVmafModelScanAsync).GetAwaiter().GetResult();
            Console.WriteLine("[4/6] verify pause/resume/stop");
            Task.Run(() => VerifyPauseResumeAndStopAsync(fixture)).GetAwaiter().GetResult();
            Console.WriteLine("[5/6] verify official loader callbacks");
            VerifyOfficialEntryAndCallbacks();
            Console.WriteLine("[6/6] render task manager UI");
            RenderMainPanel(fixture);
            Console.WriteLine("PASS: official API, queue runner, VMAF model and UI smoke tests");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            try
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static void InstallFakeAbAv1BesidePlugin()
    {
        var currentExe = Environment.ProcessPath
            ?? throw new InvalidOperationException("No process executable path.");
        File.Copy(currentExe, Path.Combine(AppContext.BaseDirectory, "ab-av1.exe"), overwrite: true);
    }

    private static async Task<int> RunFakeAbAv1Async(string[] args)
    {
        if (args.Contains("--help", StringComparer.Ordinal))
        {
            Console.WriteLine("--stdout-format json");
            return 0;
        }

        var requiredPairs = new[]
        {
            ("--encoder", "libsvtav1"),
            ("--preset", "5"),
            ("--pix-format", "yuv420p10le"),
            ("--keyint", "8s"),
            ("--svt", "tune=0"),
            ("--svt", "enable-tf=2"),
            ("--svt", "film-grain=4"),
            ("--min-vmaf", "95"),
            ("--min-crf", "5"),
            ("--max-crf", "55"),
            ("--sample-duration", "20s"),
            ("--vmaf", "model=version=vmaf_v0.6.1"),
            ("--stdout-format", "json")
        };
        foreach (var pair in requiredPairs)
        {
            if (!HasPair(args, pair.Item1, pair.Item2))
            {
                Console.Error.WriteLine($"missing expected argument pair: {pair.Item1} {pair.Item2}");
                return 17;
            }
        }
        if (args.Any(value => value.Contains("crf=40", StringComparison.OrdinalIgnoreCase)))
        {
            Console.Error.WriteLine("old CRF leaked into ab-av1 search arguments");
            return 18;
        }

        Console.WriteLine("{\"type\":\"sample-encode-done\",\"crf\":31,\"vmaf\":95.12}");
        Console.Out.Flush();
        await Task.Delay(900);
        Console.WriteLine("{\"type\":\"crf-search-done\",\"crf\":31,\"vmaf\":95.12,\"predicted_encode_size\":123456,\"predicted_encode_seconds\":10}");
        return 0;
    }

    private static void VerifyOfficialEntryAndCallbacks()
    {
        var assembly = typeof(Entry).Assembly;
        Assert(assembly.GetName().Name == "FFmpegFreeUI.AbAv1", "unexpected assembly name");
        Assert(
            !assembly.GetReferencedAssemblies().Any(
                value => value.Name?.Contains("Ext.Plugin", StringComparison.OrdinalIgnoreCase) == true),
            "plugin still references the extended SDK");

        var entryType = assembly.GetType(assembly.GetName().Name + ".Entry")
            ?? throw new InvalidOperationException("official loader could not resolve AssemblyName.Entry");

        Control? page = null;
        string? pageTitle = null;
        var queued = new List<string[]>();
        Action<string, Control> addPanel = (title, control) =>
        {
            pageTitle = title;
            page = control;
        };
        Action<string, string, string, string> enqueue =
            (preset, name, output, input) => queued.Add([preset, name, output, input]);

        entryType.GetMethod("SetHost_AddCustomWinformPanel")!
            .Invoke(null, [addPanel]);
        entryType.GetMethod("SetHost_AddMissionToQueueWith3fuiFile")!
            .Invoke(null, [enqueue]);
        entryType.GetMethod("Entry")!.Invoke(null, null);

        Assert(pageTitle == "AB-AV1", "official page title was not registered");
        Assert(page is MainPanel, "official callback did not receive MainPanel");
        Entry.EnqueuePresetTask("preset.json", "display", "out.mkv", "in.mkv");
        Assert(queued.Count == 1 && queued[0][0] == "preset.json", "official enqueue callback was not invoked");
        page!.Dispose();

        var published = Path.GetFullPath(Path.Combine(
            Directory.GetCurrentDirectory(),
            "dist",
            "FFmpegFreeUI.AbAv1.3fui.dll"));
        Assert(File.Exists(published), "published .3fui.dll artifact is missing");
        Assert(
            AssemblyName.GetAssemblyName(published).Name == "FFmpegFreeUI.AbAv1",
            "published filename/assembly name pair is incompatible with the official loader");
    }

    private static PresetFixture CreatePresetFixture(string directory)
    {
        var presetPath = Path.Combine(directory, "preset.json");
        var inputPath = Path.Combine(directory, "pause-input.mkv");
        File.WriteAllText(inputPath, "fake media input");

        var preset = new JsonObject
        {
            ["预设文件版本"] = 6,
            ["视频参数_编码器_具体编码"] = "libsvtav1",
            ["视频参数_编码器_编码预设"] = "5",
            ["视频参数_色彩管理_像素格式"] = "yuv420p10le",
            ["输出容器"] = ".mkv",
            ["视频参数_比特率_控制方式"] = 1,
            ["视频参数_质量控制_参数名"] = "crf",
            ["视频参数_质量控制_值"] = "40",
            ["视频参数_质量控制_进阶参数集"] =
                "-svtav1-params tune=0:keyint=8s:enable-tf=2:crf=40:film-grain=4"
        };
        File.WriteAllText(presetPath, preset.ToJsonString());
        return new PresetFixture(PresetProfile.Load(presetPath), inputPath, presetPath);
    }

    private static void VerifyPresetMapping(PresetFixture fixture)
    {
        var settings = NewSettings();
        var arguments = fixture.Profile.BuildSearchArguments(
            fixture.InputPath,
            settings,
            jsonOutput: true).ToArray();
        Assert(HasPair(arguments, "--encoder", "libsvtav1"), "encoder was not mapped");
        Assert(HasPair(arguments, "--preset", "5"), "preset was not mapped");
        Assert(HasPair(arguments, "--keyint", "8s"), "keyint was not mapped");
        Assert(HasPair(arguments, "--svt", "film-grain=4"), "SVT parameters were not mapped");
        Assert(
            HasPair(arguments, "--vmaf", "model=version=vmaf_v0.6.1"),
            "VMAF built-in model was not mapped");
        Assert(!arguments.Any(value => value.Contains("crf=40", StringComparison.OrdinalIgnoreCase)),
            "old CRF leaked into search arguments");

        var localModel = Path.Combine(Path.GetDirectoryName(fixture.PresetPath)!, "model.json");
        File.WriteAllText(localModel, "{}");
        var escaped = PresetProfile.BuildVmafModelArgument(localModel);
        Assert(escaped.StartsWith("model=path=", StringComparison.Ordinal), "local model did not use path=");
        Assert(escaped.Contains("\\:", StringComparison.Ordinal), "Windows model path colon was not escaped");
        Assert(escaped.Contains("\\\\", StringComparison.Ordinal), "Windows model path separators were not escaped");

        var resolved = JsonNode.Parse(fixture.Profile.ApplyCrf(31))!.AsObject();
        Assert(resolved["视频参数_质量控制_值"]!.GetValue<string>() == "31", "CRF was not written");
        Assert(
            resolved["视频参数_质量控制_进阶参数集"]!.GetValue<string>()
                .Contains("crf=31", StringComparison.Ordinal),
            "advanced SVT CRF was not synchronized");
    }

    private static async Task VerifyVmafModelScanAsync()
    {
        var help = "   model             <string>     ..FV....... Set model. (default \"version=vmaf_v0.6.1\")";
        Assert(
            VmafModelScanner.ParseModelsFromFilterHelp(help).Contains("vmaf_v0.6.1"),
            "filter help parser did not find the default model");

        var scan = await VmafModelScanner.ScanAsync(CancellationToken.None);
        Assert(File.Exists(scan.FfmpegPath), "scanner did not resolve the current ffmpeg executable");
        Assert(scan.Models.Contains("vmaf_v0.6.1"), "scanner did not find vmaf_v0.6.1");
        Console.WriteLine($"VMAF models: {string.Join(", ", scan.Models)}");
    }

    private static async Task VerifyPauseResumeAndStopAsync(PresetFixture fixture)
    {
        var runner = new AbAv1Runner();
        var runTask = runner.SearchAsync(
            fixture.Profile,
            fixture.InputPath,
            NewSettings(),
            progress: null!,
            CancellationToken.None);

        await WaitUntilAsync(() => runner.HasActiveProcess, TimeSpan.FromSeconds(5));
        var pauseError = string.Empty;
        Assert(runner.TryPause(ref pauseError), "pause failed: " + pauseError);
        Assert(runner.IsPaused, "runner did not enter paused state");
        await Task.Delay(100);
        Assert(!runTask.IsCompleted, "paused search unexpectedly completed");
        var resumeError = string.Empty;
        Assert(runner.TryResume(ref resumeError), "resume failed: " + resumeError);
        var result = await runTask;
        Assert(result.Crf == 31 && Math.Abs(result.Vmaf - 95.12) < 0.001, "fake search result mismatch");

        using var stop = new CancellationTokenSource();
        var stoppedRunner = new AbAv1Runner();
        var stoppedTask = stoppedRunner.SearchAsync(
            fixture.Profile,
            fixture.InputPath,
            NewSettings(),
            progress: null!,
            stop.Token);
        await WaitUntilAsync(() => stoppedRunner.HasActiveProcess, TimeSpan.FromSeconds(5));
        stop.Cancel();
        try
        {
            await stoppedTask;
            throw new InvalidOperationException("stopped search completed successfully");
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void RenderMainPanel(PresetFixture fixture)
    {
        Control? page = null;
        Entry.SetHost_AddCustomWinformPanel(
            new Action<string, Control>((_, control) => page = control));
        Entry.Entry();
        var panel = page as MainPanel ?? throw new InvalidOperationException("no MainPanel to render");

        GetPrivateField<Control>(panel, "_presetPath").Text = fixture.PresetPath;
        GetPrivateField<Control>(panel, "_vmafModel").Text = "vmaf_v0.6.1";
        typeof(MainPanel).GetMethod("RefreshPresetSummary", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(panel, null);
        var added = (int)typeof(MainPanel)
            .GetMethod("AddFilePaths", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(panel, new object[] { new[] { fixture.InputPath } })!;
        Assert(added == 1, "task manager did not add a waiting file");

        using var form = new Form
        {
            ClientSize = new Size(1772, 1120),
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            Opacity = 0
        };
        form.Controls.Add(panel);
        form.Show();
        Application.DoEvents();
        panel.PerformLayout();
        VerifyTaskManagerMenuAndSizing(panel);
        using var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
        panel.DrawToBitmap(bitmap, panel.ClientRectangle);
        var artifacts = Path.Combine(Directory.GetCurrentDirectory(), "artifacts");
        Directory.CreateDirectory(artifacts);
        bitmap.Save(Path.Combine(artifacts, "official-api-task-queue.png"));
        form.Hide();
    }

    private static void VerifyTaskManagerMenuAndSizing(MainPanel panel)
    {
        var list = GetPrivateField<UltraDetailListView>(panel, "_fileList");
        list.SelectedIndex = 0;
        var queueItem = list.SelectedItem?.Tag
            ?? throw new InvalidOperationException("waiting queue item was unavailable");
        var stateProperty = queueItem.GetType().GetProperty("State")
            ?? throw new InvalidOperationException("queue state property was unavailable");
        var stateType = stateProperty.PropertyType;
        var rebuildMenu = typeof(MainPanel).GetMethod(
            "RebuildTaskContextMenu",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var refreshButtons = typeof(MainPanel).GetMethod(
            "RefreshActionButtons",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var menu = GetPrivateField<ModernContextMenu>(panel, "_taskContextMenu");
        var pauseButton = GetPrivateField<Control>(panel, "_pauseResumeButton");

        void SetState(string state)
        {
            stateProperty.SetValue(queueItem, Enum.Parse(stateType, state));
            refreshButtons.Invoke(panel, null);
            rebuildMenu.Invoke(panel, null);
        }

        string[] MenuTexts() => menu.Items
            .Where(item => !item.IsSeparator)
            .Select(item => item.Text)
            .ToArray();

        SetState("Pending");
        var pendingMenu = MenuTexts();
        Assert(pendingMenu.Contains("开始搜索等待任务"), "context menu is missing start");
        Assert(pendingMenu.Contains("停止所选任务"), "context menu is missing stop");
        Assert(pendingMenu.Contains("重置所选任务状态"), "context menu is missing reset");
        Assert(pendingMenu.Contains("移除所选任务"), "context menu is missing remove");
        Assert(!pauseButton.Enabled, "pause button should ignore a pending-only selection");

        SetState("Running");
        Assert(pauseButton.Enabled && pauseButton.Text == "暂停", "running selection did not choose pause mode");
        Assert(MenuTexts().Contains("暂停所选运行任务"), "context menu is missing pause");

        SetState("Paused");
        Assert(pauseButton.Enabled && pauseButton.Text == "恢复", "paused selection did not choose resume mode");
        Assert(MenuTexts().Contains("恢复所选暂停任务"), "context menu is missing resume");

        SetState("Enqueued");
        Assert(!pauseButton.Enabled, "pause button should ignore an enqueued-only selection");
        Assert(!MenuTexts().Any(text => text.Contains("暂停") || text.Contains("恢复")),
            "context menu exposed pause/resume for a non-running task");

        SetState("Pending");
        foreach (var button in Descendants(panel).OfType<ModernButton>())
        {
            var measured = TextRenderer.MeasureText(button.Text, button.Font);
            Assert(button.ClientSize.Width >= measured.Width + 24,
                $"button text is horizontally clipped: {button.Text}");
            Assert(button.ClientSize.Height >= measured.Height + 10,
                $"button text is vertically clipped: {button.Text}");
        }

        var modelCaption = Descendants(panel)
            .First(control => control.Text == "VMAF 模型");
        var modelTextSize = TextRenderer.MeasureText(modelCaption.Text, modelCaption.Font);
        Assert(modelCaption.ClientSize.Width >= modelTextSize.Width + 16,
            "VMAF model caption does not fit on one line");
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    private static T GetPrivateField<T>(object target, string name) where T : class
    {
        return typeof(MainPanel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(target) as T
            ?? throw new InvalidOperationException($"private field {name} was unavailable");
    }

    private static SearchSettings NewSettings() => new()
    {
        TargetVmaf = 95,
        MinCrf = 5,
        MaxCrf = 55,
        SampleDuration = "20s",
        VmafModel = "vmaf_v0.6.1"
    };

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed > timeout)
            {
                throw new TimeoutException("Timed out waiting for process state.");
            }
            await Task.Delay(20);
        }
    }

    private static bool HasPair(IReadOnlyList<string> args, string option, string value)
    {
        for (var index = 0; index + 1 < args.Count; index++)
        {
            if (args[index] == option && args[index + 1] == value)
            {
                return true;
            }
        }
        return false;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed record PresetFixture(PresetProfile Profile, string InputPath, string PresetPath);
}
