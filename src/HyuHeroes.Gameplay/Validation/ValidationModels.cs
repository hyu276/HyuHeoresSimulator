/**
 * VALIDATION_MODELS
 * Purpose: Defines immutable validation results and a small collector shared by schema validators.
 * Connections: Returned by GameplaySchemaValidator and populated by primitive graph validation helpers.
 * Risk: Low because these types report errors without changing authoritative gameplay state.
 */
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace HyuHeroes.Gameplay.Validation;

public sealed class ValidationError
{
    public ValidationError(string code, string path, string message)
    {
        Code = code;
        Path = path;
        Message = message;
    }

    public string Code { get; }
    public string Path { get; }
    public string Message { get; }

    public override string ToString() => $"{Code} at {Path}: {Message}";
}

public sealed class ValidationResult
{
    public ValidationResult(IEnumerable<ValidationError> errors)
    {
        Errors = new ReadOnlyCollection<ValidationError>(errors.ToArray());
    }

    public IReadOnlyList<ValidationError> Errors { get; }
    public bool IsValid => Errors.Count == 0;
}

internal sealed class ValidationCollector
{
    private readonly List<ValidationError> _errors = new();

    public void Add(string code, string path, string message) =>
        _errors.Add(new ValidationError(code, path, message));

    public ValidationResult ToResult() => new(_errors);
}
