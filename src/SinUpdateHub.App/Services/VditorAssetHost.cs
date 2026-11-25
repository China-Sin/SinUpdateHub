using System.IO;
using System.IO.Compression;
using System.Reflection;
using SinUpdateHub.Core.Models;
using SinUpdateHub.Core.Services;

namespace SinUpdateHub.App.Services;

/// <summary>将内嵌的离线 Vditor 资源包与笔记图片缓存按版本解压/落盘到本地用户目录，供 WebView2 虚拟域名零联网加载。</summary>
public static class VditorAssetHost
{
    private const string VditorZipResourceName = "SinUpdateHub.App.Assets.vditor-dist.zip";
    private const string EditorHtmlResourceName = "SinUpdateHub.App.Assets.sinnote-editor.html";
    private const string StampVersion = "vditor-4.0.1-r11";

    public const string NoteImagesHostName = "sinnote-img.local";

    private static readonly object SyncRoot = new();

    /// <summary>WebView2 用户数据目录（统一落在 %APPDATA%\SinUpdateHub\webview2，避免污染程序同级目录）。</summary>
    public static string WebView2UserDataDir => Path.Combine(AppPaths.Root, "webview2");

    /// <summary>虚拟域名 https://sinnote.local/ 映射的本地静态资源根目录。</summary>
    public static string AssetRootDir => Path.Combine(AppPaths.Root, "editor", StampVersion);

    /// <summary>虚拟域名 https://sinnote-img.local/ 映射的本地图片缓存目录。</summary>
    public static string NoteImagesDir => Path.Combine(AppPaths.Root, "note-images");

    /// <summary>清理图片键名防止路径穿越。</summary>
    public static string? SanitizeImageKey(string? imageKey)
    {
        if (string.IsNullOrWhiteSpace(imageKey))
        {
            return null;
        }

        var name = Path.GetFileName(imageKey.Trim());
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return null;
        }

        return name;
    }

    /// <summary>确保单张图片已解码缓存到本地 note-images 目录（仅文件缺失时写入，零阻塞读取）。</summary>
    public static void EnsureImageCached(string? imageKey, string? base64)
    {
        var safeKey = SanitizeImageKey(imageKey);
        if (safeKey is null || string.IsNullOrWhiteSpace(base64))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(NoteImagesDir);
            var destPath = Path.Combine(NoteImagesDir, safeKey);
            if (File.Exists(destPath) && new FileInfo(destPath).Length > 0)
            {
                return;
            }

            var bytes = Convert.FromBase64String(base64.Trim());
            var tmpPath = destPath + ".tmp";
            File.WriteAllBytes(tmpPath, bytes);
            File.Move(tmpPath, destPath, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Write($"缓存 SinNote 图片失败：{safeKey}", ex);
        }
    }

    /// <summary>将笔记页关联的图片集合全部同步到本地缓存目录，确保编辑器切换页即刻加载图片。</summary>
    public static void SyncPageImagesToCache(NotePage? page)
    {
        if (page?.Images is null || page.Images.Count == 0)
        {
            return;
        }

        Directory.CreateDirectory(NoteImagesDir);
        foreach (var kv in page.Images)
        {
            if (kv.Value is not null)
            {
                EnsureImageCached(kv.Key, kv.Value.Base64);
            }
        }
    }

    /// <summary>确保离线 Vditor dist 与 editor.html 已完整解压并返回 AssetRootDir。</summary>
    public static string EnsureAssetsExtracted()
    {
        lock (SyncRoot)
        {
            Directory.CreateDirectory(NoteImagesDir);

            var root = AssetRootDir;
            var htmlPath = Path.Combine(root, "editor.html");
            var vditorMainJs = Path.Combine(root, "vditor", "dist", "index.min.js");
            var luteJs = Path.Combine(root, "vditor", "dist", "js", "lute", "lute.min.js");
            var stampFile = Path.Combine(root, ".asset-stamp");

            var assembly = Assembly.GetExecutingAssembly();
            using var zipStream = assembly.GetManifestResourceStream(VditorZipResourceName)
                ?? throw new InvalidOperationException($"未找到内嵌资源：{VditorZipResourceName}");
            using var htmlStream = assembly.GetManifestResourceStream(EditorHtmlResourceName)
                ?? throw new InvalidOperationException($"未找到内嵌资源：{EditorHtmlResourceName}");

            var expectedStamp = $"{StampVersion}:{zipStream.Length}:{htmlStream.Length}";
            if (File.Exists(stampFile)
                && File.Exists(htmlPath)
                && File.Exists(vditorMainJs)
                && File.Exists(luteJs))
            {
                var currentStamp = File.ReadAllText(stampFile).Trim();
                if (string.Equals(currentStamp, expectedStamp, StringComparison.Ordinal))
                {
                    CleanupStaleEditorVersions();
                    return root;
                }
            }

            Directory.CreateDirectory(root);
            var fullRoot = Path.GetFullPath(root);
            if (!fullRoot.EndsWith(Path.DirectorySeparatorChar))
            {
                fullRoot += Path.DirectorySeparatorChar;
            }

            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true))
            {
                foreach (var entry in archive.Entries)
                {
                    var normalizedRel = entry.FullName
                        .Replace('/', Path.DirectorySeparatorChar)
                        .Replace('\\', Path.DirectorySeparatorChar)
                        .TrimStart(Path.DirectorySeparatorChar);

                    if (string.IsNullOrEmpty(normalizedRel) || normalizedRel.EndsWith(Path.DirectorySeparatorChar))
                    {
                        continue;
                    }

                    var destPath = Path.GetFullPath(Path.Combine(root, normalizedRel));
                    if (!destPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                    entry.ExtractToFile(destPath, overwrite: true);
                }
            }

            using (var reader = new StreamReader(htmlStream))
            {
                File.WriteAllText(htmlPath, reader.ReadToEnd(), System.Text.Encoding.UTF8);
            }

            File.WriteAllText(stampFile, expectedStamp, System.Text.Encoding.UTF8);
            CleanupStaleEditorVersions();
            return root;
        }
    }

    /// <summary>自动清理 %APPDATA%\SinUpdateHub\editor\ 下旧版本的离线编辑器解压目录，避免随升级无限堆积。</summary>
    private static void CleanupStaleEditorVersions()
    {
        try
        {
            var editorBase = Path.Combine(AppPaths.Root, "editor");
            if (!Directory.Exists(editorBase))
            {
                return;
            }

            foreach (var dir in Directory.GetDirectories(editorBase))
            {
                var name = Path.GetFileName(dir);
                if (string.Equals(name, StampVersion, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch
                {
                    // 若旧版本目录正被占用则忽略，留待下次启动清理
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("清理旧版编辑器缓存目录失败", ex);
        }
    }

    /// <summary>清理本地 note-images 缓存目录中不再被任何笔记页或回收站引用的孤儿图片文件。</summary>
    public static void PruneUnreferencedCachedImages(
        IEnumerable<NoteSection>? sections,
        IEnumerable<NoteTrashItem>? trashItems = null)
    {
        try
        {
            if (!Directory.Exists(NoteImagesDir))
            {
                return;
            }

            var activeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void CollectPageKeys(NotePage? page)
            {
                if (page?.Images is null)
                {
                    return;
                }

                foreach (var key in page.Images.Keys)
                {
                    var safe = SanitizeImageKey(key);
                    if (safe is not null)
                    {
                        activeKeys.Add(safe);
                    }
                }
            }

            if (sections is not null)
            {
                foreach (var sec in sections)
                {
                    if (sec?.Pages is null)
                    {
                        continue;
                    }

                    foreach (var page in sec.Pages)
                    {
                        CollectPageKeys(page);
                    }
                }
            }

            if (trashItems is not null)
            {
                foreach (var item in trashItems)
                {
                    if (item is null)
                    {
                        continue;
                    }

                    if (item.IsSection && item.Section?.Pages is not null)
                    {
                        foreach (var page in item.Section.Pages)
                        {
                            CollectPageKeys(page);
                        }
                    }
                    else if (!item.IsSection)
                    {
                        CollectPageKeys(item.Page);
                    }
                }
            }

            foreach (var file in Directory.GetFiles(NoteImagesDir))
            {
                var fileName = Path.GetFileName(file);
                if (activeKeys.Contains(fileName))
                {
                    continue;
                }

                try
                {
                    File.Delete(file);
                }
                catch
                {
                    // 文件正被 WebView2 占用时忽略
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("清理未引用图片缓存失败", ex);
        }
    }
}
