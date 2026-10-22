using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;

namespace Clicky.Windows;

/// <summary>The exception the --debug-crash flag raises so crash reporting can be exercised on demand (T-016).</summary>
internal sealed class DeliberateDebugCrashException(string message) : Exception(message);

/// <summary>
/// Local crash reporting (WS-11 decision: a local minidump plus a crash text file, NO crash SDK —
/// a Sentry-style SDK would ship exception messages, which embed Worker response bodies, to a new
/// processor; the local option adds no privacy surface). A crash is one pair under
/// &lt;data directory&gt;\crashes: &lt;utc&gt;.txt (version, OS build, exception, last 200 log lines) and a
/// matching &lt;utc&gt;.dmp. Only the newest five crashes are kept.
///
/// Dump mechanism (deliberate choice, per the WS-11 posture "simplest reliable mechanism that needs
/// no admin and no SDK"): a synchronous MiniDumpWriteDump call from the managed unhandled-exception
/// handlers. The alternative considered was Windows Error Reporting's LocalDumps (HKCU registry,
/// also admin-free), but WER writes its dump asynchronously after process death — untestable without
/// timing races — and into machine settings we would then have to own. MiniDumpWriteDump lands the
/// dump in our own data directory deterministically. Native crashes (corrupted state the managed
/// handlers never see) still go to WER only, which WS-11 accepts.
/// The interop below is hand-declared instead of CsWin32-generated: CsWin32 refuses MiniDumpWriteDump
/// and its MINIDUMP structures for AnyCPU builds (error PInvoke005, architecture-specific API), and
/// pinning the whole project to x64 just for this one call would be a wider change than the
/// three-line import actually needs.
/// </summary>
internal static class ClickyCrashReporter
{
    private const int KeptCrashCount = 5;
    private const int LogLineCountInCrashTextFile = 200;

    /// <summary>0 = no report yet, 1 = written. Interlocked so the two handlers cannot both write a pair.</summary>
    private static int crashReportWasWritten;

    public static string CrashDirectoryPath { get; } = Path.Combine(ClickyDataDirectory.RootPath, "crashes");

    /// <summary>
    /// Catches every unhandled exception on any thread. This must be installed before anything else
    /// in Main can throw, or crashes during startup would leave no report (WS-11).
    /// </summary>
    public static void InstallOnCurrentAppDomain() =>
        AppDomain.CurrentDomain.UnhandledException += (_, unhandledExceptionEventArgs) =>
            WriteCrashReport(unhandledExceptionEventArgs.ExceptionObject as Exception
                ?? new Exception($"Unhandled non-exception object: {unhandledExceptionEventArgs.ExceptionObject}"));

    /// <summary>
    /// Catches exceptions on the WPF dispatcher thread while the message loop runs (WS-11 names both
    /// handlers). The event is deliberately left unhandled so the process still dies non-zero; when
    /// the exception then reaches AppDomain.UnhandledException, the once-guard makes that stop a no-op.
    /// </summary>
    public static void InstallOnApplication(Application application) =>
        application.DispatcherUnhandledException += (_, dispatcherUnhandledExceptionEventArgs) =>
            WriteCrashReport(dispatcherUnhandledExceptionEventArgs.Exception);

    public static void WriteCrashReport(Exception crashedException)
    {
        if (Interlocked.Exchange(ref crashReportWasWritten, 1) == 1)
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(CrashDirectoryPath);
            string crashUtcFileStem = BuildUnusedCrashUtcFileStem();
            File.WriteAllText(Path.Combine(CrashDirectoryPath, crashUtcFileStem + ".txt"), BuildCrashTextFileContents(crashedException));
            WriteMinidump(Path.Combine(CrashDirectoryPath, crashUtcFileStem + ".dmp"));
            DeleteCrashesBeyondKeptCount();
        }
        catch (Exception crashReportingException)
        {
            // Swallow deliberately: this runs while the process is dying, and throwing from here would
            // mask the original crash. Native failures remain WER's to report (accepted in WS-11).
            Console.Error.WriteLine($"crash reporter failed: {crashReportingException}");
        }
    }

    private static string BuildCrashTextFileContents(Exception crashedException)
    {
        // The exception message is included verbatim: this file stays on the user's disk and is never
        // uploaded (that is the whole no-Sentry decision), and our error messages embedding Worker
        // response bodies is exactly what a developer needs when triaging a local dump.
        string clickyVersion = typeof(ClickyCrashReporter).Assembly.GetName().Version?.ToString() ?? "unknown";
        string[] crashTextFileLines =
        [
            $"version: {clickyVersion}",
            $"os: {Environment.OSVersion.VersionString} (64-bit process: {Environment.Is64BitProcess})",
            $"utc: {DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)}",
            "",
            $"exception: {crashedException.GetType().FullName}: {crashedException.Message}",
            crashedException.StackTrace ?? "(no managed stack trace)",
            "",
            $"--- last {LogLineCountInCrashTextFile} log lines ---",
            .. ClickyLog.ReadLastLoggedLines(LogLineCountInCrashTextFile),
        ];
        return string.Join(Environment.NewLine, crashTextFileLines) + Environment.NewLine;
    }

    private static void WriteMinidump(string minidumpFilePath)
    {
        using FileStream minidumpFile = File.Create(minidumpFilePath);
        // Marshal.GetExceptionPointers is only meaningful on the thread currently handling the exception,
        // which is exactly where this method runs. The runtime hands out no pointers for some exception
        // kinds; the dump is still written then, just without a highlighted exception record.
        nint exceptionPointers = Marshal.GetExceptionPointers();
        bool minidumpWasWritten;
        if (exceptionPointers != nint.Zero)
        {
            var dumpExceptionInformation = new MinidumpExceptionInformation
            {
                ThreadId = GetCurrentThreadId(),
                ExceptionPointers = exceptionPointers,
                // In-process dump: the pointers are already valid in this address space, so dbghelp
                // must not try to marshal them from a remote process.
                ClientPointers = FalseAsInt,
            };
            // MiniDumpNormal (0) keeps the dump small — module list, thread stacks, exception record —
            // enough for local triage without copying the managed heap into the user's data directory.
            minidumpWasWritten = MiniDumpWriteDumpWithExceptionInformation(
                GetCurrentProcess(),
                (uint)Environment.ProcessId,
                minidumpFile.SafeFileHandle.DangerousGetHandle(),
                MiniDumpNormal,
                ref dumpExceptionInformation,
                nint.Zero,
                nint.Zero);
        }
        else
        {
            minidumpWasWritten = MiniDumpWriteDumpWithoutExceptionInformation(
                GetCurrentProcess(),
                (uint)Environment.ProcessId,
                minidumpFile.SafeFileHandle.DangerousGetHandle(),
                MiniDumpNormal,
                nint.Zero,
                nint.Zero,
                nint.Zero);
        }
        if (!minidumpWasWritten)
        {
            throw new InvalidOperationException($"MiniDumpWriteDump failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }
    }

    private const uint MiniDumpNormal = 0;
    private const int FalseAsInt = 0;

    /// <summary>dbghelp.h MINIDUMP_EXCEPTION_INFORMATION, whose containing header #includes pshpack4.h — hence Pack=4.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct MinidumpExceptionInformation
    {
        public uint ThreadId;
        public nint ExceptionPointers;
        public int ClientPointers;
    }

    [DllImport("dbghelp.dll", EntryPoint = "MiniDumpWriteDump", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MiniDumpWriteDumpWithExceptionInformation(
        nint processHandle,
        uint processId,
        nint outputFileHandle,
        uint dumpType,
        ref MinidumpExceptionInformation exceptionInformation,
        nint userStreamInformation,
        nint callbackInformation);

    [DllImport("dbghelp.dll", EntryPoint = "MiniDumpWriteDump", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MiniDumpWriteDumpWithoutExceptionInformation(
        nint processHandle,
        uint processId,
        nint outputFileHandle,
        uint dumpType,
        nint exceptionInformation,
        nint userStreamInformation,
        nint callbackInformation);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private static string BuildUnusedCrashUtcFileStem()
    {
        string crashUtcFileStem = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        // Two processes could crash within the same millisecond; the suffix keeps one from overwriting
        // the other and only affects ordering between crashes that are that close together anyway.
        while (File.Exists(Path.Combine(CrashDirectoryPath, crashUtcFileStem + ".txt")))
        {
            crashUtcFileStem += "x";
        }
        return crashUtcFileStem;
    }

    /// <summary>
    /// Deletes crash pairs (and any orphaned half of one) beyond the newest five (WS-11). Stems from
    /// both extensions count, and the fixed-width UTC names make ordinal order chronological.
    /// </summary>
    private static void DeleteCrashesBeyondKeptCount()
    {
        foreach (string staleCrashFileStem in Directory.EnumerateFiles(CrashDirectoryPath)
                     // The null-forgiving operator is safe: EnumerateFiles yields full file paths, so
                     // GetFileName never sees null here.
                     .Select(crashFilePath => Path.GetFileName(crashFilePath)!)
                     .Where(crashFileName => crashFileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                         || crashFileName.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase))
                     .Select(crashFileName => crashFileName[..^4])
                     .Distinct()
                     .OrderDescending(StringComparer.Ordinal)
                     .Skip(KeptCrashCount))
        {
            // File.Delete on a name that does not exist is a no-op, so an orphaned half is harmless.
            File.Delete(Path.Combine(CrashDirectoryPath, staleCrashFileStem + ".txt"));
            File.Delete(Path.Combine(CrashDirectoryPath, staleCrashFileStem + ".dmp"));
        }
    }
}
