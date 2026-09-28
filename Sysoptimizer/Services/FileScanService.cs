using System.IO;
using System.Security.Cryptography;

namespace Sysoptimizer.Services;

public record DuplicateGroup(long Size, List<string> Paths);
public record LargeFile(string Path, long Size);

public static class FileScanService
{
    /// <summary>Walks a tree without letting one locked or permission-denied folder abort the whole scan.</summary>
    public static IEnumerable<string> EnumerateFilesSafe(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch { continue; }
            foreach (var f in files) yield return f;

            string[] subdirs;
            try { subdirs = Directory.GetDirectories(dir); }
            catch { continue; }
            foreach (var d in subdirs) stack.Push(d);
        }
    }

    /// <summary>Groups by size first (cheap), then hashes only files that already share a size — a
    /// different-size file can never be a duplicate, so it never has to be read.</summary>
    public static List<DuplicateGroup> FindDuplicates(string root, IProgress<string>? progress = null)
    {
        var bySize = new Dictionary<long, List<string>>();
        int scanned = 0;
        foreach (var file in EnumerateFilesSafe(root))
        {
            long len;
            try { len = new FileInfo(file).Length; } catch { continue; }
            if (len == 0) continue; // every empty file "matches" every other one; not a useful duplicate
            if (!bySize.TryGetValue(len, out var list)) bySize[len] = list = new List<string>();
            list.Add(file);
            if (++scanned % 500 == 0) progress?.Report($"Scanned {scanned} files...");
        }

        var groups = new List<DuplicateGroup>();
        foreach (var candidates in bySize.Values)
        {
            if (candidates.Count < 2) continue;
            var byHash = new Dictionary<string, List<string>>();
            foreach (var file in candidates)
            {
                string hash;
                try { using var stream = File.OpenRead(file); hash = Convert.ToHexString(SHA256.HashData(stream)); }
                catch { continue; }
                if (!byHash.TryGetValue(hash, out var list)) byHash[hash] = list = new List<string>();
                list.Add(file);
            }
            foreach (var dup in byHash.Values.Where(l => l.Count > 1))
            {
                long size;
                try { size = new FileInfo(dup[0]).Length; } catch { continue; }
                groups.Add(new DuplicateGroup(size, dup));
            }
        }
        return groups.OrderByDescending(g => g.Size * (g.Paths.Count - 1)).ToList();
    }

    public static List<LargeFile> FindLargest(string root, int top = 200)
    {
        var files = new List<LargeFile>();
        foreach (var file in EnumerateFilesSafe(root))
        {
            long len;
            try { len = new FileInfo(file).Length; } catch { continue; }
            files.Add(new LargeFile(file, len));
        }
        return files.OrderByDescending(f => f.Size).Take(top).ToList();
    }
}
