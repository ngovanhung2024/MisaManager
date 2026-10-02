namespace MisaManager.Core;

public sealed class AutomationException : Exception
{
    public string Code { get; }
    public bool InputMayHaveBeenSent { get; }
    public AutomationException(string code, string message, bool inputMayHaveBeenSent = false, Exception? inner = null)
        : base(message, inner) => (Code, InputMayHaveBeenSent) = (code, inputMayHaveBeenSent);
}
