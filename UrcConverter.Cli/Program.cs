using System.CommandLine;
using Ardalis.GuardClauses;
using UrcConverter.Core.Engine;
using UrcConverter.Core.Writer;
using UrcConverter.Parser.Bms;
using UrcConverter.Parser.Ojn;
using UrcConverter.Parser.Osu;
using UrcConverter.Parser.Qua;
using UrcConverter.Parser.Sm;
using UrcParseResult = UrcConverter.Core.Abstractions.ParseResult;

#region Register Parsers

var engine = new ConverterEngine();
engine.RegisterParser(new OsuParser());
engine.RegisterParser(new BmsParser());
engine.RegisterParser(new SmParser());
engine.RegisterParser(new QuaParser());
engine.RegisterParser(new OjnParser());

#endregion

#region Convert

var convertFileArg = new Argument<FileInfo>("file")
{
    Description = "Chart file to convert"
};

var convertOutputOpt = new Option<DirectoryInfo?>("--output", "-o")
{
    Description = "Output directory (default: same as input)"
};

var convertCommand = new Command("convert", "Convert a single chart file to URC format");
convertCommand.Arguments.Add(convertFileArg);
convertCommand.Options.Add(convertOutputOpt);

convertCommand.SetAction(parseResult =>
{
    var file = parseResult.GetValue(convertFileArg);
    
    Guard.Against.Null(file);
    Guard.Against.Null(file.DirectoryName);
    
    var outputDir = parseResult.GetValue(convertOutputOpt)?.FullName ?? file.DirectoryName;

    if (!file.Exists)
    {
        Console.Error.WriteLine($"File not found: {file.FullName}");
        return 1;
    }

    Directory.CreateDirectory(outputDir);

    var result = engine.Convert(file.FullName);

    switch (result)
    {
        case UrcParseResult.Success success:
            var charts = success.Charts;
            for (var i = 0; i < charts.Count; i++)
            {
                var suffix = charts.Count > 1 ? $"_{i}" : "";
                var outName = $"{Path.GetFileNameWithoutExtension(file.Name)}{suffix}.urc";
                var outPath = Path.Combine(outputDir, outName);
                UrcWriter.WriteToFile(charts[i], outPath);
                Console.WriteLine($"  → {outPath}");
            }
            
            Console.WriteLine($"Converted {charts.Count} chart(s).");
            return 0;

        case UrcParseResult.Failure failure:
            Console.Error.WriteLine($"Error: {failure.Error}");
            return 1;

        default:
            return 1;
    }
});

#endregion

#region Batch

var batchDirArg = new Argument<DirectoryInfo>("directory")
{
    Description = "Directory containing chart files"
};

var batchOutputOpt = new Option<DirectoryInfo?>("--output", "-o")
{
    Description = "Output directory (default: same as input)"
};

var batchRecursiveOpt = new Option<bool>("--recursive", "-r")
{
    Description = "Search subdirectories"
};

var batchCommand = new Command("batch", "Batch convert all chart files in a directory");
batchCommand.Arguments.Add(batchDirArg);
batchCommand.Options.Add(batchOutputOpt);
batchCommand.Options.Add(batchRecursiveOpt);

batchCommand.SetAction(parseResult =>
{
    var dir = parseResult.GetValue(batchDirArg);
    
    Guard.Against.Null(dir);
    
    var outputDir = parseResult.GetValue(batchOutputOpt)?.FullName ?? dir.FullName;
    var recursive = parseResult.GetValue(batchRecursiveOpt);

    if (!dir.Exists)
    {
        Console.Error.WriteLine($"Directory not found: {dir.FullName}");
        return 1;
    }

    var extensions = engine.SupportedExtensions;
    var searchOptions = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
    var files = extensions.SelectMany(ext => Directory.GetFiles(dir.FullName, $"*{ext}", searchOptions)).ToArray();

    if (files.Length == 0)
    {
        Console.WriteLine("No supported chart files found.");
        return 0;
    }
    
    Console.WriteLine($"Found {files.Length} file(s). Converting...");
    Directory.CreateDirectory(outputDir);

    var (success, failed, totalCharts) = (0, 0, 0);

    foreach (var file in files)
    {
        var result = engine.Convert(file);

        switch (result)
        {
            case UrcParseResult.Success s:
                for (var i = 0; i < s.Charts.Count; i++)
                {
                    var suffix = s.Charts.Count > 1 ? $"_{i}" : "";
                    var outName = $"{Path.GetFileNameWithoutExtension(file)}{suffix}.urc";
                    var outPath = Path.Combine(outputDir, outName);
                    UrcWriter.WriteToFile(s.Charts[i], outPath);
                }

                totalCharts += s.Charts.Count;
                success++;
                break;

            case UrcParseResult.Failure f:
                Console.Error.WriteLine($"  FAIL: {Path.GetFileName(file)} — {f.Error}");
                failed++;
                break;
        }
    }

    Console.WriteLine($"Done. {success} file(s) → {totalCharts} chart(s), {failed} failed.");
    return failed > 0 ? 1 : 0;
});

#endregion

#region Root

var rootCommand = new RootCommand("URC Converter - Convert rhythm game chart files to URC format");
rootCommand.Subcommands.Add(convertCommand);
rootCommand.Subcommands.Add(batchCommand);

return rootCommand.Parse(args).Invoke();

#endregion