using System.Runtime.Versioning;
using Microsoft.Win32;
using PcmCdbEditor.Application;

namespace PcmCdbEditor.Infrastructure.FileAssociations;

[SupportedOSPlatform("windows")]
public sealed class WindowsFileAssociationService : IFileAssociationService
{
    private const string Extension = ".cdb";
    private const string ProgramId = "Peter537.PcmCdbEditor.cdb";
    private const string ExtensionKey = $@"Software\Classes\{Extension}\OpenWithProgids";
    private const string ProgramKey = $@"Software\Classes\{ProgramId}";

    public Task<FileAssociationState> InspectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(new FileAssociationState(false, null, "File associations are supported only on Windows."));
        }

        try
        {
            using var extension = Registry.CurrentUser.OpenSubKey(ExtensionKey);
            using var command = Registry.CurrentUser.OpenSubKey($@"{ProgramKey}\shell\open\command");
            var commandLine = command?.GetValue(null) as string;
            var isRegistered = extension?.GetValueNames().Contains(ProgramId, StringComparer.Ordinal) == true
                               && commandLine is not null;
            return Task.FromResult(new FileAssociationState(
                isRegistered,
                isRegistered && commandLine is not null ? ExtractExecutable(commandLine) : null,
                null));
        }
        catch (UnauthorizedAccessException exception)
        {
            return Task.FromResult(new FileAssociationState(false, null, exception.Message));
        }
    }

    public Task RegisterAsync(string executablePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWindows();
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        var fullPath = Path.GetFullPath(executablePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The application executable was not found.", fullPath);
        }

        using (var extension = Registry.CurrentUser.CreateSubKey(ExtensionKey, writable: true))
        {
            extension.SetValue(ProgramId, Array.Empty<byte>(), RegistryValueKind.None);
        }

        using (var program = Registry.CurrentUser.CreateSubKey(ProgramKey, writable: true))
        {
            program.SetValue(null, "PCM CDB Database", RegistryValueKind.String);
        }

        using (var icon = Registry.CurrentUser.CreateSubKey($@"{ProgramKey}\DefaultIcon", writable: true))
        {
            icon.SetValue(null, $"\"{fullPath}\",0", RegistryValueKind.String);
        }

        using (var command = Registry.CurrentUser.CreateSubKey($@"{ProgramKey}\shell\open\command", writable: true))
        {
            command.SetValue(null, $"\"{fullPath}\" \"%1\"", RegistryValueKind.String);
        }

        return Task.CompletedTask;
    }

    public Task RemoveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWindows();
        using (var extension = Registry.CurrentUser.OpenSubKey(ExtensionKey, writable: true))
        {
            if (extension?.GetValueNames().Contains(ProgramId, StringComparer.Ordinal) == true)
            {
                extension.DeleteValue(ProgramId, throwOnMissingValue: false);
            }
        }

        Registry.CurrentUser.DeleteSubKeyTree(ProgramKey, throwOnMissingSubKey: false);
        return Task.CompletedTask;
    }

    private static string? ExtractExecutable(string commandLine)
    {
        var trimmed = commandLine.Trim();
        if (trimmed.StartsWith('"'))
        {
            var end = trimmed.IndexOf('"', 1);
            return end > 1 ? trimmed[1..end] : null;
        }

        var separator = trimmed.IndexOf(' ', StringComparison.Ordinal);
        return separator > 0 ? trimmed[..separator] : trimmed;
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("File associations are supported only on Windows.");
        }
    }
}
