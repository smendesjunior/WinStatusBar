// Pacote do instalador com a compactação máxima do .NET (Deflate SmallestSize).
// Uso: dotnet run tools/make-payload.cs -- <pasta-do-app> <payload.zip>
using System.IO.Compression;

if (args.Length != 2 || !Directory.Exists(args[0])) { Console.Error.WriteLine("Uso: make-payload <pasta> <zip>"); return 1; }
if (File.Exists(args[1])) File.Delete(args[1]);
ZipFile.CreateFromDirectory(args[0], args[1], CompressionLevel.SmallestSize, includeBaseDirectory: false);
Console.WriteLine($"payload: {new FileInfo(args[1]).Length / 1048576.0:N1} MB");
return 0;
