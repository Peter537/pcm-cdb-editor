using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PcmCdbEditor.Infrastructure.Internal;

internal sealed class WindowsConverterProcess : IDisposable
{
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateNoWindow = 0x08000000;
    private const uint UseStandardHandles = 0x00000100;
    private const uint AttributeHandleList = 0x00020002;
    private const uint AttributeJobList = 0x0002000D;

    private readonly SafeFileHandle _job;
    private readonly SafeProcessHandle _process;

    private WindowsConverterProcess(
        SafeFileHandle job, SafeProcessHandle process, PipeStream output, PipeStream error)
    {
        _job = job;
        _process = process;
        StandardOutput = new StreamReader(output, Console.OutputEncoding);
        StandardError = new StreamReader(error, Console.OutputEncoding);
    }

    public StreamReader StandardOutput { get; }
    public StreamReader StandardError { get; }

    public int ExitCode => GetExitCodeProcess(_process, out int code)
        ? code
        : throw new Win32Exception(Marshal.GetLastWin32Error());

    public static WindowsConverterProcess Start(string executablePath, IReadOnlyList<string> arguments)
    {
        SafeFileHandle job = WindowsProcessJob.Create();
        AnonymousPipeServerStream? output = null;
        AnonymousPipeServerStream? error = null;
        SafeProcessHandle? process = null;
        try
        {
            using var input = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
            output = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
            error = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
            process = CreateInJob(executablePath, arguments, job, input, output, error);
            output.DisposeLocalCopyOfClientHandle();
            error.DisposeLocalCopyOfClientHandle();
            return new WindowsConverterProcess(job, process, output, error);
        }
        catch
        {
            job.Dispose();
            process?.Dispose();
            output?.Dispose();
            error?.Dispose();
            throw;
        }
    }

    public async Task WaitForExitAsync(CancellationToken cancellationToken)
    {
        using var exited = new EventWaitHandle(false, EventResetMode.AutoReset);
        exited.SafeWaitHandle = new SafeWaitHandle(_process.DangerousGetHandle(), ownsHandle: false);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RegisteredWaitHandle registration = ThreadPool.RegisterWaitForSingleObject(
            exited, static (state, _) => ((TaskCompletionSource)state!).TrySetResult(),
            completion, Timeout.Infinite, executeOnlyOnce: true);
        try
        {
            await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            registration.Unregister(null);
        }
    }

    public void Stop() => _job.Dispose();

    public void Dispose()
    {
        Stop();
        StandardOutput.Dispose();
        StandardError.Dispose();
        _process.Dispose();
    }

    private static SafeProcessHandle CreateInJob(
        string path, IReadOnlyList<string> arguments, SafeFileHandle job,
        AnonymousPipeServerStream input, AnonymousPipeServerStream output, AnonymousPipeServerStream error)
    {
        nuint attributeBytes = 0;
        _ = InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref attributeBytes);
        if (attributeBytes == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        IntPtr attributes = Marshal.AllocHGlobal(checked((int)attributeBytes));
        IntPtr values = IntPtr.Zero;
        IntPtr mutableCommandLine = IntPtr.Zero;
        bool initialized = false;
        try
        {
            initialized = InitializeProcThreadAttributeList(attributes, 2, 0, ref attributeBytes);
            if (!initialized)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            IntPtr stdin = input.ClientSafePipeHandle.DangerousGetHandle();
            IntPtr stdout = output.ClientSafePipeHandle.DangerousGetHandle();
            IntPtr stderr = error.ClientSafePipeHandle.DangerousGetHandle();
            values = Marshal.AllocHGlobal(4 * IntPtr.Size);
            Marshal.Copy([stdin, stdout, stderr, job.DangerousGetHandle()], 0, values, 4);
            if (!UpdateProcThreadAttribute(attributes, 0, AttributeHandleList, values, (nuint)(3 * IntPtr.Size),
                    IntPtr.Zero, IntPtr.Zero) ||
                !UpdateProcThreadAttribute(attributes, 0, AttributeJobList, values + 3 * IntPtr.Size, (nuint)IntPtr.Size,
                    IntPtr.Zero, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var startup = new StartupInfoEx
            {
                Size = Marshal.SizeOf<StartupInfoEx>(),
                Flags = UseStandardHandles,
                StandardInput = stdin,
                StandardOutput = stdout,
                StandardError = stderr,
                Attributes = attributes
            };
            var commandLine = new StringBuilder();
            AppendArgument(commandLine, path);
            foreach (string argument in arguments)
            {
                commandLine.Append(' ');
                AppendArgument(commandLine, argument);
            }

            mutableCommandLine = Marshal.StringToHGlobalUni(commandLine.ToString());
            // Windows assigns the job before any converter code can run or spawn a child.
            if (!CreateProcessW(path, mutableCommandLine, IntPtr.Zero, IntPtr.Zero, true,
                    ExtendedStartupInfoPresent | CreateNoWindow, IntPtr.Zero,
                    Path.GetDirectoryName(path), ref startup, out ProcessInformation information))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            using var thread = new SafeFileHandle(information.Thread, ownsHandle: true);
            return new SafeProcessHandle(information.Process, ownsHandle: true);
        }
        finally
        {
            if (initialized)
            {
                DeleteProcThreadAttributeList(attributes);
            }
            Marshal.FreeHGlobal(mutableCommandLine);
            Marshal.FreeHGlobal(values);
            Marshal.FreeHGlobal(attributes);
        }
    }

    private static void AppendArgument(StringBuilder commandLine, string argument)
    {
        commandLine.Append('"');
        int backslashes = 0;
        foreach (char character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            commandLine.Append('\\', character == '"' ? 2 * backslashes + 1 : backslashes);
            commandLine.Append(character);
            backslashes = 0;
        }
        commandLine.Append('\\', 2 * backslashes).Append('"');
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public int Size;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort ReservedSize;
        public IntPtr Reserved2;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
        public IntPtr Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(
        IntPtr attributes, int attributeCount, int flags, ref nuint size);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr attributes, uint flags,
        nuint attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returnedSize);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr attributes);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string application, IntPtr arguments, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, IntPtr environment,
        string? directory, ref StartupInfoEx startup, out ProcessInformation information);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out int exitCode);
}
