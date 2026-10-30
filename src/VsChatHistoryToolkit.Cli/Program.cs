using System.Text;
using VsChatHistoryToolkit.Cli;

var utf8 = new UTF8Encoding(false);
using var stdout = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true };
using var stderr = new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true };

return CliRunner.Run(args, stdout, stderr);
