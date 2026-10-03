using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using MisaManager.Core;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace MisaManager.Automation;

public sealed partial class NativeInputService : IAsyncDisposable
{
    private readonly NativeDispatcher dispatcher;
    private readonly bool ownsDispatcher;
    private UIA3Automation? automationInstance;
    private UIA3Automation automation => automationInstance ??= dispatcher.Own(() => new UIA3Automation());
    private AutomationElement? root;
    private WindowIdentity? identity;
    private long epoch;
    private long nativeQueries;
    private long readbacks;
    private long inputOperations;
    private long tabSelections;
    private readonly long startedAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public NativeWorkflowMetrics Metrics => new(
        Interlocked.Read(ref nativeQueries), Interlocked.Read(ref readbacks),
        Interlocked.Read(ref inputOperations), Interlocked.Read(ref tabSelections),
        startedAtUnixMilliseconds);

    private void RecordQuery(bool withReadback = true)
    {
        Interlocked.Increment(ref nativeQueries);
        if (withReadback) Interlocked.Increment(ref readbacks);
    }

    private void RecordInput() => Interlocked.Increment(ref inputOperations);
    private void RecordTab() => Interlocked.Increment(ref tabSelections);

    public NativeInputService(NativeDispatcher? owner = null)
    {
        dispatcher = owner ?? new();
        ownsDispatcher = owner is null;
    }

    public Task<NativeFrame> BindAsync(WindowIdentity window, CancellationToken token = default, string? scopeAutomationId = null)
        => dispatcher.RunAsync(() =>
        {
            if (window.WindowHandle == nint.Zero) throw new AutomationException("WINDOW_NOT_FOUND", "HWND MISA không hợp lệ.");
            root = null;
            identity = null;
            var candidate = automation.FromHandle(window.WindowHandle);
            if (!string.IsNullOrWhiteSpace(scopeAutomationId))
            {
                var scopes = Snapshots(candidate, Live(automation.ConditionFactory.ByAutomationId(scopeAutomationId), window.ProcessId));
                if (scopes.Length != 1) throw new AutomationException("SCOPE_AMBIGUOUS", "Không xác định duy nhất editor cho input.");
                candidate = scopes[0].Element;
            }
            root = candidate;
            identity = window;
            epoch++;
            return Capture();
        }, token);

    public Task<NativeFrame> CaptureAsync(CancellationToken token = default)
        => dispatcher.RunAsync(Capture, token);

    public Task<Readback> SetValueAsync(NativeFrame frame, string automationId, string name, string value, CancellationToken token = default)
        => dispatcher.RunAsync(() =>
        {
            RecordInput();
            EnsureFrame(frame);
            Capture();
            var element = Find(automationId, name);
            if (!element.Patterns.Value.IsSupported) throw new AutomationException("PATTERN_UNSUPPORTED", $"Ô '{automationId}' không hỗ trợ ValuePattern.");
            if (element.Patterns.Value.Pattern.IsReadOnly.Value) throw new AutomationException("READ_ONLY", $"Ô '{automationId}' chỉ đọc.");
            if (value.Length > 32768 || value.Any(char.IsControl)) throw new AutomationException("INVALID_TEXT", "Giá trị nhập không hợp lệ.");
            token.ThrowIfCancellationRequested();
            try
            {
                element.Focus();
                token.ThrowIfCancellationRequested();
                element.Patterns.Value.Pattern.SetValue(value);
                epoch++;
                var fresh = Capture();
                return ReadbackFor(fresh, element, value);
            }
            catch (Exception error) { throw new AutomationException("INPUT_UNCERTAIN", $"Input vào '{automationId}' có thể đã được gửi; cần đọc lại.", true, error); }
        }, token, maySendInput: true);

    public Task<Readback> InvokeAsync(NativeFrame frame, string automationId, string name, CancellationToken token = default)
        => dispatcher.RunAsync(() =>
        {
            RecordInput();
            EnsureFrame(frame);
            Capture();
            var element = Find(automationId, name);
            if (!element.Patterns.Invoke.IsSupported && !element.Patterns.SelectionItem.IsSupported)
                throw new AutomationException("PATTERN_UNSUPPORTED", $"Control '{automationId}' không hỗ trợ Invoke/SelectionItem.");
            token.ThrowIfCancellationRequested();
            try
            {
                if (element.Patterns.Invoke.IsSupported)
                {
                    // Several R20 Infragistics buttons (most notably the
                    // "Mở dữ liệu"/"Đồng ý" login path) execute modal work
                    // synchronously from Invoke().  Calling that provider on
                    // the bounded native owner can block the owner until the
                    // 30 second deadline and poison every later observation.
                    // The element bounds were captured immediately above and
                    // Win32 click keeps the input native while returning as
                    // soon as SendInput has handed off the click.
                    var bounds = element.BoundingRectangle;
                    if (bounds.Width <= 0 || bounds.Height <= 0)
                        throw new AutomationException("BOUNDS_UNREADABLE", $"Control '{automationId}' không có bounds để click native.");
                    NativeKeyboard.Click(
                        checked((int)Math.Round(bounds.X + bounds.Width / 2d)),
                        checked((int)Math.Round(bounds.Y + bounds.Height / 2d)),
                        identity!.WindowHandle, identity.ProcessId,
                        automationId.StartsWith("[Toolbar :", StringComparison.Ordinal) ? WorkflowTargetHandle(element) : nint.Zero);
                }
                else if (element.Patterns.SelectionItem.IsSupported)
                {
                    // Infragistics value-list items expose SelectionItem but
                    // do not implement Select() on R20.  Use the same
                    // identity-checked native click path as buttons.
                    var bounds = element.BoundingRectangle;
                    if (bounds.Width <= 0 || bounds.Height <= 0)
                        throw new AutomationException("BOUNDS_UNREADABLE", $"Control '{automationId}' không có bounds để chọn native.");
                    NativeKeyboard.Click(
                        checked((int)Math.Round(bounds.X + bounds.Width / 2d)),
                        checked((int)Math.Round(bounds.Y + bounds.Height / 2d)),
                        identity!.WindowHandle, identity.ProcessId);
                }
                else throw new AutomationException("PATTERN_UNSUPPORTED", $"Control '{automationId}' không hỗ trợ Invoke/SelectionItem.");
                epoch++;
                var fresh = Capture();
                return new Readback("invoked_unverified", null, "UIA3.Invoke", fresh.ObservedAt,
                    "Invocation không chứng minh kết quả nghiệp vụ; cần readback mới.", fresh);
            }
            catch (Exception error)
            {
                throw new AutomationException("INPUT_UNCERTAIN", $"Click '{automationId}' có thể đã được gửi; cần đọc lại.", true, error);
            }
        }, token, maySendInput: true);

    private void DismissDirtyVoucherPrompt(nint editorHandle, CancellationToken token, int waitMilliseconds)
    {
        if (identity is null) return;
        var deadline = Environment.TickCount64 + Math.Max(0, waitMilliseconds);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var dialogs = new List<nint>();
            EnumWindows((handle, _) =>
            {
                GetWindowThreadProcessId(handle, out var processId);
                if (processId == identity.ProcessId && WindowText(handle) == "MISA SME 2023"
                    && PInvoke.IsWindowVisible((HWND)handle) && OwnedBy(handle, editorHandle)) dialogs.Add(handle);
                return true;
            }, nint.Zero);
            var candidates = new List<nint>();
            foreach (var dialog in dialogs)
            {
                var texts = new List<string>();
                var noButtons = new List<nint>();
                EnumChildWindows(dialog, (handle, _) =>
                {
                    var label = WindowText(handle);
                    texts.Add(label);
                    if ((label is "&No" or "No") && PInvoke.IsWindowEnabled((HWND)handle)
                        && PInvoke.IsWindowVisible((HWND)handle)) noButtons.Add(handle);
                    return true;
                }, nint.Zero);
                var dirtyPrompt = texts.Any(text => text.Contains("đã bị thay đổi", StringComparison.OrdinalIgnoreCase) && text.Contains("muốn cất", StringComparison.OrdinalIgnoreCase));
                if (dirtyPrompt && noButtons.Count == 1)
                {
                    candidates.Add(noButtons[0]);
                }
            }
            if (candidates.Count > 1)
                throw new AutomationException("DISCARD_PROMPT_AMBIGUOUS", "Multiple dirty-voucher prompts match the bound editor.");
            if (candidates.Count == 1)
            {
                token.ThrowIfCancellationRequested();
                MarkWorkflowInput();
                epoch++;
                if (SendMessageTimeout(candidates[0], 0x00F5, 0, nint.Zero, 2, 1000, out _) == nint.Zero)
                    throw new AutomationException("INPUT_UNCERTAIN", "Discard confirmation timed out; do not repeat it.", true);
                return;
            }
            if (Environment.TickCount64 >= deadline) return;
            if (token.WaitHandle.WaitOne(50)) token.ThrowIfCancellationRequested();
        }
    }

    private static bool OwnedBy(nint dialog, nint editorHandle)
    {
        var seen = new HashSet<nint>();
        for (var owner = dialog; owner != nint.Zero && seen.Add(owner);
            owner = (nint)PInvoke.GetWindow((HWND)owner, GET_WINDOW_CMD.GW_OWNER))
            if (owner == editorHandle) return true;
        return false;
    }

    private static string WindowText(nint handle)
    {
        var buffer = new StringBuilder(512);
        GetWindowText(handle, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private delegate bool WindowEnumerator(nint handle, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowEnumerator callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint parent, WindowEnumerator callback, nint parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint handle, StringBuilder text, int maxCount);

    public Task<Readback> ReadAsync(NativeFrame frame, string automationId, string name, CancellationToken token = default)
        => dispatcher.RunAsync(() =>
        {
            RecordQuery();
            EnsureFrame(frame);
            Capture();
            var element = Find(automationId, name);
            var fresh = Capture();
            var value = element.Patterns.Value.IsSupported ? element.Patterns.Value.Pattern.Value.ValueOrDefault : element.Properties.Name.ValueOrDefault;
            return new Readback("supported", value, element.Patterns.Value.IsSupported ? "UIA3.ValuePattern" : "UIA3.Name", fresh.ObservedAt, null, fresh);
        }, token);

    public Task<Readback> ReadActiveEditorAsync(NativeFrame frame, CancellationToken token = default)
        => dispatcher.RunAsync(() =>
        {
            RecordQuery();
            GuardWorkflow(frame);
            var focused = automation.ConditionFactory.ByControlType(ControlType.Edit)
                .And(new PropertyCondition(automation.PropertyLibrary.Element.HasKeyboardFocus, true));
            var editors = Snapshots(root!, Live(focused, identity!.ProcessId))
                .Where(item => item.HasArea && item.HasKeyboardFocus).Select(item => item.Element).ToArray();
            if (editors.Length != 1) throw new AutomationException("ACTIVE_EDITOR_AMBIGUOUS", "Không xác định duy nhất editor đang nhận focus.");
            var text = ReadNativeText(editors[0]);
            return new Readback("supported", text.Value, text.Source + "+ActiveEditor", DateTimeOffset.UtcNow, null, Capture());
        }, token);

    public Task<Readback> SelectDirectTabAsync(NativeFrame frame, string caption, CancellationToken token = default)
        => dispatcher.RunAsync(() =>
        {
            RecordInput();
            EnsureFrame(frame);
            Capture();
            var target = Find("", caption);
            if (target.ControlType != ControlType.TabItem || !target.Patterns.SelectionItem.IsSupported)
                throw new AutomationException("PATTERN_UNSUPPORTED", "Tab không hỗ trợ SelectionItemPattern.");
            token.ThrowIfCancellationRequested();
            try
            {
                RecordTab();
                target.Patterns.SelectionItem.Pattern.Select(); epoch++;
                target = Find("", caption);
                if (!target.Patterns.SelectionItem.Pattern.IsSelected.Value)
                    throw new AutomationException("READBACK_MISMATCH", "Tab chưa được chọn.", true);
            }
            catch (Exception error) { throw new AutomationException("INPUT_UNCERTAIN", "Không xác nhận được chuyển tab.", true, error); }
            var fresh = Capture();
            return new Readback("supported", caption, "UIA3.SelectionItem", fresh.ObservedAt, null, fresh);
        }, token, maySendInput: true);

    public Task<Readback> ScrollAsync(NativeFrame frame, string automationId, string name, CancellationToken token = default)
        => dispatcher.RunAsync(() =>
        {
            RecordInput();
            EnsureFrame(frame);
            Capture();
            var target = Find(automationId, name, allowOffscreen: true);
            if (!target.Patterns.ScrollItem.IsSupported)
                throw new AutomationException("SCROLL_UNSUPPORTED", "Native target không hỗ trợ ScrollItemPattern.");
            token.ThrowIfCancellationRequested();
            try
            {
                target.Patterns.ScrollItem.Pattern.ScrollIntoView(); epoch++;
                target = Find(automationId, name);
                var fresh = Capture();
                return new Readback("supported", target.Properties.Name.ValueOrDefault, "UIA3.ScrollItem+IsOffscreen", fresh.ObservedAt, null, fresh);
            }
            catch (Exception error) { throw new AutomationException("INPUT_UNCERTAIN", "Không xác nhận được scroll.", true, error); }
        }, token, maySendInput: true);

    public Task<Readback> PressKeyAsync(NativeFrame frame, string automationId, string name, string key, CancellationToken token = default)
        => dispatcher.RunAsync(() =>
        {
            RecordInput();
            EnsureFrame(frame);
            Capture();
            var target = Find(automationId, name);
            var handle = target.Properties.NativeWindowHandle.ValueOrDefault;
            if (handle == nint.Zero) throw new AutomationException("KEY_TARGET_INVALID", "Target không có HWND native để xác minh phím.");
            token.ThrowIfCancellationRequested();
            NativeKeyboard.Activate(handle, identity!.WindowHandle, identity.ProcessId);
            target.Focus();
            if (!target.Properties.HasKeyboardFocus.ValueOrDefault && !NativeKeyboard.HasTargetFocus(handle))
                throw new AutomationException("FOCUS_FAILED", "Ô được chọn chưa nhận focus; không gửi phím.");
            try
            {
                NativeKeyboard.Press(handle, identity!.WindowHandle, identity.ProcessId, key, token);
                epoch++;
                var fresh = Capture();
                return new Readback("sent_unverified", null, "Win32.SendInput", fresh.ObservedAt,
                    "Phím đã gửi tới target; cần native readback mới.", fresh);
            }
            catch (AutomationException) { throw; }
            catch (Exception error) { throw new AutomationException("INPUT_UNCERTAIN", "Không xác định được kết quả phím.", true, error); }
        }, token, maySendInput: true);

    private NativeFrame Capture()
    {
        if (root is null || identity is null) throw new AutomationException("NOT_CONNECTED", "Chưa bind cửa sổ MISA.");
        var current = RefreshWindow(identity);
        if (current.ProcessStartUtc != identity.ProcessStartUtc || current.WindowHandle != identity.WindowHandle)
            throw new AutomationException("WINDOW_IDENTITY_CHANGED", "Process hoặc HWND MISA đã thay đổi.");
        identity = current;
        return new(current, root.Properties.Name.ValueOrDefault ?? "", epoch, DateTimeOffset.UtcNow);
    }

    private AutomationElement Find(string automationId, string name, bool allowOffscreen = false)
    {
        if (root is null || identity is null) throw new AutomationException("NOT_CONNECTED", "Chưa bind cửa sổ MISA.");
        ConditionBase? condition = !string.IsNullOrWhiteSpace(automationId) ? automation.ConditionFactory.ByAutomationId(automationId)
            : !string.IsNullOrWhiteSpace(name) ? automation.ConditionFactory.ByName(name) : null;
        var candidates = condition is null ? [] : Snapshots(root, Live(condition, identity.ProcessId, onscreen: !allowOffscreen))
            .Where(item => string.IsNullOrWhiteSpace(name) || string.Equals(item.Name, name, StringComparison.Ordinal))
            .Select(item => item.Element).ToList();
        if (candidates.Count != 1) throw new AutomationException(candidates.Count == 0 ? "NOT_FOUND" : "AMBIGUOUS_ELEMENT", $"Không xác định duy nhất control '{automationId}/{name}'.");
        return candidates[0];
    }

    private static Readback ReadbackFor(NativeFrame frame, AutomationElement element, string expected)
    {
        var value = element.Patterns.Value.IsSupported ? element.Patterns.Value.Pattern.Value.ValueOrDefault : element.Properties.Name.ValueOrDefault;
        if (!string.Equals(value, expected, StringComparison.Ordinal))
            throw new AutomationException("READBACK_MISMATCH", "Native readback không khớp giá trị nguồn.", true);
        return new("supported", value, "UIA3.ValuePattern", frame.ObservedAt, null, frame);
    }

    private void EnsureFrame(NativeFrame frame)
    {
        if (identity is null || frame.Window.WindowHandle != identity.WindowHandle || frame.Window.ProcessId != identity.ProcessId
            || frame.Window.ProcessStartUtc != identity.ProcessStartUtc || frame.InputEpoch != epoch)
            throw new AutomationException("STALE_NATIVE_FRAME", "Quan sát đã cũ; cần native frame mới.");
    }

    public ValueTask DisposeAsync()
    {
        windowTracker.Dispose();
        if (ownsDispatcher) dispatcher.Dispose();
        return ValueTask.CompletedTask;
    }
}
