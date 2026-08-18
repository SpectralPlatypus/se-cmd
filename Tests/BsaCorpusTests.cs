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
    /// Twice over: once re-saving the tree the reader built, and once rebuilding the
    /// whole model through the authoring API first (see <see cref="RebuildTests"/>).
    /// The second is the harder of the two and the one the FBX importer depends on.
    ///
    /// This is a different kind of check from the committed fixtures. Twenty-four
    /// files chosen for the features they demonstrate cannot tell you what the
    /// hundredth-most-common block looks like in the wild; twenty-two thousand
    /// arbitrary ones can. It found four bugs the fixtures never would have: ragged
    /// two-dimensional arrays sizing to nothing, half-precision NaNs losing their
    /// payload, block-type tables being reordered, and string tables being interned
    /// when they had to be copied.
    ///
    /// **Nothing is copied out of the archives.** They are read in place, from a
    /// folder named by <c>SECMD_SKYRIM_DATA</c>.
    ///
    /// **It does not run unless asked.** Without that variable the test returns, so
    /// an ordinary <c>dotnet test</c> is unaffected and a checkout without Skyrim
    /// passes. The two sweeps take about five and ten minutes, which is far too long
    /// to sit in the middle of everybody's build:
    ///
    /// <code>
    /// SECMD_SKYRIM_DATA="/path/to/Skyrim Special Edition/Data" dotnet test \
    ///     --filter "FullyQualifiedName~BsaCorpus"
    /// </code>
    ///
    /// <c>SECMD_BSA_SAMPLE=N</c> checks a subset instead of all of them, for when
    /// fifteen minutes is still too long.
    /// </remarks>
    [Trait("Category", "Corpus")]
    public class BsaCorpusTests
    {
        private static readonly string[] Archives = ["Skyrim - Meshes0.bsa", "Skyrim - Meshes1.bsa"];

        /// <summary>
        /// The Data folder to sweep, or null when nobody asked for one.
        /// </summary>
        /// <remarks>
        /// Named rather than searched for. A test that finds the game on its own
        /// runs on whoever happens to have it installed, which is how a five-minute
        /// sweep ends up in somebody else's ordinary build.
        /// </remarks>
        private static string? DataFolder()
        {
            string? configured = Environment.GetEnvironmentVariable("SECMD_SKYRIM_DATA");

            if (string.IsNullOrWhiteSpace(configured))
                return null;

            // Set but wrong is a different thing from not set: somebody asked for
            // this sweep and did not get it, and passing quietly would tell them it
            // had run.
            Assert.True(Directory.Exists(configured), $"SECMD_SKYRIM_DATA is not a folder: {configured}");

            string? missing = Archives.FirstOrDefault(a => !File.Exists(Path.Combine(configured, a)));

            Assert.True(missing is null, $"{missing} is not in {configured}");

            return configured;
        }

        [Fact]
        public void EveryVanillaMeshSavesBackByteForByte()
        {
            Sweep((original, db) =>
            {
                using var input = new MemoryStream(original);
                NifModel model = NifModel.Load(input, db);

                using var output = new MemoryStream();
                model.Save(output);

                return Compare(original, output);
            });
        }

        [Fact]
        public void EveryVanillaMeshRebuildsByteForByte()
        {
            // The same files through the authoring path instead: every block
            // inserted by type and filled field by field, every array sized from its
            // own count, the header and string table recomputed. That is what the FBX
            // importer does, and the only thing that says it does it correctly at
            // scale.
            Sweep((original, db) =>
            {
                using var input = new MemoryStream(original);
                NifModel model = NifModel.Load(input, db);

                using var output = new MemoryStream();
                RebuildTests.Rebuild(model).Save(output);

                return Compare(original, output);
            });
        }

        /// <summary>The reason the two differ, or null when they do not.</summary>
        private static string? Compare(byte[] original, MemoryStream saved)
        {
            byte[] actual = saved.ToArray();

            if (actual.AsSpan().SequenceEqual(original))
                return null;

            int at = 0;

            while (at < actual.Length && at < original.Length && actual[at] == original[at])
                at++;

            return $"differs at 0x{at:X} (length {original.Length} became {actual.Length})";
        }

        /// <summary>
        /// Runs a check over every mesh in the archives, or none if none were asked
        /// for.
        /// </summary>
        private static void Sweep(Func<byte[], NifXmlDatabase, string?> check)
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
                        if (check(original, db) is { } reason)
                            failures.Add((file.Path, reason));
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
