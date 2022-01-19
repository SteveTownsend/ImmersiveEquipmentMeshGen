using System;
using System.IO;

namespace AllGUD
{
    public class Logger : IDisposable
    {
        private StreamWriter? logWriter;

        public Logger(string fileName)
        {
            if (!String.IsNullOrEmpty(fileName))
            {
                logWriter = new StreamWriter(fileName, false);
                logWriter.AutoFlush = true;
            }
        }

        public void WriteLine(string format, params object?[] args)
        {
            if (logWriter != null)
            {
                logWriter.WriteLine(format, args);
            }
            Console.WriteLine(format, args);
        }

        public void Dispose()
        {
            if (logWriter != null)
            {
                logWriter.Flush();
                logWriter.Dispose();
            }
        }
    }
}
