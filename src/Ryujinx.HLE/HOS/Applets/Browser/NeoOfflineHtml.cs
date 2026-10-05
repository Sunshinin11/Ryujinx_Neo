using LibHac.Common;
using LibHac.Common.Keys;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using LibHac.Tools.Fs;
using LibHac.Tools.FsSystem;
using LibHac.Tools.FsSystem.NcaUtils;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Ryujinx.HLE.HOS.Applets.Browser
{
    /// <summary>
    /// NEOV2-WEBOFF1: offline web applet (LibAppletOff) completion, used only with the per-game "Termos offline" option.
    /// Games such as the BioShock collection show their legal terms with the offline browser on the first run and only
    /// continue when the browser ends on the "accept" page. On the console that happens when the page navigates to a
    /// http://localhost/... address, which the applet returns as LastUrl. The emulator cannot show the page, so it reads
    /// the HTML from the game's own HtmlDocument content, finds those localhost addresses and returns the accepting one.
    /// </summary>
    static class NeoOfflineHtml
    {
        private const long MaxFileBytes = 1024 * 1024;
        private const int MaxScannedFiles = 64;

        private static readonly Regex _localhostUrl = new(@"https?://localhost[^\s""'<>`\)\\]*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly string[] _acceptWords = ["accept", "agree", "consent", "confirm", "continue", "ok", "yes", "true", "next", "proceed"];
        private static readonly string[] _rejectWords = ["decline", "disagree", "reject", "refuse", "cancel", "back", "deny", "false", "quit", "exit"];

        private static readonly object _lock = new();
        private static IFileSystem _partition;
        private static KeySet _keySet;

        /// <summary>
        /// Remembers the partition (NSP/NSZ/XCI secure) of the running application, where the HtmlDocument NCA lives.
        /// </summary>
        public static void Register(IFileSystem partition, KeySet keySet)
        {
            lock (_lock)
            {
                _partition = partition;
                _keySet = keySet;
            }
        }

        public static bool TryFindLastUrl(string documentPath, out string lastUrl)
        {
            lastUrl = null;

            if (string.IsNullOrEmpty(documentPath))
            {
                return false;
            }

            // TLV strings keep their NUL terminator (and padding).
            string path = documentPath.Split('\0')[0];
            int cut = path.IndexOfAny(['?', '#']);

            if (cut >= 0)
            {
                path = path[..cut];
            }

            path = path.Replace('\\', '/').TrimStart('/');

            List<string> urls = [];

            try
            {
                lock (_lock)
                {
                    if (_partition == null)
                    {
                        return false;
                    }

                    foreach (DirectoryEntryEx entry in _partition.EnumerateEntries("/", "*.nca"))
                    {
                        if (entry.Name.EndsWith(".cnmt.nca", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        Nca nca;

                        try
                        {
                            using UniqueRef<IFile> ncaFile = new();

                            if (_partition.OpenFile(ref ncaFile.Ref, entry.FullPath.ToU8Span(), OpenMode.Read).IsFailure())
                            {
                                continue;
                            }

                            nca = new Nca(_keySet, ncaFile.Release().AsStorage());

                            if (nca.Header.ContentType != NcaContentType.Manual)
                            {
                                continue;
                            }
                        }
                        catch (Exception)
                        {
                            continue;
                        }

                        IFileSystem romFs;

                        try
                        {
                            romFs = nca.OpenFileSystem(NcaSectionType.Data, IntegrityCheckLevel.None);
                        }
                        catch (Exception)
                        {
                            continue;
                        }

                        string pagePath = null;

                        foreach (string candidate in new[] { "/html-document/" + path, "/legal-information/" + path, "/" + path })
                        {
                            if (romFs.GetEntryType(out DirectoryEntryType type, candidate.ToU8Span()).IsSuccess() && type == DirectoryEntryType.File)
                            {
                                pagePath = candidate;
                                break;
                            }
                        }

                        if (pagePath == null)
                        {
                            continue;
                        }

                        // The page itself first, then the other html/js files of its folder (the buttons often live in scripts).
                        CollectUrls(romFs, pagePath, urls);

                        string folder = pagePath[..(pagePath.LastIndexOf('/') + 1)];
                        int scanned = 0;

                        foreach (DirectoryEntryEx file in romFs.EnumerateEntries(folder.TrimEnd('/') is { Length: > 0 } f ? f : "/", "*", SearchOptions.RecurseSubdirectories))
                        {
                            if (file.Type != DirectoryEntryType.File || file.FullPath == pagePath)
                            {
                                continue;
                            }

                            string lower = file.Name.ToLowerInvariant();

                            if (!(lower.EndsWith(".html") || lower.EndsWith(".htm") || lower.EndsWith(".js")))
                            {
                                continue;
                            }

                            if (++scanned > MaxScannedFiles)
                            {
                                break;
                            }

                            CollectUrls(romFs, file.FullPath, urls);
                        }

                        break;
                    }
                }
            }
            catch (Exception)
            {
                return false;
            }

            lastUrl = PickAcceptUrl(urls);

            return lastUrl != null;
        }

        private static void CollectUrls(IFileSystem fs, string path, List<string> urls)
        {
            using UniqueRef<IFile> file = new();

            if (fs.OpenFile(ref file.Ref, path.ToU8Span(), OpenMode.Read).IsFailure())
            {
                return;
            }

            if (file.Get.GetSize(out long size).IsFailure() || size <= 0)
            {
                return;
            }

            byte[] data = new byte[Math.Min(size, MaxFileBytes)];

            if (file.Get.Read(out long read, 0, data, ReadOption.None).IsFailure() || read <= 0)
            {
                return;
            }

            string text = Encoding.UTF8.GetString(data, 0, (int)read);

            foreach (Match match in _localhostUrl.Matches(text))
            {
                string url = match.Value.TrimEnd('.', ',', ';');

                if (!urls.Contains(url))
                {
                    urls.Add(url);
                }
            }
        }

        private static string PickAcceptUrl(List<string> urls)
        {
            if (urls.Count == 0)
            {
                return null;
            }

            string best = null;
            int bestScore = int.MinValue;

            foreach (string url in urls)
            {
                string lower = url.ToLowerInvariant();
                int score = 0;

                foreach (string word in _acceptWords)
                {
                    if (lower.Contains(word))
                    {
                        score += 2;
                    }
                }

                foreach (string word in _rejectWords)
                {
                    if (lower.Contains(word))
                    {
                        score -= 3;
                    }
                }

                if (score > bestScore)
                {
                    best = url;
                    bestScore = score;
                }
            }

            // With several candidates and no clear accept word, prefer none over a reject page.
            return bestScore < 0 ? null : best;
        }
    }
}
