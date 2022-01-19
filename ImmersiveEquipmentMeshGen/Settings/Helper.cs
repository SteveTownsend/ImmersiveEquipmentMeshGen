using System;
using System.IO;

namespace AllGUD
{
    public class Helper
    {
        private static string AsAbsoluteDirPath(string path)
        {
            path = AsAbsolutePath(path);
            if (!path.EndsWith('/'))
                path += '/';
            return path;
        }

        private static string AsAbsolutePath(string path)
        {
            if (String.IsNullOrEmpty(path))
                return String.Empty;

            int badIndex = path.IndexOfAny(Path.GetInvalidPathChars());
            if (badIndex != -1)
                throw new ArgumentException(String.Format("Path contains invalid character at index {0}", badIndex));

            return Path.GetFullPath(path).Replace('\\', '/');
        }

        public static string EnsureInputPathIsValid(string path)
        {
            if (!String.IsNullOrEmpty(path))
            {
                // validate and normalize
                path = AsAbsoluteDirPath(path);
                if (!Directory.Exists(path))
                {
                    throw new ArgumentException("Invalid Input Path - directory does not exist: " + path);
                }
            }
            return path;
        }
        public static string EnsureOutputPathIsValid(string path)
        {
            if (!String.IsNullOrEmpty(path))
            {
                // validate and normalize
                path = AsAbsoluteDirPath(path);
                if (!Directory.Exists(Path.GetPathRoot(path)))
                {
                    throw new ArgumentException("Invalid Output Path - location of directory does not exist (e.g. drive letter bad): " + path);
                }
            }
            return path;
        }

        public static string EnsureOutputFileIsValid(string file)
        {
            if (String.IsNullOrEmpty(file))
            {
                return file;
            }
            // validate and normalize
            file = AsAbsolutePath(file);
            if (Directory.Exists(file))
            {
                throw new ArgumentException("Invalid Output File: reusing name of an existing directory: " + file);
            }
            if (!Directory.Exists(Path.GetDirectoryName(file)))
            {
                throw new ArgumentException("Invalid Output File, directory does not exist: " + file);
            }
            return file;
        }
    }
}
