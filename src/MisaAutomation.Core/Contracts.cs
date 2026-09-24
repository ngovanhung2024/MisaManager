namespace MisaAutomation.Core;

public sealed record WindowInfo(long Handle, int ProcessId, string Title, string Kind, bool Enabled = true);
public sealed record Bounds(double X, double Y, double Width, double Height);
public sealed record ElementInfo(
    string Token, string Id, string Name, int Kind, string ClassName,
    Bounds Bounds, bool Enabled, bool Offscreen, bool Selected, int Toggle,
    string? Value, long WindowId, string[] Patterns, string? ParentToken = null);

public sealed record UiaTreeNode(ElementInfo Element, long NativeWindowHandle,
    long ParentNativeWindowHandle, int Depth);

public sealed record Locator
{
    public string? AutomationId { get; init; }
    public string? Name { get; init; }
    public int? ControlType { get; init; }
    public string? ClassName { get; init; }
    public string? Parent { get; init; }
    public string? Anchor { get; init; }
    public string? WindowKind { get; init; }
    public bool VisibleOnly { get; init; } = true;
    public bool ReadOnly { get; init; }
    public string? Keyboard { get; init; }
}

public sealed record ElementQuery(string[]? Ids = null, string[]? Names = null,
    int[]? Kinds = null, string? ParentToken = null, bool ChildrenOnly = false);
public sealed record Inspection(ElementInfo Element, IReadOnlyList<ElementInfo> Parents, WindowInfo Window);
public sealed record GridRow(int Index, IReadOnlyList<ElementInfo> Cells, ElementInfo? Element = null);
public sealed record GridPage(int RowCount, int ColumnCount, string[] Columns,
    IReadOnlyList<GridRow> Rows, string Strategy);
public sealed record ValueObservation(string Status, string? Value, string ValueSource,
    DateTimeOffset ObservedAt, long WindowId, int ProcessId, Bounds? Bounds, string Reason);
public sealed record ObservedDateTarget(string AutomationId, Bounds Bounds, Bounds WindowBounds,
    long WindowId, int ProcessId, DateTimeOffset ObservedAt);
public sealed record ObservedValueTarget(string Purpose, string? AutomationId, Bounds Bounds,
    Bounds WindowBounds, long WindowId, int ProcessId, int? Row, DateTimeOffset ObservedAt);

public sealed record Operation(string Action, string? Locator = null,
    string? Token = null, string? Value = null, string? Expected = null,
    int TimeoutMs = 5000, string Workflow = "", int? Key = null, bool Control = false,
    Locator? Selector = null, ObservedDateTarget? DateTarget = null,
    bool AllowBackground = false);

public sealed record AutomationResult(bool Success, string? ErrorCode, string Message,
    double DurationMs, string StrategyUsed, string? ElementLocator,
    object? Data = null, double ElementSearchMs = 0, double ActionMs = 0,
    double WaitMs = 0, bool InputMayHaveBeenSent = false);

public sealed class AutomationException : Exception
{
    public string Code { get; }
    public bool InputMayHaveBeenSent { get; }
    public AutomationException(string code, string message, bool inputMayHaveBeenSent = false,
        Exception? inner = null) : base(message, inner)
        => (Code, InputMayHaveBeenSent) = (code, inputMayHaveBeenSent);
}

public interface IAutomationLog
{
    void Write(string eventName, object data);
}

public interface IElementResolver
{
    ElementInfo Find(string key, Locator locator, CancellationToken cancellationToken);
}

public interface IAutomationProvider : IElementResolver, IDisposable
{
    WindowInfo? Window { get; }
    ChangeSignal Changes { get; }
    WindowInfo Connect(long? windowHandle, int? processId);
    WindowInfo BindWindow(long handle);
    IReadOnlyList<WindowInfo> Windows();
    IReadOnlyList<UiaTreeNode> DumpTree(int maximumNodes = 10000);
    IReadOnlyList<ElementInfo> Query(ElementQuery query);
    ElementInfo Read(string token);
    string Execute(string token, string action, string? value, bool allowBackground = false);
    void Focus(string token, bool allowBackground = false);
    string WindowText(long handle);
    Inspection InspectPoint(int x, int y);
    GridPage ReadGrid(string token, int startRow, int maximumRows);
    ValueObservation ObserveValue(Bounds bounds, string? automationId, int? row);
    string SetObservedValue(ObservedValueTarget target, string value, string workflow);
    string SetObservedDate(ObservedDateTarget target, string value, string workflow);
    string CaptureFailure(Operation operation, Exception error);
    void GuardKeyboard(int key, bool control);
    void Invalidate();
    object Diagnostics();
}

public interface IInputProvider
{
    void Press(WindowInfo window, int key, bool control);
    void Paste(WindowInfo window, string text, Action ensureFocused);
}
