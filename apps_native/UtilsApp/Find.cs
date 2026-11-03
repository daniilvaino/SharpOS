using System;
using System.Collections.Generic;
using System.IO;
using SharpOS.Std.Pipes;

namespace UtilsApp
{
    /// <summary>What find -l sends: a file as an object, for KQL and sort -k.</summary>
    [Message]
    public sealed class FoundFile
    {
        public string Path;
        public string Name;
        public long Size;
        public DateTime Modified;
        public bool IsDirectory;
    }

    // find [PATH...] [-name GLOB] [-type f|d] [-maxdepth N] [-l]: a path a line;
    // with -l, an object a file. Names compare as FAT does, without case.
    internal static class Find
    {
        private static string s_glob;
        private static char s_type;
        private static long s_maxDepth = long.MaxValue;
        private static TextWriter s_lines;
        private static PipeWriter<FoundFile> s_objects;

        public static int Run(string[] args)
        {
            const string usage = "find [PATH...] [-name GLOB] [-type f|d] [-maxdepth N] [-l] — paths, or with -l objects";
            var paths = new List<string>();
            bool objects = false;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                bool hasValue = i + 1 < args.Length;
                if (a == "-name" && hasValue) s_glob = args[++i];
                else if (a == "-type" && hasValue && (args[i + 1] == "f" || args[i + 1] == "d")) s_type = args[++i][0];
                else if (a == "-maxdepth" && hasValue && long.TryParse(args[i + 1], out s_maxDepth) && s_maxDepth >= 0) i++;
                else if (a == "-l") objects = true;
                else if (a == "--help" || a == "-h" || a == "-?") { Console.WriteLine("usage: " + usage); return 0; }
                else if (a.Length > 1 && a[0] == '-')
                {
                    Console.Error.WriteLine("find: unknown option: " + a);
                    Console.Error.WriteLine("usage: " + usage);
                    return 2;
                }
                else paths.Add(a);
            }
            if (paths.Count == 0) paths.Add(".");

            int code = 0;
            if (objects) s_objects = Pipe.Write<FoundFile>();
            else s_lines = Pipe.WriteText();
            try
            {
                foreach (string path in paths)
                {
                    bool isDirectory = Directory.Exists(path);
                    if (!isDirectory && !File.Exists(path))
                    {
                        Console.Error.WriteLine("find: no such file or directory: " + path);
                        code = 1;
                        continue;
                    }
                    Visit(path, isDirectory, 0);
                }
            }
            finally
            {
                s_objects?.Dispose();
                s_lines?.Dispose();
            }
            return code;
        }

        private static void Visit(string path, bool isDirectory, long depth)
        {
            string name = LastName(path);
            if ((s_type == 0 || (s_type == 'd') == isDirectory) && (s_glob == null || Matches(s_glob, 0, name, 0)))
                Report(path, name, isDirectory);
            if (!isDirectory || depth >= s_maxDepth) return;

            string[] files, directories;
            try
            {
                files = Directory.GetFiles(path);
                directories = Directory.GetDirectories(path);
            }
            catch (IOException e)
            {
                Console.Error.WriteLine("find: " + path + ": " + e.Message);
                return;
            }
            Array.Sort(files, string.CompareOrdinal);
            Array.Sort(directories, string.CompareOrdinal);
            foreach (string f in files) Visit(Joined(path, LastName(f)), false, depth + 1);
            foreach (string d in directories) Visit(Joined(path, LastName(d)), true, depth + 1);
        }

        private static void Report(string path, string name, bool isDirectory)
        {
            if (s_lines != null)
            {
                s_lines.WriteLine(path);
                return;
            }
            var found = new FoundFile { Path = path, Name = name, IsDirectory = isDirectory };
            if (isDirectory)
            {
                found.Modified = new DirectoryInfo(path).LastWriteTime;
            }
            else
            {
                var info = new FileInfo(path);
                found.Size = info.Length;
                found.Modified = info.LastWriteTime;
            }
            s_objects.Copy(found);
        }

        private static string Joined(string directory, string name)
            => directory.EndsWith("/") ? directory + name : directory + "/" + name;

        private static string LastName(string path)
        {
            int end = path.Length;
            while (end > 1 && (path[end - 1] == '/' || path[end - 1] == '\\')) end--;
            int start = end;
            while (start > 0 && path[start - 1] != '/' && path[start - 1] != '\\') start--;
            return path.Substring(start, end - start);
        }

        // * any run, ? any one character, without case.
        private static bool Matches(string glob, int g, string name, int n)
        {
            while (g < glob.Length)
            {
                char c = glob[g];
                if (c == '*')
                {
                    for (int rest = n; rest <= name.Length; rest++)
                        if (Matches(glob, g + 1, name, rest)) return true;
                    return false;
                }
                if (n >= name.Length) return false;
                if (c != '?' && char.ToUpperInvariant(c) != char.ToUpperInvariant(name[n])) return false;
                g++;
                n++;
            }
            return n == name.Length;
        }
    }
}
