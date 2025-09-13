using DynamicData;
using FluentResults;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Environments;
using Mutagen.Bethesda.Skyrim;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Enums;
using NiflySharp.Structs;
using Noggog;
using pxr;
using SECmd.Utils;
using System.CommandLine;
using System.Numerics;
using USD.NET;
using static SECmd.AnimData.HandVariableData;
using Scene = USD.NET.Scene;

namespace SECmd.Commands
{
    internal class ExportNif
    {
        public static void Register(RootCommand root)
        {
            Option<FileInfo> fileOption = new("--input", "-i") { Description = "Source project file to be retargeted", Required = true };
            Option<DirectoryInfo> outputOption = new("--output", "-o") { Description = "Output directory", DefaultValueFactory = parseResult => new(Environment.CurrentDirectory) };
            //Option<FileInfo?> skelRefOption = new("--skel", "-s") { Description = "Reference skeleton file", DefaultValueFactory = parseResult => null };

            Command exportCommand = new("export", "Export nif file to usd")
            {
                fileOption,
                outputOption,
                //skelRefOption
            };

            exportCommand.SetAction(parseResults =>
                Execute(parseResults.GetValue(fileOption)!,
                parseResults.GetValue(outputOption)!
                //parseResults.GetValue(skelRefOption)
                ));

            root.Subcommands.Add(exportCommand);
        }

        static Dictionary<string, string>? bonePaths = null;
        public static void Execute(FileInfo inputFile, DirectoryInfo outputFolder)
        {
            using NifConverter conv = new(inputFile);
            conv.Convert();

            var outputFile = new FileInfo(Path.Combine(outputFolder.FullName, inputFile.Name));

            conv.Save(outputFile);

            return;
        }
    }
}
