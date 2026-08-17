using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

internal static class FakeConverterProgram
{
    private const string ScenarioFileName = "scenario.txt";

    private static int Main(string[] arguments)
    {
        string executablePath = Assembly.GetExecutingAssembly().Location;
        string root = Path.GetDirectoryName(executablePath);
        if (arguments.Length == 1 && arguments[0] == "--child")
        {
            File.WriteAllText(
                Path.Combine(root, "child.pid"),
                Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture));
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }

        string[] scenario = File.ReadAllLines(Path.Combine(root, ScenarioFileName));
        string mode = scenario[0];
        string outputPath = GetOutputPath(arguments);

        if (mode == "nonzero")
        {
            Console.Out.Write("neutral standard output");
            Console.Error.Write("neutral standard error");
            return 23;
        }

        if (mode == "missing")
        {
            return 0;
        }

        if (mode == "empty")
        {
            File.WriteAllBytes(outputPath, new byte[0]);
            return 0;
        }

        if (mode == "noise")
        {
            string sensitive = executablePath + "\n" + arguments[arguments.Length - 1] + "\n";
            Console.Out.Write(sensitive + "out\0" + new string('O', 65536));
            Console.Error.Write(sensitive + "err\u0001" + new string('E', 65536));
            return 29;
        }

        if (mode == "boundary-noise")
        {
            Console.Out.Write(new string('P', 16381) + executablePath + new string('O', 4096));
            Console.Error.Write(new string('P', 16381) + arguments[arguments.Length - 1] + new string('E', 4096));
            return 29;
        }

        if (mode == "sleep")
        {
            WriteProcessId(root, "parent.pid");
            Console.Out.WriteLine("ready");
            Console.Out.Flush();
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }

        if (mode == "spawn-child")
        {
            WriteProcessId(root, "parent.pid");
            Process child = Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = "--child",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            if (child == null)
            {
                return 31;
            }

            Console.Out.WriteLine("child started");
            Console.Out.Flush();
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }

        if (mode == "validate")
        {
            string expectedOperation = scenario[1];
            string expectedPath = Encoding.UTF8.GetString(Convert.FromBase64String(scenario[2]));
            if (arguments.Length != 3
                || arguments[0] != "-a"
                || arguments[1] != expectedOperation
                || arguments[2] != expectedPath)
            {
                Console.Error.Write("argument mismatch");
                return 37;
            }

            File.WriteAllText(outputPath, "synthetic converter output");
            return 0;
        }

        Console.Error.Write("unknown scenario");
        return 41;
    }

    private static string GetOutputPath(string[] arguments)
    {
        if (arguments.Length != 3 || arguments[0] != "-a")
        {
            throw new InvalidOperationException("Unexpected converter argument shape.");
        }

        if (arguments[1] == "-export")
        {
            return Path.ChangeExtension(arguments[2], ".sqlite");
        }

        if (arguments[1] == "-import")
        {
            return arguments[2] + ".cdb";
        }

        throw new InvalidOperationException("Unexpected converter operation.");
    }

    private static void WriteProcessId(string root, string fileName)
    {
        File.WriteAllText(
            Path.Combine(root, fileName),
            Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture));
    }
}
