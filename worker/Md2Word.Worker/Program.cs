using System.Text;
using Md2Word.Worker.Protocol;

Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

return await WorkerProtocolHost.RunAsync(Console.In, Console.Out, Console.Error);
