using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using MisaAutomation.Core;
using MisaAutomation.Input;
using MisaAutomation.UIA;

namespace MisaAutomation.Inspector;

internal sealed class InspectorForm : Form
{
    private readonly TextBox details = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill, WordWrap = false };
    private readonly TextBox key = new() { Text = "captured.control", Width = 190 };
    private readonly TextBox value = new() { Width = 220 };
    private readonly Label status = new() { AutoSize = true, Text = "Connect to an open MISA session." };
    private readonly CheckBox hover = new() { Text = "Inspect pointer", AutoSize = true };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 300 };
    private readonly JsonAutomationLog log;
    private readonly WindowsInputProvider input = new();
    private readonly MisaAutomationClient client;
    private readonly Recorder recorder;
    private readonly ControlHighlight highlight = new();
    private Inspection? selected;
    private bool busy, connected, closing, allowClose;
    private Point previousPoint;
    private readonly string recordings;

    public InspectorForm()
    {
        Text = "MISA Inspector - UIA3";
        Width = 850; Height = 750;
        var storage = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MisaInvoiceTool", "inspector");
        recordings = Path.Combine(storage, "recordings");
        Directory.CreateDirectory(recordings);
        log = new JsonAutomationLog(Path.Combine(storage, $"inspector-{Environment.ProcessId}.jsonl"));
        var repository = new LocatorRepository();
        var folder = Path.Combine(AppContext.BaseDirectory, "locators");
        if (Directory.Exists(folder)) repository.Load(folder);
        client = new MisaAutomationClient(() => new UiaProvider(repository, log), repository, log, input);
        recorder = new Recorder(this);
        recorder.Captured += point => _ = Run(async () =>
        {
            await InspectAt(point);
            var entry = new { capturedAt = DateTimeOffset.UtcNow, inspection = selected,
                proposedLocator = await Locator(), verification = "observed_only" };
            var path = Path.Combine(recordings, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + ".json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(entry, LocatorRepository.Json));
            status.Text = "Recorded control and hierarchy: " + Path.GetFileName(path);
        });
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(8) };
        void Button(string text, Func<Task> action)
        {
            var button = new Button { Text = text, AutoSize = true };
            button.Click += async (_, _) => await Run(action);
            bar.Controls.Add(button);
        }
        Button("Connect", async () =>
        {
            var processes = Process.GetProcessesByName("MISA.SME2023");
            try
            {
                if (processes.Length != 1)
                    throw new AutomationException("AMBIGUOUS_MISA", "Expected exactly one MISA.SME2023 process.");
                var process = processes[0];
                if (process.MainWindowHandle == IntPtr.Zero)
                    throw new AutomationException("MISA_WINDOW_UNAVAILABLE", "MISA has no main window handle.");
                string? executable = null;
                try { executable = process.MainModule?.FileName; }
                catch (Win32Exception) { /* The elevated inspector may still lack module access. */ }
                var window = await client.ConnectAsync(process.MainWindowHandle.ToInt64(), process.Id);
                connected = true;
                status.Text = $"{window.Title} (PID {window.ProcessId}, HWND {window.Handle})";
                var trees = await client.DumpTreesAsync(20000);
                var payload = new
                {
                    capturedAt = DateTimeOffset.Now,
                    backend = "FlaUI.UIA3",
                    inspectionMode = "elevated_gui_read_only",
                    inspector = new
                    {
                        processId = Environment.ProcessId,
                        elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent())
                            .IsInRole(WindowsBuiltInRole.Administrator),
                        is64Bit = Environment.Is64BitProcess
                    },
                    process = new
                    {
                        processId = process.Id,
                        name = process.ProcessName,
                        executable,
                        is64BitOperatingSystem = Environment.Is64BitOperatingSystem
                    },
                    windows = trees
                };
                var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                    "..", "..", "..", "..", ".."));
                var output = Path.Combine(root, "misa_uia_controls_elevated.json");
                var serialized = JsonSerializer.Serialize(payload, LocatorRepository.Json);
                var treeText = RenderTreeText(trees);
                await File.WriteAllTextAsync(output, serialized);
                await File.WriteAllTextAsync(Path.Combine(root, "misa_uia_controls.json"), serialized);
                var textOutput = Path.ChangeExtension(output, ".txt");
                await File.WriteAllTextAsync(textOutput, treeText);
                await File.WriteAllTextAsync(Path.Combine(root, "misa_uia_controls.txt"), treeText);
                status.Text += $" · UIA dump: {Path.GetFileName(output)}";
            }
            finally
            {
                foreach (var process in processes) process.Dispose();
            }
        });
        bar.Controls.Add(hover);
        Button("Highlight", () => { highlight.Show(Required().Element.Bounds); return Task.CompletedTask; });
        bar.Controls.Add(key);
        Button("Copy Locator", async () => Clipboard.SetText(await LocatorJson()));
        Button("Save Locator", async () =>
        {
            var json = await LocatorJson();
            using var dialog = new SaveFileDialog { Filter = "Locator JSON|*.json", FileName = "captured-locator.json" };
            if (dialog.ShowDialog(this) == DialogResult.OK) await File.WriteAllTextAsync(dialog.FileName, json);
        });
        Button("Test Click", async () => await Action("click"));
        bar.Controls.Add(value);
        Button("Test SetValue", async () => await Action("set"));
        Button("Recorder Ctrl+Shift+R", () => { ToggleRecorder(); return Task.CompletedTask; });
        Controls.Add(details); Controls.Add(bar);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8) };
        footer.Controls.Add(status); Controls.Add(footer);
        timer.Tick += async (_, _) =>
        {
            var point = Cursor.Position;
            if (hover.Checked && connected && !busy && !Bounds.Contains(point) && point != previousPoint)
            {
                previousPoint = point;
                await Run(() => InspectAt(point));
            }
        };
        timer.Start();
    }

    private static string RenderTreeText(object trees)
    {
        var serialized = JsonSerializer.Serialize(trees, LocatorRepository.Json);
        using var document = JsonDocument.Parse(serialized);
        var output = new StringBuilder("MISA UIA3 elevated read-only dump\n");
        foreach (var dump in document.RootElement.EnumerateArray())
        {
            var window = dump.GetProperty("window");
            output.AppendLine();
            output.AppendLine($"WINDOW kind={window.GetProperty("kind").GetString()} hwnd={window.GetProperty("handle").GetInt64()} " +
                              $"enabled={window.GetProperty("enabled").GetBoolean()} nodes={dump.GetProperty("nodeCount").GetInt32()} " +
                              $"title={window.GetProperty("title").GetString()}");
            foreach (var node in dump.GetProperty("nodes").EnumerateArray())
            {
                var element = node.GetProperty("element");
                var bounds = element.GetProperty("bounds");
                var patterns = string.Join(',', element.GetProperty("patterns").EnumerateArray()
                    .Select(pattern => pattern.GetString()));
                var depth = node.GetProperty("depth").GetInt32();
                var indent = new string(' ', Math.Min(depth, 80) * 2);
                output.AppendLine($"{indent}[{ControlTypeName(element.GetProperty("kind").GetInt32())}] " +
                    $"id={element.GetProperty("id").GetString()} name={element.GetProperty("name").GetString()} " +
                    $"value={element.GetProperty("value").GetString()} hwnd={node.GetProperty("nativeWindowHandle").GetInt64()} " +
                    $"parent={node.GetProperty("parentNativeWindowHandle").GetInt64()} " +
                    $"bounds=({bounds.GetProperty("x")},{bounds.GetProperty("y")},{bounds.GetProperty("width")},{bounds.GetProperty("height")}) " +
                    $"enabled={element.GetProperty("enabled").GetBoolean()} offscreen={element.GetProperty("offscreen").GetBoolean()} patterns={patterns}");
            }
        }
        return output.ToString();
    }

    private static string ControlTypeName(int kind) => kind switch
    {
        50000 => "Button", 50001 => "Calendar", 50002 => "CheckBox", 50003 => "ComboBox",
        50004 => "Edit", 50005 => "Hyperlink", 50007 => "ListItem", 50011 => "MenuItem",
        50013 => "ProgressBar", 50018 => "ToolBar", 50019 => "TabItem", 50020 => "Text",
        50021 => "Thumb", 50023 => "Tree", 50025 => "Custom", 50026 => "Group",
        50029 => "DataItem", 50031 => "Tab", 50032 => "Window", 50033 => "Pane",
        50034 => "Header", 50035 => "HeaderItem", 50037 => "SplitButton", _ => kind.ToString()
    };

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (!recorder.RegisterHotkey(Handle)) status.Text = "Recorder hotkey unavailable; use the Recorder button.";
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312 && m.WParam.ToInt32() == Recorder.HotkeyId) ToggleRecorder();
        base.WndProc(ref m);
    }
    private void ToggleRecorder()
    {
        if (!connected) { status.Text = "Connect to MISA first."; return; }
        try { recorder.Toggle(); status.Text = recorder.Active ? "Recording MISA clicks; Ctrl+Shift+R stops." : "Recorder stopped."; }
        catch (Exception error) { status.Text = error.Message; }
    }
    private Inspection Required() => selected ?? throw new AutomationException("NO_SELECTION", "Inspect a MISA control first.");
    private async Task InspectAt(Point point)
    {
        selected = await client.InspectPointAsync(point.X, point.Y);
        details.Text = JsonSerializer.Serialize(selected, LocatorRepository.Json);
        status.Text = selected.Element.Name + " | " + selected.Element.ClassName;
    }
    private async Task<Locator> Locator()
    {
        var inspection = Required();
        var element = inspection.Element;
        var selector = new Locator { Name = element.Name, ControlType = element.Kind,
            WindowKind = inspection.Window.Kind, ReadOnly = element.Id == "txtJournalMemo" };
        if (!string.IsNullOrWhiteSpace(element.Id))
        {
            var matches = await client.QueryAsync(new ElementQuery(Ids: [element.Id]));
            if (matches.Count == 1) return selector with { AutomationId = element.Id };
        }
        var named = await client.QueryAsync(new ElementQuery(Names: [element.Name], Kinds: [element.Kind]));
        if (named.Count != 1) throw new AutomationException("AMBIGUOUS_LOCATOR", "Add a parent scope from the captured hierarchy; this selector is not unique.");
        return selector;
    }
    private async Task<string> LocatorJson()
    {
        if (string.IsNullOrWhiteSpace(key.Text)) throw new InvalidOperationException("Locator key is required.");
        return JsonSerializer.Serialize(new Dictionary<string, Locator> { [key.Text] = await Locator() }, LocatorRepository.Json);
    }
    private async Task Action(string action)
    {
        hover.Checked = false;
        var element = Required().Element;
        var result = await client.ExecuteAsync(new Operation(action, Token: element.Token, Value: value.Text, Workflow: "inspector"));
        details.Text = JsonSerializer.Serialize(result, LocatorRepository.Json);
        status.Text = result.Success ? "Input sent; inspect MISA to verify the resulting state." : result.ErrorCode + ": " + result.Message;
    }
    private async Task Run(Func<Task> operation)
    {
        if (busy || closing) return;
        busy = true;
        try { await operation(); }
        catch (Exception error) { status.Text = error.Message; log.Write("inspector_error", new { error = error.ToString() }); }
        finally { busy = false; }
    }
    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (allowClose) return;
        e.Cancel = true;
        if (closing) return;
        closing = true;
        timer.Stop(); recorder.Dispose(); highlight.Dispose();
        try { await client.DisposeAsync(); input.Dispose(); }
        catch (Exception error) { log.Write("shutdown_error", new { error = error.ToString() }); }
        log.Dispose(); timer.Dispose(); allowClose = true; Close();
    }
}
