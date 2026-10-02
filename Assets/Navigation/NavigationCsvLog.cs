using System;
using System.IO;
using System.Text;

namespace ShipRobot.Navigation
{
    // Per-Play CSV. Quoted cells preserve commas, quotes and newlines in fault details.
    public sealed class NavigationCsvLog : IDisposable
    {
        private readonly StreamWriter writer;
        public NavigationCsvLog(string path, params string[] header)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            writer = new StreamWriter(path, false, new UTF8Encoding(true));
            Write(header);
        }
        public void Write(params string[] cells)
        {
            for (int i = 0; i < cells.Length; i++)
            {
                if (i > 0) writer.Write(',');
                writer.Write('"');
                writer.Write((cells[i] ?? "").Replace("\"", "\"\""));
                writer.Write('"');
            }
            writer.WriteLine();
            writer.Flush();
        }
        public void Dispose() => writer.Dispose();
    }
}
