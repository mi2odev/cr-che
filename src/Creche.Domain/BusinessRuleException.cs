namespace Creche.Domain;

/// <summary>
/// A business rule was broken. <see cref="Code"/> is a stable key the UI uses to show a translated message
/// (looked up as "Error.{Code}"); <see cref="Exception.Message"/> is the English fallback.
/// </summary>
public class BusinessRuleException : Exception
{
    public string Code { get; }
    public object[] Args { get; }

    public BusinessRuleException(string code, string message, params object[] args) : base(message)
    {
        Code = code;
        Args = args;
    }
}

/// <summary>The group is full but the crèche only warns: the caller may retry with "allow over capacity".</summary>
public sealed class GroupFullWarningException(string groupName, int capacity)
    : BusinessRuleException("GroupFullWarn", $"Group '{groupName}' is full ({capacity} places).", groupName, capacity);

/// <summary>The signed-in user's role does not allow the operation (or nobody is signed in).</summary>
public sealed class PermissionDeniedException(string message)
    : BusinessRuleException("PermissionDenied", message);
