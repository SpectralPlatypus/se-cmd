using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using SECmd.Nif;
using Xunit;

namespace SECmd.Tests
{
    /// <summary>
    /// The reader and writer against every mesh Skyrim ships.
    /// </summary>
    /// <remarks>
    /// <c>Skyrim - Meshes0.bsa</c> and <c>Meshes1.bsa</c> hold 22,047 NIFs between
    /// them: every vanilla static, creature, architecture piece, weapon and effect,
    /// across every block type Bethesda actually used. Loading each and saving it
    /// back has to reproduce the file byte for byte.
    ///
    /// This is a different kind of check from the committed fixtures. Twenty-four
    /// files chosen for the features they demonstrate cannot tell you what the
    /// hundredth-most-common block looks like in the wild; twenty-two thousand
    /// arbitrary ones can. It found two bugs the fixtures never would have: ragged
    /// two-dimensional arrays sizing to nothing, and half-precision NaNs losing
    /// their payload.
    ///
    /// **Nothing is copied out of the archives.** They are read in place, from a
    /// path this machine happens to have. Where the game is not installed the test
    /// returns, because a checkout without Skyrim must still pass.
    ///
    /// Set <c>SECMD_SKYRIM_DATA</c> to point at a Data folder elsewhere, and
    /// <c>SECMD_BSA_SAMPLE</c> to a number to check a random subset instead of all
    /// of them. The full sweep takes about five minutes, so
    /// <c>dotnet test --filter "FullyQualifiedName!~BsaCorpus"</c> is the way to
    /// skip it while working on something else.
    /// </remarks>
    public class BsaCorpusTests
    {
        private static readonly string[] Archives = ["Skyrim - Meshes0.bsa", "Skyrim - Meshes1.bsa"];

        /// <summary>Places a Skyrim Special Edition Data folder is likely to be.</summary>
        private static readonly string[] Candidates =
        [
            "/run/media/ecanepa/53908dc5-345d-4cc7-9152-708d998bd88a/.local/share/Steam/steamapps/common/Skyrim Special Edition/Data",
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local/share/Steam/steamapps/common/Skyrim Special Edition/Data"),
            @"C:\Program Files (x86)\Steam\steamapps\common\Skyrim Special Edition\Data"
        ];

        /// <summary>The Data folder, or null when the game is not installed here.</summary>
        private static string? DataFolder()
        {
            string? configured = Environment.GetEnvironmentVariable("SECMD_SKYRIM_DATA");

            if (!string.IsNullOrWhiteSpace(configured))
                return Directory.Exists(configured) ? configured : null;

            return Candidates.FirstOrDefault(
                c => Archives.All(a => File.Exists(Path.Combine(c, a))));
        }

        [Fact]
        public void EveryVanillaMeshSavesBackByteForByte()
        {
            if (DataFolder() is not { } data)
                return;

            var db = NifXmlDatabase.LoadEmbedded();
            var failures = new ConcurrentBag<(string Path, string Reason)>();
            var stopwatch = Stopwatch.StartNew();
            int checked_ = 0;

            foreach (string archive in Archives)
            {
                var reader = Archive.CreateReader(GameRelease.SkyrimSE, Path.Combine(data, archive));

                var files = reader.Files
                    .Where(f => f.Path.EndsWith(".nif", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (Sample() is { } sample && sample < files.Count)
                    files = files.OrderBy(f => f.Path.GetHashCode()).Take(sample).ToList();

                Parallel.ForEach(files, file =>
                {
                    Interlocked.Increment(ref checked_);

                    byte[] original;

                    try
                    {
                        original = file.GetBytes();
                    }
                    catch (Exception e)
                    {
                        failures.Add((file.Path, $"could not be read out of the archive: {e.GetType().Name}"));
                        return;
                    }

                    try
                    {
                        using var input = new MemoryStream(original);
                        NifModel model = NifModel.Load(input, db);

                        using var output = new MemoryStream();
                        model.Save(output);

                        byte[] actual = output.ToArray();

                        if (actual.AsSpan().SequenceEqual(original))
                            return;

                        int at = 0;

                        while (at < actual.Length && at < original.Length && actual[at] == original[at])
                            at++;

                        failures.Add((file.Path,
                            $"differs at 0x{at:X} (length {original.Length} became {actual.Length})"));
                    }
                    catch (Exception e)
                    {
                        failures.Add((file.Path, e.Message));
                    }
                });
            }

            // Something has to have been checked, or a silent change to the archive
            // names would turn this into a test that always passes.
            Assert.True(checked_ > 0, $"no meshes found in {data}");

            if (failures.IsEmpty)
                return;

            Assert.Fail(Describe(failures, checked_, stopwatch.Elapsed));
        }

        /// <summary>
        /// Groups failures by cause, since one bug shows up as hundreds of files.
        /// </summary>
        private static string Describe(
            ConcurrentBag<(string Path, string Reason)> failures, int checked_, TimeSpan elapsed)
        {
            var report = new System.Text.StringBuilder()
                .AppendLine($"{failures.Count} of {checked_} meshes did not survive the round trip "
                            + $"({elapsed.TotalSeconds:F0}s):")
                .AppendLine();

            var groups = failures
                .GroupBy(f => Generalise(f.Reason))
                .OrderByDescending(g => g.Count());

            foreach (var group in groups)
            {
                report.AppendLine($"  {group.Count(),5}  {group.Key}");

                foreach ((string path, string reason) in group.Take(3))
                    report.AppendLine($"           {path}  [{reason}]");
            }

            return report.ToString();
        }

        /// <summary>Strips the numbers out of a message so one cause groups as one.</summary>
        private static string Generalise(string reason) =>
            System.Text.RegularExpressions.Regex.Replace(reason, @"0x[0-9A-Fa-f]+|\d+", "N");

        private static int? Sample() =>
            int.TryParse(
                Environment.GetEnvironmentVariable("SECMD_BSA_SAMPLE"),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0 ? n : null;
    }
}
