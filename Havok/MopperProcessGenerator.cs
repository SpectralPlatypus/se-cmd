using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using SECmd.Nif;

namespace SECmd.Havok
{
    /// <summary>
    /// Generates MOPP code by running niftools' <c>mopper.exe</c> as a child process.
    /// </summary>
    /// <remarks>
    /// This is the portable backend. mopper is a Win32 executable, but it talks pure
    /// stdin/stdout with no GUI and no COM, so it runs unmodified under Wine — which
    /// is what makes MOPP generation possible on Linux at all. Running it
    /// out-of-process also sidesteps the bitness matching that in-process P/Invoke
    /// into NifMopp.dll requires.
    ///
    /// Contract (from mopper's own <c>--help</c>):
    /// <code>
    /// mopper.exe -msm --      read a simple mesh from stdin
    /// mopper.exe -ccm --      read geometries from stdin, build a compressed mesh
    /// </code>
    /// Input is whitespace-separated ASCII; output is one number per line.
    /// </remarks>
    public sealed class MopperProcessGenerator : IMoppGenerator
    {
        /// <summary>How long to let mopper run before giving up.</summary>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(2);

        /// <summary>
        /// Path to <c>mopper.exe</c>. When null, it is looked for beside this
        /// executable and then on PATH.
        /// </summary>
        public string? MopperPath { get; set; }

        /// <summary>
        /// The launcher used on non-Windows hosts. Defaults to <c>wine</c>; set to an
        /// empty string to run the binary directly.
        /// </summary>
        public string WineCommand { get; set; } = "wine";

        private string? _resolvedPath;
        private bool _probed;
        private string? _reason;

        public string? UnavailableReason
        {
            get
            {
                Probe();
                return _reason;
            }
        }

        public bool IsAvailable
        {
            get
            {
                Probe();
                return _resolvedPath is not null;
            }
        }

        private static bool NeedsWine => !RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        private void Probe()
        {
            if (_probed)
                return;

            _probed = true;
            _resolvedPath = ResolveMopper();

            if (_resolvedPath is null)
            {
                _reason = "mopper.exe was not found. Place it beside the executable, put it on PATH, "
                    + $"or set {nameof(MopperPath)}.";
                return;
            }

            if (NeedsWine && WineCommand.Length > 0 && ResolveOnPath(WineCommand) is null)
            {
                _resolvedPath = null;
                _reason = $"mopper.exe was found at \"{ResolveMopper()}\" but \"{WineCommand}\" is not "
                    + "installed, and it is a Windows binary. Install Wine to use it on this platform.";
            }
        }

        /// <summary>
        /// Where mopper.exe is looked for, in order: an explicitly configured path,
        /// the current working directory, then the directory holding the executable.
        /// </summary>
        /// <remarks>
        /// The working directory comes first so a copy sitting next to the files
        /// being converted wins over an installed one. If none of them has it, PATH
        /// is searched.
        /// </remarks>
        public IEnumerable<string> SearchPaths()
        {
            if (MopperPath is { Length: > 0 } configured)
            {
                yield return configured;
                yield break;
            }

            yield return Path.Combine(Environment.CurrentDirectory, "mopper.exe");
            yield return Path.Combine(AppContext.BaseDirectory, "mopper.exe");
        }

        private string? ResolveMopper()
        {
            foreach (string candidate in SearchPaths())
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            // An explicit path that does not exist is an error, not a reason to go
            // hunting on PATH for something the caller did not ask for.
            return MopperPath is { Length: > 0 } ? null : ResolveOnPath("mopper.exe");
        }

        private static string? ResolveOnPath(string fileName)
        {
            string? path = Environment.GetEnvironmentVariable("PATH");

            if (path is null)
                return null;

            foreach (string directory in path.Split(Path.PathSeparator))
            {
                if (directory.Length == 0)
                    continue;

                string candidate = Path.Combine(directory, fileName);

                if (File.Exists(candidate))
                    return candidate;

                // On Unix a launcher such as wine has no extension.
                if (!fileName.Contains('.') && File.Exists(candidate))
                    return candidate;
            }

            return null;
        }

        /// <inheritdoc/>
        public MoppResult? GenerateSimpleMesh(IReadOnlyList<NifVector3> vertices, IReadOnlyList<NifTriangle> triangles)
        {
            if (!IsAvailable || vertices.Count == 0 || triangles.Count == 0)
                return null;

            string output = Run("-msm", BuildSimpleMeshInput(vertices, triangles));

            return ParseSimpleMeshOutput(output);
        }

        /// <summary>
        /// Serialises a mesh into mopper's input format: a vertex count and vertices,
        /// a triangle count and triangles, then a zero material-index count.
        /// </summary>
        internal static string BuildSimpleMeshInput(
            IReadOnlyList<NifVector3> vertices,
            IReadOnlyList<NifTriangle> triangles)
        {
            var text = new StringBuilder();

            text.Append(vertices.Count).Append('\n');

            foreach (NifVector3 v in vertices)
            {
                text.Append(Format(v.X)).Append(' ')
                    .Append(Format(v.Y)).Append(' ')
                    .Append(Format(v.Z)).Append('\n');
            }

            text.Append(triangles.Count).Append('\n');

            foreach (NifTriangle t in triangles)
                text.Append(t.V1).Append(' ').Append(t.V2).Append(' ').Append(t.V3).Append('\n');

            // mopper reads a material-index count next. It parses each index with
            // operator>> into a uint8, which reads a *character* rather than a
            // number, so anything non-zero here would be misread. Always send none.
            text.Append("0\n");

            return text.ToString();
        }

        /// <summary>
        /// Parses mopper's <c>-msm</c> output: origin, scale, code length, the code
        /// bytes as integers, then a triangle count and per-triangle welding info.
        /// </summary>
        internal static MoppResult? ParseSimpleMeshOutput(string output)
        {
            using var reader = new StringReader(output);
            var numbers = new List<string>();

            while (reader.ReadLine() is { } line)
            {
                line = line.Trim();

                if (line.Length > 0)
                    numbers.Add(line);
            }

            int at = 0;

            if (!TryNextFloat(numbers, ref at, out float x)
                || !TryNextFloat(numbers, ref at, out float y)
                || !TryNextFloat(numbers, ref at, out float z)
                || !TryNextFloat(numbers, ref at, out float scale)
                || !TryNextInt(numbers, ref at, out int length))
            {
                // mopper prints Havok's error text on failure rather than numbers.
                return null;
            }

            if (length <= 0 || at + length > numbers.Count)
                return null;

            byte[] code = new byte[length];

            for (int i = 0; i < length; i++)
            {
                if (!TryNextInt(numbers, ref at, out int value))
                    return null;

                code[i] = (byte)value;
            }

            var welding = new List<ushort>();

            if (TryNextInt(numbers, ref at, out int weldingCount) && weldingCount > 0)
            {
                for (int i = 0; i < weldingCount && TryNextInt(numbers, ref at, out int value); i++)
                    welding.Add((ushort)value);
            }

            return new MoppResult(code, new NifVector3(x, y, z), scale, welding);
        }

        private string Run(string mode, string input)
        {
            string executable = NeedsWine && WineCommand.Length > 0 ? WineCommand : _resolvedPath!;

            var start = new ProcessStartInfo
            {
                FileName = executable,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            if (NeedsWine && WineCommand.Length > 0)
                start.ArgumentList.Add(_resolvedPath!);

            start.ArgumentList.Add(mode);
            start.ArgumentList.Add("--");

            using var process = Process.Start(start)
                ?? throw new InvalidOperationException($"could not start {executable}");

            // Read stdout on a separate task: mopper can emit more than a pipe buffer
            // holds, and writing stdin while it blocks on a full stdout would deadlock.
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();

            process.StandardInput.Write(input);
            process.StandardInput.Close();

            if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Already gone.
                }

                throw new TimeoutException($"mopper did not finish within {Timeout}.");
            }

            // Wine writes its own diagnostics to stderr, so a non-empty stderr is not
            // on its own a failure; the output parse decides.
            _ = stderr;
            return stdout.GetAwaiter().GetResult();
        }

        private static string Format(float value) =>
            value.ToString("R", CultureInfo.InvariantCulture);

        private static bool TryNextFloat(List<string> numbers, ref int at, out float value)
        {
            value = 0;

            if (at >= numbers.Count)
                return false;

            bool ok = float.TryParse(numbers[at], NumberStyles.Float, CultureInfo.InvariantCulture, out value);
            at++;
            return ok;
        }

        private static bool TryNextInt(List<string> numbers, ref int at, out int value)
        {
            value = 0;

            if (at >= numbers.Count)
                return false;

            bool ok = int.TryParse(numbers[at], NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
            at++;
            return ok;
        }
    }
}
