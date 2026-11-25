using System.Management.Automation;
using System.Management.Automation.Runspaces;

namespace SecretsManager.Core;

/// <summary>
/// One in-process PowerShell runspace, used by one call at a time on a background thread.
/// In-process matters: SecretStore keeps its unlocked state per process, so a single long-lived
/// host means the user unlocks once per session instead of once per command.
/// </summary>
public sealed class PowerShellHost : IDisposable
{
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly Lazy<Runspace> _runspace;

    public PowerShellHost()
    {
        _runspace = new Lazy<Runspace>(() =>
        {
            var iss = InitialSessionState.CreateDefault2();
            // Scripts are constants in this assembly; Bypass only stops the machine's policy from
            // blocking module loading inside this process.
            iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
            // Every script runs with terminating errors; modules (SecretManagement, SecretStore) autoload.
            iss.Variables.Add(new SessionStateVariableEntry("ErrorActionPreference", ActionPreference.Stop, "Set by SecretsManager"));
            var rs = RunspaceFactory.CreateRunspace(iss);
            rs.Open();
            return rs;
        }, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// Runs a script whose param() block receives <paramref name="parameters"/> (values are bound as
    /// objects, never spliced into script text). Errors become <see cref="SecretsOperationException"/>
    /// or <see cref="VaultLockedException"/>.
    /// </summary>
    public async Task<IReadOnlyList<PSObject>> InvokeAsync(string script, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Invoke(script, parameters), ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    IReadOnlyList<PSObject> Invoke(string script, IReadOnlyDictionary<string, object?>? parameters)
    {
        using var ps = PowerShell.Create();
        ps.Runspace = _runspace.Value;
        ps.AddScript(script, useLocalScope: true);
        if (parameters is not null)
            foreach (var (k, v) in parameters) ps.AddParameter(k, v);
        try
        {
            var output = ps.Invoke();
            if (ps.Streams.Error.Count > 0) throw Translate(ps.Streams.Error[0], null);
            return output.Where(o => o is not null).ToList();
        }
        catch (RuntimeException ex) { throw Translate(ex.ErrorRecord, ex); }
    }

    static SecretsOperationException Translate(ErrorRecord? record, Exception? thrown)
    {
        var messages = new List<string>();
        if (record?.ErrorDetails?.Message is { } details) messages.Add(details);
        for (Exception? e = record?.Exception ?? thrown; e is not null; e = e.InnerException) messages.Add(e.Message);
        var message = messages.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m)) ?? "PowerShell command failed.";
        if (message.Contains("is not recognized", StringComparison.OrdinalIgnoreCase) && message.Contains("Secret", StringComparison.OrdinalIgnoreCase))
            message = "The Microsoft.PowerShell.SecretManagement module (or SecretStore) is not installed for PowerShell 7. " +
                      "Install it with: Install-PSResource Microsoft.PowerShell.SecretManagement, Microsoft.PowerShell.SecretStore -Scope CurrentUser" +
                      Environment.NewLine + "(" + message + ")";
        var inner = record?.Exception ?? thrown;
        return messages.Any(VaultLockedException.IsLockedMessage)
            ? new VaultLockedException(message, inner)
            : new SecretsOperationException(message, inner);
    }

    public void Dispose()
    {
        if (_runspace.IsValueCreated) _runspace.Value.Dispose();
        _gate.Dispose();
    }
}
